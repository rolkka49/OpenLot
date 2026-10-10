using Godot;
using System.Collections.Generic;

/// <summary>
/// The primitive shapes <see cref="PartMeshFactory"/> can build. Deliberately its own vocabulary
/// rather than <see cref="LotObjectKind"/>'s: a kind names a *scene object* (a part, a decal, a
/// capsule), a shape names the *geometry* to generate. The two meet wherever a caller maps a
/// part's kind onto its mesh, and that mapping is the caller's business.
/// </summary>
public enum PartShape
{
	Block,
	Sphere,
	Cylinder,
	Wedge
}

/// <summary>
/// How a generated mesh lays its UVs out. <see cref="WorldUnits"/> is the default and the reason
/// this factory exists: 1.0 UV = 1 world unit of part size, so a texture keeps a fixed density on
/// any part size. <see cref="FaceStretch"/> maps each face (each side, cap and slope) to the full
/// 0..1 image instead — the look Godot's own primitives gave OpenLot's pre-migration parts, kept
/// as a per-part choice so an existing textured lot can render exactly as it was saved.
/// </summary>
public enum MeshUvMode
{
	WorldUnits,
	FaceStretch
}

/// <summary>
/// Procedural part meshes with **world-unit UVs**: 1.0 UV = 1 world unit of part size (times
/// <c>tilesPerUnit</c>), so a texture tiles at a fixed density on any part size — a 4x1x2 block's
/// top face spans 4 x 2 in UV, its front face 4 x 1. Plain mesh generation through
/// <see cref="SurfaceTool"/> — no shaders, no triplanar sampling. A caller that wants the
/// pre-migration stretch-to-fit look asks for <see cref="MeshUvMode.FaceStretch"/>, which maps
/// every face to 0..1 instead.
///
/// Conventions this file exists to keep straight (each one is pinned by PartMeshSelfTest):
///
///   * **Geometry is centred on the origin** at the requested extents, exactly like the built-in
///     unit primitives the builder shipped with, so a part's position math is unchanged.
///
///   * **UV orientation**: the per-face UV basis is <see cref="DecalMath"/>'s face basis with v
///     negated. DecalMath orders its axes so u x v == normal (a proper rotation, used for patch
///     placement); Godot samples V=0 at the *image top* and V increasing downward, so mapping
///     DecalMath's v straight through would show every side face upside down. Negating it gives
///     u x v == -normal on every face, and that is what makes an image read **upright and
///     unmirrored** from outside every face — the same result Godot's own BoxMesh / SphereMesh /
///     CylinderMesh produce (verified against them empirically, not assumed). A visible
///     consequence: generated tangents carry binormal sign w = +1 on every vertex, the same value
///     Godot's own primitives carry, which the self-test pins as the "no mirrored UVs" tripwire.
///
///   * **Winding**: clockwise seen from outside, like every Godot primitive. The measured rule
///     (all 4224 of Godot's own SphereMesh triangles satisfy it) is: a triangle front-facing
///     outward has (b-a) x (c-a) pointing *opposite* its outward normal. The quad helper below
///     orders corners (u0,v0) (u1,v0) (u1,v1) (u0,v1) to satisfy that by construction.
///
///   * **Normals**: flat per face for block and wedge (no vertex is shared between faces that
///     need different normals or UVs), smooth around the sides of sphere and cylinder, flat on
///     the cylinder caps. Tangents come from <see cref="SurfaceTool.GenerateTangents"/>.
///
///   * **Resizing regenerates the mesh** — <see cref="UpdatePartMesh"/> swaps a new mesh in
///     rather than scaling the node; a part sized this way keeps Node3D scale 1.
///
/// Godot's own size conventions are followed where they exist: sphere/cylinder size.X and size.Z
/// are the diameters (their average is used if they differ, so an elliptical part stretches
/// rather than breaks) and size.Y is the height; the wedge is a right-angle ramp whose vertical
/// wall faces -Z and whose slope descends toward +Z.
/// </summary>
public static class PartMeshFactory
{
	/// <summary>Below this an axis is treated as this value, so a degenerate size can never
	/// produce NaN normals (the same floor the gizmo's scale clamp uses).</summary>
	public const float MinSize = 1e-4f;

