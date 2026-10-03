using Godot;

// =============================================================================================
// Ported from Im3d (MIT License, https://github.com/john-chapman/im3d, by john-chapman).
//
// Every type and function below is a direct transliteration of the named Im3d source location and
// is kept in the same order as the original so the port can be verified by reading the two side
// by side. The deliberate deviations are:
//   * Godot's Vector3 replaces Im3d::Vec3.
//   * Im3d's Mat3/Mat4 are replaced by Godot's Basis (a separate math stack is not introduced).
//
// Im3d's whole Vec/Mat layer is skipped, but Ray/Line/LineSegment/Sphere/Plane/Capsule are not in
// Godot's math library, so they are reproduced here as plain structs. They are stack-allocated
// value types, so the per-frame picking path allocates nothing.
// =============================================================================================

/// <summary>A ray: origin + direction (Im3d Ray). Direction is not guaranteed normalized.</summary>
public struct GizmoRay
{
	public Vector3 Origin;
	public Vector3 Direction;

	public GizmoRay(Vector3 origin, Vector3 direction)
	{
		Origin = origin;
		Direction = direction;
	}
}

/// <summary>An infinite line (Im3d Line).</summary>
public struct GizmoLine
{
	public Vector3 Origin;
	public Vector3 Direction;

	public GizmoLine(Vector3 origin, Vector3 direction)
	{
		Origin = origin;
		Direction = direction;
	}
}

/// <summary>A finite line segment (Im3d LineSegment).</summary>
public struct GizmoLineSegment
{
	public Vector3 Start;
	public Vector3 End;

	public GizmoLineSegment(Vector3 start, Vector3 end)
	{
		Start = start;
		End = end;
	}
}

/// <summary>A sphere (Im3d Sphere).</summary>
public struct GizmoSphere
{
	public Vector3 Origin;
	public float Radius;

	public GizmoSphere(Vector3 origin, float radius)
	{
		Origin = origin;
		Radius = radius;
	}
}

/// <summary>A plane in the form Dot(Normal, x) == Offset (Im3d Plane).</summary>
public struct GizmoPlane
{
	public Vector3 Normal;
	public float Offset;

	public GizmoPlane(Vector3 normal, float offset)
	{
		Normal = normal;
		Offset = offset;
	}

	/// <summary>Plane through <paramref name="origin"/> with the given normal (Im3d Plane::Plane).</summary>
	public GizmoPlane(Vector3 normal, Vector3 origin)
	{
		Normal = normal;
		Offset = normal.Dot(origin);
	}
}

/// <summary>A capsule: a line segment swept by a radius (Im3d Capsule).</summary>
public struct GizmoCapsule
{
	public Vector3 Start;
	public Vector3 End;
	public float Radius;

	public GizmoCapsule(Vector3 start, Vector3 end, float radius)
	{
		Start = start;
		End = end;
		Radius = radius;
	}
}

/// <summary>
/// Im3d's free geometry functions, ported. Godot's Vector3/Basis stand in for Im3d's Vec3/Mat3.
/// </summary>
public static class GizmoMath
{
	// C#'s float.Epsilon is the smallest *denormal* (~1.4e-45), not C's FLT_EPSILON (~1.19e-7).
	// Im3d compares against FLT_EPSILON, so the C value must be spelled out explicitly — using
	// float.Epsilon here would silently change the parallel-line and degenerate-segment branches.
	private const float FloatEpsilon = 1.1920929E-07f;

	// --- Plane ---

	/// <summary>Im3d::Intersects(Ray, Plane) — im3d.cpp:3320. True when the ray faces the plane.</summary>
	public static bool Intersects(GizmoRay ray, GizmoPlane plane)
	{
		float x = plane.Normal.Dot(ray.Direction);
		return x <= 0.0f;
	}

	/// <summary>Im3d::Intersect(Ray, Plane, t) — im3d.cpp:3325. False when the hit is behind the ray.</summary>
	public static bool Intersect(GizmoRay ray, GizmoPlane plane, out float t)
	{
		t = plane.Normal.Dot(plane.Normal * plane.Offset - ray.Origin)
			/ plane.Normal.Dot(ray.Direction);
		return t >= 0.0f;
	}

