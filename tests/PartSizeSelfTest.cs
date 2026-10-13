using Godot;

/// <summary>
/// Verification for the mesh-regeneration size migration: the authored <c>Size</c> on a part
/// (Create defaults, SetSize's mesh/hull/outline/sensor refresh, refusals and clamps, mesh
/// sharing), the per-part texture mapping (tiles per unit, per-face stretch), the wedge kind
/// (mesh, collision hull, event sensor), the decal-follows-a-resized-host contract, the
/// archive record's size field with its old-lot migration marker, the size-aware TransformCommand,
/// the Size tween kind on the scheduler, the two new property declarations, and every new bound
/// Lua verb driven through the real VM. Pure resource/tree checks on detached fixtures — no
/// session, so it runs headless from <c>BuilderScene._Ready</c> under <c>#if DEBUG</c> like its
/// neighbours.
/// </summary>
public static class PartSizeSelfTest
{
	private static int _failures;
	private static int _checks;

	public static int Run()
	{
		_failures = 0;
		_checks = 0;

		TestCreateDefaults();
		TestSetSize();
		TestRefusalsAndClamps();
		TestMeshSharing();
		TestUvModes();
		TestDecalFollowsHostResize();
		TestEventSensor();
		TestArchiveRecords();
		TestUndoCommand();
		TestGizmoSizeDomain();
		TestSchedulerTween();
		TestPropertyDeclarations();
		TestLuaBindings();

		GD.Print("[PartSizeSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
		return _failures;
	}

	// --- Create defaults ---------------------------------------------------------------------

	/// <summary>Every sized kind ships unit-extent, node-scale-1, world-unit UVs — and the fixed
	/// kinds say so by refusing.</summary>
	private static void TestCreateDefaults()
	{
		LotObjectKind[] sized = { LotObjectKind.Cube, LotObjectKind.Sphere, LotObjectKind.Cylinder, LotObjectKind.Wedge };
		for (int i = 0; i < sized.Length; i++)
		{
			LotObject part = LotObject.Create(sized[i], "Sized" + i, new Color(0.5f, 0.5f, 0.5f));
			try
			{
				Check("create: " + sized[i] + " has an authored size", part.HasSize);
				Check("create: " + sized[i] + " starts unit-sized at scale 1",
					Near(part.Size, Vector3.One) && Near(part.Scale, Vector3.One));
				Check("create: " + sized[i] + " mesh is the generated ArrayMesh at the unit extents",
					part.MeshInstance.Mesh is ArrayMesh && Near(part.MeshInstance.Mesh.GetAabb().Size, Vector3.One));
				Check("create: " + sized[i] + " starts tiling at 1 and unstretched",
					Near(part.TextureTilesPerUnit, 1f) && !part.TextureStretch);
				Check("create: " + sized[i] + " collider is unit-scaled to start",
					Near(part.CollisionBody.Scale, Vector3.One));
			}
			finally
			{
				Free(part);
			}
		}

		LotObject cube = LotObject.Create(LotObjectKind.Cube, "Box", new Color(1f, 1f, 1f));
		LotObject sphere = LotObject.Create(LotObjectKind.Sphere, "Ball", new Color(1f, 1f, 1f));
		LotObject cylinder = LotObject.Create(LotObjectKind.Cylinder, "Tube", new Color(1f, 1f, 1f));
		LotObject wedge = LotObject.Create(LotObjectKind.Wedge, "Ramp", new Color(1f, 1f, 1f));
		LotObject plane = LotObject.Create(LotObjectKind.Plane, "Ground", new Color(1f, 1f, 1f));
		LotObject capsule = LotObject.Create(LotObjectKind.Capsule, "Player", new Color(1f, 1f, 1f));
		LotObject decal = LotObject.Create(LotObjectKind.Decal, "Patch", new Color(1f, 1f, 1f, 1f));
		try
		{
			Check("create: the cube's hull is a unit box", cube.CollisionShape.Shape is BoxShape3D boxShape
				&& Near(boxShape.Size, Vector3.One));
			Check("create: the sphere's hull is the unit sphere shape",
				sphere.CollisionShape.Shape is SphereShape3D sphereShape && Near(sphereShape.Radius, 0.5f));
			Check("create: the cylinder's hull is the unit cylinder shape",
				cylinder.CollisionShape.Shape is CylinderShape3D cylinderShape
				&& Near(cylinderShape.Radius, 0.5f) && Near(cylinderShape.Height, 1f));
			Check("create: the wedge's hull is a convex polygon of its six corners",
				wedge.CollisionShape.Shape is ConvexPolygonShape3D hull && hull.Points.Length == 6
				&& Near(hull.Points[5], new Vector3(0.5f, 0.5f, -0.5f)));
			Check("create: plane, capsule and decal have no authored size",
				!plane.HasSize && !capsule.HasSize && !decal.HasSize);
			Check("factory: the shape mapping is the kind's own", LotObject.ShapeFor(LotObjectKind.Cube) == PartShape.Block
				&& LotObject.ShapeFor(LotObjectKind.Sphere) == PartShape.Sphere
				&& LotObject.ShapeFor(LotObjectKind.Cylinder) == PartShape.Cylinder
				&& LotObject.ShapeFor(LotObjectKind.Wedge) == PartShape.Wedge);
		}
		finally
		{
			Free(cube);
			Free(sphere);
			Free(cylinder);
			Free(wedge);
			Free(plane);
			Free(capsule);
			Free(decal);
		}
	}

	// --- SetSize -----------------------------------------------------------------------------

	/// <summary>The resize contract per kind: mesh, collision hull, and the node scale it must
	/// never touch. The selection highlight is not part of it any more — that is a screen-space
	/// outline built from the part's state (SelectionOutline), not a mesh child — so the checks
	/// below pin that no hull is regenerated behind the part's back.</summary>
	private static void TestSetSize()
	{
		LotObject cube = LotObject.Create(LotObjectKind.Cube, "Box", new Color(1f, 1f, 1f));
		try
		{
			Check("size: a part carries no outline hull to keep in step",
				cube.GetNodeOrNull<MeshInstance3D>("Outline") == null);

			Check("size: SetSize succeeds on a cube", cube.SetSize(new Vector3(4f, 1f, 2f)));
			Check("size: the size reads back", Near(cube.Size, new Vector3(4f, 1f, 2f)));
			Check("size: the mesh is regenerated at the extents",
				Near(cube.MeshInstance.Mesh.GetAabb().Size, new Vector3(4f, 1f, 2f)));
			Check("size: the box hull carries the size natively",
				cube.CollisionShape.Shape is BoxShape3D box && Near(box.Size, new Vector3(4f, 1f, 2f))
				&& Near(cube.CollisionBody.Scale, Vector3.One));
			Check("size: the outline state still tracks the part after a resize",
				cube.HasOutlineSilhouette && cube.OutlineState == LotOutlineState.None);
			Check("size: the node scale was not touched", Near(cube.Scale, Vector3.One));

			// A legacy node scale (a script SetScale from before the migration) is a plain
			// transform and keeps multiplying the mesh: SetSize must leave it exactly alone.
			cube.Scale = new Vector3(2f, 2f, 2f);
			cube.SetSize(new Vector3(3f, 3f, 3f));
			Check("size: a legacy node scale survives a resize", Near(cube.Scale, new Vector3(2f, 2f, 2f)));
			Check("size: the mesh still carries the authored extents", Near(cube.Size, new Vector3(3f, 3f, 3f)));
			cube.Scale = Vector3.One;

			// Negative values take their magnitude (the factory's rule), so a mirrored request
			// degrades instead of producing inverted geometry.
			cube.SetSize(new Vector3(-2f, 3f, -4f));
			Check("size: negative axes take their magnitude", Near(cube.Size, new Vector3(2f, 3f, 4f)));
			Check("size: the mesh followed the sign-stripped request",
				Near(cube.MeshInstance.Mesh.GetAabb().Size, new Vector3(2f, 3f, 4f)));
		}
		finally
		{
			Free(cube);
		}

		LotObject sphere = LotObject.Create(LotObjectKind.Sphere, "Ball", new Color(1f, 1f, 1f));
		LotObject cylinder = LotObject.Create(LotObjectKind.Cylinder, "Tube", new Color(1f, 1f, 1f));
		LotObject wedge = LotObject.Create(LotObjectKind.Wedge, "Ramp", new Color(1f, 1f, 1f));
		try
		{
			// The round shapes keep the unit shape resource and carry the size as the collider
			// child's scale — the exact compound arithmetic every pre-migration lot relied on.
			sphere.SetSize(new Vector3(2f, 3f, 4f));
			Check("size: the sphere's hull stays the unit shape resource",
				sphere.CollisionShape.Shape is SphereShape3D ball && Near(ball.Radius, 0.5f));
			Check("size: the sphere's collider carries the size as its scale",
				Near(sphere.CollisionBody.Scale, new Vector3(2f, 3f, 4f)));
			Check("size: the sphere's mesh follows the extents",
				Near(sphere.MeshInstance.Mesh.GetAabb().Size, new Vector3(2f, 3f, 4f)));

			cylinder.SetSize(new Vector3(1f, 2f, 1f));
			Check("size: the cylinder's hull stays the unit shape resource",
				cylinder.CollisionShape.Shape is CylinderShape3D tube && Near(tube.Height, 1f));
			Check("size: the cylinder's collider carries the size as its scale",
				Near(cylinder.CollisionBody.Scale, new Vector3(1f, 2f, 1f)));

			wedge.SetSize(new Vector3(2f, 2f, 2f));
			Check("size: the wedge's hull is rebuilt from the scaled corners",
				wedge.CollisionShape.Shape is ConvexPolygonShape3D hull && hull.Points.Length == 6
				&& Near(hull.Points[5], new Vector3(1f, 1f, -1f))
				&& Near(wedge.CollisionBody.Scale, Vector3.One));
			Check("size: the wedge's mesh follows the extents",
				Near(wedge.MeshInstance.Mesh.GetAabb().Size, new Vector3(2f, 2f, 2f)));
		}
		finally
		{
			Free(sphere);
			Free(cylinder);
			Free(wedge);
		}
	}

	// --- Refusals and clamps -----------------------------------------------------------------

	/// <summary>The fixed-shape kinds refuse the resize, and every request is sanitized to the
	/// factory's own rule (magnitude, floor, ceiling) so neither the mesh nor the cache key can
	/// disagree with what was built.</summary>
	private static void TestRefusalsAndClamps()
	{
		LotObject plane = LotObject.Create(LotObjectKind.Plane, "Ground", new Color(1f, 1f, 1f));
		LotObject capsule = LotObject.Create(LotObjectKind.Capsule, "Player", new Color(1f, 1f, 1f));
		LotObject decal = LotObject.Create(LotObjectKind.Decal, "Patch", new Color(1f, 1f, 1f, 1f));
		try
		{
			Check("refuse: the plane rejects size, tiling and stretch",
				!plane.SetSize(new Vector3(2f, 2f, 2f)) && !plane.SetTilesPerUnit(2f) && !plane.SetTextureStretch(true));
			Check("refuse: the capsule rejects size, tiling and stretch",
				!capsule.SetSize(new Vector3(2f, 2f, 2f)) && !capsule.SetTilesPerUnit(2f) && !capsule.SetTextureStretch(true));
			Check("refuse: the decal rejects size, tiling and stretch",
				!decal.SetSize(new Vector3(2f, 2f, 2f)) && !decal.SetTilesPerUnit(2f) && !decal.SetTextureStretch(true));
			Check("refuse: a refused call leaves the defaults standing",
				Near(plane.Size, Vector3.One) && Near(capsule.Size, Vector3.One)
				&& Near(capsule.TextureTilesPerUnit, 1f) && !capsule.TextureStretch);
		}
		finally
		{
			Free(plane);
			Free(capsule);
			Free(decal);
		}

		LotObject cube = LotObject.Create(LotObjectKind.Cube, "Box", new Color(1f, 1f, 1f));
		try
		{
			cube.SetSize(new Vector3(PartMeshFactory.MaxSize * 10f, 2f, 2f));
			Check("clamp: an oversized axis lands on the factory ceiling", Near(cube.Size.X, PartMeshFactory.MaxSize));
			cube.SetSize(new Vector3(0f, -0f, 2f));
			Check("clamp: a zero axis lands on the factory floor",
				Near(cube.Size.X, PartMeshFactory.MinSize) && Near(cube.Size.Y, PartMeshFactory.MinSize));
			cube.SetSize(new Vector3(float.NaN, float.PositiveInfinity, 2f));
			Check("clamp: non-finite axes land on the factory floor",
				Near(cube.Size.X, PartMeshFactory.MinSize) && Near(cube.Size.Y, PartMeshFactory.MinSize)
				&& Near(cube.Size.Z, 2f));

			cube.SetSize(new Vector3(2f, 2f, 2f));
			Mesh beforeMesh = cube.MeshInstance.Mesh;
			cube.SetTilesPerUnit(0f);
			Check("clamp: tiling floors at the factory minimum", Near(cube.TextureTilesPerUnit, PartMeshFactory.MinTilesPerUnit));
			cube.SetTilesPerUnit(float.NaN);
			Check("clamp: non-finite tiling falls back to one", Near(cube.TextureTilesPerUnit, 1f));
			Check("clamp: setting tiling back to one restores the original mesh",
				cube.SetTilesPerUnit(1f) && cube.MeshInstance.Mesh == beforeMesh);
			cube.SetTilesPerUnit(2f);
			Mesh tiledMesh = cube.MeshInstance.Mesh;
			Check("tiling: the value sticks and the mesh changes", Near(cube.TextureTilesPerUnit, 2f)
				&& tiledMesh != beforeMesh);
			cube.SetTextureStretch(true);
			Check("stretch: the flag sticks and the mesh changes again", cube.TextureStretch
				&& cube.MeshInstance.Mesh != tiledMesh);
			Mesh stretchedMesh = cube.MeshInstance.Mesh;
			cube.SetTextureStretch(true);
			Check("stretch: re-setting the same value is a no-op", cube.MeshInstance.Mesh == stretchedMesh);
			cube.SetTextureStretch(false);
			Check("stretch: turning it back off restores the tiled mesh", !cube.TextureStretch
				&& cube.MeshInstance.Mesh == tiledMesh);
		}
		finally
		{
			Free(cube);
		}
	}

	// --- Mesh sharing ------------------------------------------------------------------------

	/// <summary>The factory cache is what keeps a drag's per-frame resizes (and every same-sized
	/// part) from regenerating: identical (shape, size, tiles, mode) requests share one mesh
	/// resource instance.</summary>
	private static void TestMeshSharing()
	{
		LotObject first = LotObject.Create(LotObjectKind.Cube, "A", new Color(1f, 1f, 1f));
		LotObject second = LotObject.Create(LotObjectKind.Cube, "B", new Color(1f, 1f, 1f));
		LotObject third = LotObject.Create(LotObjectKind.Sphere, "C", new Color(1f, 1f, 1f));
		try
		{
			first.SetSize(new Vector3(2f, 2f, 2f));
			second.SetSize(new Vector3(2f, 2f, 2f));
			third.SetSize(new Vector3(2f, 2f, 2f));
			Check("cache: two same-size cubes share one mesh", first.MeshInstance.Mesh == second.MeshInstance.Mesh);
			Check("cache: a same-size sphere is a different mesh", first.MeshInstance.Mesh != third.MeshInstance.Mesh);

			// Resize away and back: the earlier mesh is still cached and comes back by reference.
			Mesh known = first.MeshInstance.Mesh;
			first.SetSize(new Vector3(5f, 5f, 5f));
			Check("cache: a new size is a new mesh", first.MeshInstance.Mesh != known);
			first.SetSize(new Vector3(2f, 2f, 2f));
			Check("cache: resizing back reuses the earlier mesh", first.MeshInstance.Mesh == known);

			// The tiling and the UV mode are part of the key, so neither can leak between parts.
			Mesh plain = second.MeshInstance.Mesh;
			second.SetTilesPerUnit(3f);
			Check("cache: a different tiling is a different mesh", second.MeshInstance.Mesh != plain);
			second.SetTilesPerUnit(1f);
			Check("cache: dropping the tiling finds the old mesh", second.MeshInstance.Mesh == plain);
			second.SetTextureStretch(true);
			Check("cache: the UV mode is part of the key", second.MeshInstance.Mesh != plain);
			second.SetTextureStretch(false);
			Check("cache: and turning it off finds the old mesh again", second.MeshInstance.Mesh == plain);

			// Tiling means repeats per world unit: a 2-unit block at 3 per unit spans 6 in UV.
			second.SetTilesPerUnit(3f);
			Vector2[] uvs = MeshUvs(second);
			float maxU = 0f;
			for (int i = 0; i < uvs.Length; i++) maxU = Mathf.Max(maxU, uvs[i].X);
			Check("cache: a 2-unit block tiled 3/unit spans 6 in U (" + maxU + ")", Near(maxU, 6f, 1e-3f));
		}
		finally
		{
			Free(first);
			Free(second);
			Free(third);
		}
	}

	// --- UV modes ----------------------------------------------------------------------------

	/// <summary>World-unit UVs span the part's real extents (times the tiling), and FaceStretch
	/// spans each face 0..1 — the two looks the migration has to tell apart.</summary>
	private static void TestUvModes()
	{
		LotObject cube = LotObject.Create(LotObjectKind.Cube, "Box", new Color(1f, 1f, 1f));
		LotObject sphere = LotObject.Create(LotObjectKind.Sphere, "Ball", new Color(1f, 1f, 1f));
		LotObject cylinder = LotObject.Create(LotObjectKind.Cylinder, "Tube", new Color(1f, 1f, 1f));
		try
		{
			cube.SetSize(new Vector3(2f, 2f, 2f));
			Vector2 range = UvRange(cube);
			Check("uv: a 2-unit block at 1/unit spans 0..2 in both axes",
				Near(range.X, 2f, 1e-3f) && Near(range.Y, 2f, 1e-3f));

			cube.SetTilesPerUnit(2f);
			range = UvRange(cube);
			Check("uv: tiling doubles the span", Near(range.X, 4f, 1e-3f) && Near(range.Y, 4f, 1e-3f));

			cube.SetTextureStretch(true);
			range = UvRange(cube);
			Check("uv: FaceStretch spans the full image on every face",
				Near(range.X, 1f, 1e-3f) && Near(range.Y, 1f, 1e-3f));

			sphere.SetSize(new Vector3(2f, 2f, 2f));
			sphere.SetTextureStretch(true);
			range = UvRange(sphere);
			Check("uv: a stretched sphere spans 0..1 around and pole to pole",
				Near(range.X, 1f, 1e-3f) && Near(range.Y, 1f, 1e-3f));
			sphere.SetTextureStretch(false);
			range = UvRange(sphere);
			Check("uv: the world-unit sphere spans one turn by half a turn in world units",
				Near(range.X, 2f * Mathf.Pi, 1e-2f) && Near(range.Y, Mathf.Pi, 1e-2f));

			cylinder.SetTextureStretch(true);
			Vector2[] uvs = MeshUvs(cylinder);
			bool centerFound = false;
			for (int i = 0; i < uvs.Length; i++)
			{
				if (Near(uvs[i].X, 0.5f) && Near(uvs[i].Y, 0.5f)) centerFound = true;
			}
			Check("uv: a stretched cylinder cap is centred at (0.5, 0.5)", centerFound);
		}
		finally
		{
			Free(cube);
			Free(sphere);
			Free(cylinder);
		}
	}

	// --- Decals on a resized host --------------------------------------------------------------

	/// <summary>A decal's placement is derived from the host's mesh, so a resize must move and
	/// scale the patch with the face — the decal contract this migration must not break.</summary>
	private static void TestDecalFollowsHostResize()
	{
		LotObject host = LotObject.Create(LotObjectKind.Cube, "Host", new Color(0.5f, 0.5f, 0.5f));
		LotObject decal = LotObject.Create(LotObjectKind.Decal, "Logo", new Color(1f, 1f, 1f, 1f));
		host.AddChild(decal);
		try
		{
			decal.SetDecalFace(DecalMath.FaceIndex("+Z"));
			decal.SetDecalScaleU(0.5f);
			decal.SetDecalScaleV(0.5f);
			host.SetSize(new Vector3(2f, 2f, 2f));
			Check("decal: a half-face patch on a 2-unit host is one unit wide", Near(decal.Scale.X, 1f, 1e-3f));
			Check("decal: and sits on the resized face", Near(decal.Position.Z, 1f, 0.05f));

			host.SetSize(new Vector3(4f, 2f, 2f));
			Check("decal: a doubled face widens the patch with it", Near(decal.Scale.X, 2f, 1e-3f));
			Check("decal: the vertical fraction is unchanged", Near(decal.Scale.Y, 1f, 1e-3f));
			Check("decal: the patch rides the new surface", Near(decal.Position.Z, 1f, 0.05f)
				&& Near(decal.Scale.Z, 1f, 1e-3f));

			// The patch stays a fraction of the face through a stretch-mode switch too (the mesh
			// extents do not change, so the placement must not move).
			Vector3 before = decal.Scale;
			host.SetTextureStretch(true);
			Check("decal: a UV-mode change does not move the patch", Near(decal.Scale, before, 1e-4f));
		}
		finally
		{
			Free(host);
		}
	}

	// --- Event sensor ------------------------------------------------------------------------

	/// <summary>The event sensor is a padded copy of the hull; a resize must re-pad it and, for a
	/// round part, mirror the collider's size-as-scale onto it.</summary>
	private static void TestEventSensor()
	{
		LotObject cube = LotObject.Create(LotObjectKind.Cube, "Plate", new Color(1f, 1f, 1f));
		LotObject ball = LotObject.Create(LotObjectKind.Sphere, "Ball", new Color(1f, 1f, 1f));
		LotObject wedge = LotObject.Create(LotObjectKind.Wedge, "Ramp", new Color(1f, 1f, 1f));
		try
		{
			Check("sensor: the cube takes a sensor", cube.EnsureEventSensor((a, b) => { }, (a, b) => { }));
			Area3D cubeSensor = cube.GetNodeOrNull<Area3D>("EventSensor");
			Check("sensor: it exists as an internal child", cubeSensor != null);
			BoxShape3D boxSensor = cubeSensor != null && cubeSensor.GetChild(0) is CollisionShape3D shapeChild
				? shapeChild.Shape as BoxShape3D : null;
			Check("sensor: its shape is a padded copy of the box", boxSensor != null);
			float pad = boxSensor != null ? (boxSensor.Size.X - 1f) * 0.5f : 0f;
			Check("sensor: the pad is the declared SensorPad (" + pad + ")", Near(pad, LotObject.SensorPad, 1e-5f));

			cube.SetSize(new Vector3(2f, 2f, 2f));
			// The sensor's shape is a fresh padded copy after a resize, so re-read it rather than
			// the reference captured at sensor-build time.
			BoxShape3D grownBox = cubeSensor != null && cubeSensor.GetChild(0) is CollisionShape3D grownChild
				? grownChild.Shape as BoxShape3D : null;
			Check("sensor: a resize re-pads the sensor box",
				grownBox != null && Near(grownBox.Size, Vector3.One * (2f + 2f * LotObject.SensorPad)));
			Check("sensor: and the sensor stays unsized for a box part",
				cubeSensor != null && Near(cubeSensor.Scale, Vector3.One));

			ball.EnsureEventSensor((a, b) => { }, (a, b) => { });
			ball.SetSize(new Vector3(2f, 3f, 4f));
			Area3D ballSensor = ball.GetNodeOrNull<Area3D>("EventSensor");
			Check("sensor: a round part mirrors the collider's size-as-scale",
				ballSensor != null && Near(ballSensor.Scale, new Vector3(2f, 3f, 4f)));
			Check("sensor: while its shape stays the padded unit sphere",
				ballSensor != null && ballSensor.GetChild(0) is CollisionShape3D ballShapeChild
				&& ballShapeChild.Shape is SphereShape3D ballShape && Near(ballShape.Radius, 0.5f + LotObject.SensorPad));

			wedge.EnsureEventSensor((a, b) => { }, (a, b) => { });
			wedge.SetSize(new Vector3(2f, 2f, 2f));
			Area3D wedgeSensor = wedge.GetNodeOrNull<Area3D>("EventSensor");
			ConvexPolygonShape3D wedgeShape = wedgeSensor != null && wedgeSensor.GetChild(0) is CollisionShape3D wedgeShapeChild
				? wedgeShapeChild.Shape as ConvexPolygonShape3D : null;
			Vector3[] corners = LotObject.WedgeHullPoints(new Vector3(2f, 2f, 2f));
			bool padded = wedgeShape != null && wedgeShape.Points.Length == 6;
			if (padded)
			{
				for (int i = 0; i < 6; i++)
				{
					if (!Near(wedgeShape.Points[i].DistanceTo(corners[i]), LotObject.SensorPad, 1e-4f)) padded = false;
				}
			}
			Check("sensor: the wedge's sensor is its hull pushed out by the pad", padded);

			cube.ReleaseEventSensor();
			Check("sensor: the sensor comes down on release", cube.GetNodeOrNull<Area3D>("EventSensor") == null);
		}
		finally
		{
			Free(cube);
			Free(ball);
			Free(wedge);
		}
	}

	// --- Archive records ---------------------------------------------------------------------

	/// <summary>The size field's format half: the kind tags, the record round trip, and the
	/// "no size key = an older lot" migration marker the loader keys its stretch fallback on.</summary>
	private static void TestArchiveRecords()
	{
		Check("archive: the wedge tag round-trips through the kind table",
			LotSceneWalk.LotKindTag(LotObjectKind.Wedge) == "wedge"
			&& LotSceneWalk.TryParseLotKind("wedge", out LotObjectKind parsed) && parsed == LotObjectKind.Wedge);

		LotArchive.LotNodeRecord record = new LotArchive.LotNodeRecord
		{
			Kind = "wedge",
			Name = "Ramp",
			Size = new Vector3(2f, 3f, 4f),
			HasExplicitSize = true
		};
		LotArchive.LotNodeRecord back = LotArchive.LotNodeRecord.FromJson(
			Json.ParseString(Json.Stringify(record.ToJson())));
		Check("archive: a record's size survives the real encoder",
			back.HasExplicitSize && Near(back.Size, new Vector3(2f, 3f, 4f)));

		LotArchive.LotNodeRecord older = LotArchive.LotNodeRecord.FromJson(
			new Godot.Collections.Dictionary { { "kind", "cube" }, { "name", "Old" }, { "scale", new Vector3(2f, 1f, 1f) } });
		Check("archive: a record without a size key reads as an older lot",
			!older.HasExplicitSize && Near(older.Size, Vector3.One) && Near(older.Scale, new Vector3(2f, 1f, 1f)));

		Node3D root = new Node3D();
		LotObject cube = LotObject.Create(LotObjectKind.Cube, "Box", new Color(1f, 1f, 1f));
		LotObject plane = LotObject.Create(LotObjectKind.Plane, "Ground", new Color(1f, 1f, 1f));
		root.AddChild(cube);
		root.AddChild(plane);
		try
		{
			cube.SetSize(new Vector3(2f, 1f, 3f));
			System.Collections.Generic.List<LotArchive.LotNodeRecord> records
				= new System.Collections.Generic.List<LotArchive.LotNodeRecord>();
			LotSceneWalk.CaptureChildren(root, records);
			Check("archive: a sized part is captured with its extent",
				records.Count == 2 && records[0].HasExplicitSize);
			Check("archive: and with the right numbers", records.Count == 2 && Near(records[0].Size, new Vector3(2f, 1f, 3f)));
			Check("archive: a fixed-shape part carries no size marker",
				records.Count == 2 && !records[1].HasExplicitSize);
		}
		finally
		{
			Free(root);
		}
	}

	// --- Undo command --------------------------------------------------------------------------

	/// <summary>One history entry carries the transform AND the authored size, so one Ctrl+Z
	/// restores a resize whole (extent, mesh and any origin shift).</summary>
	private static void TestUndoCommand()
	{
		LotObject cube = LotObject.Create(LotObjectKind.Cube, "Box", new Color(1f, 1f, 1f));
		try
		{
			Transform3D before = cube.Transform;
			cube.SetSize(new Vector3(2f, 2f, 2f));
			TransformCommand command = new TransformCommand("Size", cube, before, cube.Transform,
				cube, Vector3.One, new Vector3(2f, 2f, 2f));

			command.Undo(null);
			Check("undo: the size restores to the recorded extent",
				Near(cube.Size, Vector3.One) && Near(cube.MeshInstance.Mesh.GetAabb().Size, Vector3.One));
			command.Redo(null);
			Check("undo: redo reapplies the new extent",
				Near(cube.Size, new Vector3(2f, 2f, 2f)) && Near(cube.MeshInstance.Mesh.GetAabb().Size, new Vector3(2f, 2f, 2f)));

			Check("undo: the size comparison helper is tolerance based",
				TransformCommand.SizeChanged(Vector3.One, new Vector3(2f, 2f, 2f))
				&& !TransformCommand.SizeChanged(Vector3.One, Vector3.One));

			// A plain transform entry (the four-argument form) must leave the size alone.
			Transform3D moved = cube.Transform;
			moved.Origin += new Vector3(1f, 0f, 0f);
			TransformCommand plain = new TransformCommand("Move", cube, cube.Transform, moved);
			plain.Undo(null);
			Check("undo: a plain transform entry never touches the size", Near(cube.Size, new Vector3(2f, 2f, 2f)));
		}
		finally
		{
			Free(cube);
		}
	}

	// --- The scale gizmo's size domain ---------------------------------------------------------

	/// <summary>The §2.7 face drag, driven with the size-domain values the controller feeds it.
	/// The contract: one unit of cursor motion on a face grows the extent by exactly one unit, and
	/// the origin shifts by half of it — which keeps the OPPOSITE face exactly where it was. The
	/// regression this pins: with the live mesh's half as the reference instead, a 4-unit part grew
	/// only a quarter of the motion while still shifting the full half, so the part slid away as it
	/// was scaled up (a unit part cancels the error, which is why it read as working before).</summary>
	private static void TestGizmoSizeDomain()
	{
		int face = DecalMath.FaceIndex("+X");
		Check("gizmo: the size domain's half-per-unit is 0.5 at node scale 1",
			Near(FaceScaleMath.SizeDomainHalfPerUnit(face, Vector3.One), 0.5f));
		Check("gizmo: a legacy node scale is carried into the reference",
			Near(FaceScaleMath.SizeDomainHalfPerUnit(face, new Vector3(2f, 1f, 1f)), 1f)
			&& Near(FaceScaleMath.SizeDomainHalfPerUnit(face, new Vector3(1f, 3f, 1f)), 0.5f)
			&& Near(FaceScaleMath.SizeDomainHalfPerUnit(DecalMath.FaceIndex("+Y"), new Vector3(1f, 3f, 1f)), 1.5f));

		// A +1 drag on the +X face of a 4-unit-wide part at scale 1.
		FaceScaleMath.FaceDrag drag = FaceScaleMath.ResolveFaceDrag(face, 4f,
			FaceScaleMath.SizeDomainHalfPerUnit(face, Vector3.One), Vector3.Right, Vector3.Right, 0f);
		Check("gizmo: a +1 face drag grows the extent by exactly 1 (not a quarter of it)",
			drag.Changed && Near(drag.ScaleAxis, 5f));
		Check("gizmo: the origin shifts by half the drag", Near(drag.PositionShift, new Vector3(0.5f, 0f, 0f)));

		// The anchor invariant in numbers: the opposite face must not move, and the dragged face
		// must land exactly one unit further out.
		float draggedFaceBefore = 0f + 4f * 0.5f;                  // centre 0, half 2
		float oppositeFaceBefore = 0f - 4f * 0.5f;
		float oppositeFaceAfter = drag.PositionShift.X - drag.ScaleAxis * 0.5f;   // centre +0.5, half 2.5
		float draggedFaceAfter = drag.PositionShift.X + drag.ScaleAxis * 0.5f;
		Check("gizmo: the opposite face stays exactly put", Near(oppositeFaceAfter, oppositeFaceBefore));
		Check("gizmo: the dragged face follows the cursor 1:1", Near(draggedFaceAfter, draggedFaceBefore + 1f));

		// The value the old wiring produced, kept explicit so the regression cannot creep back.
		Check("gizmo: feeding the live mesh half is the bug (it yields 4.25, not 5)",
			Near(FaceScaleMath.ResolveFaceDrag(face, 4f, 2f, Vector3.Right, Vector3.Right, 0f).ScaleAxis, 4.25f));

		// A legacy-scaled part: the visual half is mesh × node scale, so doubling the node scale
		// doubles the reference — the same +1 drag must still grow the VISUAL half by 0.5.
		FaceScaleMath.FaceDrag scaled = FaceScaleMath.ResolveFaceDrag(face, 4f,
			FaceScaleMath.SizeDomainHalfPerUnit(face, new Vector3(2f, 2f, 2f)), Vector3.Right, Vector3.Right, 0f);
		float visualHalfBefore = 4f * 0.5f * 2f;
		float visualHalfAfter = scaled.ScaleAxis * 0.5f * 2f;
		Check("gizmo: a legacy-scaled part still grows its visual half by half the drag",
			Near(visualHalfAfter - visualHalfBefore, 0.5f) && Near(scaled.PositionShift, new Vector3(0.5f, 0f, 0f)));
	}

	// --- Size tween ----------------------------------------------------------------------------

	/// <summary>The scheduler's Size kind interpolates the part's authored extent through the same
	/// SetSize path Lua and the editor use, and refuses a node without one.</summary>
	private static void TestSchedulerTween()
	{
		System.Collections.Generic.List<string> warns = new System.Collections.Generic.List<string>();
		LotScheduler scheduler = new LotScheduler(warns.Add);
		LotObject cube = LotObject.Create(LotObjectKind.Cube, "Box", new Color(1f, 1f, 1f));
		LotObject capsule = LotObject.Create(LotObjectKind.Capsule, "Player", new Color(1f, 1f, 1f));
		try
		{
			scheduler.ResolveTarget = handle => handle == 1 ? cube : (handle == 2 ? capsule : null);

			int id = scheduler.ScheduleTween(1, "size", 3f, 3f, 3f, 0f, 1f, "linear");
			Check("tween: a size tween schedules on a sized part", id > 0 && scheduler.Count == 1);
			scheduler.Advance(0.5);
			Check("tween: halfway is halfway between the extents", Near(cube.Size, new Vector3(2f, 2f, 2f), 1e-3f));
			scheduler.Advance(0.5);
			Check("tween: it lands exactly and leaves the schedule",
				Near(cube.Size, new Vector3(3f, 3f, 3f), 1e-3f) && scheduler.Count == 0);

			int refused = scheduler.ScheduleTween(2, "size", 3f, 3f, 3f, 0f, 1f, "linear");
			Check("tween: a capsule refuses a size tween with a warning", refused == -1 && warns.Count > 0);
			Check("tween: the refusal left the capsule alone", Near(capsule.Size, Vector3.One));
		}
		finally
		{
			Free(cube);
			Free(capsule);
		}
	}

	// --- Property declarations -----------------------------------------------------------------

	/// <summary>The two declared texture-mapping properties exist for sized kinds, reach the part
	/// through the descriptor path the Inspector uses, and are absent for the fixed shapes.</summary>
	private static void TestPropertyDeclarations()
	{
		LotObject cube = LotObject.Create(LotObjectKind.Cube, "Box", new Color(1f, 1f, 1f));
		LotObject capsule = LotObject.Create(LotObjectKind.Capsule, "Player", new Color(1f, 1f, 1f));
		try
		{
			System.Collections.Generic.List<LotPropertyDescriptor> buffer
				= new System.Collections.Generic.List<LotPropertyDescriptor>();
			LotPropertyRegistry.CollectFor(cube, buffer);
			bool hasTiles = false;
			bool hasStretch = false;
			for (int i = 0; i < buffer.Count; i++)
			{
				if (buffer[i].Id == "tilesPerUnit") hasTiles = true;
				if (buffer[i].Id == "stretchTexture") hasStretch = true;
			}
			Check("registry: a sized part declares tiling and stretch", hasTiles && hasStretch);

			buffer.Clear();
			LotPropertyRegistry.CollectFor(capsule, buffer);
			bool capsuleMapping = false;
			for (int i = 0; i < buffer.Count; i++)
			{
				if (buffer[i].Id == "tilesPerUnit" || buffer[i].Id == "stretchTexture") capsuleMapping = true;
			}
			Check("registry: a fixed-shape part does not declare them", !capsuleMapping);

			LotPropertyDescriptor tiles = LotPropertyRegistry.Find("tilesPerUnit");
			LotPropertyDescriptor stretch = LotPropertyRegistry.Find("stretchTexture");
			Check("registry: both descriptors exist and gate on the sized kinds",
				tiles != null && stretch != null && tiles.AppliesTo(cube) && !tiles.AppliesTo(capsule));
			tiles.SetFloat(cube, 2.5f);
			stretch.SetBool(cube, true);
			Check("registry: the descriptors write through to the part",
				Near(cube.TextureTilesPerUnit, 2.5f) && cube.TextureStretch
				&& Near(tiles.GetFloat(cube), 2.5f) && stretch.GetBool(cube));
		}
		finally
		{
			Free(cube);
			Free(capsule);
		}
	}

	// --- Lua ------------------------------------------------------------------------------------

	/// <summary>Every new bound verb driven through the real VM (the project's convention for a
	/// new API surface): the wedge spawn and its sugar, size get/set with the fixed-shape refusal,
	/// and the texture-mapping pair. The TweenSize sugar itself is covered by TweenSelfTest, whose
	/// private VM is the entity sandbox the tween verbs live in.</summary>
	private static void TestLuaBindings()
	{
		LuaManager lua = LuaManager.Instance;
		if (!lua.IsRuntimeAvailable)
		{
			Check("lua: size and wedge verbs are bound and behave (runtime unavailable, skipped)", true);
			return;
		}

		const string script =
			"local w = Lot.SpawnWedge(460, 300, 460)\n" +
			"assert(type(w) == 'number' and w >= 0, 'wedge spawn failed')\n" +
			"local w2 = wedge(461, 300, 461)\n" +
			"assert(type(w2) == 'number' and w2 >= 0, 'wedge sugar failed')\n" +
			"assert(math.abs(Lot.GetSizeX(w) - 1) < 1e-6, 'a new wedge is unit-sized')\n" +
			"Lot.SetSize(w, 2, 1, 3)\n" +
			"assert(math.abs(Lot.GetSizeX(w) - 2) < 1e-6, 'SetSize should stick (x)')\n" +
			"assert(math.abs(Lot.GetSizeY(w) - 1) < 1e-6, 'SetSize should stick (y)')\n" +
			"assert(math.abs(Lot.GetSizeZ(w) - 3) < 1e-6, 'SetSize should stick (z)')\n" +
			"local cap = Lot.SpawnCapsule(462, 300, 462)\n" +
			"Lot.SetSize(cap, 2, 2, 2)\n" +
			"assert(math.abs(Lot.GetSizeX(cap) - 0) < 1e-6, 'a capsule has no authored size')\n" +
			"Lot.SetTilesPerUnit(w, 2)\n" +
			"assert(math.abs(Lot.GetTilesPerUnit(w) - 2) < 1e-6, 'tiling should stick')\n" +
			"Lot.SetTextureStretch(w, true)\n" +
			"assert(Lot.GetTextureStretch(w) == true, 'stretch should stick')\n" +
			"Lot.SetTextureStretch(w, false)\n" +
			"assert(Lot.GetTextureStretch(w) == false, 'stretch should turn off')\n" +
			"Lot.DestroyObject(w)\n" +
			"Lot.DestroyObject(w2)\n" +
			"Lot.DestroyObject(cap)";

		bool ran = lua.RunString(script, "size_selftest");
		Check("lua: size and wedge verbs are bound and behave" + (ran ? "" : " (" + lua.LastError + ")"), ran);
	}

	// --- helpers -------------------------------------------------------------------------------

	private static void Check(string label, bool ok)
	{
		_checks++;
		if (!ok)
		{
			_failures++;
			GD.Print("[PartSizeSelfTest] FAIL  " + label);
		}
	}

	private static bool Near(float a, float b, float tolerance = 1e-5f)
	{
		return Mathf.Abs(a - b) <= tolerance;
	}

	private static bool Near(Vector3 a, Vector3 b, float tolerance = 1e-4f)
	{
		return a.DistanceTo(b) <= tolerance;
	}

	/// <summary>Frees a fixture (and its subtree). Safe for a node that was never added to a tree,
	/// which is how every fixture here lives.</summary>
	private static void Free(Node node)
	{
		if (node != null && GodotObject.IsInstanceValid(node)) node.Free();
	}

	/// <summary>The mesh's UVs from surface 0.</summary>
	private static Vector2[] MeshUvs(LotObject part)
	{
		ArrayMesh mesh = part.MeshInstance != null ? part.MeshInstance.Mesh as ArrayMesh : null;
		if (mesh == null) return new Vector2[0];
		return mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.TexUV].AsVector2Array();
	}

	/// <summary>The UV span (max - min on each axis) of the mesh — 0..extent*tiles for world-unit
	/// UVs, 0..1 for FaceStretch.</summary>
	private static Vector2 UvRange(LotObject part)
	{
		Vector2[] uvs = MeshUvs(part);
		if (uvs.Length == 0) return Vector2.Zero;
		Vector2 min = uvs[0];
		Vector2 max = uvs[0];
		for (int i = 1; i < uvs.Length; i++)
		{
			min.X = Mathf.Min(min.X, uvs[i].X);
			min.Y = Mathf.Min(min.Y, uvs[i].Y);
			max.X = Mathf.Max(max.X, uvs[i].X);
			max.Y = Mathf.Max(max.Y, uvs[i].Y);
		}
		return max - min;
	}
}