	/// <summary>Upper sanity bound on an axis, mostly so the cache key stays far inside int
	/// range after the x1000 rounding. 100 km is far past any lot.</summary>
	public const float MaxSize = 1e5f;

	/// <summary>Below this a tiling factor is treated as this value. Non-finite and negative
	/// values fall back to the positive value: a negative factor would mirror the UVs, which is
	/// exactly what this file's orientation rules exist to prevent.</summary>
	public const float MinTilesPerUnit = 1e-4f;

	/// <summary>Fewer segments than this and a circle is not a circle.</summary>
	public const int MinRadialSegments = 3;

	/// <summary>Fewer rings than this and a sphere is a cone.</summary>
	public const int MinRings = 2;

	public const int DefaultRadialSegments = 32;
	public const int DefaultRings = 16;

	/// <summary>
	/// How many meshes the resize cache keeps. Bounded on purpose (a gizmo drag can walk through
	/// hundreds of distinct sizes): once full, the oldest entry is dropped and gets rebuilt if it
	/// is asked for again. Small enough that the memory cost stays trivial, large enough to cover
	/// a whole part's undo/redo history.
	/// </summary>
	public const int MeshCacheCapacity = 64;

	/// <summary>
	/// The one-call front door for callers that already hold a <see cref="PartShape"/>: dispatches
	/// to the matching generator with the same defaults.
	/// </summary>
	public static ArrayMesh Create(PartShape shape, Vector3 size, float tilesPerUnit = 1f,
		MeshUvMode uvMode = MeshUvMode.WorldUnits)
	{
		switch (shape)
		{
			case PartShape.Sphere: return CreateSphere(size, DefaultRadialSegments, DefaultRings, tilesPerUnit, uvMode);
			case PartShape.Cylinder: return CreateCylinder(size, DefaultRadialSegments, tilesPerUnit, uvMode);
			case PartShape.Wedge: return CreateWedge(size, tilesPerUnit, uvMode);
			default: return CreateBlock(size, tilesPerUnit, uvMode);
		}
	}

	/// <summary>
	/// A box. Six flat-shaded faces, 24 vertices (four per face — nothing is shared across faces,
	/// because every face needs its own normal and its own UV patch). Each face's UVs run
	/// 0..extent x tilesPerUnit from the face's bottom-left-when-viewed-from-outside corner in
	/// that face's u/v axes, so a 4x1x2 block's top face spans 0..4 by 0..2 and its front face
	/// 0..4 by 0..1. <see cref="MeshUvMode.FaceStretch"/> spans every face 0..1 instead.
	/// </summary>
	public static ArrayMesh CreateBlock(Vector3 size, float tilesPerUnit = 1f,
		MeshUvMode uvMode = MeshUvMode.WorldUnits)
	{
		Vector3 s = SanitizeSize(size);
		float t = SanitizeTiles(tilesPerUnit);
		Vector3 h = s * 0.5f;

		SurfaceTool st = BeginTriangles();

		// Each call: face centre, U and V world axes (U x V == -normal — see the class docs),
		// the extents along U and V, and the outward normal. +Z/-Z/+X/-X keep V pointing down
		// the face (image up = up); +Y/-Y use the planar X/Z axes Godot's cylinder caps use.
		AddQuadFace(st, new Vector3(0f, 0f, h.Z), Vector3.Right, Vector3.Down, s.X, s.Y, Vector3.Back, t, uvMode);
		AddQuadFace(st, new Vector3(0f, 0f, -h.Z), Vector3.Left, Vector3.Down, s.X, s.Y, Vector3.Forward, t, uvMode);
		AddQuadFace(st, new Vector3(h.X, 0f, 0f), Vector3.Forward, Vector3.Down, s.Z, s.Y, Vector3.Right, t, uvMode);
		AddQuadFace(st, new Vector3(-h.X, 0f, 0f), Vector3.Back, Vector3.Down, s.Z, s.Y, Vector3.Left, t, uvMode);
		AddQuadFace(st, new Vector3(0f, h.Y, 0f), Vector3.Right, Vector3.Back, s.X, s.Z, Vector3.Up, t, uvMode);
		AddQuadFace(st, new Vector3(0f, -h.Y, 0f), Vector3.Right, Vector3.Forward, s.X, s.Z, Vector3.Down, t, uvMode);

		return Finish(st);
	}