	// --- Sphere ---

	/// <summary>Im3d::Intersects(Ray, Sphere) — im3d.cpp:3330.</summary>
	public static bool Intersects(GizmoRay ray, GizmoSphere sphere)
	{
		Vector3 p = sphere.Origin - ray.Origin;
		float p2 = p.LengthSquared();
		float q = p.Dot(ray.Direction);
		float r2 = sphere.Radius * sphere.Radius;
		if (q < 0.0f && p2 > r2)
		{
			return false;
		}

		return p2 - (q * q) <= r2;
	}

	/// <summary>Im3d::Intersect(Ray, Sphere, t0, t1) — im3d.cpp:3343.</summary>
	public static bool Intersect(GizmoRay ray, GizmoSphere sphere, out float t0, out float t1)
	{
		Vector3 p = sphere.Origin - ray.Origin;
		float q = p.Dot(ray.Direction);
		if (q < 0.0f)
		{
			t0 = 0.0f;
			t1 = 0.0f;
			return false;
		}

		float p2 = p.LengthSquared() - q * q;
		float r2 = sphere.Radius * sphere.Radius;
		if (p2 > r2)
		{
			t0 = 0.0f;
			t1 = 0.0f;
			return false;
		}

		float s = Mathf.Sqrt(r2 - p2);
		t0 = Mathf.Max(q - s, 0.0f);
		t1 = q + s;

		return true;
	}

	// --- Capsule ---

	/// <summary>Im3d::Intersects(Ray, Capsule) — im3d.cpp:3365.</summary>
	public static bool Intersects(GizmoRay ray, GizmoCapsule capsule)
	{
		return DistanceSquared(ray, new GizmoLineSegment(capsule.Start, capsule.End))
			< capsule.Radius * capsule.Radius;
	}

	/// <summary>
	/// Im3d::Intersect(Ray, Capsule, t0, t1) — im3d.cpp:3369. Im3d only reports a boolean here
	/// (its own "\todo implement"); the t values are not meaningful and are returned as 0.
	/// </summary>
	public static bool Intersect(GizmoRay ray, GizmoCapsule capsule, out float t0, out float t1)
	{
		t0 = 0.0f;
		t1 = 0.0f;
		return Intersects(ray, capsule);
	}

	// --- Closest points between lines/rays/segments ---

	/// <summary>Im3d::Nearest(Line, Line, t0, t1) — im3d.cpp:3376.</summary>
	public static void Nearest(GizmoLine line0, GizmoLine line1, out float t0, out float t1)
	{
		Vector3 p = line0.Origin - line1.Origin;
		float q = line0.Direction.Dot(line1.Direction);
		float s = line1.Direction.Dot(p);

		float d = 1.0f - q * q;
		if (d < FloatEpsilon) // lines are parallel
		{
			t0 = 0.0f;
			t1 = s;
		}
		else
		{
			float r = line0.Direction.Dot(p);
			t0 = (q * s - r) / d;
			t1 = (s - q * r) / d;
		}
	}

	/// <summary>Im3d::Nearest(Ray, Line, tr, tl) — im3d.cpp:3395.</summary>
	public static void Nearest(GizmoRay ray, GizmoLine line, out float tr, out float tl)
	{
		Nearest(new GizmoLine(ray.Origin, ray.Direction), line, out tr, out tl);
		tr = Mathf.Max(tr, 0.0f);
	}

	/// <summary>Im3d::Nearest(Ray, LineSegment, tr) — im3d.cpp:3400. Returns the point on the segment.</summary>
	public static Vector3 Nearest(GizmoRay ray, GizmoLineSegment segment, out float tr)
	{
		Vector3 ldir = segment.End - segment.Start;
		Vector3 p = segment.Start - ray.Origin;
		float q = ldir.LengthSquared();
		float r = ldir.Dot(ray.Direction);
		float s = ldir.Dot(p);
		float t = ray.Direction.Dot(p);

		float sn, sd, tn, td;
		float denom = q - r * r;
		if (denom < FloatEpsilon)
		{
			sd = td = 1.0f;
			sn = 0.0f;
			tn = t;
		}
		else
		{
			sd = td = denom;
			sn = r * t - s;
			tn = q * t - r * s;
			if (sn < 0.0f)
			{
				sn = 0.0f;
				tn = t;
				td = 1.0f;
			}
			else if (sn > sd)
			{
				sn = sd;
				tn = t + r;
				td = 1.0f;
			}
		}

		float ts;
		if (tn < 0.0f)
		{
			tr = 0.0f;
			if (r >= 0.0f)
			{
				ts = 0.0f;
			}
			else if (s <= q)
			{
				ts = 1.0f;
			}
			else
			{
				ts = -s / q;
			}
		}
		else
		{
			tr = tn / td;
			ts = sn / sd;
		}

		return segment.Start + ldir * ts;
	}

