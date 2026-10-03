using Godot;

public enum LotObjectKind
{
	Cube,
	Sphere,
	Cylinder,
	Plane,
	/// <summary>Upright capsule — the default player character for Test/Game mode (§3.4).</summary>
	Capsule
}

/// <summary>Selection highlight state for a 3D lot object. Mirrors the UI elements' selection tint.</summary>
public enum LotOutlineState
{
	None,
	Selected,
	Dragged
}

/// <summary>
/// A 3D lot object (port of Unity's CreatePrimitive results). The node IS the object transform;
/// its MeshInstance3D child carries a per-object StandardMaterial3D so Lua SetColor and the
/// inspector's color edit recolor one object without touching shared materials.
///
/// Two internal children were added for build-mode interaction. Both are marked with
/// <see cref="InternalChildMeta"/> so the hierarchy never shows them as rows and so callers can
/// tell them apart from real objects:
///   * "Outline"   — an inverted-hull MeshInstance3D used for the selection / drag highlight.
///   * "Collision" — a StaticBody3D whose CollisionShape3D is what picking raycasts hit and what
///                   direct dragging collides against.
/// </summary>
public partial class LotObject : Node3D
{
	/// <summary>
	/// Meta key marking children that are implementation details of a lot object (mesh, outline,
	/// collision body, drag mover). The hierarchy skips any node carrying it.
	/// </summary>
	public const string InternalChildMeta = "openlot_internal";

	// Selection blue matches LotUIElement's outline so 3D and UI selections read the same.
	private static readonly Color OutlineSelectedColor = new Color(0.18f, 0.52f, 0.89f, 1f);
	private static readonly Color OutlineDraggedColor = new Color(0.35f, 0.72f, 1.0f, 1f);

	// Shared across every object: the outline colour depends on the state, not the object, so one
	// material per state is enough and no per-object material is created.
	private static StandardMaterial3D _outlineSelectedMaterial;
	private static StandardMaterial3D _outlineDraggedMaterial;

	public LotObjectKind Kind { get; private set; }
	public MeshInstance3D MeshInstance { get; private set; }
	public StandardMaterial3D Material { get; private set; }
	public CollisionShape3D CollisionShape { get; private set; }
	public StaticBody3D CollisionBody { get; private set; }
	public LotOutlineState OutlineState { get; private set; } = LotOutlineState.None;

	private MeshInstance3D _outline;

	public static LotObject Create(LotObjectKind kind, string name, Color color)
	{
		LotObject obj = new LotObject();
		obj.Name = name;
		obj.Kind = kind;

		Mesh meshResource;
		Shape3D collisionResource;
		switch (kind)
		{
			case LotObjectKind.Cube:
			{
				BoxMesh box = new BoxMesh();
				box.Size = new Vector3(1f, 1f, 1f);
				meshResource = box;

				BoxShape3D boxShape = new BoxShape3D();
				boxShape.Size = new Vector3(1f, 1f, 1f);
				collisionResource = boxShape;
				break;
			}
			case LotObjectKind.Sphere:
			{
				SphereMesh sphere = new SphereMesh();
				sphere.Radius = 0.5f;
				sphere.Height = 1f;
				meshResource = sphere;

				SphereShape3D sphereShape = new SphereShape3D();
				sphereShape.Radius = 0.5f;
				collisionResource = sphereShape;
				break;
			}
			case LotObjectKind.Capsule:
			{
				// CapsuleMesh.Height is the TOTAL height (cylinder section + both caps) in Godot 4,
				// matching CapsuleShape3D.Height — so Radius 0.5 + Height 2.0 makes the visual mesh
				// and its collision shape coincide. Total height 2.0 also puts the body's origin at
				// its centre, which is where a character spawn point belongs (spawn at y = 1 to
				// stand on the ground plane).
				CapsuleMesh capsule = new CapsuleMesh();
				capsule.Radius = 0.5f;
				capsule.Height = 2.0f;
				meshResource = capsule;

				CapsuleShape3D capsuleShape = new CapsuleShape3D();
				capsuleShape.Radius = 0.5f;
				capsuleShape.Height = 2.0f;
				collisionResource = capsuleShape;
				break;
			}
			case LotObjectKind.Cylinder:
			{
				CylinderMesh cylinder = new CylinderMesh();
				cylinder.TopRadius = 0.5f;
				cylinder.BottomRadius = 0.5f;
				cylinder.Height = 1f;
				meshResource = cylinder;

				CylinderShape3D cylinderShape = new CylinderShape3D();
				cylinderShape.Radius = 0.5f;
				cylinderShape.Height = 1f;
				collisionResource = cylinderShape;
				break;
			}
			default:
			{
				PlaneMesh plane = new PlaneMesh();
				plane.Size = new Vector2(20f, 20f);
				meshResource = plane;

				// A flat mesh needs a thin box to be hittable. A WorldBoundaryShape3D would be
				// unbounded and would swallow everything below the ground plane.
				BoxShape3D planeShape = new BoxShape3D();
				planeShape.Size = new Vector3(20f, 0.05f, 20f);
				collisionResource = planeShape;
				break;
			}
		}

		MeshInstance3D mesh = new MeshInstance3D();
		mesh.Name = "Mesh";
		mesh.Mesh = meshResource;
		mesh.SetMeta(InternalChildMeta, true);

		StandardMaterial3D material = new StandardMaterial3D();
		material.AlbedoColor = color;
		mesh.MaterialOverride = material;

		obj.MeshInstance = mesh;
		obj.Material = material;
		obj.AddChild(mesh);

		obj.AddOutlineChild(meshResource);
		obj.AddCollisionChild(collisionResource);
		return obj;
	}