	/// <summary>
	/// A right-angle ramp: size.X wide, size.Y tall at the back, size.Z deep. The vertical wall is
	/// the -Z face, the slope descends from its top edge toward the +Z bottom edge, and the two
	/// side faces are the right triangles. Everything except the slope is shaded like the block's
	/// matching face; the slope's UVs use its real edge lengths (width x slant), so a texture
	/// keeps its density across the ramp. <see cref="MeshUvMode.FaceStretch"/> spans every face
	/// 0..1 instead.
	/// </summary>
	public static ArrayMesh CreateWedge(Vector3 size, float tilesPerUnit = 1f,
		MeshUvMode uvMode = MeshUvMode.WorldUnits)
	{
		Vector3 s = SanitizeSize(size);
		float t = SanitizeTiles(tilesPerUnit);
		Vector3 h = s * 0.5f;

		// The slope's geometry: its surface direction is (0, -Y, +Z) (down toward the viewer) and
		// its outward normal (0, +Z, +Y) — perpendicular to that, pointing up and forward. The
		// slant is the hypotenuse the UVs measure along.
		float slant = Mathf.Sqrt(s.Y * s.Y + s.Z * s.Z);
		Vector3 slopeNormal = new Vector3(0f, s.Z, s.Y) / slant;
		Vector3 slopeV = new Vector3(0f, -s.Y, s.Z) / slant;

		SurfaceTool st = BeginTriangles();

		// Bottom and back wall: identical bases to the block's -Y and -Z faces, so textures agree
		// between a wedge and a block. The slope plane passes through the origin, so its face
		// centre is the origin; its v runs from the top edge (v = 0, image top) down the ramp.
		AddQuadFace(st, new Vector3(0f, -h.Y, 0f), Vector3.Right, Vector3.Forward, s.X, s.Z, Vector3.Down, t, uvMode);
		AddQuadFace(st, new Vector3(0f, 0f, -h.Z), Vector3.Left, Vector3.Down, s.X, s.Y, Vector3.Forward, t, uvMode);
		AddQuadFace(st, Vector3.Zero, Vector3.Right, slopeV, s.X, slant, slopeNormal, t, uvMode);

		// The two side triangles follow the block's +X and -X face bases (U = -Z / +Z, V = down),
		// which lands their UVs on the real edge lengths: the vertical leg spans size.Y, the
		// horizontal leg size.Z, and the hypotenuse the same slant as the slope. FaceStretch
		// spans the same triangle over 0..1 instead.
		float uvZ = uvMode == MeshUvMode.FaceStretch ? 1f : s.Z * t;
		float uvY = uvMode == MeshUvMode.FaceStretch ? 1f : s.Y * t;
		AddTriangle(st, new Vector3(-h.X, -h.Y, -h.Z), new Vector3(-h.X, h.Y, -h.Z), new Vector3(-h.X, -h.Y, h.Z),
			Vector3.Left,
			new Vector2(0f, uvY), new Vector2(0f, 0f), new Vector2(uvZ, uvY));
		AddTriangle(st, new Vector3(h.X, -h.Y, -h.Z), new Vector3(h.X, -h.Y, h.Z), new Vector3(h.X, h.Y, -h.Z),
			Vector3.Right,
			new Vector2(uvZ, uvY), new Vector2(0f, uvY), new Vector2(uvZ, 0f));

		return Finish(st);
	}

