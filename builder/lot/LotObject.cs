using System;
using Godot;

public enum LotObjectKind
{
	Cube,
	Sphere,
	Cylinder,
	Plane,
	/// <summary>Upright capsule — the default player character for Test/Game mode (§3.4).</summary>
	Capsule,
	/// <summary>
	/// An image patch on one face of its host part (milestone 2.6). Deliberately NOT a primitive:
	/// a decal has no collision body and no outline hull, its mesh is a unit quad, and its own
	/// transform is derived from face/offset/scale against the host (see <see cref="DecalMath"/>).
	/// It is still a full lot object — handle, hierarchy row, `lot.json` record, session snapshot —
	/// because it is parented to the part it decorates and persisted with it.
	/// </summary>
	Decal,
	/// <summary>
	/// A right-angle ramp (the size-migration step): size.X wide, size.Y tall at the back, size.Z
	/// deep, vertical wall at -Z, slope descending toward +Z. Mesh from
	/// <see cref="PartMeshFactory.CreateWedge"/>, collision from a convex hull of its six corners
	/// (Godot has no prism shape). A sized part like Cube/Sphere/Cylinder.
	/// </summary>
	Wedge,
	/// <summary>
	/// A gameplay query region (milestone 3.13): no visible part geometry and no gameplay
	/// collision — just a build-mode preview and an editor pick proxy. While a session runs it
	/// samples the shared <see cref="SpatialQuery"/> once per frame and reports `entered` /
	/// `exited` through the event registry. Sphere (QueryRadius) or box (the authored Size).
	/// </summary>
	QueryVolume
}

/// <summary>Selection highlight state for a 3D lot object. Mirrors the UI elements' selection tint.</summary>
public enum LotOutlineState
{
	None,
	Selected,
	Dragged
}

/// <summary>
/// What a decal draws on its face (milestone 2.6's on-face UI step). Image is a picture patch;
/// Text, Button and Scrollbar are UI content the panel hosts. Button and Scrollbar render and are
/// configurable today; their *input* routing is the shared §3.7/§5.2/§5.3 blocker, so they are
/// visuals until that lands.
/// </summary>
public enum DecalContent
{
	Image,
	Text,
	Button,
	Scrollbar
}

/// <summary>The shape a query volume samples (milestone 3.13).</summary>
public enum QueryVolumeShape
{
	/// <summary>A sphere of the volume's QueryRadius.</summary>
	Sphere,
	/// <summary>A box of the volume's authored Size (rotated with it).</summary>
	Box
}

/// <summary>
/// A 3D lot object (port of Unity's CreatePrimitive results). The node IS the object transform;
/// its MeshInstance3D child carries a per-object StandardMaterial3D so Lua SetColor and the
/// inspector's color edit recolor one object without touching shared materials.
///
/// One internal child was added for build-mode interaction, marked with
/// <see cref="InternalChildMeta"/> so the hierarchy never shows it as a row and so callers can
/// tell it apart from real objects:
///   * "Collision" — a StaticBody3D whose CollisionShape3D is what picking raycasts hit and what
///                   direct dragging collides against.
///
/// The selection highlight is NOT a scene object: it is a screen-space (2D) outline drawn by
/// <see cref="SelectionOutline"/> from the <see cref="OutlineState"/> this node carries — see that
/// class for why the earlier world-space inverted hull was replaced.
///
/// A query volume (milestone 3.13) swaps the collision child for its own pair: a "QueryVisual"
/// preview (build mode only) and a "QueryPick" proxy pinned to the no-collide layer, so it is
/// selectable in the editor and invisible to every gameplay mask.
/// </summary>
public partial class LotObject : Node3D
{
	/// <summary>
	/// Meta key marking children that are implementation details of a lot object (mesh, collision
	/// body, drag mover). The hierarchy skips any node carrying it.
	/// </summary>
	public const string InternalChildMeta = "openlot_internal";

	/// <summary>
	/// Meta key carrying the owning entity's handle on a physics body (milestone 3.7). Set on a
	/// simulated body, which lives at the lot root away from its part, so the event layer can
	/// resolve a touched body back to its entity; a static body is a child of its part and is
	/// resolved by walking up to the nearest node with <see cref="BuilderScene.HandleMeta"/>.
	/// </summary>
	public const string SimulatedOwnerMeta = "openlot_body_owner";

	/// <summary>
	/// Layer of the default collision group (milestone 3.5). The single source of truth is
	/// <see cref="LotCollisionGroups"/>; this aliases its Default bit, so a part that never chose a
	/// group sits on exactly the layer it always did. A part's real layer is its group's bit — or
	/// <see cref="NoCollideLayer"/> while <see cref="CanCollide"/> is false.
	/// </summary>
	public const uint PartLayer = LotCollisionGroups.DefaultBit;

	/// <summary>
	/// Layer a part moves to while <see cref="CanCollide"/> is false. Its shape stays live and
	/// pickable; only the gameplay masks stop seeing it, so nothing solid passes through it.
	/// </summary>
	public const uint NoCollideLayer = LotCollisionGroups.NoCollideBit;

	/// <summary>What the editor's picking raycasts test against: every part in every group, whether
	/// or not it is collidable.</summary>
	public const uint EditorPickMask = LotCollisionGroups.PickMask;

	/// <summary>Godot's own default friction — declaring the override must not change an existing
	/// part, so a part whose values are both defaults carries no material resource at all.</summary>
	public const float DefaultFriction = 1f;

	/// <summary>Godot's own default bounce (none). See <see cref="DefaultFriction"/>.</summary>
	public const float DefaultBounce = 0f;

	/// <summary>Smallest explicit mass <see cref="SetMass"/> accepts; 0 always means "auto"
	/// (<see cref="MassForBody"/>), and a smaller positive value would be a physics-stability
	/// hazard rather than a meaningful weight.</summary>
	public const float MinExplicitMass = 0.01f;

	// The selection highlight's colours live with the screen-space outline that draws them
	// (SelectionOutline), because the highlight is no longer a per-object material.

	public LotObjectKind Kind { get; private set; }
	public MeshInstance3D MeshInstance { get; private set; }
	public StandardMaterial3D Material { get; private set; }
	public CollisionShape3D CollisionShape { get; private set; }

	/// <summary>
	/// The part's collision body: a <c>StaticBody3D</c> in build mode, or a <c>RigidBody3D</c> while
	/// the part simulates inside a player session (§3.6's dynamic-body layer — see
	/// <see cref="SetSimulated"/>). Typed as the shared <c>PhysicsBody3D</c> base so every existing
	/// reader (picking, dragging, joints, the collision-group refresh) works with either without
	/// caring which one is live.
	/// </summary>
	public PhysicsBody3D CollisionBody { get; private set; }
	public LotOutlineState OutlineState { get; private set; } = LotOutlineState.None;

	/// <summary>True while the part carries a <c>RigidBody3D</c> instead of its static body.</summary>
	public bool IsSimulated { get { return _simulated; } }

	/// <summary>
	/// Anchor state (milestone 2.3). <c>true</c> (the default) means the part stays in place:
	/// gravity never moves it. <c>false</c> means the part is affected by gravity once it runs in
	/// a player session.
	///
	/// Where it applies today: in Build mode both states stay put, so the part remains editable —
	/// that is the editor's whole job. In Test/Game mode an unanchored part will fall as soon as
	/// the dynamic-body layer exists (§3.6); until then the flag is stored, round-trips and is
	/// reported, but nothing is simulated (no fake physics is invented here).
	///
	/// This is a physics property, not a write lock: scripts may still position an anchored part
	/// (it simply will not fall afterwards), which keeps existing creator code working.
	/// </summary>
	public bool Anchored { get { return _anchored; } }

	/// <summary>
	/// Collision state (milestone 2.3). <c>false</c> moves the part's collision body onto
	/// <see cref="NoCollideLayer"/> instead of disabling its shape, and that choice is deliberate:
	///   * A disabled shape disappears from raycasts, which would make a non-collidable part
	///     unselectable and undraggable in the editor — the milestone requires the opposite.
	///   * Layer filtering costs nothing per frame and keeps the shape resource intact, so toggling
	///     back is lossless and needs no rebuild.
	/// Gameplay masks (the player character, the drag mover, later dynamic bodies) test the part's
	/// group layer, so they pass straight through a non-collidable part regardless of its group.
	/// </summary>
	public bool CanCollide { get { return _canCollide; } }

