using Godot;

/// <summary>
/// The shared ball-and-rod hinge marker (milestone 3.6): the placement step's hinge point and the
/// build-mode orientation markers of a selected part's hinges are the same visual, built here once
/// so the two cannot drift apart. Everything is marked <see cref="LotObject.InternalChildMeta"/>,
/// so the hierarchy, the save walk, the session snapshot and the entity-destroy walk all skip it,
/// and the meshes carry no collision, so a pick ray passes straight through.
/// </summary>
public static class ConstraintVisuals
{
	/// <summary>Creates a marker (green ball + amber axis rod) under <paramref name="parent"/> and
	/// hands the rod back so the caller can aim it with <see cref="AimAxis"/>.</summary>
	public static Node3D CreateMarker(Node parent, string name, out MeshInstance3D axisRod)
	{
		Node3D root = new Node3D();
		root.Name = name;
		root.SetMeta(LotObject.InternalChildMeta, true);

		SphereMesh ball = new SphereMesh();
		ball.Radius = 0.1f;
		ball.Height = 0.2f;
		MeshInstance3D ballMesh = new MeshInstance3D();
		ballMesh.Mesh = ball;
		ballMesh.MaterialOverride = MakeMaterial(new Color(0.25f, 0.9f, 0.45f));
		ballMesh.SetMeta(LotObject.InternalChildMeta, true);
		root.AddChild(ballMesh);

		CylinderMesh rod = new CylinderMesh();
		rod.TopRadius = 0.02f;
		rod.BottomRadius = 0.02f;
		rod.Height = 0.7f;
		axisRod = new MeshInstance3D();
		axisRod.Mesh = rod;
		axisRod.MaterialOverride = MakeMaterial(new Color(0.95f, 0.75f, 0.2f));
		axisRod.SetMeta(LotObject.InternalChildMeta, true);
		root.AddChild(axisRod);

		parent.AddChild(root);
		return root;
	}

	/// <summary>Aims the rod's long (local Y) axis at <paramref name="axis"/> in world space.</summary>
	public static void AimAxis(MeshInstance3D rod, Vector3 axis)
	{
		if (rod == null || !GodotObject.IsInstanceValid(rod)) return;
		Vector3 reference = Mathf.Abs(axis.Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up;
		Vector3 x = reference.Cross(axis).Normalized();
		Vector3 z = x.Cross(axis);
		rod.Basis = new Basis(x, axis, z);
	}

	private static StandardMaterial3D MakeMaterial(Color color)
	{
		StandardMaterial3D material = new StandardMaterial3D();
		material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
		material.AlbedoColor = color;
		material.DisableReceiveShadows = true;
		return material;
	}
}
