using System.Collections.Generic;
using Godot;

/// <summary>
/// Drives the lot UI editor inside the Viewport window: it hit-tests the 2D overlay, moves and
/// resizes elements with the mouse, snaps them to the frame and to their neighbours, and draws the
/// resize handles plus the alignment guides.
///
/// It follows the same pattern as <see cref="GizmoController"/>: raw ImGui mouse state (not ImGui
/// item state, which the full-image invisible button in ViewportWindow already claims) and its own
/// <see cref="WantsMouse"/> flag. All geometry lives in <see cref="UiGizmoMath"/> / <see cref="UiSnap"/>
/// so the rules can be verified by <see cref="GizmoSelfTest"/>.
///
/// Only the left button is owned here. The freecam keeps its right-drag look and WASD, so the 3D
/// background can still be framed while editing the overlay. There is no rotation: lot UI is
/// axis-aligned by design (constraint of milestone 2.2-3).
/// </summary>
public sealed class UiGizmoController
{
	private const float HandleDrawSize = 9f;
	private const float HandleHitSize = 13f;

	private static readonly Color HandleFill = new Color(0.95f, 0.95f, 0.98f, 1f);
	private static readonly Color HandleHotFill = new Color(0.35f, 0.72f, 1.0f, 1f);
	private static readonly Color HandleBorder = new Color(0.10f, 0.10f, 0.14f, 1f);
	private static readonly Color GuideColor = new Color(1.0f, 0.35f, 0.55f, 0.9f);

	private readonly Builder _builder;

	// Reused per-frame scratch so the drag path allocates nothing.
	private readonly List<float> _targetXs = new List<float>();
	private readonly List<float> _targetYs = new List<float>();
	private readonly List<float> _sourceXs = new List<float>();
	private readonly List<float> _sourceYs = new List<float>();

	// Active drag. The rect is always recomputed from the drag-start rect plus the total mouse
	// delta, so the element can neither drift nor lag.
	private LotUIElement _dragElement;
	private bool _dragging;
	private int _dragHandle = -1; // -1 = body move, otherwise a UiGizmoMath handle index
	private Vector2 _dragStartMouse;
	private Vector2 _dragStartPos;
	private Vector2 _dragStartSize;

	private int _hotHandle = -1;

	// Alignment guides for the current frame; only valid while dragging.
	private bool _hasGuideX;
	private bool _hasGuideY;
	private float _guideX;
	private float _guideY;

	/// <summary>True while the overlay owns the left button (hovering an element/handle or dragging).</summary>
	public bool WantsMouse { get; private set; }

	public UiGizmoController(Builder builder)
	{
		_builder = builder;
		_builder.Selection.OnSelectionChanged += CancelDrag;
	}

	/// <summary>Drops the selection subscription so the event cannot outlive this controller.</summary>
	public void Shutdown()
	{
		_builder.Selection.OnSelectionChanged -= CancelDrag;
	}

