using Godot;

/// <summary>
/// Key/action slots Im3d reads from AppData (im3d.h:495-511). The values are kept identical to
/// Im3d's so the action names line up with the original gizmo logic.
/// </summary>
public static class GizmoKeys
{
	public const int MouseLeft = 0;
	public const int KeyL = 1;
	public const int KeyR = 2;
	public const int KeyS = 3;
	public const int KeyT = 4;
	public const int Count = 5;

	public const int ActionSelect = MouseLeft;
	public const int ActionGizmoLocal = KeyL;
	public const int ActionGizmoRotation = KeyR;
	public const int ActionGizmoScale = KeyS;
	public const int ActionGizmoTranslation = KeyT;
}

/// <summary>
/// The subset of Im3d's AppData (im3d.h:529-545) that the gizmo reads. This is a pure data holder
/// on purpose — Im3d keeps AppData free of platform code and lets the application fill it in, so
/// the Godot-specific fill lives in GizmoController instead of here.
/// </summary>
public sealed class GizmoAppData
{
	public Vector3 WorldUp = Vector3.Up;
	public Vector3 ViewOrigin;
	public Vector3 ViewDirection;
	public Vector2 ViewportSize;
	public float ProjectionScaleY = 1.0f;
	public bool ProjectionOrtho;
	public float SnapTranslation;
	public float SnapRotation;            // Im3d default (im3d.h:541) — 0 means disabled (radians)
	public float SnapScale;               // Im3d default (im3d.h:542) — 0 means disabled
	public bool FlipGizmoWhenBehind = true; // Im3d default (im3d.h:543)
	public Vector3 CursorRayOrigin;
	public Vector3 CursorRayDirection;

	/// <summary>Raw key state for this frame (Im3d AppData::m_keyDown, im3d.h:529).</summary>
	public readonly bool[] KeyDown = new bool[GizmoKeys.Count];
}

/// <summary>
/// Translates Godot's camera parameters into Im3d's AppData::m_projScaleY.
/// </summary>
public static class GizmoProjection
{
	/// <summary>
	/// Im3d's m_projScaleY (im3d.h:537): "scale factor used to convert from pixel size -> world
	/// scale; use tan(fov) for perspective projections, far plane height for ortho."
	///
	/// The header comment is loose about which fov it means, and the answer follows from Im3d's own
	/// formula — Context::pixelsToWorldSize (im3d.cpp:2380) is:
	///
	///     worldSize = projScaleY * d * (pixels / viewportSize.y)
	///
	/// For a handle to hold a constant pixel size, worldSize must equal the pixel fraction of the
	/// viewport's world height at distance d. Godot's Camera3D.Fov is the full vertical angle, so
	/// that world height is 2*d*tan(fov/2), giving projScaleY = 2*tan(fov/2). Substituting tan(fov)
	/// instead would make every handle exactly twice its intended pixel size.
	/// </summary>
	public static float ScaleY(Camera3D camera, Vector2I viewportSize)
	{
		if (camera.Projection == Camera3D.ProjectionType.Orthogonal)
		{
			// Ortho: "far plane height" — a constant world height, no distance falloff.
			float aspect = viewportSize.Y > 0 ? (float)viewportSize.X / viewportSize.Y : 1.0f;
			if (camera.KeepAspect == Camera3D.KeepAspectEnum.Width)
			{
				return aspect > 0.0f ? camera.Size / aspect : camera.Size;
			}
			// KeepAspectEnum.Height is Godot's default, and Camera3D.Size already reads as the
			// vertical extent in that case.
			return camera.Size;
		}

		return 2.0f * Mathf.Tan(Mathf.DegToRad(camera.Fov) * 0.5f);
	}
}