	/// <summary>
	/// A (possibly stretched) sphere. Smooth normals everywhere (the ellipsoid gradient, which on
	/// a round sphere is just the radial direction), U running once around the equator scaled by
	/// its circumference starting at +Z and turning toward +X (Godot's own SphereMesh starts and
	/// turns the same way), V running pole to pole scaled by the vertical radius. Each pole
	/// duplicates one vertex per segment so the wedge triangles around it keep exact U values, and
	/// the seam column is duplicated for the same reason. Each fan triangle uses its wedge's
	/// first-edge copy, so the wrap's last pole copy is never referenced and plays no part in the
	/// committed mesh — deliberately: adding Godot-SphereMesh-style degenerate pole triangles to
	/// reference it would spend indices on zero-area geometry for no visual gain. The cost of the
	/// whole scheme is the usual polar stretching, accepted by design here.
	/// </summary>
	public static ArrayMesh CreateSphere(Vector3 size, int radialSegments = DefaultRadialSegments,
		int rings = DefaultRings, float tilesPerUnit = 1f, MeshUvMode uvMode = MeshUvMode.WorldUnits)
	{
		Vector3 s = SanitizeSize(size);
		float t = SanitizeTiles(tilesPerUnit);
		int segments = Mathf.Max(MinRadialSegments, radialSegments);
		int ringCount = Mathf.Max(MinRings, rings);

		float rx = s.X * 0.5f;
		float ry = s.Y * 0.5f;
		float rz = s.Z * 0.5f;

		// UV scale per radian. World units: U by the average equatorial radius (the equatorial
		// circumference is 2*PI*r, so u(phi) = phi * r_avg -> a full turn spans PI * diameter),
		// V by the vertical radius so the pole-to-pole arc is exactly half the equator
		// circumference on a round sphere and follows the stretched axis on an ellipsoid.
		// FaceStretch: one full turn of U and the whole pole-to-pole arc of V are 0..1.
		float uPerRadian;
		float vPerRadian;
		if (uvMode == MeshUvMode.FaceStretch)
		{
			uPerRadian = 1f / Mathf.Tau;
			vPerRadian = 1f / Mathf.Pi;
		}
		else
		{
			uPerRadian = (rx + rz) * 0.5f * t;
			vPerRadian = ry * t;
		}

		int stride = segments + 1;
		int rows = ringCount + 1;
		EnsureGridCapacity(rows * stride);
		Vector3[] positions = _gridPositions;
		Vector3[] normals = _gridNormals;
		Vector2[] uvs = _gridUvs;

		// Row i sits at polar angle theta = PI * i / rings (row 0 is the top pole); column j at
		// azimuth phi = TAU * j / segments measured from +Z toward +X.
		for (int i = 0; i < rows; i++)
		{
			float theta = Mathf.Pi * i / ringCount;
			float sinTheta = Mathf.Sin(theta);
			float cosTheta = Mathf.Cos(theta);
			float y = ry * cosTheta;
			float v = theta * vPerRadian;
			for (int j = 0; j < stride; j++)
			{
				float phi = Mathf.Tau * j / segments;
				float x = rx * sinTheta * Mathf.Sin(phi);
				float z = rz * sinTheta * Mathf.Cos(phi);
				int index = i * stride + j;
				positions[index] = new Vector3(x, y, z);
				// The ellipsoid's surface normal is the gradient of (x/rx)^2+(y/ry)^2+(z/rz)^2;
				// at a pole the x and z terms are zero and it reduces to +/-Y exactly.
				normals[index] = new Vector3(x / (rx * rx), y / (ry * ry), z / (rz * rz)).Normalized();
				uvs[index] = new Vector2(phi * uPerRadian, v);
			}
		}

		SurfaceTool st = BeginTriangles();
		// Quad corners in (u, v) order: c0 (u0,v0) -> c1 (u1,v0) -> c2 (u1,v1) -> c3 (u0,v1),
		// triangles (c0,c1,c2) and (c0,c2,c3). On a pole row the quad's whole edge collapses to
		// the pole point: the degenerate zero-area triangle is skipped and the remaining fan
		// triangle keeps the wedge's first-edge pole copy (c0 at the top pole, c3 at the bottom),
		// so both poles drop the same final wrap copy and the mesh contains no degenerate
		// triangles at all.
		for (int i = 0; i < ringCount; i++)
		{
			for (int j = 0; j < segments; j++)
			{
				int c0 = i * stride + j;
				int c1 = c0 + 1;
				int c2 = c1 + stride;
				int c3 = c0 + stride;
				if (i == 0)
				{
					PushTriangle(st, positions, normals, uvs, c0, c2, c3);
				}
				else if (i == ringCount - 1)
				{
					PushTriangle(st, positions, normals, uvs, c0, c1, c3);
				}
				else
				{
					PushTriangle(st, positions, normals, uvs, c0, c1, c2);
					PushTriangle(st, positions, normals, uvs, c0, c2, c3);
				}
			}
		}

		return Finish(st);
	}