	/// <summary>
	/// Inverted-hull outline: a slightly grown copy of the mesh with the front faces culled and no
	/// lighting, so only the ring that pokes past the silhouette shows. Depth-tested, which means
	/// it is correctly hidden behind other parts rather than drawing through them.
	/// </summary>
	private void AddOutlineChild(Mesh meshResource)
	{
		MeshInstance3D outline = new MeshInstance3D();
		outline.Name = "Outline";
		outline.Mesh = meshResource;
		outline.MaterialOverride = SelectedOutlineMaterial;
		outline.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
		outline.Visible = false;
		outline.SetMeta(InternalChildMeta, true);

		_outline = outline;
		AddChild(outline);
	}

	private void AddCollisionChild(Shape3D shapeResource)
	{
		StaticBody3D body = new StaticBody3D();
		body.Name = "Collision";
		body.CollisionLayer = 1;
		// Parts are never pushed by each other; a direct drag resolves its own contact, so a
		// zero mask keeps ordinary parts out of the physics pipeline.
		body.CollisionMask = 0;
		body.SetMeta(InternalChildMeta, true);

		CollisionShape3D shape = new CollisionShape3D();
		shape.Name = "Shape";
		shape.Shape = shapeResource;
		shape.SetMeta(InternalChildMeta, true);

		body.AddChild(shape);
		CollisionBody = body;
		CollisionShape = shape;
		AddChild(body);
	}

	public Color Color
	{
		get { return Material.AlbedoColor; }
		set { Material.AlbedoColor = value; }
	}

	/// <summary>
	/// Sets the selection highlight. Cheap and idempotent, so callers can sync it freely without
	/// worrying about redundant work.
	/// </summary>
	public void SetOutline(LotOutlineState state)
	{
		if (OutlineState == state)
		{
			return;
		}

		OutlineState = state;
		if (state == LotOutlineState.None)
		{
			_outline.Visible = false;
			return;
		}

		_outline.MaterialOverride = state == LotOutlineState.Dragged ? DraggedOutlineMaterial : SelectedOutlineMaterial;
		_outline.Visible = true;
	}

	private static StandardMaterial3D SelectedOutlineMaterial
	{
		get
		{
			if (_outlineSelectedMaterial == null)
			{
				_outlineSelectedMaterial = MakeOutlineMaterial(OutlineSelectedColor);
			}
			return _outlineSelectedMaterial;
		}
	}

	private static StandardMaterial3D DraggedOutlineMaterial
	{
		get
		{
			if (_outlineDraggedMaterial == null)
			{
				_outlineDraggedMaterial = MakeOutlineMaterial(OutlineDraggedColor);
			}
			return _outlineDraggedMaterial;
		}
	}

	private static StandardMaterial3D MakeOutlineMaterial(Color color)
	{
		StandardMaterial3D material = new StandardMaterial3D();
		material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
		// Culling the front faces leaves only the back of the grown shell visible, which is what
		// produces the ring around the silhouette while the object itself covers the rest.
		material.CullMode = BaseMaterial3D.CullModeEnum.Front;
		material.Grow = true;
		material.GrowAmount = 0.02f;
		material.AlbedoColor = color;
		material.DisableReceiveShadows = true;
		return material;
	}
}