	/// <summary>
	/// Runs the UI editor for one frame. Must be called from inside the Viewport ImGui window right
	/// after the overlay is drawn, because it reads ImGui mouse state and draws into that window's
	/// draw list (so handles and guides stay clipped to the picture frame).
	/// </summary>
	public void Update(Vector2 imageOrigin, Vector2 imageSize)
	{
		WantsMouse = false;
		_hotHandle = -1;

		if (imageSize.X < UiGizmoMath.MinElementSize || imageSize.Y < UiGizmoMath.MinElementSize) return;

		Vector2 mouse = ImGui.GetMousePos() - imageOrigin;
		bool inside = mouse.X >= 0f && mouse.Y >= 0f && mouse.X <= imageSize.X && mouse.Y <= imageSize.Y;

		// A drag continues even when the cursor leaves the frame; the result is clamped into the
		// frame every frame, so the element can never be dragged out of the picture.
		if (_dragging)
		{
			_hotHandle = _dragHandle;
			UpdateDrag(imageOrigin, imageSize);
			WantsMouse = true;
			DrawHandles(_dragElement, imageOrigin, _dragHandle >= 0);
			DrawGuides(imageOrigin, imageSize);
			return;
		}

		if (!inside)
		{
			// The cursor left the picture frame, so there is no pointer interaction this frame, but
			// the selected element's handles stay drawn so the selection remains readable.
			DrawHandles(_builder.Selection.GetFirstValid() as LotUIElement, imageOrigin,
				_builder.Toolbox.CurrentUiToolMode == UiToolMode.Resize);
			return;
		}

		LotUIElement selected = _builder.Selection.GetFirstValid() as LotUIElement;
		bool resizeMode = _builder.Toolbox.CurrentUiToolMode == UiToolMode.Resize;

		// Handles win over element bodies, so a corner handle sitting on a neighbour still resizes.
		if (selected != null && resizeMode)
		{
			_hotHandle = HitHandle(selected, mouse);
		}

		bool wantsHandle = _hotHandle >= 0;
		bool wantsElement = !wantsHandle && HitElement(mouse) != null;
		WantsMouse = wantsHandle || wantsElement;

		if (ImGui.IsMouseClicked(ImGui.MouseButtonLeft))
		{
			HandlePress(mouse, selected, wantsHandle, resizeMode);
		}

		DrawHandles(selected, imageOrigin, resizeMode);
	}

	private void HandlePress(Vector2 mouse, LotUIElement selected, bool wantsHandle, bool resizeMode)
	{
		if (wantsHandle)
		{
			BeginDrag(selected, _hotHandle, mouse);
			return;
		}

		LotUIElement hit = HitElement(mouse);
		bool additive = Input.IsPhysicalKeyPressed(Key.Ctrl) || Input.IsPhysicalKeyPressed(Key.Shift);
		if (hit == null)
		{
			if (!additive) _builder.Selection.ClearSelection();
			return;
		}

		_builder.Selection.Select(hit, additive);
		// Only a plain body click in Move mode starts a drag; in Resize mode a body click selects.
		if (!additive && !resizeMode) BeginDrag(hit, -1, mouse);
	}

	/// <summary>Topmost element under the point (children drawn last are on top), or null.</summary>
	private LotUIElement HitElement(Vector2 point)
	{
		Node root = _builder.Scene.LotUIRoot;
		for (int i = root.GetChildCount() - 1; i >= 0; i--)
		{
			LotUIElement element = root.GetChild(i) as LotUIElement;
			if (element == null || !element.Visible) continue;
			if (UiGizmoMath.ContainsPoint(element.Position, element.Size, point)) return element;
		}
		return null;
	}

	/// <summary>Handle index under the point for the given element, or -1.</summary>
	private static int HitHandle(LotUIElement element, Vector2 point)
	{
		float half = HandleHitSize * 0.5f;
		for (int i = 0; i < UiGizmoMath.HandleCount; i++)
		{
			Vector2 handle = UiGizmoMath.HandlePoint(i, element.Position, element.Size);
			if (Mathf.Abs(point.X - handle.X) <= half && Mathf.Abs(point.Y - handle.Y) <= half) return i;
		}
		return -1;
	}

	private void BeginDrag(LotUIElement element, int handle, Vector2 mouse)
	{
		if (element == null) return;
		_dragElement = element;
		_dragHandle = handle;
		_dragging = true;
		_dragStartMouse = mouse;
		_dragStartPos = element.Position;
		_dragStartSize = element.Size;
		ClearGuides();
	}

	/// <summary>
	/// Ends any in-flight drag. Also the SelectionManager.OnSelectionChanged handler: a selection
	/// change invalidates the stored target, and since the drag is re-created after the selection
	/// call in HandlePress this cannot cancel a drag that is just starting.
	/// </summary>
	public void CancelDrag()
	{
		_dragging = false;
		_dragElement = null;
		_dragHandle = -1;
		_hotHandle = -1;
		ClearGuides();
	}