	/// <summary>
	/// An upright (possibly elliptical) cylinder: smooth normals around the side, flat caps. The
	/// side's U is the circumference around (PI * diameter), starting at +Z and turning toward +X
	/// like Godot's own CylinderMesh, V is the height with 0 at the top edge. Caps use planar UVs
	/// straight from each point's cap coordinates scaled by tilesPerUnit (so a 2-wide cap spans
	/// -1..1 across), top and bottom facing opposite ways so each reads unmirrored from outside.
	/// </summary>
	public static ArrayMesh CreateCylinder(Vector3 size, int radialSegments = DefaultRadialSegments,
		float tilesPerUnit = 1f, MeshUvMode uvMode = MeshUvMode.WorldUnits)
	{
		Vector3 s = SanitizeSize(size);
		float t = SanitizeTiles(tilesPerUnit);
		int segments = Mathf.Max(MinRadialSegments, radialSegments);

		float rx = s.X * 0.5f;
		float ry = s.Y * 0.5f;
		float rz = s.Z * 0.5f;
		// World units: U at the circumference's rate (one turn spans PI * diameter) and V the
		// full height. FaceStretch: one turn of U and the full height are 0..1.
		float uPerRadian = uvMode == MeshUvMode.FaceStretch ? 1f / Mathf.Tau : (rx + rz) * 0.5f * t;
		float vHeight = uvMode == MeshUvMode.FaceStretch ? 1f : s.Y * t;

		SurfaceTool st = BeginTriangles();

		// The side: two vertex rows (top, bottom) over columns j = 0..segments, phi measured from
		// +Z toward +X. The seam column is duplicated (u = 0 and u = full circumference) so the
		// wrap stays exact; its normals are the ellipse's horizontal gradient.
		int stride = segments + 1;
		// Shared scratch grid (see EnsureGridCapacity): the side's triangles are pushed before the
		// caps run, so reusing the buffer for the cap fan afterwards is safe.
		EnsureGridCapacity(2 * stride);
		Vector3[] sidePositions = _gridPositions;
		Vector3[] sideNormals = _gridNormals;
		Vector2[] sideUvs = _gridUvs;
		for (int j = 0; j < stride; j++)
		{
			float phi = Mathf.Tau * j / segments;
			float x = rx * Mathf.Sin(phi);
			float z = rz * Mathf.Cos(phi);
			Vector3 normal = new Vector3(x / (rx * rx), 0f, z / (rz * rz)).Normalized();
			float u = phi * uPerRadian;
			sidePositions[j] = new Vector3(x, ry, z);
			sideNormals[j] = normal;
			sideUvs[j] = new Vector2(u, 0f);
			sidePositions[stride + j] = new Vector3(x, -ry, z);
			sideNormals[stride + j] = normal;
			sideUvs[stride + j] = new Vector2(u, vHeight);
		}
		for (int j = 0; j < segments; j++)
		{
			// Corners top[j] (u0,v0) -> top[j+1] (u1,v0) -> bottom[j+1] (u1,v1) -> bottom[j] (u0,v1).
			PushTriangle(st, sidePositions, sideNormals, sideUvs, j, j + 1, stride + j + 1);
			PushTriangle(st, sidePositions, sideNormals, sideUvs, j, stride + j + 1, stride + j);
		}

		// Top cap (+Y): a centre vertex plus a ring, planar UVs u = x, v = +z. The fan triangle
		// order is (centre, ring[j+1], ring[j]) — the pole-fan rule, since the centre plays the
		// collapsed-row role. The ring is rebuilt rather than reused from the side because a cap
		// vertex needs a different normal and different UVs, which is exactly why the side's ring
		// stays its own vertices.
		AddCylinderCap(st, ry, rx, rz, segments, t, true, uvMode);
		AddCylinderCap(st, -ry, rx, rz, segments, t, false, uvMode);

		return Finish(st);
	}

