using Godot;

/// <summary>
/// Pure 2D geometry for the lot UI editor: frame clamping, element hit-testing and the resize handle
/// layout. Kept free of Godot nodes and ImGui state (the same split as <see cref="GizmoMath"/> for
/// the 3D gizmo) so every rule here can be hand-verified by <see cref="GizmoSelfTest"/>.
///
/// All coordinates are pixels relative to the viewport picture frame's top-left corner, Y down —
/// exactly what <see cref="LotUIElement"/> stores.
/// </summary>
public static class UiGizmoMath
{
	/// <summary>Smallest element the editor allows. Resizing can never collapse an element to nothing.</summary>
	public const float MinElementSize = 8f;

	/// <summary>Resize handles: 4 corners + 4 edge midpoints, clockwise from the top-left corner.</summary>
	public const int HandleCount = 8;

	public const int HandleTopLeft = 0;
	public const int HandleTop = 1;
	public const int HandleTopRight = 2;
	public const int HandleRight = 3;
	public const int HandleBottomRight = 4;
	public const int HandleBottom = 5;
	public const int HandleBottomLeft = 6;
	public const int HandleLeft = 7;

	/// <summary>
	/// Clamps an element rect inside the picture frame. The size is capped to the frame and floored
	/// at <see cref="MinElementSize"/>, then the position is clamped so the element always lies fully
	/// inside. This is the single place that enforces "UI elements cannot cross outside the viewport
	/// picture frame", so every mutation path (gizmo drag, inspector, Lua, spawn, duplicate) agrees.
	/// </summary>
	public static void ClampRect(ref Vector2 position, ref Vector2 size, Vector2 bounds)
	{
		// A frame narrower than the minimum element wins: never grow an element past the frame.
		float maxX = Mathf.Max(bounds.X, MinElementSize);
		float maxY = Mathf.Max(bounds.Y, MinElementSize);
		size.X = Mathf.Clamp(size.X, MinElementSize, maxX);
		size.Y = Mathf.Clamp(size.Y, MinElementSize, maxY);
		position.X = Mathf.Clamp(position.X, 0f, maxX - size.X);
		position.Y = Mathf.Clamp(position.Y, 0f, maxY - size.Y);
	}

	/// <summary>True when the point lies inside the rect (edges inclusive).</summary>
	public static bool ContainsPoint(Vector2 position, Vector2 size, Vector2 point)
	{
		return point.X >= position.X && point.X <= position.X + size.X
			&& point.Y >= position.Y && point.Y <= position.Y + size.Y;
	}

	/// <summary>The on-screen point of a resize handle, given the element rect.</summary>
	public static Vector2 HandlePoint(int handle, Vector2 position, Vector2 size)
	{
		float midX = position.X + size.X * 0.5f;
		float midY = position.Y + size.Y * 0.5f;
		float right = position.X + size.X;
		float bottom = position.Y + size.Y;
		switch (handle)
		{
			case HandleTopLeft: return new Vector2(position.X, position.Y);
			case HandleTop: return new Vector2(midX, position.Y);
			case HandleTopRight: return new Vector2(right, position.Y);
			case HandleRight: return new Vector2(right, midY);
			case HandleBottomRight: return new Vector2(right, bottom);
			case HandleBottom: return new Vector2(midX, bottom);
			case HandleBottomLeft: return new Vector2(position.X, bottom);
			default: return new Vector2(position.X, midY);
		}
	}

	public static bool AffectsLeft(int handle) { return handle == HandleTopLeft || handle == HandleBottomLeft || handle == HandleLeft; }
	public static bool AffectsRight(int handle) { return handle == HandleTopRight || handle == HandleRight || handle == HandleBottomRight; }
	public static bool AffectsTop(int handle) { return handle == HandleTopLeft || handle == HandleTop || handle == HandleTopRight; }
	public static bool AffectsBottom(int handle) { return handle == HandleBottomRight || handle == HandleBottom || handle == HandleBottomLeft; }

	/// <summary>True for the four corner handles, the only ones that can aspect-lock.</summary>
	public static bool IsCorner(int handle)
	{
		return handle == HandleTopLeft || handle == HandleTopRight || handle == HandleBottomRight || handle == HandleBottomLeft;
	}

	/// <summary>
	/// Resizes a rect by dragging one handle. The edge(s) the handle controls follow the mouse delta;
	/// the opposite edge stays anchored. Edges are normalized afterwards, so dragging an edge past
	/// its opposite flips the rect cleanly instead of inverting it.
	///
	/// <paramref name="aspectLock"/> keeps the start aspect ratio (corner handles only) by deriving
	/// the shorter axis from the longer one. The result is not clamped or snapped — the caller does
	/// both, so the frame constraint and the snap guides stay in one place.
	/// </summary>
	public static void ResizeRect(Vector2 startPosition, Vector2 startSize, int handle, Vector2 delta, bool aspectLock,
		out Vector2 position, out Vector2 size)
	{
		float left = startPosition.X;
		float top = startPosition.Y;
		float right = startPosition.X + startSize.X;
		float bottom = startPosition.Y + startSize.Y;

		if (AffectsLeft(handle)) left += delta.X;
		if (AffectsRight(handle)) right += delta.X;
		if (AffectsTop(handle)) top += delta.Y;
		if (AffectsBottom(handle)) bottom += delta.Y;

		// Normalize: a handle dragged past the opposite edge swaps them rather than going negative.
		float minX = Mathf.Min(left, right);
		float maxX = Mathf.Max(left, right);
		float minY = Mathf.Min(top, bottom);
		float maxY = Mathf.Max(top, bottom);

		float width = maxX - minX;
		float height = maxY - minY;

		if (aspectLock && IsCorner(handle) && startSize.X > 0f && startSize.Y > 0f)
		{
			float aspect = startSize.X / startSize.Y;
			if (height > 0f && width > height * aspect) height = width / aspect;
			else width = height * aspect;
		}

		// Re-anchor on the edge(s) the handle does not control, so only the dragged side grows.
		if (AffectsLeft(handle)) minX = maxX - width; else maxX = minX + width;
		if (AffectsTop(handle)) minY = maxY - height; else maxY = minY + height;

		position = new Vector2(minX, minY);
		size = new Vector2(maxX - minX, maxY - minY);
	}
}