	private void UpdateDrag(Vector2 imageOrigin, Vector2 imageSize)
	{
		if (_dragElement == null || !GodotObject.IsInstanceValid(_dragElement))
		{
			CancelDrag();
			return;
		}

		Vector2 mouse = ImGui.GetMousePos() - imageOrigin;
		Vector2 delta = mouse - _dragStartMouse;

		Vector2 pos;
		Vector2 size;
		if (_dragHandle < 0) ApplyMove(delta, imageSize, out pos, out size);
		else ApplyResize(delta, imageSize, out pos, out size);

		if (pos != _dragElement.Position || size != _dragElement.Size)
		{
			_dragElement.Position = pos;
			_dragElement.Size = size;
			_builder.MarkDirty();
		}

		if (ImGui.IsMouseReleased(ImGui.MouseButtonLeft)) CancelDrag();
	}

	/// <summary>Body drag: offset from the start rect, clamped into the frame, then snapped.</summary>
	private void ApplyMove(Vector2 delta, Vector2 bounds, out Vector2 pos, out Vector2 size)
	{
		pos = _dragStartPos + delta;
		size = _dragStartSize;
		ClearGuides();

		UiGizmoMath.ClampRect(ref pos, ref size, bounds);
		if (!AltHeld) SnapMove(ref pos, size, bounds);
		UiGizmoMath.ClampRect(ref pos, ref size, bounds);
		ValidateMoveGuides(pos, size);
	}

	/// <summary>Handle drag: only the edges the handle controls follow the mouse.</summary>
	private void ApplyResize(Vector2 delta, Vector2 bounds, out Vector2 pos, out Vector2 size)
	{
		ClearGuides();

		bool aspectLock = Input.IsPhysicalKeyPressed(Key.Shift);
		UiGizmoMath.ResizeRect(_dragStartPos, _dragStartSize, _dragHandle, delta, aspectLock, out pos, out size);

		if (!AltHeld) SnapResize(ref pos, ref size, bounds);
		UiGizmoMath.ClampRect(ref pos, ref size, bounds);
		ValidateResizeGuides(pos, size);
	}

	private bool AltHeld { get { return Input.IsPhysicalKeyPressed(Key.Alt); } }

	/// <summary>Fills the snap candidate lists from the frame plus every other visible element.</summary>
	private void CollectTargets(Vector2 bounds)
	{
		UiSnap.CollectTargets(_builder.Scene.LotUIRoot, _dragElement, bounds, _targetXs, _targetYs);
	}

	/// <summary>Snaps the moving rect's edges and centre on each axis, independently.</summary>
	private void SnapMove(ref Vector2 pos, Vector2 size, Vector2 bounds)
	{
		CollectTargets(bounds);

		_sourceXs.Clear();
		_sourceYs.Clear();
		UiSnap.AddRectLines(pos, size, _sourceXs, _sourceYs);

		float delta, guide;
		if (UiSnap.TrySnapLine(_sourceXs, _targetXs, UiSnap.ThresholdPixels, out delta, out guide))
		{
			pos.X += delta;
			_guideX = guide;
			_hasGuideX = true;
		}
		if (UiSnap.TrySnapLine(_sourceYs, _targetYs, UiSnap.ThresholdPixels, out delta, out guide))
		{
			pos.Y += delta;
			_guideY = guide;
			_hasGuideY = true;
		}
	}

