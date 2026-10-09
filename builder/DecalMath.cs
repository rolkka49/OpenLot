using Godot;

/// <summary>
/// The pure placement math behind a part decal (milestone 2.6): which way a face points, where an
/// image patch sits on it, and how that patch is expressed as a child transform of the host part.
///
/// A decal is a scene object parented to the part it decorates, so it inherits the host's
/// transform — including its scale. Godot applies the host scale to a child's local position AND
/// its local basis, so everything below is computed in "visual space" (the space the host's mesh
/// occupies once its scale is applied) and then divided by the host scale component-wise. Because
/// every face axis is a signed part axis, that division stays a rotation + per-axis scale — no
/// shear — which is what keeps a patch on its face when the host is non-uniformly scaled. Cost of
/// the shortcut: a *mirrored* host (a negative scale component) is not compensated, so a patch
/// there can sit slightly inside the surface. Mirrored hosts are left alone deliberately rather
/// than half-handled.
///
/// Nothing here touches nodes: the self-test drives this file directly, and <see cref="LotObject"/>
/// is the only caller that binds it to a live node.
/// </summary>
public static class DecalMath
{
	/// <summary>Face names, in the order the Inspector's Face dropdown lists them. +Z first because
	/// it is the default — the front face of an unrotated part.</summary>
	public static readonly string[] FaceNames = { "+Z", "-Z", "+X", "-X", "+Y", "-Y" };

	/// <summary>What a decal draws on its face. Image is a picture patch; Text, Button and Scrollbar
	/// are the on-face UI contents (the first step of §5.3's lot-UI-on-a-surface work). Button and
	/// Scrollbar are drawn and configurable today; their *input* routing is the shared §3.7/§5.2/§5.3
	/// blocker, so they are visuals until that lands.</summary>
	public static readonly string[] ContentNames = { "Image", "Text", "Button", "Scrollbar" };

	/// <summary>Index of the default content (Image) in <see cref="ContentNames"/>.</summary>
	public const int DefaultContent = 0;

	/// <summary>Scrollbar thumb size as a fraction of the panel, and the track fraction it may move
	/// over (the thumb stays fully on the track).</summary>
	public const float ScrollThumbWidth = 0.18f;
	public const float ScrollThumbHeight = 0.9f;

	/// <summary>Smallest and largest decal font size, so a text panel can never be typed to zero.</summary>
	public const float MinFontSize = 8f;
	public const float MaxFontSize = 160f;

	/// <summary>
	/// The label's world-to-pixel factor: with a font size of N, one line occupies N/160 of the
	/// panel's height (48 → 30%, the default). Kept as one constant so Label3D's pixel size, the
	/// wrap width and the line height can never disagree.
	/// </summary>
	public const float LabelPixelsPerPanelUnit = 160f;

	/// <summary>Line height of a font size, as a fraction of the panel height.</summary>
	public static float LineFraction(float fontSize)
	{
		return Mathf.Clamp(fontSize, MinFontSize, MaxFontSize) / LabelPixelsPerPanelUnit;
	}

	/// <summary>Wrap width in Label3D "pixels": 90% of the panel, so text never touches the edge.</summary>
	public static float LabelWrapPixels()
	{
		return 0.9f * LabelPixelsPerPanelUnit;
	}

	/// <summary>Index of a content name, or -1 when the name is not one of <see cref="ContentNames"/>.</summary>
	public static int ContentIndex(string name)
	{
		if (string.IsNullOrEmpty(name)) return -1;
		for (int i = 0; i < ContentNames.Length; i++)
		{
			if (ContentNames[i] == name) return i;
		}
		return -1;
	}

	/// <summary>Clamps a content index; an out-of-range value falls back to the default.</summary>
	public static int ClampContent(int content)
	{
		return content >= 0 && content < ContentNames.Length ? content : DefaultContent;
	}

	/// <summary>Clamps a font size into [<see cref="MinFontSize"/>, <see cref="MaxFontSize"/>].</summary>
	public static float ClampFontSize(float size)
	{
		return Mathf.Clamp(size, MinFontSize, MaxFontSize);
	}

	/// <summary>Clamps a scrollbar value into [0, 1] (0 = top, 1 = bottom).</summary>
	public static float ClampScroll(float value)
	{
		return Mathf.Clamp(value, 0f, 1f);
	}