	/// <summary>Im3d::Distance2(Ray, LineSegment) — im3d.cpp:3461.</summary>
	public static float DistanceSquared(GizmoRay ray, GizmoLineSegment segment)
	{
		float tr;
		Vector3 p = Nearest(ray, segment, out tr);
		return (ray.Origin + ray.Direction * tr - p).LengthSquared();
	}

	// --- Snapping ---

	/// <summary>
	/// Im3d::Snap(float, float) — im3d.cpp:844. Note this floors rather than rounds; that is
	/// Im3d's behavior and it is part of how the snap feels, so it is preserved verbatim.
	/// </summary>
	public static float Snap(float value, float snap)
	{
		if (snap > 0.0f)
		{
			return Mathf.Floor(value / snap) * snap;
		}
		return value;
	}

	/// <summary>
	/// Im3d::Snap(Vec3, Plane, float) — im3d.cpp:862. Snaps a position in the plane's own basis
	/// (decompose along the two plane tangents, floor each length, recompose).
	/// </summary>
	public static Vector3 Snap(Vector3 pos, GizmoPlane plane, float snap)
	{
		if (snap > 0.0f)
		{
			// Get basis vectors on the plane.
			Basis basis = AlignZ(plane.Normal, Vector3.Up);
			Vector3 i = basis.X; // Im3d Mat3::getCol(0) — Godot Basis vectors are the columns.
			Vector3 j = basis.Y; // Im3d Mat3::getCol(1)

			// Decompose _pos in terms of the basis vectors.
			i = i * pos.Dot(i);
			j = j * pos.Dot(j);

			// Snap the vector lengths.
			float iLen = i.Length();
			float jLen = j.Length();

			if (iLen < 1e-7f || jLen < 1e-7f) // \hack prevent DBZ when _pos is 0
			{
				return pos;
			}

			i = i / iLen;
			iLen = Mathf.Floor(iLen / snap) * snap;
			i = i * iLen;
			j = j / jLen;
			jLen = Mathf.Floor(jLen / snap) * snap;
			j = j * jLen;

			return i + j;
		}
		return pos;
	}

	// --- Misc math ---

	/// <summary>Im3d::Remap — im3d_math.h:304. Remaps x from [start,end] to [0,1], clamped.</summary>
	public static float Remap(float x, float start, float end)
	{
		return Mathf.Clamp(x * (1.0f / (end - start)) + (-start / (end - start)), 0.0f, 1.0f);
	}

	/// <summary>
	/// Im3d's AllLess(Abs(a - b), Vec3(halfSize)) — the axis-aligned box test Im3d uses for plane
	/// handle hits. Note it is an axis-aligned box in world space, so a rotated local plane
	/// over-picks at the corners; that is Im3d's behavior and is preserved.
	/// </summary>
	public static bool WithinBox(Vector3 a, Vector3 b, float halfSize)
	{
		Vector3 d = a - b;
		return Mathf.Abs(d.X) < halfSize && Mathf.Abs(d.Y) < halfSize && Mathf.Abs(d.Z) < halfSize;
	}