	/// <summary>One cylinder cap: fan triangles from a centre vertex to the rim ring. Top caps
	/// face +Y with v = +z; bottom caps face -Y with v = -z, so both read unmirrored from
	/// outside, matching Godot's own caps. FaceStretch spans the full image across the cap,
	/// centred on its centre, with the same orientation senses.</summary>
	private static void AddCylinderCap(SurfaceTool st, float y, float rx, float rz, int segments,
		float tilesPerUnit, bool top, MeshUvMode uvMode)
	{
		Vector3 normal = top ? Vector3.Up : Vector3.Down;
		float vSign = top ? 1f : -1f;

		EnsureGridCapacity(segments + 2);
		Vector3[] positions = _gridPositions;
		Vector3[] normals = _gridNormals;
		Vector2[] uvs = _gridUvs;
		positions[0] = new Vector3(0f, y, 0f);
		normals[0] = normal;
		uvs[0] = uvMode == MeshUvMode.FaceStretch ? new Vector2(0.5f, 0.5f) : Vector2.Zero;
		for (int j = 0; j <= segments; j++)
		{
			float phi = Mathf.Tau * j / segments;
			float x = rx * Mathf.Sin(phi);
			float z = rz * Mathf.Cos(phi);
			positions[j + 1] = new Vector3(x, y, z);
			normals[j + 1] = normal;
			uvs[j + 1] = uvMode == MeshUvMode.FaceStretch
				? new Vector2((x + rx) / (2f * rx), top ? (z + rz) / (2f * rz) : (rz - z) / (2f * rz))
				: new Vector2(x * tilesPerUnit, z * tilesPerUnit * vSign);
		}
		for (int j = 0; j < segments; j++)
		{
			if (top)
			{
				PushTriangle(st, positions, normals, uvs, 0, j + 2, j + 1);
			}
			else
			{
				PushTriangle(st, positions, normals, uvs, j + 1, j + 2, 0);
			}
		}
	}
	// --- Mesh assembly helpers -------------------------------------------------------------

	private static SurfaceTool BeginTriangles()
	{
		// Reused, not re-allocated: a resize drag regenerates a mesh every frame, so a fresh
		// SurfaceTool per call would be one RefCounted round-trip more per frame for no gain.
		// Safe because the factory is main-thread-only (see UpdatePartMesh) and Finish commits the
		// surface before the next caller begins — the tool is never held across frames.
		_scratchTool.Clear();
		_scratchTool.Begin(Mesh.PrimitiveType.Triangles);
		return _scratchTool;
	}

	private static readonly SurfaceTool _scratchTool = new SurfaceTool();

	// Grow-only grid buffers for the generators that walk a lat/long or ring grid. Every slot up to
	// the requested count is overwritten before it is read, so stale data past the count is never
	// read; the buffer only ever grows (a drag reuses it frame after frame without allocating).
	private static Vector3[] _gridPositions = new Vector3[0];
	private static Vector3[] _gridNormals = new Vector3[0];
	private static Vector2[] _gridUvs = new Vector2[0];

	private static void EnsureGridCapacity(int count)
	{
		if (_gridPositions.Length >= count) return;
		int capacity = Mathf.Max(count, _gridPositions.Length * 2);
		_gridPositions = new Vector3[capacity];
		_gridNormals = new Vector3[capacity];
		_gridUvs = new Vector2[capacity];
	}

	/// <summary>Pushes one vertex with its attributes. SurfaceTool is a triangle soup until
	/// <see cref="Finish"/> calls <c>Index()</c>, which is what merges the repeated corners of
	/// neighbouring triangles into shared vertices — so a quad pushes six vertices and comes out
	/// as four.</summary>
	private static void AddVertex(SurfaceTool st, Vector3 position, Vector3 normal, Vector2 uv)
	{
		st.SetNormal(normal);
		st.SetUV(uv);
		st.AddVertex(position);
	}

	/// <summary>One quad as two triangles, corners in (u, v) order c00 -> c10 -> c11 -> c01.
	/// That order is the winding rule: with every face basis satisfying u x v == -normal, it makes
	/// (b-a) x (c-a) oppose the outward normal on both triangles, which is Godot's clockwise
	/// front-face convention (see the class docs).</summary>
	private static void AddQuad(SurfaceTool st, Vector3 p00, Vector3 p10, Vector3 p11, Vector3 p01,
		Vector3 normal, Vector2 uv00, Vector2 uv10, Vector2 uv11, Vector2 uv01)
	{
		AddVertex(st, p00, normal, uv00);
		AddVertex(st, p10, normal, uv10);
		AddVertex(st, p11, normal, uv11);
		AddVertex(st, p00, normal, uv00);
		AddVertex(st, p11, normal, uv11);
		AddVertex(st, p01, normal, uv01);
	}