	/// <summary>
	/// Snaps only the edge(s) the dragged handle controls; the anchored edge must not move, which is
	/// why this uses the single-edge snap rather than the rect snap used for a body move.
	/// </summary>
	private void SnapResize(ref Vector2 pos, ref Vector2 size, Vector2 bounds)
	{
		CollectTargets(bounds);

		float left = pos.X;
		float top = pos.Y;
		float right = pos.X + size.X;
		float bottom = pos.Y + size.Y;
		float delta, guide;

		if (UiGizmoMath.AffectsLeft(_dragHandle))
		{
			if (UiSnap.TrySnapEdge(left, _targetXs, UiSnap.ThresholdPixels, out delta, out guide))
			{
				left += delta;
				_guideX = guide;
				_hasGuideX = true;
			}
		}
		else if (UiGizmoMath.AffectsRight(_dragHandle))
		{
			if (UiSnap.TrySnapEdge(right, _targetXs, UiSnap.ThresholdPixels, out delta, out guide))
			{
				right += delta;
				_guideX = guide;
				_hasGuideX = true;
			}
		}

		if (UiGizmoMath.AffectsTop(_dragHandle))
		{
			if (UiSnap.TrySnapEdge(top, _targetYs, UiSnap.ThresholdPixels, out delta, out guide))
			{
				top += delta;
				_guideY = guide;
				_hasGuideY = true;
			}
		}
		else if (UiGizmoMath.AffectsBottom(_dragHandle))
		{
			if (UiSnap.TrySnapEdge(bottom, _targetYs, UiSnap.ThresholdPixels, out delta, out guide))
			{
				bottom += delta;
				_guideY = guide;
				_hasGuideY = true;
			}
		}

		float minX = Mathf.Min(left, right);
		float maxX = Mathf.Max(left, right);
		float minY = Mathf.Min(top, bottom);
		float maxY = Mathf.Max(top, bottom);
		pos = new Vector2(minX, minY);
		size = new Vector2(maxX - minX, maxY - minY);
	}

	private void ClearGuides()
	{
		_hasGuideX = false;
		_hasGuideY = false;
	}

	/// <summary>
	/// Keeps a guide only when the final (post-clamp) rect still touches it, so the frame clamp can
	/// never leave a guide drawn for an alignment that no longer holds.
	/// </summary>
	private void ValidateMoveGuides(Vector2 pos, Vector2 size)
	{
		if (_hasGuideX) _hasGuideX = UiSnap.RectTouchesX(pos, size, _guideX);
		if (_hasGuideY) _hasGuideY = UiSnap.RectTouchesY(pos, size, _guideY);
	}

	/// <summary>Resize equivalent: only the edge under the handle is allowed to justify a guide.</summary>
	private void ValidateResizeGuides(Vector2 pos, Vector2 size)
	{
		if (_hasGuideX)
		{
			float edge = UiGizmoMath.AffectsLeft(_dragHandle) ? pos.X : pos.X + size.X;
			_hasGuideX = Mathf.Abs(edge - _guideX) <= UiSnap.GuideEpsilon;
		}
		if (_hasGuideY)
		{
			float edge = UiGizmoMath.AffectsTop(_dragHandle) ? pos.Y : pos.Y + size.Y;
			_hasGuideY = Mathf.Abs(edge - _guideY) <= UiSnap.GuideEpsilon;
		}
	}

	/// <summary>Draws the eight resize handles for the selected element (Resize mode, or mid-resize).</summary>
	private void DrawHandles(LotUIElement element, Vector2 origin, bool visible)
	{
		if (element == null || !visible) return;

		float half = HandleDrawSize * 0.5f;
		for (int i = 0; i < UiGizmoMath.HandleCount; i++)
		{
			Vector2 center = origin + UiGizmoMath.HandlePoint(i, element.Position, element.Size);
			Vector2 min = center - new Vector2(half, half);
			Vector2 max = center + new Vector2(half, half);
			ImGui.DrawRectFilled(min, max, i == _hotHandle ? HandleHotFill : HandleFill);
			ImGui.DrawRect(min, max, HandleBorder, 0f, 1f);
		}
	}

	/// <summary>Draws the active alignment guides as full-frame lines.</summary>
	private void DrawGuides(Vector2 origin, Vector2 size)
	{
		if (_hasGuideX)
			ImGui.DrawLine(origin + new Vector2(_guideX, 0f), origin + new Vector2(_guideX, size.Y), GuideColor, 1f);
		if (_hasGuideY)
			ImGui.DrawLine(origin + new Vector2(0f, _guideY), origin + new Vector2(size.X, _guideY), GuideColor, 1f);
	}
}