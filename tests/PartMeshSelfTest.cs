using Godot;

/// <summary>
/// Verification for <see cref="PartMeshFactory"/>: the world-unit UV contract (1.0 UV = 1 unit
/// of part size, upright and unmirrored on every face), the winding Godot's clockwise front faces
/// require (checked against Godot's own BoxMesh as the external reference), flat vs smooth
/// normals, generated tangents, mesh sharing across faces, and the resize helper's bounded mesh
/// cache. Pure resource/math checks — no scene tree, every fixture freed before returning — so it
/// runs headless from <c>BuilderScene._Ready</c> under <c>#if DEBUG</c> like its neighbours.
/// </summary>
public static class PartMeshSelfTest
{
	private static int _failures;
	private static int _checks;

	public static int Run()
	{
		_failures = 0;
		_checks = 0;

		PartMeshFactory.ClearMeshCache();

		TestBlock();
		TestSphere();
		TestCylinder();
		TestWedge();
		TestWindingAgainstGodot();
		TestTangents();
		TestSanitization();
		TestCacheAndUpdate();

		PartMeshFactory.ClearMeshCache();

		GD.Print("[PartMeshSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
		return _failures;
	}

	// --- Block ------------------------------------------------------------------------------

	/// <summary>The block's topology, extents and flat face normals.</summary>
	private static void TestBlock()
	{
		ArrayMesh mesh = PartMeshFactory.CreateBlock(new Vector3(4f, 1f, 2f));
		SurfaceData data = SurfaceData.Read(mesh);

		Check("block: 4x1x2 is 24 vertices (four per face, nothing shared)", data.Positions.Length == 24);
		Check("block: 36 indices / 12 triangles", data.Indices.Length == 36 && data.TriangleCount == 12);

		Aabb bounds = mesh.GetAabb();
		Check("block: bounds are the requested extents, centred on the origin",
			Near(bounds.Size, new Vector3(4f, 1f, 2f)) && Near(bounds.GetCenter(), Vector3.Zero));

		// Flat shading: each of the six faces' four vertices carry that face's normal exactly.
		Check("block: the +Z and -Z faces are flat",
			data.CountWithNormal(Vector3.Back) == 4 && data.CountWithNormal(Vector3.Forward) == 4);
		Check("block: the +X and -X faces are flat",
			data.CountWithNormal(Vector3.Right) == 4 && data.CountWithNormal(Vector3.Left) == 4);
		Check("block: the +Y and -Y faces are flat",
			data.CountWithNormal(Vector3.Up) == 4 && data.CountWithNormal(Vector3.Down) == 4);

		// The milestone's worked example: a 4x1x2 block's top face (X by Z) is 0..4 by 0..2 and
		// its front face (X by Y) is 0..4 by 0..1 — from the bottom-left-when-viewed corner.
		Check("block: top face spans 0..4 by 0..2",
			data.HasUv(new Vector3(-2f, 0.5f, -1f), Vector2.Zero)
			&& data.HasUv(new Vector3(2f, 0.5f, 1f), new Vector2(4f, 2f))
			&& data.HasUv(new Vector3(2f, 0.5f, -1f), new Vector2(4f, 0f))
			&& data.HasUv(new Vector3(-2f, 0.5f, 1f), new Vector2(0f, 2f)));
		Check("block: front face spans 0..4 by 0..1",
			data.HasUv(new Vector3(-2f, 0.5f, 1f), Vector2.Zero)
			&& data.HasUv(new Vector3(2f, 0.5f, 1f), new Vector2(4f, 0f))
			&& data.HasUv(new Vector3(2f, -0.5f, 1f), new Vector2(4f, 1f))
			&& data.HasUv(new Vector3(-2f, -0.5f, 1f), new Vector2(0f, 1f)));

		// "Up in the texture points up": every side face starts V at its top edge and ends at its
		// bottom (Godot samples V = 0 at the image top, the same arrangement its own QuadMesh and
		// BoxMesh use). Checked per face via V alone, since a corner position carries one vertex
		// per adjacent face.
		Check("block: image up points up on the +Z and -Z faces",
			data.HasUvWithV(new Vector3(-2f, 0.5f, 1f), 0f) && data.HasUvWithV(new Vector3(-2f, -0.5f, 1f), 1f)
			&& data.HasUvWithV(new Vector3(2f, 0.5f, -1f), 0f) && data.HasUvWithV(new Vector3(2f, -0.5f, -1f), 1f));
		Check("block: image up points up on the +X and -X faces",
			data.HasUvWithV(new Vector3(2f, 0.5f, 1f), 0f) && data.HasUvWithV(new Vector3(2f, -0.5f, -1f), 1f)
			&& data.HasUvWithV(new Vector3(-2f, 0.5f, -1f), 0f) && data.HasUvWithV(new Vector3(-2f, -0.5f, 1f), 1f));

		// No mirroring between opposite faces: U = 0 always sits at the edge that is on the left
		// when the face is viewed from outside, so a label reads the same on both.
		Check("block: +Z / -Z are not mirrored",
			data.HasUv(new Vector3(-2f, 0.5f, 1f), new Vector2(0f, 0f))
			&& data.HasUv(new Vector3(2f, 0.5f, -1f), new Vector2(0f, 0f)));
		Check("block: +X / -X are not mirrored",
			data.HasUv(new Vector3(2f, 0.5f, 1f), new Vector2(0f, 0f))
			&& data.HasUv(new Vector3(-2f, 0.5f, -1f), new Vector2(0f, 0f)));
		Check("block: +Y / -Y use the planar X/Z convention",
			data.HasUv(new Vector3(-2f, 0.5f, -1f), Vector2.Zero)
			&& data.HasUv(new Vector3(-2f, -0.5f, 1f), Vector2.Zero)
			&& data.HasUv(new Vector3(2f, -0.5f, -1f), new Vector2(4f, 2f))
			&& data.HasUv(new Vector3(2f, 0.5f, -1f), new Vector2(4f, 0f)));

		Check("block: the other side faces follow the same rule",
			data.HasUv(new Vector3(2f, 0.5f, -1f), new Vector2(2f, 0f))
			&& data.HasUv(new Vector3(-2f, 0.5f, 1f), new Vector2(2f, 0f))
			&& data.HasUv(new Vector3(2f, -0.5f, -1f), new Vector2(2f, 1f))
			&& data.HasUv(new Vector3(-2f, -0.5f, 1f), new Vector2(2f, 1f)));

		// tilesPerUnit multiplies every UV, so 2 tiles per unit halves the texture's world size.
		SurfaceData tiled = SurfaceData.Read(PartMeshFactory.CreateBlock(new Vector3(4f, 1f, 2f), 2f));
		Check("block: tilesPerUnit scales every UV",
			tiled.HasUv(new Vector3(2f, 0.5f, 1f), new Vector2(8f, 4f))
			&& tiled.HasUv(new Vector3(2f, -0.5f, 1f), new Vector2(8f, 2f)));
	}

	// --- Sphere -----------------------------------------------------------------------------

	/// <summary>A round sphere's topology, smooth normals, and the equator/pole UV anchors that
	/// scale U by the circumference and V by the pole-to-pole arc.</summary>
	private static void TestSphere()
	{
		// Radius 1 (diameter 2) keeps the arithmetic readable: circumference 2*PI.
		ArrayMesh mesh = PartMeshFactory.CreateSphere(new Vector3(2f, 2f, 2f), 16, 8);
		SurfaceData data = SurfaceData.Read(mesh);

		// (rings+1) x (segments+1) grid vertices minus the two unreferenced pole-wrap copies
		// (each fan keeps its wedge's first-edge copy; there are no degenerate pole triangles).
		Check("sphere: the committed vertex count drops the two unused pole-wrap copies",
			data.Positions.Length == 9 * 17 - 2);
		Check("sphere: 2 x segments x (rings-1) triangles",
			data.TriangleCount == 2 * 16 * 7 && data.Indices.Length == data.TriangleCount * 3);

		Aabb bounds = mesh.GetAabb();
		Check("sphere: bounds are the requested extents, centred on the origin",
			Near(bounds.Size, new Vector3(2f, 2f, 2f), 1e-3f) && Near(bounds.GetCenter(), Vector3.Zero, 1e-3f));

		Check("sphere: U starts at +Z (phi = 0) and the equator's V sits at a quarter turn",
			data.HasUv(new Vector3(0f, 0f, 1f), new Vector2(0f, Mathf.Pi * 0.5f)));
		Check("sphere: U at +X is a quarter of the circumference (PI/2 x radius)",
			data.HasUv(new Vector3(1f, 0f, 0f), new Vector2(Mathf.Pi * 0.5f, Mathf.Pi * 0.5f)));
		Check("sphere: V runs pole to pole, image top at the top pole",
			data.HasUv(new Vector3(0f, 1f, 0f), new Vector2(0f, 0f))
			&& data.HasUv(new Vector3(0f, -1f, 0f), new Vector2(0f, Mathf.Pi)));
		Check("sphere: the seam column duplicates the wrap (u = 0 and u = circumference)",
			data.HasUv(new Vector3(0f, 0f, 1f), new Vector2(0f, Mathf.Pi * 0.5f))
			&& data.HasUv(new Vector3(0f, 0f, 1f), new Vector2(Mathf.Tau, Mathf.Pi * 0.5f)));
		Check("sphere: each pole carries one vertex per segment, starting at u = 0",
			data.CountAt(new Vector3(0f, 1f, 0f), Vector3.Up, 1e-3f) == 16
			&& data.CountAt(new Vector3(0f, -1f, 0f), Vector3.Down, 1e-3f) == 16);

		// Smooth normals: they follow the surface, not the face — at phi = 0, theta = PI/4 the
		// vertex sits at (0, cos, sin) and its normal is the radial direction.
		Check("sphere: a smooth normal is the radial direction",
			data.HasNormal(new Vector3(0f, Mathf.Cos(Mathf.Pi * 0.25f), Mathf.Sin(Mathf.Pi * 0.25f)),
				new Vector3(0f, Mathf.Cos(Mathf.Pi * 0.25f), Mathf.Sin(Mathf.Pi * 0.25f))));
		Check("sphere: pole normals are exactly +/-Y",
			data.HasNormal(new Vector3(0f, 1f, 0f), Vector3.Up)
			&& data.HasNormal(new Vector3(0f, -1f, 0f), Vector3.Down));

		// A stretched sphere's normals are the true ellipsoid gradient, not the radial direction:
		// size (4,2,2) at theta = PI/4, phi = PI/2 sits at (1.414, 0.707, 0) with normal
		// normalize(x/rx^2, y/ry^2, 0) = (0.447, 0.894, 0).
		SurfaceData stretched = SurfaceData.Read(PartMeshFactory.CreateSphere(new Vector3(4f, 2f, 2f), 16, 8));
		Check("sphere: a stretched sphere uses the ellipsoid gradient (not radial normals)",
			stretched.HasNormal(new Vector3(1.41421f, 0.70711f, 0f), new Vector3(0.44721f, 0.89443f, 0f), 1e-3f)
			&& !stretched.HasNormal(new Vector3(1.41421f, 0.70711f, 0f), new Vector3(0.89443f, 0.44721f, 0f), 1e-3f));
	}

	// --- Cylinder ---------------------------------------------------------------------------

	/// <summary>The cylinder's side ring, its duplicated seam, planar cap UVs, and the separation
	/// of side from cap vertices (same positions, different normals and UVs).</summary>
	private static void TestCylinder()
	{
		ArrayMesh mesh = PartMeshFactory.CreateCylinder(new Vector3(2f, 3f, 2f), 16);
		SurfaceData data = SurfaceData.Read(mesh);

		Check("cylinder: 4 x segments + 6 vertices (two side rows + cap fans)",
			data.Positions.Length == 4 * 16 + 6);
		Check("cylinder: 4 x segments triangles",
			data.TriangleCount == 4 * 16 && data.Indices.Length == data.TriangleCount * 3);

		Aabb bounds = mesh.GetAabb();
		Check("cylinder: bounds are the requested extents, centred on the origin",
			Near(bounds.Size, new Vector3(2f, 3f, 2f), 1e-3f) && Near(bounds.GetCenter(), Vector3.Zero, 1e-3f));

		Check("cylinder: side U starts at +Z, V starts at the top edge",
			data.HasUv(new Vector3(0f, 1.5f, 1f), Vector3.Back, Vector2.Zero)
			&& data.HasUv(new Vector3(0f, -1.5f, 1f), Vector3.Back, new Vector2(0f, 3f)));
		Check("cylinder: side U at +X is a quarter of the circumference",
			data.HasUv(new Vector3(1f, 1.5f, 0f), Vector3.Right, new Vector2(Mathf.Pi * 0.5f, 0f)));
		Check("cylinder: the seam column duplicates the wrap",
			data.HasUv(new Vector3(0f, 1.5f, 1f), Vector3.Back, new Vector2(Mathf.Tau, 0f)));
		Check("cylinder: the side's normals are horizontal (smooth around)",
			data.HasNormal(new Vector3(0f, 1.5f, 1f), Vector3.Back)
			&& data.HasNormal(new Vector3(1f, 1.5f, 0f), Vector3.Right));

		// Caps: planar UVs taken straight from the cap position (u = x, v = +/-z), flat normals.
		Check("cylinder: top cap is planar (u = x, v = +z), centre at 0,0",
			data.HasUv(new Vector3(0f, 1.5f, 0f), Vector3.Up, Vector2.Zero)
			&& data.HasUv(new Vector3(1f, 1.5f, 0f), Vector3.Up, new Vector2(1f, 0f))
			&& data.HasUv(new Vector3(0f, 1.5f, 1f), Vector3.Up, new Vector2(0f, 1f)));
		Check("cylinder: bottom cap mirrors the planar axes so it reads unmirrored from below",
			data.HasUv(new Vector3(0f, -1.5f, 0f), Vector3.Down, Vector2.Zero)
			&& data.HasUv(new Vector3(0f, -1.5f, 1f), Vector3.Down, new Vector2(0f, -1f))
			&& data.HasUv(new Vector3(1f, -1.5f, 0f), Vector3.Down, new Vector2(1f, 0f)));

		// A rim position carries two vertices — one side, one cap — with different normals and
		// UVs, which is exactly why they cannot be shared.
		Check("cylinder: a rim position has separate side and cap vertices",
			data.CountAt(new Vector3(1f, 1.5f, 0f), Vector3.Right) == 1
			&& data.CountAt(new Vector3(1f, 1.5f, 0f), Vector3.Up) == 1
			&& data.CountAt(new Vector3(1f, -1.5f, 0f), Vector3.Right) == 1
			&& data.CountAt(new Vector3(1f, -1.5f, 0f), Vector3.Down) == 1);

		// tilesPerUnit reaches the side and the caps.
		SurfaceData tiled = SurfaceData.Read(PartMeshFactory.CreateCylinder(new Vector3(2f, 3f, 2f), 16, 0.5f));
		Check("cylinder: tilesPerUnit scales side and cap UVs",
			tiled.HasUv(new Vector3(0f, -1.5f, 1f), Vector3.Back, new Vector2(0f, 1.5f))
			&& tiled.HasUv(new Vector3(1f, 1.5f, 0f), Vector3.Up, new Vector2(0.5f, 0f)));
	}

	// --- Wedge ------------------------------------------------------------------------------

	/// <summary>The right-angle ramp: five flat faces with nothing shared between them, its slope
	/// measuring real edge lengths (width x slant), and its side triangles landing their UVs on
	/// the real leg lengths.</summary>
	private static void TestWedge()
	{
		ArrayMesh mesh = PartMeshFactory.CreateWedge(new Vector3(4f, 2f, 6f));
		SurfaceData data = SurfaceData.Read(mesh);

		Check("wedge: 18 vertices / 24 indices / 8 triangles (5 flat faces, nothing shared)",
			data.Positions.Length == 18 && data.Indices.Length == 24 && data.TriangleCount == 8);

		Aabb bounds = mesh.GetAabb();
		Check("wedge: bounds are the requested extents, centred on the origin",
			Near(bounds.Size, new Vector3(4f, 2f, 6f), 1e-3f) && Near(bounds.GetCenter(), Vector3.Zero, 1e-3f));

		float slant = Mathf.Sqrt(2f * 2f + 6f * 6f);          // sqrt(40) ≈ 6.3246
		Vector3 slopeNormal = new Vector3(0f, 6f, 2f) / slant; // the slope's outward normal

		Check("wedge: all five faces are flat, with nothing shared between them",
			data.CountWithNormal(Vector3.Down) == 4
			&& data.CountWithNormal(Vector3.Forward) == 4
			&& data.CountWithNormal(slopeNormal) == 4
			&& data.CountWithNormal(Vector3.Left) == 3
			&& data.CountWithNormal(Vector3.Right) == 3);

		// The slope's UVs are its real edge lengths: width along U, the slant along V, image top
		// at the top edge (v = 0).
		Check("wedge: the slope spans its width by its slant",
			data.HasUv(new Vector3(-2f, 1f, -3f), Vector2.Zero)
			&& data.HasUv(new Vector3(2f, 1f, -3f), new Vector2(4f, 0f))
			&& data.HasUv(new Vector3(2f, -1f, 3f), new Vector2(4f, slant))
			&& data.HasUv(new Vector3(-2f, -1f, 3f), new Vector2(0f, slant)));

		// The side triangles follow the block's +/-X face bases: vertical leg = size.Y, horizontal
		// leg = size.Z, and the UV hypotenuse is therefore the same slant.
		Check("wedge: the left triangle measures its real edges",
			data.HasUv(new Vector3(-2f, -1f, -3f), new Vector2(0f, 2f))
			&& data.HasUv(new Vector3(-2f, 1f, -3f), Vector2.Zero)
			&& data.HasUv(new Vector3(-2f, -1f, 3f), new Vector2(6f, 2f)));
		Check("wedge: the right triangle measures its real edges",
			data.HasUv(new Vector3(2f, -1f, -3f), new Vector2(6f, 2f))
			&& data.HasUv(new Vector3(2f, -1f, 3f), new Vector2(0f, 2f))
			&& data.HasUv(new Vector3(2f, 1f, -3f), new Vector2(6f, 0f)));

		// Bottom and back wall reuse the block's -Y and -Z conventions exactly, so a texture laid
		// across a wedge and a block agrees.
		Check("wedge: the bottom matches the block's -Y face",
			data.HasUv(new Vector3(-2f, -1f, -3f), new Vector2(0f, 6f))
			&& data.HasUv(new Vector3(2f, -1f, 3f), new Vector2(4f, 0f))
			&& data.HasUv(new Vector3(2f, -1f, -3f), new Vector2(4f, 6f)));
		Check("wedge: the back wall matches the block's -Z face",
			data.HasUv(new Vector3(2f, 1f, -3f), Vector2.Zero)
			&& data.HasUv(new Vector3(-2f, 1f, -3f), new Vector2(4f, 0f))
			&& data.HasUv(new Vector3(-2f, -1f, -3f), new Vector2(4f, 2f)));

		// 1 UV = 1 world unit, measured: the slope's world edge lengths equal their UV spans.
		Check("wedge: 1 UV on the slope is 1 world unit (edges match)",
			Near(data.UvDistance(new Vector3(-2f, 1f, -3f), slopeNormal, new Vector3(2f, 1f, -3f), slopeNormal), 4f)
			&& Near(data.UvDistance(new Vector3(-2f, 1f, -3f), slopeNormal, new Vector3(-2f, -1f, 3f), slopeNormal), slant));
	}

	// --- Winding, against Godot's own meshes --------------------------------------------------

	/// <summary>
	/// The winding contract, measured rather than assumed: Godot's front faces are wound so that
	/// (b-a) x (c-a) points OPPOSITE the stored outward normal (its primitives all satisfy this),
	/// and every generated mesh must too — strictly, with no triangle left degenerate. Godot's
	/// BoxMesh / PlaneMesh / SphereMesh / CylinderMesh are read here as the external reference so
	/// the convention can never drift silently with an engine update; Godot's own SphereMesh is
	/// allowed to carry its degenerate pole triangles (128 of them at the default segment count —
	/// this build does not reproduce them), everything else is held to the strict rule.
	/// </summary>
	private static void TestWindingAgainstGodot()
	{
		BoxMesh box = new BoxMesh();
		box.Size = new Vector3(4f, 1f, 2f);
		PlaneMesh plane = new PlaneMesh();
		SphereMesh sphere = new SphereMesh();
		sphere.Radius = 1f;
		sphere.Height = 2f;
		CylinderMesh cylinder = new CylinderMesh();
		int referenceTriangles;
		Check("winding: Godot's own BoxMesh obeys the clockwise-front convention",
			AllTrianglesFrontFacing(box, out referenceTriangles) && referenceTriangles == 12);
		Check("winding: Godot's PlaneMesh and CylinderMesh obey it strictly too",
			AllTrianglesFrontFacing(plane, out _) && AllTrianglesFrontFacing(cylinder, out _));
		Check("winding: Godot's SphereMesh obeys it up to its degenerate pole triangles",
			AllTrianglesFrontFacing(sphere, out referenceTriangles, true)
			&& DegenerateTriangles(sphere) == 128 && referenceTriangles == 4224);

		int triangles;
		Check("winding: the block matches it on all 12 triangles",
			AllTrianglesFrontFacing(PartMeshFactory.CreateBlock(new Vector3(4f, 1f, 2f)), out triangles)
			&& triangles == 12);
		Check("winding: the sphere matches it on every triangle",
			AllTrianglesFrontFacing(PartMeshFactory.CreateSphere(new Vector3(1.5f, 2.5f, 1.5f), 12, 6), out triangles)
			&& triangles == 2 * 12 * 5);
		Check("winding: the cylinder matches it on every triangle",
			AllTrianglesFrontFacing(PartMeshFactory.CreateCylinder(new Vector3(2f, 1f, 2f), 12), out triangles)
			&& triangles == 4 * 12);
		Check("winding: the wedge matches it on all 8 triangles",
			AllTrianglesFrontFacing(PartMeshFactory.CreateWedge(new Vector3(4f, 2f, 6f)), out triangles)
			&& triangles == 8);
		Check("winding: our pole fans are real triangles, with no degenerates to inherit",
			DegenerateTriangles(PartMeshFactory.CreateSphere(new Vector3(2f, 2f, 2f), 16, 8)) == 0);
	}

	/// <summary>True when every triangle's right-hand-rule normal opposes its stored outward
	/// normal — the measured form of Godot's clockwise front faces. With
	/// <paramref name="allowDegenerate"/> a zero-area triangle passes, which is how Godot's own
	/// sphere's collapsed pole quads are excused.</summary>
	private static bool AllTrianglesFrontFacing(Mesh mesh, out int triangleCount, bool allowDegenerate = false)
	{
		SurfaceData data = SurfaceData.Read(mesh);
		triangleCount = data.TriangleCount;
		for (int t = 0; t < data.TriangleCount; t++)
		{
			Vector3 a = data.Positions[data.Indices[t * 3]];
			Vector3 b = data.Positions[data.Indices[t * 3 + 1]];
			Vector3 c = data.Positions[data.Indices[t * 3 + 2]];
			Vector3 n = data.Normals[data.Indices[t * 3]];
			float dot = (b - a).Cross(c - a).Dot(n);
			if (dot < 0f) continue;
			if (allowDegenerate && Mathf.Abs(dot) <= 1e-9f) continue;
			return false;
		}
		return true;
	}

	/// <summary>How many triangles have zero area (their right-hand-rule normal vanishes).</summary>
	private static int DegenerateTriangles(Mesh mesh)
	{
		SurfaceData data = SurfaceData.Read(mesh);
		int count = 0;
		for (int t = 0; t < data.TriangleCount; t++)
		{
			Vector3 a = data.Positions[data.Indices[t * 3]];
			Vector3 b = data.Positions[data.Indices[t * 3 + 1]];
			Vector3 c = data.Positions[data.Indices[t * 3 + 2]];
			if ((b - a).Cross(c - a).LengthSquared() <= 1e-9f) count++;
		}
		return count;
	}

	// --- Tangents ---------------------------------------------------------------------------

	/// <summary>
	/// Tangents exist for every vertex, are unit length and perpendicular to the normal, and all
	/// carry binormal sign w = +1 — the value every Godot primitive carries (measured: BoxMesh,
	/// PlaneMesh, SphereMesh and CylinderMesh all read back w = +1 on every vertex). That is the
	/// tripwire for the orientation contract: the whole UV table is built so no face's texture is
	/// mirrored relative to any other, and a single flipped axis would show up as a -1 somewhere.
	/// </summary>
	private static void TestTangents()
	{
		Check("tangents: the block's are well formed and unmirrored",
			TangentsWellFormed(PartMeshFactory.CreateBlock(new Vector3(4f, 1f, 2f))));
		Check("tangents: the sphere's are well formed and unmirrored",
			TangentsWellFormed(PartMeshFactory.CreateSphere(new Vector3(2f, 2f, 2f), 16, 8)));
		Check("tangents: the cylinder's are well formed and unmirrored",
			TangentsWellFormed(PartMeshFactory.CreateCylinder(new Vector3(2f, 3f, 2f), 16)));
		Check("tangents: the wedge's are well formed and unmirrored",
			TangentsWellFormed(PartMeshFactory.CreateWedge(new Vector3(4f, 2f, 6f))));

		// The block's +Z face U runs along +X, so its tangent must too (which is what makes an
		// image's left edge sit at the face's left edge).
		SurfaceData data = SurfaceData.Read(PartMeshFactory.CreateBlock(new Vector3(4f, 1f, 2f)));
		int index = data.FindVertex(new Vector3(-2f, 0.5f, 1f), Vector3.Back);
		Check("tangents: the block's +Z tangent runs along +X",
			index >= 0 && Near(data.TangentOf(index), Vector3.Right, 1e-3f));
	}

	/// <summary>Every vertex's tangent: unit length, perpendicular to the normal, w = +1.</summary>
	private static bool TangentsWellFormed(Mesh mesh)
	{
		SurfaceData data = SurfaceData.Read(mesh);
		if (data.Tangents.Length != data.Positions.Length * 4) return false;
		for (int i = 0; i < data.Positions.Length; i++)
		{
			if (!Near(data.TangentOf(i).Length(), 1f, 1e-3f)) return false;
			if (Mathf.Abs(data.TangentOf(i).Dot(data.Normals[i])) > 1e-2f) return false;
			if (Mathf.Abs(data.Tangents[i * 4 + 3] - 1f) > 1e-3f) return false;
		}
		return true;
	}

	// --- Degenerate input -------------------------------------------------------------------

	/// <summary>Zero, negative and non-finite sizes and tiling factors degrade to sane geometry:
	/// a mirrored size is a caller's transform, not a mirrored mesh, and nothing may ever produce
	/// NaN vertices.</summary>
	private static void TestSanitization()
	{
		SurfaceData negative = SurfaceData.Read(PartMeshFactory.CreateBlock(new Vector3(-4f, 1f, 2f)));
		Check("sanitize: a negative size builds the positive geometry",
			Near(negative.AabbSize(), new Vector3(4f, 1f, 2f), 1e-3f)
			&& negative.HasUv(new Vector3(2f, 0.5f, 1f), new Vector2(4f, 2f))
			&& negative.AllFinite());

		SurfaceData zero = SurfaceData.Read(PartMeshFactory.CreateBlock(Vector3.Zero));
		Check("sanitize: a zero size clamps to the minimum instead of collapsing",
			Near(zero.AabbSize(), new Vector3(PartMeshFactory.MinSize, PartMeshFactory.MinSize, PartMeshFactory.MinSize), 1e-5f)
			&& zero.AllFinite());

		SurfaceData nan = SurfaceData.Read(PartMeshFactory.CreateBlock(new Vector3(float.NaN, 1f, 1f)));
		Check("sanitize: a non-finite size never poisons the mesh",
			nan.AllFinite() && nan.AabbSize().X >= PartMeshFactory.MinSize);

		SurfaceData clamped = SurfaceData.Read(PartMeshFactory.CreateSphere(new Vector3(1f, 1f, 1f), 1, 0));
		Check("sanitize: segment and ring counts clamp to a valid sphere",
			clamped.Positions.Length == 3 * 4 - 2 && clamped.TriangleCount == 2 * 3 * 1
			&& clamped.AllFinite() && AllTrianglesFrontFacing(PartMeshFactory.CreateSphere(new Vector3(1f, 1f, 1f), 1, 0), out _));

		SurfaceData clampedCylinder = SurfaceData.Read(PartMeshFactory.CreateCylinder(new Vector3(1f, 1f, 1f), 1));
		Check("sanitize: the cylinder's segment count clamps too",
			clampedCylinder.Positions.Length == 4 * 3 + 6 && clampedCylinder.AllFinite());

		SurfaceData tiles = SurfaceData.Read(PartMeshFactory.CreateBlock(new Vector3(1f, 1f, 1f), -2f));
		Check("sanitize: a negative tiling factor behaves as its positive value",
			tiles.HasUv(new Vector3(0.5f, 0.5f, 0.5f), new Vector2(2f, 2f)));
		SurfaceData zeroTiles = SurfaceData.Read(PartMeshFactory.CreateBlock(new Vector3(1f, 1f, 1f), 0f));
		Check("sanitize: a zero tiling factor clamps up instead of flattening every UV",
			zeroTiles.HasUv(new Vector3(0.5f, 0.5f, 0.5f),
				new Vector2(PartMeshFactory.MinTilesPerUnit, PartMeshFactory.MinTilesPerUnit)));
	}

	// --- The resize helper and its bounded cache ---------------------------------------------

	/// <summary>The mesh-sharing contract of <see cref="PartMeshFactory.UpdatePartMesh"/>: equal
	/// (shape, rounded size, tiles) reuse one mesh, different inputs do not, the node's own scale
	/// is never touched, and the cache stays bounded.</summary>
	private static void TestCacheAndUpdate()
	{
		PartMeshFactory.ClearMeshCache();

		MeshInstance3D first = new MeshInstance3D();
		MeshInstance3D second = new MeshInstance3D();
		PartMeshFactory.UpdatePartMesh(first, PartShape.Block, new Vector3(3f, 1f, 2f));
		PartMeshFactory.UpdatePartMesh(second, PartShape.Block, new Vector3(3f, 1f, 2f));
		Check("cache: an identical (shape, size) reuses the same mesh",
			first.Mesh != null && second.Mesh != null
			&& first.Mesh.GetInstanceId() == second.Mesh.GetInstanceId());

		// Rounded to three decimals: 3.0002 and 3.0004 are one entry, 3.0006 is not.
		MeshInstance3D rounded = new MeshInstance3D();
		PartMeshFactory.UpdatePartMesh(rounded, PartShape.Block, new Vector3(3.0002f, 1f, 2f));
		Check("cache: sizes within the 3-decimal rounding share their mesh",
			rounded.Mesh.GetInstanceId() == first.Mesh.GetInstanceId());
		PartMeshFactory.UpdatePartMesh(rounded, PartShape.Block, new Vector3(3.0006f, 1f, 2f));
		Check("cache: sizes beyond the rounding do not",
			rounded.Mesh.GetInstanceId() != first.Mesh.GetInstanceId());

		PartMeshFactory.UpdatePartMesh(rounded, PartShape.Wedge, new Vector3(3f, 1f, 2f));
		Check("cache: the shape separates entries", rounded.Mesh.GetInstanceId() != first.Mesh.GetInstanceId());
		PartMeshFactory.UpdatePartMesh(rounded, PartShape.Block, new Vector3(3f, 1f, 2f), 4f);
		Check("cache: the tiling factor separates entries", rounded.Mesh.GetInstanceId() != first.Mesh.GetInstanceId());

		// A mirrored size is the same cache entry as its positive form (the mesh is generated
		// from the sanitized size).
		PartMeshFactory.UpdatePartMesh(rounded, PartShape.Block, new Vector3(-3f, 1f, 2f));
		Check("cache: a negative size maps onto its positive entry",
			rounded.Mesh.GetInstanceId() == first.Mesh.GetInstanceId());

		Check("update: the node's own scale is left at 1 (the mesh carries the size)",
			first.Scale == Vector3.One && rounded.Scale == Vector3.One);

		PartMeshFactory.UpdatePartMesh(null, PartShape.Block, Vector3.One);
		Check("update: a null instance is a safe no-op", true);

		// The cache is bounded: after capacity + 1 distinct sizes the oldest entry is gone (its
		// mesh is rebuilt rather than shared) while the newest is still shared.
		PartMeshFactory.ClearMeshCache();
		ulong firstId = 0;
		for (int i = 0; i < PartMeshFactory.MeshCacheCapacity + 1; i++)
		{
			PartMeshFactory.UpdatePartMesh(first, PartShape.Block, new Vector3(i + 1f, 1f, 1f));
			if (i == 0) firstId = first.Mesh.GetInstanceId();
		}
		PartMeshFactory.UpdatePartMesh(second, PartShape.Block, new Vector3(1f, 1f, 1f));
		Check("cache: the oldest entry is evicted once the capacity is passed",
			second.Mesh.GetInstanceId() != firstId);

		ulong beforeClear = second.Mesh.GetInstanceId();
		PartMeshFactory.ClearMeshCache();
		PartMeshFactory.UpdatePartMesh(second, PartShape.Block, new Vector3(1f, 1f, 1f));
		Check("cache: clearing regenerates on the next request", second.Mesh.GetInstanceId() != beforeClear);

		first.Free();
		second.Free();
		rounded.Free();
	}

	// --- Helpers ----------------------------------------------------------------------------

	/// <summary>One surface's arrays, read through Mesh.SurfaceGetArrays — the same path the
	/// checks verify, so what they assert is what a renderer sees. A corner position legitimately
	/// carries one vertex per adjacent face (each with its own normal and UV), which is why the
	/// lookups below search for a matching vertex rather than indexing by position.</summary>
	private sealed class SurfaceData
	{
		public Mesh Source;
		public Vector3[] Positions;
		public Vector3[] Normals;
		public Vector2[] Uvs;
		public float[] Tangents;
		public int[] Indices;

		public int TriangleCount { get { return Indices.Length / 3; } }

		public static SurfaceData Read(Mesh mesh)
		{
			Godot.Collections.Array arrays = mesh.SurfaceGetArrays(0);
			SurfaceData data = new SurfaceData();
			data.Source = mesh;
			data.Positions = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
			data.Normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
			data.Uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
			data.Tangents = arrays[(int)Mesh.ArrayType.Tangent].AsFloat32Array();
			data.Indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
			return data;
		}

		public Vector3 AabbSize()
		{
			return Source.GetAabb().Size;
		}

		/// <summary>The first vertex within tolerance of both the position and the normal.</summary>
		public int FindVertex(Vector3 position, Vector3 normal, float tolerance = 1e-3f)
		{
			for (int i = 0; i < Positions.Length; i++)
			{
				if (Positions[i].DistanceTo(position) <= tolerance && Normals[i].DistanceTo(normal) <= tolerance)
					return i;
			}
			return -1;
		}

		/// <summary>True when some vertex near the position carries the expected UV — true even
		/// when the same position also carries vertices for neighbouring faces.</summary>
		public bool HasUv(Vector3 position, Vector2 expected, float tolerance = 1e-4f)
		{
			for (int i = 0; i < Positions.Length; i++)
			{
				if (Positions[i].DistanceTo(position) <= 1e-3f && Uvs[i].DistanceTo(expected) <= tolerance)
					return true;
			}
			return false;
		}

		/// <summary>As above, disambiguated by normal so a shared rim position's side and cap
		/// vertices can be told apart.</summary>
		public bool HasUv(Vector3 position, Vector3 normal, Vector2 expected, float tolerance = 1e-4f)
		{
			for (int i = 0; i < Positions.Length; i++)
			{
				if (Positions[i].DistanceTo(position) <= 1e-3f && Normals[i].DistanceTo(normal) <= 1e-3f
					&& Uvs[i].DistanceTo(expected) <= tolerance)
					return true;
			}
			return false;
		}

		/// <summary>True when some vertex near the position carries the expected V (the image's
		/// vertical axis), whatever its U — the "up points up" probe.</summary>
		public bool HasUvWithV(Vector3 position, float expectedV, float tolerance = 1e-4f)
		{
			for (int i = 0; i < Positions.Length; i++)
			{
				if (Positions[i].DistanceTo(position) <= 1e-3f && Mathf.Abs(Uvs[i].Y - expectedV) <= tolerance)
					return true;
			}
			return false;
		}

		/// <summary>True when some vertex at the position carries the expected normal.</summary>
		public bool HasNormal(Vector3 position, Vector3 expected, float tolerance = 1e-4f)
		{
			for (int i = 0; i < Positions.Length; i++)
			{
				if (Positions[i].DistanceTo(position) <= 1e-3f && Normals[i].DistanceTo(expected) <= tolerance)
					return true;
			}
			return false;
		}

		/// <summary>How many vertices sit at the position carrying the normal (a rim position has
		/// one per adjacent surface; a flat face's four corners have one each).</summary>
		public int CountAt(Vector3 position, Vector3 normal, float tolerance = 1e-4f)
		{
			int count = 0;
			for (int i = 0; i < Positions.Length; i++)
			{
				if (Positions[i].DistanceTo(position) <= tolerance && Normals[i].DistanceTo(normal) <= tolerance)
					count++;
			}
			return count;
		}

		/// <summary>How many vertices carry the normal, wherever they sit — the flat-face test
		/// for a whole surface.</summary>
		public int CountWithNormal(Vector3 normal, float tolerance = 1e-4f)
		{
			int count = 0;
			for (int i = 0; i < Positions.Length; i++)
			{
				if (Normals[i].DistanceTo(normal) <= tolerance) count++;
			}
			return count;
		}

		/// <summary>True when every position, normal, UV and tangent component is finite.</summary>
		public bool AllFinite()
		{
			for (int i = 0; i < Positions.Length; i++)
			{
				if (!Positions[i].IsFinite() || !Normals[i].IsFinite() || !Uvs[i].IsFinite()) return false;
			}
			for (int i = 0; i < Tangents.Length; i++)
			{
				if (!float.IsFinite(Tangents[i])) return false;
			}
			return true;
		}

		/// <summary>The UV distance between two vertices, found by position — the "1 UV = 1
		/// world unit" measurement, or -1 when either position is absent.</summary>
		public float UvDistance(Vector3 from, Vector3 to)
		{
			int a = FindAnyVertex(from);
			int b = FindAnyVertex(to);
			return a >= 0 && b >= 0 ? Uvs[a].DistanceTo(Uvs[b]) : -1f;
		}

		/// <summary>As above, with the normals naming which face's vertex to measure (a corner
		/// position carries one per adjacent face).</summary>
		public float UvDistance(Vector3 from, Vector3 fromNormal, Vector3 to, Vector3 toNormal)
		{
			int a = FindVertex(from, fromNormal);
			int b = FindVertex(to, toNormal);
			return a >= 0 && b >= 0 ? Uvs[a].DistanceTo(Uvs[b]) : -1f;
		}

		public Vector3 TangentOf(int vertexIndex)
		{
			return new Vector3(Tangents[vertexIndex * 4], Tangents[vertexIndex * 4 + 1],
				Tangents[vertexIndex * 4 + 2]);
		}

		private int FindAnyVertex(Vector3 position, float tolerance = 1e-3f)
		{
			for (int i = 0; i < Positions.Length; i++)
			{
				if (Positions[i].DistanceTo(position) <= tolerance) return i;
			}
			return -1;
		}
	}

	private static void Check(string label, bool ok)
	{
		_checks++;
		if (!ok)
		{
			_failures++;
			GD.Print("[PartMeshSelfTest] FAIL  " + label);
		}
	}

	private static bool Near(float a, float b, float tolerance = 1e-5f)
	{
		return Mathf.Abs(a - b) <= tolerance;
	}

	private static bool Near(Vector2 a, Vector2 b, float tolerance = 1e-5f)
	{
		return a.DistanceTo(b) <= tolerance;
	}

	private static bool Near(Vector3 a, Vector3 b, float tolerance = 1e-4f)
	{
		return a.DistanceTo(b) <= tolerance;
	}
}