	/// <summary>Where a scrollbar thumb sits along the track, as a local offset from the panel
	/// centre: -0.5..0.5 minus the thumb's own half-width, so the thumb never leaves the track.</summary>
	public static float ScrollThumbOffset(float scroll)
	{
		return (ClampScroll(scroll) - 0.5f) * (1f - ScrollThumbWidth);
	}

	/// <summary>
	/// Which face a surface normal belongs to: the axis it leans on most, in the host's local
	/// frame. This is what turns "the creator clicked this side of the part" into a face, so the
	/// decal tool can place a patch on exactly the face that was clicked.
	/// </summary>
	public static int FaceFromNormal(Vector3 localNormal)
	{
		Vector3 n = localNormal;
		if (n.LengthSquared() < 1e-8f) return DefaultFace;

		float ax = Mathf.Abs(n.X);
		float ay = Mathf.Abs(n.Y);
		float az = Mathf.Abs(n.Z);
		if (ax >= ay && ax >= az) return n.X >= 0f ? FaceIndex("+X") : FaceIndex("-X");
		if (ay >= ax && ay >= az) return n.Y >= 0f ? FaceIndex("+Y") : FaceIndex("-Y");
		return n.Z >= 0f ? FaceIndex("+Z") : FaceIndex("-Z");
	}

	/// <summary>
	/// Whether a point on the host's surface (in visual space) lies inside a patch's rectangle —
	/// its face must match, and its (u, v) must fall within the patch's half-extents. The decal
	/// tool uses this to tell "clicked one of this host's existing decals" from "clicked bare
	/// surface, so place a new one".
	/// </summary>
	public static bool CoversPoint(Vector3 size, Vector3 hostScale, int face,
		float offsetU, float offsetV, float scaleU, float scaleV, int pointFace, Vector3 visualPoint)
	{
		if (ClampFace(face) != ClampFace(pointFace)) return false;
		Vector2 at = OffsetsFromPoint(size, hostScale, face, visualPoint);
		float halfU = ClampScale(scaleU) * 0.5f;
		float halfV = ClampScale(scaleV) * 0.5f;
		return Mathf.Abs(at.X - offsetU) <= halfU && Mathf.Abs(at.Y - offsetV) <= halfV;
	}

	/// <summary>
	/// A readable label colour for a button surface: dark text on a light button, light text on a
	/// dark one. Kept here (and tested) so the contrast rule is one inspectable function rather
	/// than a magic colour picked at the call site.
	/// </summary>
	public static Color LabelColorFor(Color surface)
	{
		float luminance = 0.2126f * surface.R + 0.7152f * surface.G + 0.0722f * surface.B;
		return luminance > 0.5f ? new Color(0.1f, 0.1f, 0.1f) : new Color(0.95f, 0.95f, 0.95f);
	}

	/// <summary>The scrollbar thumb colour for a track colour: the same hue, lifted so the thumb
	/// reads against the track without needing a second colour property.</summary>
	public static Color ThumbColorFor(Color track)
	{
		return new Color(
			Mathf.Min(1f, track.R + 0.25f),
			Mathf.Min(1f, track.G + 0.25f),
			Mathf.Min(1f, track.B + 0.25f),
			track.A);
	}


	/// <summary>Index of the default face (+Z) in <see cref="FaceNames"/>.</summary>
	public const int DefaultFace = 0;

	/// <summary>Smallest decal scale: a patch can never collapse to nothing (the same spirit as the
	/// gizmo's minimum-scale clamp).</summary>
	public const float MinScale = 0.05f;

	/// <summary>Largest decal scale: the full face. This clamp plus the offset clamp are what keep a
	/// patch from spilling past the face edge onto neighbouring faces.</summary>
	public const float MaxScale = 1f;

	/// <summary>Clamps a face index; an out-of-range value falls back to the default face.</summary>
	public static int ClampFace(int face)
	{
		return face >= 0 && face < FaceNames.Length ? face : DefaultFace;
	}

	/// <summary>Clamps a decal scale into [<see cref="MinScale"/>, <see cref="MaxScale"/>].</summary>
	public static float ClampScale(float scale)
	{
		return Mathf.Clamp(scale, MinScale, MaxScale);
	}

