using Godot;

/// <summary>
/// The drawing surface the gizmo behaviors write to. Im3d emits vertices with a pixel size and
/// expands them into geometry in its own shader; OpenLot's UI layer is ImGui, whose draw calls are
/// screen-space. This interface is the seam between the two: behaviors pass world-space points and
/// pixel sizes, and the implementation decides how to rasterize them.
/// </summary>
public interface IGizmoDraw
{
	/// <summary>A world-space line segment drawn with a constant on-screen thickness.</summary>
	void Line(Vector3 a, Vector3 b, float pixelThickness, Color color);

	/// <summary>A filled world-space triangle (Im3d PrimitiveMode_Triangles).</summary>
	void Triangle(Vector3 a, Vector3 b, Vector3 c, Color color);

	/// <summary>A filled world-space quad (Im3d DrawQuadFilled corner order).</summary>
	void QuadFilled(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color);

	/// <summary>A world-space quad outline (Im3d DrawQuad / PrimitiveMode_LineLoop).</summary>
	void QuadOutline(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float pixelThickness, Color color);

	/// <summary>A screen-space filled dot of the given pixel diameter (Im3d PrimitiveMode_Points).</summary>
	void Dot(Vector3 center, float pixelDiameter, Color color);

	/// <summary>Screen-space text, used by the debug readout.</summary>
	void Text(Vector2 screenPosition, Color color, string text);

	/// <summary>Top-left of the 3D view in screen space, for anchoring overlays.</summary>
	Vector2 ImageOrigin { get; }

	/// <summary>Projects a world position into screen space; false when it is behind the camera.</summary>
	bool WorldToScreen(Vector3 world, out Vector2 screen);
}

/// <summary>
/// <see cref="IGizmoDraw"/> on top of the dear-imgui-godot draw list. Created once per frame with
/// the viewport's current screen rect, then thrown away — it caches the near plane and the
/// viewport-to-image scale so the per-handle draw calls do no repeated work.
///
/// Draw calls target ImGui's DrawLayerWindow, i.e. the Viewport window's own draw list, so the
/// gizmo is clipped to the 3D view exactly like the lot UI overlay.
/// </summary>
public sealed class ImGuiGizmoDraw : IGizmoDraw
{
	private readonly Camera3D _camera;
	private readonly Vector2 _imageOrigin;
	private readonly Vector2 _screenScale;
	private readonly Plane _nearPlane;
	private readonly bool _valid;

	public ImGuiGizmoDraw(Camera3D camera, Vector2 imageOrigin, Vector2 imageSize, Vector2I viewportSize)
	{
		_camera = camera;
		_imageOrigin = imageOrigin;

		Vector2 viewport = new Vector2(viewportSize.X, viewportSize.Y);
		_valid = camera != null && viewport.X > 0f && viewport.Y > 0f;
		_screenScale = _valid
			? new Vector2(imageSize.X / viewport.X, imageSize.Y / viewport.Y)
			: Vector2.One;

		// Built directly from the camera instead of Camera3D.GetFrustum(), which would allocate a
		// Godot Array on every frame. Only the near plane is needed: it is the one that the
		// active-axis "infinite" line (origin +/- axis * 999) always crosses.
		if (_valid)
		{
			Vector3 forward = -camera.GlobalTransform.Basis.Z;
			_nearPlane = new Plane(forward, camera.GlobalPosition + forward * camera.Near);
		}
	}

	public bool WorldToScreen(Vector3 world, out Vector2 screen)
	{
		screen = Vector2.Zero;
		if (!_valid || _camera.IsPositionBehind(world))
		{
			return false;
		}

		Vector2 viewportPoint = _camera.UnprojectPosition(world);
		screen = _imageOrigin + viewportPoint * _screenScale;
		return true;
	}

	/// <summary>
	/// Clips a world-space segment against the camera's near plane so it can be projected. Returns
	/// false when the whole segment is behind the camera. Im3d does the equivalent clipping before
	/// its shader runs; ImGui has no such stage, so it happens here.
	/// </summary>
	private bool ClipToNear(ref Vector3 a, ref Vector3 b)
	{
		float da = _nearPlane.DistanceTo(a);
		float db = _nearPlane.DistanceTo(b);
		if (da < 0.0f && db < 0.0f)
		{
			return false;
		}
		if (da < 0.0f)
		{
			a = a + (b - a) * (da / (da - db));
		}
		else if (db < 0.0f)
		{
			b = a + (b - a) * (da / (da - db));
		}
		return true;
	}

	public void Line(Vector3 a, Vector3 b, float pixelThickness, Color color)
	{
		if (!_valid || !ClipToNear(ref a, ref b))
		{
			return;
		}

		Vector2 sa, sb;
		if (!WorldToScreen(a, out sa) || !WorldToScreen(b, out sb))
		{
			return;
		}

		ImGui.DrawLine(sa, sb, color, pixelThickness);
	}

	public void Triangle(Vector3 a, Vector3 b, Vector3 c, Color color)
	{
		Vector2 sa, sb, sc;
		if (!WorldToScreen(a, out sa) || !WorldToScreen(b, out sb) || !WorldToScreen(c, out sc))
		{
			return;
		}

		ImGui.DrawTriangleFilled(sa, sb, sc, color);
	}

	public void QuadFilled(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color)
	{
		// Im3d DrawQuadFilled emits (a,b,c) then (a,c,d).
		Triangle(a, b, c, color);
		Triangle(a, c, d, color);
	}

	public void QuadOutline(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float pixelThickness, Color color)
	{
		// Im3d DrawQuad emits a LineLoop through all four corners.
		Line(a, b, pixelThickness, color);
		Line(b, c, pixelThickness, color);
		Line(c, d, pixelThickness, color);
		Line(d, a, pixelThickness, color);
	}

	public void Dot(Vector3 center, float pixelDiameter, Color color)
	{
		Vector2 screen;
		if (!WorldToScreen(center, out screen))
		{
			return;
		}

		ImGui.DrawCircleFilled(screen, pixelDiameter * 0.5f, color);
	}

	public Vector2 ImageOrigin { get { return _imageOrigin; } }

	public void Text(Vector2 screenPosition, Color color, string text)
	{
		ImGui.DrawText(screenPosition, color, text);
	}
}