	/// <summary>
	/// The part's named collision group (milestone 3.5), one of <see cref="LotCollisionGroups.Names"/>.
	/// Its Godot collision layer is this group's bit; its mask is the group's row of the interaction
	/// matrix. "Default" until a creator or script changes it, so an existing part is unchanged.
	/// </summary>
	public string CollisionGroup { get { return _collisionGroup; } }

	/// <summary>Surface friction of the part's collision body (milestone 3.11, default 1). See
	/// <see cref="SetFriction"/>.</summary>
	public float Friction { get { return _friction; } }

	/// <summary>Bounciness of the part's collision body (milestone 3.11, clamped 0..1, default 0).
	/// See <see cref="SetBounce"/>.</summary>
	public float Bounce { get { return _bounce; } }

	/// <summary>Explicit mass, or 0 for "derive it from the mesh's volume" — see
	/// <see cref="MassForBody"/>.</summary>
	public float Mass { get { return _mass; } }

	/// <summary>
	/// How solid the part looks (milestone 2.9): 0 = fully opaque (the default), 1 = invisible.
	/// An invisible part stays selectable and draggable — picking uses the collision shape, not
	/// the renderer — and its collision is unchanged; <see cref="CanCollide"/> is what lets things
	/// pass through it. See <see cref="SetTransparency"/>.
	/// </summary>
	public float Transparency { get { return _transparency; } }

	/// <summary>
	/// How much the part glows (milestone 2.9): 0 = a normal lit part; above 0 its material emits
	/// its own colour (a lamp keeps its tint) and the part competes for a slot in the lot's bounded
	/// cast-light pool (<see cref="LotGlowLights"/>). See <see cref="SetLuminescence"/>.
	/// </summary>
	public float Luminescence { get { return _luminescence; } }

	/// <summary>
	/// Opaque asset id of the part's texture (<see cref="LotTextureCache"/>), or "" for none.
	/// Kept as a string so it can be persisted in the §4.1 lot format and handed to Lua without
	/// exposing a Godot resource.
	/// </summary>
	public string TextureId { get { return _textureId; } }

	private bool _anchored = true;
	private bool _canCollide = true;
	private string _collisionGroup = LotCollisionGroups.DefaultGroup;
	private string _textureId = "";
	private bool _simulated;

	// Authored size and texture mapping (the mesh-regeneration migration). _size is the part's
	// extent in world units for the sized kinds; the UV fields drive the mesh's baked UVs and are
	// carried through the property registry (per-part tiling) rather than hidden here.
	private Vector3 _size = Vector3.One;
	private float _textureTilesPerUnit = 1f;
	private MeshUvMode _textureUvMode = MeshUvMode.WorldUnits;

	// Physics material state (milestone 3.11): friction/bounce carried by the part's collision body,
	// and an explicit mass (0 = derive from the mesh's volume, §3.6's rule). Defaults are Godot's own
	// (friction 1, bounce 0), so an untouched part is unchanged — and no material resource is created
	// until a value actually differs (ApplyPhysicsMaterial).
	private float _friction = DefaultFriction;
	private float _bounce = DefaultBounce;
	private float _mass;
	private PhysicsMaterial _physicsMaterial;

	// Surface material state (milestone 2.9): transparency (0 = opaque .. 1 = invisible) and
	// luminescence (0 = off). Both are lot state and ride the property registry; the material's
	// transparency mode, albedo alpha and emission are derived from them through ONE helper
	// (RefreshSurfaceMaterial), so the three writers that can reach the material (transparency,
	// luminescence, colour) cannot disagree. Defaults (0, 0) leave the material exactly as it was
	// before this milestone — opaque and unlit — which the self-test pins.
	private float _transparency = LotMaterialMath.DefaultTransparency;
	private float _luminescence = LotMaterialMath.DefaultLuminescence;

	// Decal state (milestone 2.6). Face, offset and scale are the authored values; the node's own
	// Transform is derived from them against the host (RefreshDecalTransform). The image reuses
	// _textureId and SetTexture/ReleaseTexture deliberately: one refcount rule, one cleanup path
	// and one archive route for "this object holds an image", whether it is a part or a patch.
	private int _decalFace = DecalMath.DefaultFace;
	private float _decalOffsetU;
	private float _decalOffsetV;
	private float _decalScaleU = 0.5f;
	private float _decalScaleV = 0.5f;

	// On-face UI content (the decal's second job after the image patch): what the panel draws, the
	// text a Text/Button panel shows, the scrollbar position, and the label font size. The two
	// internal content nodes are created with the decal and shown/hidden by RefreshDecalContent.
	private DecalContent _decalContent = DecalContent.Image;
	private string _decalText = "";
	private float _decalScroll;
	private float _decalFontSize = 48f;
	private Label3D _decalLabel;
	private MeshInstance3D _decalThumb;

	// Query-volume state (milestone 3.13): the shape it samples and a sphere's radius (a box uses
	// the authored Size). The preview mesh is an internal child, like the decal's content nodes.
	private QueryVolumeShape _queryShape = QueryVolumeShape.Sphere;
	private float _queryRadius = 2f;
	private MeshInstance3D _queryVisual;

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
				// Mesh regeneration migration: sized kinds build their mesh at the authored Size
				// through PartMeshFactory (world-unit UVs), starting at the unit extents — the
				// exact geometry the BoxMesh/SphereMesh/CylinderMesh primitives gave a part at
				// size 1. Resizing goes through SetSize, never node scale.
				meshResource = PartMeshFactory.Create(PartShape.Block, Vector3.One);