	/// <summary>Clamps an opacity into [0, 1].</summary>
	public static float ClampOpacity(float opacity)
	{
		return Mathf.Clamp(opacity, 0f, 1f);
	}

	/// <summary>The index of a face name from <see cref="FaceNames"/>, or -1 when the name is not
	/// one of them — the inverse of indexing the table.</summary>
	public static int FaceIndex(string name)
	{
		if (string.IsNullOrEmpty(name)) return -1;
		for (int i = 0; i < FaceNames.Length; i++)
		{
			if (FaceNames[i] == name) return i;
		}
		return -1;
	}

	/// <summary>The outward normal of a face, in the host's local axes.</summary>
	public static Vector3 Normal(int face)
	{
		switch (ClampFace(face))
		{
			case 1: return new Vector3(0f, 0f, -1f);
			case 2: return new Vector3(1f, 0f, 0f);
			case 3: return new Vector3(-1f, 0f, 0f);
			case 4: return new Vector3(0f, 1f, 0f);
			case 5: return new Vector3(0f, -1f, 0f);
			default: return new Vector3(0f, 0f, 1f);
		}
	}

	/// <summary>
	/// The face's in-plane axes, ordered so u x v == normal: the patch's basis is a proper rotation
	/// (no mirroring), which is what keeps an image's left/right and top/bottom reading the same on
	/// every face.
	/// </summary>
	public static void Axes(int face, out Vector3 u, out Vector3 v)
	{
		switch (ClampFace(face))
		{
			case 1: // -Z
				u = new Vector3(-1f, 0f, 0f);
				v = new Vector3(0f, 1f, 0f);
				return;
			case 2: // +X
				u = new Vector3(0f, 0f, -1f);
				v = new Vector3(0f, 1f, 0f);
				return;
			case 3: // -X
				u = new Vector3(0f, 0f, 1f);
				v = new Vector3(0f, 1f, 0f);
				return;
			case 4: // +Y
				u = new Vector3(1f, 0f, 0f);
				v = new Vector3(0f, 0f, -1f);
				return;
			case 5: // -Y
				u = new Vector3(1f, 0f, 0f);
				v = new Vector3(0f, 0f, 1f);
				return;
			default: // +Z
				u = new Vector3(1f, 0f, 0f);
				v = new Vector3(0f, 1f, 0f);
				return;
		}
	}

	/// <summary>The size of <paramref name="size"/>'s face (width along u, height along v).</summary>
	public static Vector2 Extents(int face, Vector3 size)
	{
		switch (ClampFace(face))
		{
			case 2:
			case 3: return new Vector2(size.Z, size.Y);
			case 4:
			case 5: return new Vector2(size.X, size.Z);
			default: return new Vector2(size.X, size.Y);
		}
	}

	/// <summary>How much of <paramref name="size"/> lies along the face normal's axis.</summary>
	public static float NormalExtent(int face, Vector3 size)
	{
		switch (ClampFace(face))
		{
			case 2:
			case 3: return size.X;
			case 4:
			case 5: return size.Y;
			default: return size.Z;
		}
	}

	/// <summary>
	/// The host's mesh size in visual space (mesh extent times the ABSOLUTE scale component), which
	/// is the space a decal's size and offset fractions are measured in.
	/// </summary>
	public static Vector3 VisualSize(Vector3 size, Vector3 scale)
	{
		return new Vector3(size.X * Mathf.Abs(scale.X), size.Y * Mathf.Abs(scale.Y), size.Z * Mathf.Abs(scale.Z));
	}

	/// <summary>
	/// How far a patch is floated off its face along the normal, so it cannot z-fight with the
	/// surface it decorates. Proportional to the host's visual size, because a fixed distance is
	/// either invisible on a building-sized part or a visible gap on a small one.
	/// </summary>
	public static float Lift(Vector3 visualSize)
	{
		float biggest = Mathf.Max(Mathf.Abs(visualSize.X), Mathf.Max(Mathf.Abs(visualSize.Y), Mathf.Abs(visualSize.Z)));
		return 0.001f + 0.001f * biggest;
	}