	private static void AddTriangle(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c,
		Vector3 normal, Vector2 uvA, Vector2 uvB, Vector2 uvC)
	{
		AddVertex(st, a, normal, uvA);
		AddVertex(st, b, normal, uvB);
		AddVertex(st, c, normal, uvC);
	}

	/// <summary>Pushes a triangle whose vertices are already computed in shared arrays, in the
	/// given corner order (the caller is responsible for the winding rule).</summary>
	private static void PushTriangle(SurfaceTool st, Vector3[] positions, Vector3[] normals, Vector2[] uvs,
		int a, int b, int c)
	{
		AddVertex(st, positions[a], normals[a], uvs[a]);
		AddVertex(st, positions[b], normals[b], uvs[b]);
		AddVertex(st, positions[c], normals[c], uvs[c]);
	}

	/// <summary>A flat quad face: centred on <paramref name="centre"/>, spanning the given extents
	/// along the in-plane axes <paramref name="u"/> and <paramref name="v"/>, facing
	/// <paramref name="normal"/>. In WorldUnits mode UVs run 0..extent x tilesPerUnit from the
	/// (u0, v0) corner, so every face's texture is anchored at its
	/// bottom-left-when-viewed-from-outside corner; in FaceStretch mode the face spans 0..1.</summary>
	private static void AddQuadFace(SurfaceTool st, Vector3 centre, Vector3 u, Vector3 v,
		float extentU, float extentV, Vector3 normal, float tilesPerUnit, MeshUvMode uvMode)
	{
		Vector3 uHalf = u * (extentU * 0.5f);
		Vector3 vHalf = v * (extentV * 0.5f);
		Vector2 uvU;
		Vector2 uvV;
		if (uvMode == MeshUvMode.FaceStretch)
		{
			uvU = new Vector2(1f, 0f);
			uvV = new Vector2(0f, 1f);
		}
		else
		{
			uvU = new Vector2(extentU * tilesPerUnit, 0f);
			uvV = new Vector2(0f, extentV * tilesPerUnit);
		}
		AddQuad(st,
			centre - uHalf - vHalf,
			centre + uHalf - vHalf,
			centre + uHalf + vHalf,
			centre - uHalf + vHalf,
			normal,
			Vector2.Zero, uvU, uvU + uvV, uvV);
	}

	/// <summary>Indexes the pushed triangle soup, generates tangents and commits. Index() after
	/// all vertices are added is the SurfaceTool contract; GenerateTangents needs the surface to
	/// already carry normals and UVs, which every path here does.</summary>
	private static ArrayMesh Finish(SurfaceTool st)
	{
		st.Index();
		st.GenerateTangents();
		return st.Commit();
	}

	// --- Resize helper + bounded mesh cache ------------------------------------------------

	private static readonly Dictionary<(PartShape Shape, int X, int Y, int Z, int Tiles, int Mode), ArrayMesh> _meshCache
		= new Dictionary<(PartShape, int, int, int, int, int), ArrayMesh>();
	private static readonly Queue<(PartShape Shape, int X, int Y, int Z, int Tiles, int Mode)> _meshCacheOrder
		= new Queue<(PartShape, int, int, int, int, int)>();