				BoxShape3D boxShape = new BoxShape3D();
				boxShape.Size = Vector3.One;
				collisionResource = boxShape;
				break;
			}
			case LotObjectKind.Sphere:
			{
				// The round shapes keep the unit shape resource they always shipped with; SetSize
				// carries the size as the collider child's scale (see ResizeCollision).
				meshResource = PartMeshFactory.Create(PartShape.Sphere, Vector3.One);

				SphereShape3D sphereShape = new SphereShape3D();
				sphereShape.Radius = 0.5f;
				collisionResource = sphereShape;
				break;
			}
			case LotObjectKind.Wedge:
			{
				meshResource = PartMeshFactory.Create(PartShape.Wedge, Vector3.One);

				// No Godot prism shape exists, so the hull is a convex polygon built from the
				// wedge's own corners — exact, and rebuilt by SetSize.
				ConvexPolygonShape3D wedgeShape = new ConvexPolygonShape3D();
				wedgeShape.Points = WedgeHullPoints(Vector3.One);
				collisionResource = wedgeShape;
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
				meshResource = PartMeshFactory.Create(PartShape.Cylinder, Vector3.One);

				CylinderShape3D cylinderShape = new CylinderShape3D();
				cylinderShape.Radius = 0.5f;
				cylinderShape.Height = 1f;
				collisionResource = cylinderShape;
				break;
			}
			case LotObjectKind.Decal:
			{
				// A unit quad facing +Z; face, offset and scale live in the decal's own state and
				// arrive as a derived local transform (RefreshDecalTransform), never as mesh edits.
				// No collision resource: a decal is decoration — picking raycasts pass through it to
				// the host part, which is also what keeps a stray quad out of the gameplay physics
				// space entirely.
				QuadMesh quad = new QuadMesh();
				quad.Size = new Vector2(1f, 1f);
				meshResource = quad;
				collisionResource = null;
				break;
			}
			case LotObjectKind.QueryVolume:
			{
				// No part geometry in the world: the visible face is the translucent preview the
				// early branch below adds, and the pick proxy is the editor's only collider. The
				// placeholder box mesh keeps the generic MeshInstance/material paths valid.
				BoxMesh placeholder = new BoxMesh();
				placeholder.Size = Vector3.One;
				meshResource = placeholder;
				collisionResource = null;
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

		if (kind == LotObjectKind.Decal)
		{
			// The image patch's material (milestone 2.6): the PNG's alpha channel comes through
			// untouched, unshaded so the image keeps its own colours regardless of lighting, and
			// front-face culling so it is never seen from behind its host. The initial transform is
			// set once the decal is attached to a host (_EnterTree -> RefreshDecalTransform).
			material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
			material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
			material.CullMode = BaseMaterial3D.CullModeEnum.Back;
			material.DisableReceiveShadows = true;
			mesh.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;

			// The on-face UI content nodes (label + scrollbar thumb) are created here and owned by
			// the decal for its whole life; RefreshDecalContent decides which are visible. They are
			// internal children like the mesh, so the hierarchy, the walk and the snapshots never see
			// them as separate objects.
			obj.AddDecalContentChildren();
			obj.RefreshDecalContent();
			return obj;
		}

		if (kind == LotObjectKind.QueryVolume)
		{
			// The generic mesh/material exist but stay hidden; the volume's build-mode preview and
			// its editor pick proxy are its real children, and it carries no selection silhouette
			// — a volume is a region, so an outline of its bounds would misrepresent its shape.
			mesh.Visible = false;
			obj.AddQueryVolumeChildren();
			obj.RefreshQueryVolumeVisual();
			return obj;
		}

		obj.AddCollisionChild(collisionResource);
		return obj;
	}

	private void AddCollisionChild(Shape3D shapeResource)
	{
		StaticBody3D body = new StaticBody3D();
		body.Name = "Collision";
		// The part's group decides both: its bit as the layer, and its row of the interaction matrix
		// as the mask. A default part is layer 1 / mask 0xFF — the mask was 0 before milestone 3.5,
		// which is equivalent today (parts are static and never pushed) and is what makes the group
		// matrix real once dynamic bodies exist. CanCollide is applied through RefreshCollision.
		body.CollisionLayer = LotCollisionGroups.BitForName(_collisionGroup);
		body.CollisionMask = LotCollisionGroups.MaskForName(_collisionGroup);
		// Milestone 3.11: friction/bounce ride on the body (nothing changes while both values are
		// Godot's defaults — ApplyPhysicsMaterial only allocates a material once one differs).
		ApplyPhysicsMaterial(body);
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
		set
		{
			// A colour write is RGB-only: the alpha channel belongs to the object's own "how
			// solid" property (a decal's Opacity, a part's Transparency — see RefreshSurfaceMaterial
			// and the decal's own branch), so tinting or recolouring can never silently reset how
			// solid the object is. (Before milestone 2.9 a part's colour write carried an alpha that
			// the opaque render pass ignored anyway; nothing visible changes.)
			Material.AlbedoColor = new Color(value.R, value.G, value.B, Material.AlbedoColor.A);
			// On a panel the colour also drives the label and the scrollbar thumb; on a part it
			// keeps the derived material state (alpha, emission tint) in agreement.
			if (Kind == LotObjectKind.Decal) RefreshDecalContent();
			else RefreshSurfaceMaterial();
		}
	}

	// --- surface material (milestone 2.9): transparency + luminescence -------------------------

	/// <summary>
	/// Sets how solid the part looks, clamped into [0, 1] — 0 fully opaque (the default), 1
	/// invisible. Callable on a decal for signature safety but does nothing: a panel's "how solid"
	/// knob is its own Opacity property, and its material must stay the unshaded alpha pass. See
	/// <see cref="Transparency"/> for what invisibility does and does not change.
	/// </summary>
	public void SetTransparency(float value)
	{
		if (Kind == LotObjectKind.Decal) return;
		float clamped = LotMaterialMath.ClampTransparency(value);
		if (clamped == _transparency) return;
		_transparency = clamped;
		RefreshSurfaceMaterial();
	}

	/// <summary>
	/// Sets how much the part glows, clamped at 0. There is deliberately no upper bound — a very
	/// bright lamp is the creator's call, and the value scales emission linearly. Callable on a
	/// decal for signature safety but does nothing: a panel is imagery, not a light source. See
	/// <see cref="Luminescence"/>.
	/// </summary>
	public void SetLuminescence(float value)
	{
		if (Kind == LotObjectKind.Decal) return;
		float clamped = LotMaterialMath.ClampLuminescence(value);
		if (clamped == _luminescence) return;
		_luminescence = clamped;
		RefreshSurfaceMaterial();
	}

	/// <summary>
	/// The single writer of the part's surface material state, derived from <see cref="_transparency"/>
	/// and <see cref="_luminescence"/>:
	///   * transparency 0 keeps Godot's OPAQUE path (`Transparency = Disabled`, alpha 1), so a part
	///     that never touched either property renders exactly as it did before milestone 2.9;
	///   * above 0 switches to the alpha pass and maps the value to the albedo alpha (1 − transparency);
	///   * luminescence above 0 enables emission in the part's own colour (a lamp keeps its tint).
	/// The cast light itself is the budgeted pool in <see cref="LotGlowLights"/>, never a node per
	/// part — see docs/material-api.md for the budget and why.
	/// </summary>
	private void RefreshSurfaceMaterial()
	{
		if (Material == null) return;
		Color current = Material.AlbedoColor;
		Material.Transparency = _transparency > 0f
			? BaseMaterial3D.TransparencyEnum.Alpha
			: BaseMaterial3D.TransparencyEnum.Disabled;
		Material.AlbedoColor = new Color(current.R, current.G, current.B,
			LotMaterialMath.AlphaForTransparency(_transparency));

		bool glowing = _luminescence > 0f;
		Material.EmissionEnabled = glowing;
		if (!glowing) return;
		Material.Emission = new Color(current.R, current.G, current.B);
		Material.EmissionEnergyMultiplier = LotMaterialMath.EmissionEnergyFor(_luminescence);
	}

	/// <summary>Sets the anchor state. See <see cref="Anchored"/> for exactly what it means.</summary>
	public void SetAnchored(bool value)
	{
		_anchored = value;
	}

	/// <summary>
	/// Sets the collision state. <see cref="CanCollide"/> false always wins: the body parks on
	/// <see cref="NoCollideLayer"/> whatever its group, and toggling back restores the group's bit.
	/// The shape is never disabled or rebuilt, so editor picking keeps working. See
	/// <see cref="CanCollide"/> for the reasoning.
	/// </summary>
	public void SetCanCollide(bool value)
	{
		_canCollide = value;
		RefreshCollision();
	}

	/// <summary>
	/// Assigns the part's named collision group (milestone 3.5). An unknown name falls back to
	/// <see cref="LotCollisionGroups.DefaultGroup"/> rather than leaving the part on no layer. Only
	/// the group's layer/mask change; <see cref="CanCollide"/> still governs whether the body is a
	/// collidable target at all.
	/// </summary>
	public void SetCollisionGroup(string group)
	{
		_collisionGroup = LotCollisionGroups.Normalize(group);
		RefreshCollision();
	}

	/// <summary>
	/// Re-derives the collision body's layer and mask from the part's group and
	/// <see cref="CanCollide"/>. Idempotent and cheap, so it is called freely: the group setters use
	/// it, and <see cref="BuilderScene.RefreshCollision"/> walks every part with it after the shared
	/// interaction matrix changes.
	/// </summary>
	public void RefreshCollision()
	{
		if (CollisionBody == null || !GodotObject.IsInstanceValid(CollisionBody)) return;
		// A query volume's body is the editor's pick proxy, never a gameplay collider: whatever a
		// script sets, the region stays on the no-collide layer where only the pick mask sees it.
		CollisionBody.CollisionLayer = _canCollide && Kind != LotObjectKind.QueryVolume
			? LotCollisionGroups.BitForName(_collisionGroup)
			: NoCollideLayer;
		CollisionBody.CollisionMask = LotCollisionGroups.MaskForName(_collisionGroup);
	}

	// --- Authored size + texture mapping (mesh-regeneration migration) ------------------------

	/// <summary>True for the kinds whose geometry is generated at an authored <see cref="Size"/>
	/// instead of a fixed primitive mesh. Plane, capsule and decal keep their shapes; a decal's
	/// geometry is a derived unit quad.</summary>
	public bool HasSize
	{
		get
		{
			return Kind == LotObjectKind.Cube || Kind == LotObjectKind.Sphere
				|| Kind == LotObjectKind.Cylinder || Kind == LotObjectKind.Wedge
				|| Kind == LotObjectKind.QueryVolume;
		}
	}

	/// <summary>
	/// The part's authored extent, in world units, centred on its origin. This — not the node's
	/// scale — is what resizing writes: <see cref="SetSize"/> regenerates the mesh (and the
	/// collision hull) at the new extents, so the baked world-unit UVs keep a fixed texture
	/// density and the node scale stays 1. Node scale remains a plain Node3D transform (a legacy
	/// script SetScale still multiplies the mesh visually), but no OpenLot editor path writes it.
	/// </summary>
	public Vector3 Size { get { return _size; } }

	/// <summary>How many texture repeats one world unit of this part shows (1 = one repeat per
	/// unit, the default). Changing it regenerates the mesh's UVs.</summary>
	public float TextureTilesPerUnit { get { return _textureTilesPerUnit; } }

	/// <summary>True when the part's texture is stretched across each face (0..1, the pre-migration
	/// look) instead of repeating per world unit.</summary>
	public bool TextureStretch { get { return _textureUvMode == MeshUvMode.FaceStretch; } }

	/// <summary>
	/// Resizes the part by regenerating its mesh and collision hull at the new extents — the one
	/// resize path for the gizmo, the Inspector and scripts; never node scale. The request is
	/// clamped exactly like the factory clamps it (absolute values, its size floor and ceiling),
	/// so a mirrored or degenerate request degrades instead of producing NaN geometry. Returns
	/// false for a kind without an authored size (plane, capsule, decal), which is what lets the
	/// bound Lua verb report the refusal.
	/// </summary>
	public bool SetSize(Vector3 size)
	{
		if (!HasSize) return false;
		Vector3 clamped = PartMeshFactory.ClampSize(size);
		if (clamped.IsEqualApprox(_size)) return true;
		_size = clamped;
		RefreshGeneratedGeometry();
		return true;
	}

	/// <summary>Sets the texture tiling (repeats per world unit); see
	/// <see cref="TextureTilesPerUnit"/>. Returns false for a kind without an authored size.</summary>
	public bool SetTilesPerUnit(float tilesPerUnit)
	{
		if (!HasSize) return false;
		float clamped = PartMeshFactory.ClampTiles(tilesPerUnit);
		if (Mathf.IsEqualApprox(clamped, _textureTilesPerUnit)) return true;
		_textureTilesPerUnit = clamped;
		RefreshGeneratedGeometry();
		return true;
	}

	/// <summary>Chooses world-unit tiling (false, the default) or stretch-to-fit per face (true,
	/// the pre-migration look); see <see cref="TextureStretch"/>.</summary>
	public bool SetTextureStretch(bool stretch)
	{
		if (!HasSize) return false;
		MeshUvMode mode = stretch ? MeshUvMode.FaceStretch : MeshUvMode.WorldUnits;
		if (mode == _textureUvMode) return true;
		_textureUvMode = mode;
		RefreshGeneratedGeometry();
		return true;
	}

	/// <summary>The factory shape behind a sized kind (the default is the block, which is what
	/// every other caller of the factory would want for a generic box).</summary>
	public static PartShape ShapeFor(LotObjectKind kind)
	{
		switch (kind)
		{
			case LotObjectKind.Sphere: return PartShape.Sphere;
			case LotObjectKind.Cylinder: return PartShape.Cylinder;
			case LotObjectKind.Wedge: return PartShape.Wedge;
			default: return PartShape.Block;
		}
	}

	/// <summary>The six corners of a right-angle wedge's convex hull, matching the mesh
	/// <see cref="PartMeshFactory.CreateWedge"/> generates (bottom rectangle + the top edge over
	/// -Z). The wedge's collision shape is built from these — Godot has no prism shape.</summary>
	public static Vector3[] WedgeHullPoints(Vector3 size)
	{
		Vector3 h = size * 0.5f;
		return new[]
		{
			new Vector3(-h.X, -h.Y, -h.Z), new Vector3(h.X, -h.Y, -h.Z),
			new Vector3(h.X, -h.Y, h.Z), new Vector3(-h.X, -h.Y, h.Z),
			new Vector3(-h.X, h.Y, -h.Z), new Vector3(h.X, h.Y, -h.Z)
		};
	}

	/// <summary>
	/// Rebuilds everything derived from the authored size and UV mode in one place: the visual
	/// mesh (shared through the factory's cache), the collision shape, the event sensor, and every
	/// decal parented to this part (a patch's placement is derived from the host's mesh size).
	/// </summary>
	private void RefreshGeneratedGeometry()
	{
		// A query volume has no generated part geometry: its authored size drives its preview and
		// its pick proxy instead, and its box query samples exactly those extents.
		if (Kind == LotObjectKind.QueryVolume)
		{
			RefreshQueryVolumeVisual();
			return;
		}

		PartMeshFactory.UpdatePartMesh(MeshInstance, ShapeFor(Kind), _size, _textureTilesPerUnit, _textureUvMode);
		ResizeCollision();
		RefreshEventSensor();
		RefreshChildDecals();
	}

	/// <summary>
	/// Resizes the live collision hull to match <see cref="Size"/>, mutating the existing shape
	/// resource in place rather than recreating bodies. The box carries its size natively; the
	/// round shapes keep the unit resource they shipped with and carry the size as the collider
	/// child's scale, which reproduces the compound node-scale behavior every existing lot, test
	/// and physics result relies on; the wedge's convex hull is rebuilt from its corners.
	/// </summary>
	private void ResizeCollision()
	{
		if (CollisionBody == null || !GodotObject.IsInstanceValid(CollisionBody)) return;
		if (CollisionShape == null || !GodotObject.IsInstanceValid(CollisionShape)) return;

		switch (Kind)
		{
			case LotObjectKind.Cube:
				if (CollisionShape.Shape is BoxShape3D box) box.Size = _size;
				CollisionBody.Scale = Vector3.One;
				break;
			case LotObjectKind.Sphere:
			case LotObjectKind.Cylinder:
				CollisionBody.Scale = _size;
				break;
			case LotObjectKind.Wedge:
				if (CollisionShape.Shape is ConvexPolygonShape3D hull) hull.Points = WedgeHullPoints(_size);
				CollisionBody.Scale = Vector3.One;
				break;
		}
	}

	/// <summary>Re-pads the event sensor's shape after a resize and mirrors the collider's scale
	/// onto it, so "touched" keeps meaning "touching the part" (the sensor is a child of the
	/// part, while a round part's size-as-scale lives on the collider child).</summary>
	private void RefreshEventSensor()
	{
		if (!HasEventSensor) return;
		if (_eventSensor.GetChildCount() > 0 && _eventSensor.GetChild(0) is CollisionShape3D sensorShape)
		{
			sensorShape.Shape = MakeSensorShape(CollisionShape.Shape);
		}
		_eventSensor.Scale = CollisionBody.Scale;
	}

	/// <summary>Re-derives every decal parented to this part (its placement depends on the host's
	/// mesh size, so a resize must move the patch with the face).</summary>
	private void RefreshChildDecals()
	{
		for (int i = 0; i < GetChildCount(); i++)
		{
			LotObject child = GetChild(i) as LotObject;
			if (child != null && child.Kind == LotObjectKind.Decal) child.RefreshDecalTransform();
		}
	}

	// --- Query volume (milestone 3.13) ----------------------------------------------------------

	/// <summary>The two shapes a query volume can sample, as the Shape property spells them.</summary>
	public static readonly string[] QueryShapeNames = { "Sphere", "Box" };

	/// <summary>Maps a Shape property value back to the enum; an unknown string degrades to
	/// Sphere, the default, rather than throwing.</summary>
	public static QueryVolumeShape QueryShapeFromName(string name)
	{
		for (int i = 0; i < QueryShapeNames.Length; i++)
		{
			if (QueryShapeNames[i] == name) return (QueryVolumeShape)i;
		}
		return QueryVolumeShape.Sphere;
	}

	private void AddQueryVolumeChildren()
	{
		MeshInstance3D visual = new MeshInstance3D();
		visual.Name = "QueryVisual";
		visual.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
		StandardMaterial3D material = new StandardMaterial3D();
		// A ghost: translucent, unshaded and double-sided, so the region reads as a region rather
		// than a part. (Godot's primitives have no wireframe material mode; a ghost is the honest
		// equivalent and needs no custom shader — Tier 1/2 code stays out of the render internals.)
		material.AlbedoColor = new Color(0.25f, 0.75f, 1f, 0.22f);
		material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
		material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
		material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
		material.DisableReceiveShadows = true;
		visual.MaterialOverride = material;
		visual.SetMeta(InternalChildMeta, true);
		_queryVisual = visual;
		AddChild(visual);

		// The pick proxy: the editor's raycasts use the pick mask, which includes the no-collide
		// layer, so clicking the region selects it and a drag moves it — while every gameplay mask
		// (and the query's own self-hit filter) keeps it out of play. RefreshCollision pins the
		// layer here whatever CanCollide says, so a script cannot turn a region solid.
		StaticBody3D pick = new StaticBody3D();
		pick.Name = "QueryPick";
		pick.CollisionLayer = NoCollideLayer;
		pick.CollisionMask = 0;
		pick.SetMeta(InternalChildMeta, true);
		CollisionShape3D pickShape = new CollisionShape3D();
		pickShape.Name = "Shape";
		pickShape.SetMeta(InternalChildMeta, true);
		pick.AddChild(pickShape);
		CollisionBody = pick;
		CollisionShape = pickShape;
		AddChild(pick);
	}

	/// <summary>
	/// Rebuilds the preview mesh and the pick proxy from the current shape/radius/size. Called on
	/// create, on every property write and from <see cref="RefreshGeneratedGeometry"/>, so the two
	/// visual halves of a volume can never disagree with what it samples.
	/// </summary>
	public void RefreshQueryVolumeVisual()
	{
		if (Kind != LotObjectKind.QueryVolume) return;
		bool sphere = _queryShape == QueryVolumeShape.Sphere;
		if (_queryVisual != null && GodotObject.IsInstanceValid(_queryVisual))
		{
			if (sphere)
			{
				SphereMesh mesh = new SphereMesh();
				mesh.Radius = _queryRadius;
				mesh.Height = _queryRadius * 2f;
				_queryVisual.Mesh = mesh;
			}
			else
			{
				BoxMesh mesh = new BoxMesh();
				mesh.Size = _size;
				_queryVisual.Mesh = mesh;
			}
		}
		if (CollisionBody == null || !GodotObject.IsInstanceValid(CollisionBody)) return;
		if (CollisionShape == null || !GodotObject.IsInstanceValid(CollisionShape)) return;
		if (sphere)
		{
			SphereShape3D shape = new SphereShape3D();
			shape.Radius = _queryRadius;
			CollisionShape.Shape = shape;
		}
		else
		{
			BoxShape3D shape = new BoxShape3D();
			shape.Size = _size;
			CollisionShape.Shape = shape;
		}
		CollisionBody.Scale = Vector3.One;
	}

	/// <summary>Shows or hides the build-mode preview. Sessions hide it (a volume is a gameplay
	/// region, not scenery); the pick proxy stays live so editor tooling around it keeps working.</summary>
	public void SetQueryVolumeVisualVisible(bool visible)
	{
		if (_queryVisual != null && GodotObject.IsInstanceValid(_queryVisual)) _queryVisual.Visible = visible;
	}

	/// <summary>True while the build-mode preview is drawn (the session boundary toggles it).</summary>
	public bool QueryVolumeVisualVisible
	{
		get { return _queryVisual != null && GodotObject.IsInstanceValid(_queryVisual) && _queryVisual.Visible; }
	}

	/// <summary>The shape the volume samples (sphere or box).</summary>
	public QueryVolumeShape QueryShape { get { return _queryShape; } }

	/// <summary>The sphere radius; a box volume samples its authored <see cref="Size"/> instead.</summary>
	public float QueryRadius { get { return _queryRadius; } }

	public void SetQueryShape(QueryVolumeShape shape)
	{
		if (_queryShape == shape) return;
		_queryShape = shape;
		RefreshQueryVolumeVisual();
	}

	/// <summary>The sphere radius, floored at 0.1 m so a degenerate request cannot produce a
	/// volume that samples nothing. A box's extents go through <see cref="SetSize"/> instead.</summary>
	public void SetQueryRadius(float radius)
	{
		float clamped = Mathf.Max(0.1f, radius);
		if (Mathf.IsEqualApprox(clamped, _queryRadius)) return;
		_queryRadius = clamped;
		RefreshQueryVolumeVisual();
	}

	// --- Physics material (milestone 3.11) ---

	/// <summary>
	/// Sets the surface friction (clamped at 0; higher = grippier). Applied to the live body
	/// immediately — build-mode and simulated alike — through the part's shared material. The
	/// default is Godot's own (1), so an untouched part behaves exactly as it did before this
	/// milestone.
	/// </summary>
	public void SetFriction(float value)
	{
		_friction = Mathf.Max(0f, value);
		RefreshPhysicsMaterial();
	}

	/// <summary>Sets the bounciness (clamped into 0..1). Reaches the body exactly like
	/// <see cref="SetFriction"/> does.</summary>
	public void SetBounce(float value)
	{
		_bounce = Mathf.Clamp(value, 0f, 1f);
		RefreshPhysicsMaterial();
	}

	/// <summary>
	/// Sets the explicit mass. 0 (or anything below 0) means "auto": the mass is derived from the
	/// mesh's volume — the rule §3.6's body swap has always used. A positive value overrides it,
	/// floored at <see cref="MinExplicitMass"/>, and a live simulated body picks the change up
	/// immediately.
	/// </summary>
	public void SetMass(float value)
	{
		_mass = value <= 0f ? 0f : Mathf.Max(MinExplicitMass, value);
		RigidBody3D rigid = CollisionBody as RigidBody3D;
		if (rigid != null && GodotObject.IsInstanceValid(rigid)) rigid.Mass = MassForBody();
	}

	/// <summary>The mass a simulated body should carry: the explicit <see cref="Mass"/> when set,
	/// otherwise the mesh-volume estimate (§3.6). One rule, used by the body swap and by the tests
	/// alike.</summary>
	public float MassForBody()
	{
		return _mass > 0f ? _mass : EstimateMass();
	}

	/// <summary>
	/// Applies the part's friction/bounce to a body. Both of the part's body forms (static and
	/// simulated) get the same small per-part material, so a body swap cannot lose the values and a
	/// live edit updates whichever body is current. While both values equal Godot's defaults the
	/// override is cleared instead — a part that never asked for custom material allocates nothing.
	/// </summary>
	public void ApplyPhysicsMaterial(PhysicsBody3D body)
	{
		if (body == null || !GodotObject.IsInstanceValid(body)) return;

		PhysicsMaterial material = null;
		if (_friction != DefaultFriction || _bounce != DefaultBounce)
		{
			if (_physicsMaterial == null) _physicsMaterial = new PhysicsMaterial();
			_physicsMaterial.Friction = _friction;
			_physicsMaterial.Bounce = _bounce;
			material = _physicsMaterial;
		}

		// GodotSharp declares the property on each CONCRETE body type (StaticBody3D / RigidBody3D),
		// not on their shared PhysicsBody3D base, so the assignment goes through exactly the two
		// forms this part ever has. Both accept it — an anchored part's floor carries its
		// friction/bounce as fully as a simulated part does, and the engine combines the two
		// materials of a contact.
		if (body is RigidBody3D rigid) rigid.PhysicsMaterialOverride = material;
		else if (body is StaticBody3D staticBody) staticBody.PhysicsMaterialOverride = material;
	}

	/// <summary>Updates the live body after a friction/bounce edit (a no-op on a part with no body,
	/// e.g. a decal).</summary>
	private void RefreshPhysicsMaterial()
	{
		ApplyPhysicsMaterial(CollisionBody);
	}

	// --- Decal state (milestone 2.6) ---

	/// <summary>Which face of the host the image sits on; indexes <see cref="DecalMath.FaceNames"/>
	/// ("+Z" .. "-Y"). See <see cref="SetDecalFace"/>.</summary>
	public int DecalFace { get { return _decalFace; } }

	/// <summary>Horizontal slide of the image across its face, as a fraction of the face width
	/// (-0.5 .. 0.5). See <see cref="SetDecalOffsetU"/>.</summary>
	public float DecalOffsetU { get { return _decalOffsetU; } }

	/// <summary>Vertical slide of the image across its face, as a fraction of the face height.</summary>
	public float DecalOffsetV { get { return _decalOffsetV; } }

	/// <summary>Image width as a fraction of the face width (0.05 .. 1).</summary>
	public float DecalScaleU { get { return _decalScaleU; } }

	/// <summary>Image height as a fraction of the face height (0.05 .. 1).</summary>
	public float DecalScaleV { get { return _decalScaleV; } }

	/// <summary>How solid the image is (0 invisible .. 1 opaque). Lives in the material's alpha,
	/// so the image's own transparent pixels stay transparent at any opacity.</summary>
	public float DecalOpacity { get { return Material.AlbedoColor.A; } }

	/// <summary>
	/// Moves the decal to another face of its host. An out-of-range index is refused rather than
	/// silently reset, so a value this build does not know (a face a newer OpenLot added) is left
	/// alone instead of being rewritten.
	/// </summary>
	public bool SetDecalFace(int face)
	{
		if (Kind != LotObjectKind.Decal) return false;
		if (face < 0 || face >= DecalMath.FaceNames.Length) return false;
		_decalFace = face;
		ClampDecalOffsets();
		RefreshDecalTransform();
		return true;
	}

	/// <summary>Slides the image horizontally. The value is clamped so the image stays on the face
	/// (see <see cref="DecalMath.ClampOffset"/>).</summary>
	public void SetDecalOffsetU(float value)
	{
		if (Kind != LotObjectKind.Decal) return;
		_decalOffsetU = value;
		ClampDecalOffsets();
		RefreshDecalTransform();
	}

	/// <summary>Slides the image vertically, clamped like <see cref="SetDecalOffsetU"/>.</summary>
	public void SetDecalOffsetV(float value)
	{
		if (Kind != LotObjectKind.Decal) return;
		_decalOffsetV = value;
		ClampDecalOffsets();
		RefreshDecalTransform();
	}

	/// <summary>Resizes the image horizontally (fraction of the face). The offset is re-clamped,
	/// because a larger image has less room to move.</summary>
	public void SetDecalScaleU(float value)
	{
		if (Kind != LotObjectKind.Decal) return;
		_decalScaleU = DecalMath.ClampScale(value);
		ClampDecalOffsets();
		RefreshDecalTransform();
	}

	/// <summary>Resizes the image vertically, clamped like <see cref="SetDecalScaleU"/>.</summary>
	public void SetDecalScaleV(float value)
	{
		if (Kind != LotObjectKind.Decal) return;
		_decalScaleV = DecalMath.ClampScale(value);
		ClampDecalOffsets();
		RefreshDecalTransform();
	}

	/// <summary>Sets how solid the image is. Only the alpha changes, so a tint set through
	/// <see cref="Color"/> survives.</summary>
	public void SetDecalOpacity(float value)
	{
		if (Kind != LotObjectKind.Decal) return;
		Color current = Material.AlbedoColor;
		Material.AlbedoColor = new Color(current.R, current.G, current.B, DecalMath.ClampOpacity(value));
		// The opacity lives on the material, so the label / thumb have to pick it up.
		RefreshDecalContent();
	}

	/// <summary>
	/// Recomputes the patch's local transform from the host part's mesh size and scale — the only
	/// writer of a decal's Transform, so face/offset/scale stay the single source of truth. A
	/// decal with no host (detached, or parented to something that is not a part) keeps a defined
	/// shape via the unit-size fallback rather than failing.
	/// </summary>
	public void RefreshDecalTransform()
	{
		if (Kind != LotObjectKind.Decal) return;
		LotObject host = GetParent() as LotObject;
		if (host != null && host.Kind == LotObjectKind.Decal) host = null;

		Vector3 size = Vector3.One;
		Vector3 hostScale = Vector3.One;
		if (host != null)
		{
			size = DecalMath.MeshSize(host.MeshInstance != null ? host.MeshInstance.Mesh : null);
			hostScale = host.Scale;
		}
		Transform = DecalMath.LocalTransform(size, hostScale, _decalFace, _decalOffsetU, _decalOffsetV, _decalScaleU, _decalScaleV);
	}

	/// <summary>
	/// A decal's transform is derived, and both its host link and the host's scale can change while
	/// it is out of the tree (first attach, undo re-attach, a reparent in the hierarchy, a restored
	/// lot). Recomputing on entry is what keeps the patch on its face through all of those.
	/// </summary>
	public override void _EnterTree()
	{
		if (Kind == LotObjectKind.Decal) RefreshDecalTransform();
	}

	private void ClampDecalOffsets()
	{
		Vector2 clamped = DecalMath.ClampOffset(_decalScaleU, _decalScaleV, _decalOffsetU, _decalOffsetV);
		_decalOffsetU = clamped.X;
		_decalOffsetV = clamped.Y;
	}

	// --- Decal on-face UI content (milestone 2.6, on-face UI step) ---

	/// <summary>What the panel draws: Image, Text, Button or Scrollbar.</summary>
	public DecalContent Content { get { return _decalContent; } }

	/// <summary>The text a Text or Button panel shows; unused by the other contents.</summary>
	public string DecalText { get { return _decalText; } }

	/// <summary>Scrollbar position, 0 (top) .. 1 (bottom); unused by the other contents.</summary>
	public float DecalScroll { get { return _decalScroll; } }

	/// <summary>Label font size (8 .. 160). One line is FontSize/160 of the panel's height, so the
	/// text scales with the panel and with the part it decorates.</summary>
	public float DecalFontSize { get { return _decalFontSize; } }

	/// <summary>Switches what the panel draws. An out-of-range index is refused rather than reset,
	/// so a content this build does not know is left alone instead of being rewritten.</summary>
	public bool SetDecalContent(int content)
	{
		if (Kind != LotObjectKind.Decal) return false;
		if (content < 0 || content >= DecalMath.ContentNames.Length) return false;
		_decalContent = (DecalContent)content;
		RefreshDecalContent();
		return true;
	}

	/// <summary>Sets the panel's text (Text and Button contents draw it; the others ignore it).</summary>
	public void SetDecalText(string text)
	{
		if (Kind != LotObjectKind.Decal) return;
		_decalText = text ?? "";
		RefreshDecalContent();
	}

	/// <summary>Sets the scrollbar position, clamped into [0, 1].</summary>
	public void SetDecalScroll(float value)
	{
		if (Kind != LotObjectKind.Decal) return;
		_decalScroll = DecalMath.ClampScroll(value);
		RefreshDecalContent();
	}

	/// <summary>Sets the label font size, clamped into [8, 160].</summary>
	public void SetDecalFontSize(float value)
	{
		if (Kind != LotObjectKind.Decal) return;
		_decalFontSize = DecalMath.ClampFontSize(value);
		RefreshDecalContent();
	}

	/// <summary>
	/// Creates the decal's two content nodes: a <see cref="Label3D"/> for text/button content and a
	/// unit quad for a scrollbar thumb. Label3D is a deliberate borrow from Godot's 3D layer — the
	/// in-house ImGui UI is screen-space and cannot put glyphs on a face — and it is an internal
	/// child of the decal like the mesh, so it never appears as its own object anywhere.
	///
	/// The label is configured in the panel's own unit frame: the decal's unit quad spans -0.5..0.5,
	/// so one local unit is the whole panel and FontSize/160 of it is one text line.
	/// </summary>
	private void AddDecalContentChildren()
	{
		Label3D label = new Label3D();
		label.Name = "Label";
		label.PixelSize = 1f / DecalMath.LabelPixelsPerPanelUnit;
		label.Width = DecalMath.LabelWrapPixels();
		label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		label.FontSize = (int)_decalFontSize;
		label.DoubleSided = false;
		label.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
		label.SetMeta(InternalChildMeta, true);
		label.Visible = false;
		_decalLabel = label;
		AddChild(label);

		MeshInstance3D thumb = new MeshInstance3D();
		thumb.Name = "Thumb";
		QuadMesh quad = new QuadMesh();
		quad.Size = new Vector2(1f, 1f);
		thumb.Mesh = quad;

		StandardMaterial3D thumbMaterial = new StandardMaterial3D();
		thumbMaterial.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
		thumbMaterial.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
		thumbMaterial.CullMode = BaseMaterial3D.CullModeEnum.Back;
		thumbMaterial.DisableReceiveShadows = true;
		thumb.MaterialOverride = thumbMaterial;

		thumb.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
		thumb.SetMeta(InternalChildMeta, true);
		thumb.Visible = false;
		_decalThumb = thumb;
		AddChild(thumb);
	}

	/// <summary>
	/// Applies the panel's content to its internal nodes: which of them is visible, whether the
	/// quad shows the image (only an Image panel does), the label's text/colour/size, and the
	/// scrollbar thumb's position. Idempotent and cheap, so every content-affecting setter calls it
	/// — and <see cref="SetTexture"/> too, because only an Image panel may show a texture.
	///
	/// The label and the thumb live in the decal's unit frame: the panel is one unit square there,
	/// so their numbers are fractions of the panel and the host's scale multiplies everything
	/// together — text and panel stay proportional under any part resize.
	/// </summary>
	public void RefreshDecalContent()
	{
		if (Kind != LotObjectKind.Decal || _decalLabel == null || _decalThumb == null) return;

		bool isImage = _decalContent == DecalContent.Image;
		bool isText = _decalContent == DecalContent.Text;
		bool isButton = _decalContent == DecalContent.Button;
		bool isScrollbar = _decalContent == DecalContent.Scrollbar;

		Color tint = Material.AlbedoColor;

		// The quad is the picture (Image), the button surface (Button) or the scroll track
		// (Scrollbar); a Text panel has no surface of its own.
		if (MeshInstance != null) MeshInstance.Visible = !isText;
		Material.AlbedoTexture = isImage && _textureId.Length > 0 ? LotTextureCache.Get(_textureId) : null;
		Material.AlbedoColor = tint;

		bool showLabel = isText || isButton;
		_decalLabel.Visible = showLabel;
		if (showLabel)
		{
			Color labelColor = isButton ? DecalMath.LabelColorFor(tint) : tint;
			_decalLabel.Text = _decalText;
			_decalLabel.FontSize = (int)_decalFontSize;
			_decalLabel.Modulate = new Color(labelColor.R, labelColor.G, labelColor.B, tint.A);
		}

		_decalThumb.Visible = isScrollbar;
		if (isScrollbar)
		{
			Material thumbMaterial = _decalThumb.MaterialOverride;
			if (thumbMaterial is StandardMaterial3D standardThumb)
			{
				Color thumbColor = DecalMath.ThumbColorFor(tint);
				standardThumb.AlbedoColor = new Color(thumbColor.R, thumbColor.G, thumbColor.B, tint.A);
			}
			Vector3 thumbScale = new Vector3(DecalMath.ScrollThumbWidth, DecalMath.ScrollThumbHeight, 1f);
			Vector3 thumbOrigin = new Vector3(DecalMath.ScrollThumbOffset(_decalScroll), 0f, 0.001f);
			_decalThumb.Transform = new Transform3D(Basis.FromScale(thumbScale), thumbOrigin);
		}
	}

	/// <summary>
	/// Swaps the part's collision body between static (build mode, and anchored parts in a session)
	/// and a simulated <c>RigidBody3D</c> (unanchored parts in a player session, §3.6).
	///
	/// A simulated body is parented to <paramref name="simulationHost"/> (the lot root), NOT to the
	/// part: physics owns the body's own transform, and a body nested inside the node that is meant
	/// to follow it would stack that motion on top of itself every step. The part then puppets the
	/// body once per physics step (<see cref="LotConstraintSession.SyncVisuals"/>) — the same shape
	/// the capsule controller uses, where the swept body and the visual are siblings. On the way
	/// back, the body is re-parented under the part with its world pose preserved, so build mode
	/// sees exactly the structure it always had.
	///
	/// The <see cref="CollisionShape"/> node travels between bodies unchanged, so its shape,
	/// layer/mask derivation and the editor pick ray all carry over; the new body re-runs
	/// <see cref="RefreshCollision"/> so the §3.5 group semantics hold in both forms.
	///
	/// Idempotent, and only ever called from the session build/teardown (entering or leaving Test/
	/// Game mode, or a script reload inside one) — never mid-step, so freeing the old body here is
	/// safe. Mid-session <c>SetAnchored</c> deliberately does not call this: a part's body form is
	/// fixed for a session and a reload applies the new flag, the same reload-on-entry rule §3.4 set.
	/// </summary>
	public void SetSimulated(bool simulated, Node simulationHost)
	{
		if (simulated == _simulated) return;
		if (CollisionShape == null || CollisionBody == null || !GodotObject.IsInstanceValid(CollisionBody)) return;

		PhysicsBody3D oldBody = CollisionBody;
		Transform3D bodyWorld = oldBody.GlobalTransform;
		oldBody.RemoveChild(CollisionShape);

		PhysicsBody3D newBody;
		Node newParent;
		if (simulated)
		{
			RigidBody3D rigid = new RigidBody3D();
			// Milestone 3.11: an explicit mass wins over the §3.6 volume estimate (MassForBody).
			rigid.Mass = MassForBody();
			// Sleeping is what keeps a resting assembly from costing anything; the engine wakes a
			// body the moment something touches or constrains it.
			rigid.CanSleep = true;
			newBody = rigid;
			// Named after the part so a lot-root-level body stays identifiable in the debugger;
			// Godot disambiguates duplicates on its own when names collide.
			newBody.Name = Name + "_Body";
			newParent = simulationHost != null ? simulationHost : this;
		}
		else
		{
			newBody = new StaticBody3D();
			newBody.Name = "Collision"; // build mode's canonical name under the part
			newParent = this;
		}

		newBody.SetMeta(InternalChildMeta, true);
		// The event layer resolves a touched body back to its entity through this (§3.7): a
		// simulated body sits at the lot root, so walking up its parents would never find the part.
		if (HasMeta(BuilderScene.HandleMeta)) newBody.SetMeta(SimulatedOwnerMeta, GetMeta(BuilderScene.HandleMeta));
		newParent.AddChild(newBody);
		newBody.AddChild(CollisionShape);
		newBody.GlobalTransform = bodyWorld; // the swap must not move anything
		// Milestone 3.11: the material follows the swap, so friction/bounce are identical in both
		// body forms (and an explicit mass just came along on the rigid branch above).
		ApplyPhysicsMaterial(newBody);
		CollisionBody = newBody;
		oldBody.Free();

		_simulated = simulated;
		RefreshCollision();
	}

	/// <summary>
	/// Frees a simulated part's body. It lives at the lot root rather than under the part (see
	/// <see cref="SetSimulated"/>), so the entity-destroy walk has to release it explicitly — a body
	/// that outlives its part would be an invisible collider nothing can ever delete. A no-op
	/// outside a session; the shape is the body's child and dies with it.
	/// </summary>
	public void ReleaseSimulatedBody()
	{
		if (!_simulated) return;
		PhysicsBody3D body = CollisionBody;
		_simulated = false;
		CollisionBody = null;
		CollisionShape = null;
		if (body != null && GodotObject.IsInstanceValid(body)) body.Free();
	}

	// --- Event sensor (milestone 3.7) ---

	/// <summary>
	/// How far the event sensor is grown past the part's collision shape, in local units. Physics
	/// stops a moving body exactly at contact — which is touching, not overlapping — so an
	/// exact-size volume would never report a resting ball or a character pressing a wall. The pad
	/// makes "touched" mean "in contact or within this distance" (design doc D11).
	/// </summary>
	public const float SensorPad = 0.15f;

	/// <summary>True while this part carries an event sensor (a session artifact, §3.7).</summary>
	public bool HasEventSensor { get { return _eventSensor != null && GodotObject.IsInstanceValid(_eventSensor); } }

	private Area3D _eventSensor;
	private Action<LotObject, Node3D> _sensorBodyEntered;
	private Action<LotObject, Node3D> _sensorBodyExited;

	/// <summary>
	/// Creates the part's event sensor if it does not exist yet: an <c>Area3D</c> whose shape is a
	/// padded copy of the part's collision shape, monitoring bodies on
	/// <see cref="LotCollisionGroups.EventSensorMask"/>. Internal-marked like the collision body
	/// and never monitorable, so no hierarchy row, no snapshot and no other area ever sees it; it
	/// inherits the part's transform and scale exactly like the collision body does.
	///
	/// Returns false when the node cannot host a sensor (a decal, or a mesh-less group), which is
	/// what lets the event registry report a refused subscription instead of failing silently.
	/// Idempotent: a second call keeps the existing sensor and just refreshes the callbacks.
	/// </summary>
	public bool EnsureEventSensor(Action<LotObject, Node3D> bodyEntered, Action<LotObject, Node3D> bodyExited)
	{
		if (HasEventSensor)
		{
			_sensorBodyEntered = bodyEntered;
			_sensorBodyExited = bodyExited;
			return true;
		}
		if (Kind == LotObjectKind.Decal) return false;
		if (CollisionShape == null || !GodotObject.IsInstanceValid(CollisionShape) || CollisionShape.Shape == null) return false;

		Area3D area = new Area3D();
		area.Name = "EventSensor";
		// Layer 0: the sensor itself is never detected by anything. The mask is the event set —
		// every group, the no-collide layer and the player's event-only bit (§3.7 D10/D12).
		area.CollisionLayer = 0;
		area.CollisionMask = LotCollisionGroups.EventSensorMask;
		area.Monitoring = true;
		area.Monitorable = false;
		// The sensor is a child of the part, so for a sized round part it must carry the same
		// size-as-scale the collider child does, or the sensor would stay unit-sized (see
		// ResizeCollision / RefreshEventSensor).
		area.Scale = CollisionBody.Scale;
		area.SetMeta(InternalChildMeta, true);

		CollisionShape3D sensorShape = new CollisionShape3D();
		sensorShape.Name = "Shape";
		sensorShape.Shape = MakeSensorShape(CollisionShape.Shape);
		sensorShape.SetMeta(InternalChildMeta, true);
		area.AddChild(sensorShape);

		area.BodyEntered += OnSensorBodyEntered;
		area.BodyExited += OnSensorBodyExited;

		_sensorBodyEntered = bodyEntered;
		_sensorBodyExited = bodyExited;
		_eventSensor = area;
		AddChild(area);
		return true;
	}

	/// <summary>Removes the event sensor. Called when the last subscription on this part goes away
	/// and when a session ends (sensors are session artifacts). Safe to call when absent.</summary>
	public void ReleaseEventSensor()
	{
		_sensorBodyEntered = null;
		_sensorBodyExited = null;
		if (_eventSensor != null && GodotObject.IsInstanceValid(_eventSensor)) _eventSensor.Free();
		_eventSensor = null;
	}

	private void OnSensorBodyEntered(Node3D body)
	{
		Action<LotObject, Node3D> handler = _sensorBodyEntered;
		if (handler != null) handler(this, body);
	}

	private void OnSensorBodyExited(Node3D body)
	{
		Action<LotObject, Node3D> handler = _sensorBodyExited;
		if (handler != null) handler(this, body);
	}

	/// <summary>
	/// A padded copy of a part's collision shape for the event sensor. An unknown shape type is
	/// shared as-is rather than invented — an unpadded sensor still detects real overlaps, it
	/// just cannot catch flush contact.
	/// </summary>
	private static Shape3D MakeSensorShape(Shape3D source)
	{
		if (source is BoxShape3D box)
		{
			BoxShape3D grown = new BoxShape3D();
			grown.Size = box.Size + new Vector3(SensorPad * 2f, SensorPad * 2f, SensorPad * 2f);
			return grown;
		}
		if (source is SphereShape3D sphere)
		{
			SphereShape3D grown = new SphereShape3D();
			grown.Radius = sphere.Radius + SensorPad;
			return grown;
		}
		if (source is CapsuleShape3D capsule)
		{
			CapsuleShape3D grown = new CapsuleShape3D();
			grown.Radius = capsule.Radius + SensorPad;
			grown.Height = capsule.Height + SensorPad * 2f;
			return grown;
		}
		if (source is CylinderShape3D cylinder)
		{
			CylinderShape3D grown = new CylinderShape3D();
			grown.Radius = cylinder.Radius + SensorPad;
			grown.Height = cylinder.Height + SensorPad * 2f;
			return grown;
		}
		if (source is ConvexPolygonShape3D hull)
		{
			// A hull has no pad primitive: push every point outward from the shape's centroid, so
			// the sensor still catches flush contact (the pad's whole purpose). A point at the
			// centroid cannot happen for a real hull corner, but the guard keeps it finite anyway.
			Vector3 centroid = Vector3.Zero;
			int count = hull.Points.Length;
			for (int i = 0; i < count; i++) centroid += hull.Points[i];
			if (count > 0) centroid /= count;

			Vector3[] points = new Vector3[count];
			for (int i = 0; i < count; i++)
			{
				Vector3 offset = hull.Points[i] - centroid;
				points[i] = offset.LengthSquared() > 1e-12f
					? hull.Points[i] + offset.Normalized() * SensorPad
					: hull.Points[i];
			}
			ConvexPolygonShape3D grown = new ConvexPolygonShape3D();
			grown.Points = points;
			return grown;
		}
		return source;
	}

	/// <summary>
	/// Mass from the mesh's bounding volume and the part's scale (density 1) — a deliberately plain
	/// rule, since §3.6 only needs plausible relative weights (a 20x20 ground slab must not be as
	/// light as a 1x1 crate). Clamped so a flat plane (zero-height AABB) still has positive mass.
	/// §3.11 replaces this with a per-part mass property, reserved in the registry for then.
	/// </summary>
	private float EstimateMass()
	{
		Mesh mesh = MeshInstance != null ? MeshInstance.Mesh : null;
		if (mesh == null) return 1f;
		Vector3 size = mesh.GetAabb().Size * Scale;
		float volume = Mathf.Abs(size.X * size.Y * size.Z);
		return Mathf.Max(0.1f, volume);
	}

	/// <summary>
	/// Applies a texture by opaque asset id ("" clears it). Returns false when the id is unknown,
	/// so a caller cannot silently set a texture that will never draw.
	///
	/// Reference rule: the part takes one <see cref="LotTextureCache"/> reference for the id it
	/// holds and drops the one it held before, which is what keeps swapping textures from leaking
	/// images. Callers that imported an asset should release their own import reference afterwards
	/// (<see cref="LotPropertyService.SetTextureFromFile"/> does this in one place).
	/// </summary>
	public bool SetTexture(string assetId)
	{
		bool clearing = string.IsNullOrEmpty(assetId);
		if (!clearing && !LotTextureCache.Contains(assetId)) return false;

		string previous = _textureId;
		if (previous == assetId) return true;

		if (!clearing) LotTextureCache.Acquire(assetId);
		_textureId = clearing ? "" : assetId;
		Material.AlbedoTexture = clearing ? null : LotTextureCache.Get(assetId);

		if (previous.Length > 0) LotTextureCache.Release(previous);
		// Only an Image panel shows a texture; the content refresh re-applies that rule (and keeps
		// the id itself, so switching the panel back to Image restores the picture).
		if (Kind == LotObjectKind.Decal) RefreshDecalContent();
		return true;
	}

	/// <summary>
	/// Drops the part's texture reference. Called from the single entity-destroy path so a deleted
	/// part cannot pin its image (see <see cref="BuilderScene.DestroyEntity"/>).
	/// </summary>
	public void ReleaseTexture()
	{
		if (_textureId.Length == 0) return;
		string previous = _textureId;
		_textureId = "";
		LotTextureCache.Release(previous);
	}

	/// <summary>
	/// Sets the selection highlight. Cheap and idempotent, so callers can sync it freely without
	/// worrying about redundant work.
	/// </summary>
	/// <summary>
	/// Sets the selection highlight state. Cheap and idempotent, so callers can sync it freely
	/// without worrying about redundant work.
	///
	/// This only records state: the visible highlight is the screen-space outline
	/// <see cref="SelectionOutline"/> draws from it, so no node or material is created or freed
	/// here. An object with nothing to outline (a decal's transform is derived; a query volume is
	/// a region) is a no-op, so a selection walk can call this on every lot object.
	/// </summary>
	public void SetOutline(LotOutlineState state)
	{
		if (!HasOutlineSilhouette)
		{
			return;
		}

		OutlineState = state;
	}

	/// <summary>
	/// True when this object has a silhouette worth outlining. A decal and a query volume
	/// deliberately do not: a decal's transform is derived from its host's face, and a volume is a
	/// region rather than a surface, so a silhouette ring would misrepresent either.
	/// </summary>
	public bool HasOutlineSilhouette
	{
		get
		{
			return Kind != LotObjectKind.Decal
				&& Kind != LotObjectKind.QueryVolume
				&& MeshInstance != null;
		}
	}
}