	/// <summary>
	/// Clamps an offset so the patch stays entirely on its face: |offset| + scale/2 &lt;= 0.5, in
	/// fractions of the face. This is the whole clipping rule — a patch can never be larger than
	/// the face or hang over its edge.
	/// </summary>
	public static Vector2 ClampOffset(float scaleU, float scaleV, float offsetU, float offsetV)
	{
		float maxU = 0.5f - ClampScale(scaleU) * 0.5f;
		float maxV = 0.5f - ClampScale(scaleV) * 0.5f;
		return new Vector2(Mathf.Clamp(offsetU, -maxU, maxU), Mathf.Clamp(offsetV, -maxV, maxV));
	}

	/// <summary>
	/// The patch's local transform as a child of the host part: position, orientation and size all
	/// derived from the face, the offset, the scale and the host's size and scale. The quad mesh it
	/// drives is a unit square, so the basis columns carry the patch's size directly.
	/// </summary>
	public static Transform3D LocalTransform(Vector3 size, Vector3 hostScale, int face,
		float offsetU, float offsetV, float scaleU, float scaleV)
	{
		face = ClampFace(face);
		Axes(face, out Vector3 u, out Vector3 v);
		Vector3 n = Normal(face);
		Vector3 visualSize = VisualSize(size, hostScale);
		Vector2 extents = Extents(face, visualSize);
		float su = ClampScale(scaleU);
		float sv = ClampScale(scaleV);
		Vector2 offset = ClampOffset(su, sv, offsetU, offsetV);

		// The face sits at half the mesh extent along the normal, measured in the same visual
		// space (so a scaled host's face is where it looks like it is).
		float faceOffset = SignedExtent(face, size, hostScale) * 0.5f;
		Vector3 centre = n * (faceOffset + Lift(visualSize))
			+ u * (offset.X * extents.X)
			+ v * (offset.Y * extents.Y);

		Vector3 origin = Divide(centre, hostScale);
		Vector3 columnU = Divide(u * (extents.X * su), hostScale);
		Vector3 columnV = Divide(v * (extents.Y * sv), hostScale);
		Vector3 columnN = Divide(n, hostScale);
		return new Transform3D(new Basis(columnU, columnV, columnN), origin);
	}

	/// <summary>
	/// The inverse of <see cref="LocalTransform"/>'s placement: which fractional (u, v) on a face a
	/// point sits at, given the point in visual space (the host's local space with its scale
	/// applied). The decal drag converts the cursor ray through this to slide a patch.
	/// </summary>
	public static Vector2 OffsetsFromPoint(Vector3 size, Vector3 hostScale, int face, Vector3 visualPoint)
	{
		face = ClampFace(face);
		Axes(face, out Vector3 u, out Vector3 v);
		Vector3 n = Normal(face);
		Vector3 visualSize = VisualSize(size, hostScale);
		Vector2 extents = Extents(face, visualSize);
		Vector3 relative = visualPoint - n * (SignedExtent(face, size, hostScale) * 0.5f);
		return new Vector2(
			extents.X > 1e-6f ? relative.Dot(u) / extents.X : 0f,
			extents.Y > 1e-6f ? relative.Dot(v) / extents.Y : 0f);
	}

	/// <summary>The host's mesh size for the placement math, with a unit fallback for a mesh-less
	/// node so an orphaned patch still has a defined shape.</summary>
	public static Vector3 MeshSize(Mesh mesh)
	{
		return mesh != null ? mesh.GetAabb().Size : Vector3.One;
	}

	/// <summary>The mesh extent along the face normal, signed by the matching scale component.</summary>
	private static float SignedExtent(int face, Vector3 size, Vector3 scale)
	{
		switch (ClampFace(face))
		{
			case 2:
			case 3: return size.X * scale.X;
			case 4:
			case 5: return size.Y * scale.Y;
			default: return size.Z * scale.Z;
		}
	}

	/// <summary>Divides component-wise by the host scale, so a patch's local numbers cancel the
	/// scale the host passes down to it. A degenerate (near-zero) component is treated as 1, which
	/// keeps the result finite instead of infinite.</summary>
	private static Vector3 Divide(Vector3 value, Vector3 divisor)
	{
		return new Vector3(
			value.X / SafeComponent(divisor.X),
			value.Y / SafeComponent(divisor.Y),
			value.Z / SafeComponent(divisor.Z));
	}

	private static float SafeComponent(float scale)
	{
		return Mathf.Abs(scale) < 1e-6f ? 1f : scale;
	}
}