	/// <summary>
	/// Sizes a part by swapping in a regenerated mesh — never by scaling its node. <c>size</c> is
	/// the full extent the part should occupy, and the mesh is built at exactly that size with
	/// world-unit UVs, so a resize keeps the texture density fixed. The instance's own transform
	/// (scale included) is deliberately left alone: a part sized this way keeps scale 1, and
	/// anything else on the node stays untouched.
	///
	/// Meshes are shared from a bounded cache keyed by (shape, size, tiles, UV mode) with the size
	/// rounded to three decimals — a resize back to a size used moments ago (an undo, a drag
	/// oscillation, a second part of the same size) reuses the mesh instead of regenerating it.
	/// Cached meshes are owned by the cache and shared between the nodes using them: treat them as
	/// read-only and do not free them. The cache is bounded and main-thread only, like the rest of
	/// the lot layer.
	/// </summary>
	public static void UpdatePartMesh(MeshInstance3D instance, PartShape shape, Vector3 size,
		float tilesPerUnit = 1f, MeshUvMode uvMode = MeshUvMode.WorldUnits)
	{
		if (instance == null || !GodotObject.IsInstanceValid(instance)) return;

		(PartShape, int, int, int, int, int) key = CacheKey(shape, size, tilesPerUnit, uvMode);
		if (!_meshCache.TryGetValue(key, out ArrayMesh mesh))
		{
			mesh = Create(shape, size, tilesPerUnit, uvMode);
			_meshCache[key] = mesh;
			_meshCacheOrder.Enqueue(key);
			// Drop-oldest, so a long drag's worth of distinct sizes cannot grow the cache past its
			// bound (and the sizes a creator is actually working with stay in it). Every cached
			// key has exactly one queue entry, so the dequeued key is always the oldest insert.
			if (_meshCache.Count > MeshCacheCapacity) _meshCache.Remove(_meshCacheOrder.Dequeue());
		}
		instance.Mesh = mesh;
	}

	/// <summary>Drops every cached mesh. Meshes still assigned to nodes stay alive through those
	/// nodes; the next <see cref="UpdatePartMesh"/> call regenerates what it needs.</summary>
	public static void ClearMeshCache()
	{
		_meshCache.Clear();
		_meshCacheOrder.Clear();
	}

	/// <summary>The size a generator will actually build at: each axis by magnitude, clamped into
	/// [<see cref="MinSize"/>, <see cref="MaxSize"/>] with non-finite values falling back to the
	/// minimum. Public so the part layer's <c>Size</c> property and the cache key cannot drift
	/// from what the mesh was built at.</summary>
	public static Vector3 ClampSize(Vector3 size)
	{
		return SanitizeSize(size);
	}

	/// <summary>The tiling factor a generator will actually use; see
	/// <see cref="MinTilesPerUnit"/>.</summary>
	public static float ClampTiles(float tilesPerUnit)
	{
		return SanitizeTiles(tilesPerUnit);
	}

	/// <summary>The cache key: the shape, each axis (sanitized, so -2 and 2 are one entry) rounded
	/// to three decimals, the tiling factor rounded the same way, and the UV mode. Two sizes that
	/// round to the same key share a mesh — the difference is under a thousandth of a unit, far
	/// below a pixel.</summary>
	private static (PartShape, int, int, int, int, int) CacheKey(PartShape shape, Vector3 size, float tilesPerUnit,
		MeshUvMode uvMode)
	{
		Vector3 s = SanitizeSize(size);
		float t = SanitizeTiles(tilesPerUnit);
		return (shape,
			Mathf.RoundToInt(s.X * 1000f),
			Mathf.RoundToInt(s.Y * 1000f),
			Mathf.RoundToInt(s.Z * 1000f),
			Mathf.RoundToInt(t * 1000f),
			(int)uvMode);
	}

	/// <summary>Sizes are used by magnitude (a mirrored part is a matter for the caller's
	/// transform, not for UV semantics) and clamped into a sane positive range, so a zero, a
	/// negative or a non-finite axis can never produce NaN normals or inverted geometry.</summary>
	private static Vector3 SanitizeSize(Vector3 size)
	{
		return new Vector3(ClampComponent(size.X), ClampComponent(size.Y), ClampComponent(size.Z));
	}

	private static float ClampComponent(float value)
	{
		if (!float.IsFinite(value)) return MinSize;
		return Mathf.Clamp(Mathf.Abs(value), MinSize, MaxSize);
	}

	/// <summary>Non-finite and negative tiling factors fall back to the positive value; see
	/// <see cref="MinTilesPerUnit"/>.</summary>
	private static float SanitizeTiles(float tilesPerUnit)
	{
		if (!float.IsFinite(tilesPerUnit)) return 1f;
		return Mathf.Max(Mathf.Abs(tilesPerUnit), MinTilesPerUnit);
	}
}