	/// <summary>
	/// Im3d::AlignZ(axis, up) — im3d.cpp:3249. Builds an orthonormal basis whose Z axis is
	/// <paramref name="axis"/>, falling back to other reference vectors when the requested up is
	/// parallel to the axis.
	/// </summary>
	public static Basis AlignZ(Vector3 axis, Vector3 up)
	{
		Vector3 x, y;
		y = up - axis * up.Dot(axis);
		float yLen = y.Length();
		if (yLen < FloatEpsilon)
		{
			Vector3 k = new Vector3(1.0f, 0.0f, 0.0f);
			y = k - axis * k.Dot(axis);
			yLen = y.Length();
			if (yLen < FloatEpsilon)
			{
				k = new Vector3(0.0f, 0.0f, 1.0f);
				y = k - axis * k.Dot(axis);
				yLen = y.Length();
			}
		}
		y = y / yLen;
		x = y.Cross(axis);

		// Im3d builds a Mat4 from columns (x, y, axis); Godot's Basis constructor takes columns too.
		return new Basis(x, y, axis);
	}

	// --- Mat3 / Mat4 helpers (Im3d's transform decomposition) ---
	//
	// These are the pieces Im3d::Gizmo uses to read a rotation out of the transform, apply the
	// gizmo's delta, and put it back while preserving the object's scale. They are ported against
	// Godot's Basis, whose X/Y/Z are the matrix columns (see the column note at the top of this
	// file), so `Basis.X` == Im3d Mat3::getCol(0).

	private const float HalfPi = 1.5707964f;

	/// <summary>Im3d Mat3::getScale / Mat4::getScale — im3d.cpp:3019,3181. Column lengths.</summary>
	public static Vector3 GetScale(Basis basis)
	{
		return new Vector3(basis.X.Length(), basis.Y.Length(), basis.Z.Length());
	}

	/// <summary>
	/// Im3d Mat4::getRotation — im3d.cpp:3166. Each column normalized, i.e. the pure rotation part
	/// with the scale divided out.
	/// </summary>
	public static Basis GetRotation(Basis basis)
	{
		return new Basis(basis.X.Normalized(), basis.Y.Normalized(), basis.Z.Normalized());
	}

	/// <summary>Im3d Mat4::setRotation — im3d.cpp:3174. Replaces the rotation, keeps the scale.</summary>
	public static Basis SetRotation(Basis basis, Basis rotation)
	{
		Vector3 scale = GetScale(basis);
		return new Basis(rotation.X * scale.X, rotation.Y * scale.Y, rotation.Z * scale.Z);
	}

	/// <summary>Im3d Mat4::setScale — im3d.cpp:3185. Scales each column by the ratio to its length.</summary>
	public static Basis SetScale(Basis basis, Vector3 scale)
	{
		Vector3 current = GetScale(basis);
		Vector3 ratio = new Vector3(scale.X / current.X, scale.Y / current.Y, scale.Z / current.Z);
		return new Basis(basis.X * ratio.X, basis.Y * ratio.Y, basis.Z * ratio.Z);
	}

	/// <summary>
	/// Im3d ToEulerXYZ — im3d.cpp:3030 (the matrix element reads map Mat3(row,col) to Godot's
	/// column accessors: m(0,0)=X.X, m(1,0)=X.Y, m(2,0)=X.Z, m(0,1)=Y.X, m(2,1)=Y.Z, m(0,2)=Z.X,
	/// m(2,2)=Z.Z). The rotation gizmo only uses these as per-axis scalar state, but the port is
	/// kept verbatim so the values can be read against Im3d directly.
	/// </summary>
	public static Vector3 ToEulerXYZ(Basis basis)
	{
		Vector3 ret = Vector3.Zero;
		float m20 = basis.X.Z;
		if (Mathf.Abs(m20) < 1.0f)
		{
			ret.Y = -Mathf.Asin(m20);
			float c = 1.0f / Mathf.Cos(ret.Y);
			ret.X = Mathf.Atan2(basis.Y.Z * c, basis.Z.Z * c);
			ret.Z = Mathf.Atan2(basis.X.Y * c, basis.X.X * c);
		}
		else
		{
			ret.Z = 0.0f;
			if (!(m20 > -1.0f))
			{
				ret.X = ret.Z + Mathf.Atan2(basis.Y.X, basis.Z.X);
				ret.Y = HalfPi;
			}
			else
			{
				ret.X = -ret.Z + Mathf.Atan2(-basis.Y.X, -basis.Z.X);
				ret.Y = -HalfPi;
			}
		}
		return ret;
	}
}