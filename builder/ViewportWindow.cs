using System;
using Godot;

/// <summary>
/// The 3D lot view as a dockable ImGui window. The SubViewport render target is resized to match
/// the displayed image rect (2px hysteresis, same guard as Unity's ViewportRenderTextureScaler),
/// so the view always maps 1:1 to the window and never stretches at any size or aspect ratio.
/// Also ports ViewportClickHandler (background click clears selection) and gathers freecam
/// right-drag look deltas while the ImGui layout state is valid.
/// </summary>
public class ViewportWindow : ICameraInputSource
{
	private const int MinImageSize = 8;
	private const int ResizeHysteresisPixels = 2;

	/// <summary>Chrome-less window flags for Game mode's fullscreen 3D view.</summary>
	private static readonly int GameWindowFlags = ImGui.WindowNoTitleBar | ImGui.WindowNoResize | ImGui.WindowNoMove
		| ImGui.WindowNoScrollbar | ImGui.WindowNoSavedSettings | ImGui.WindowNoDocking;

	private readonly BuilderScene _scene;

	private static readonly Color SuspendedBannerColor = new Color(1f, 0.35f, 0.3f, 1f);

	private long _textureId;
	private Texture2D _registeredTexture;
	private Vector2 _imageOrigin;
	private Vector2 _imageSize;
	private Vector2 _lastDragDelta;
	private bool _looking;

	/// <summary>Set by Builder every layout pass: false while a pause/confirm modal owns input.</summary>
	public bool InputEnabled { get; set; } = true;

	/// <summary>Layout-time answer to "may the freecam read input?" — editor session only.</summary>
	public bool EditorCameraActive { get; private set; }

	/// <summary>Layout-time answer to "may the player camera read input?" — player session only.</summary>
	public bool PlayerCameraActive { get; private set; }

	/// <summary>Right-drag look delta gathered during the last layout pass. Handed to whichever
	/// camera is active by <see cref="TakeLookDelta"/> — ImGui wrapper state is only valid during
	/// layout, so it is collected there and consumed later, from a camera's _Process.</summary>
	private Vector2 _pendingLookDelta;

	public ViewportWindow(BuilderScene scene)
	{
		_scene = scene;
		// Both cameras read from this one source, so the editor/player handoff is resolved in one
		// place alongside the ImGui focus and modal conditions.
		scene.BindCameraInput(this);
	}

	public void Draw(Builder builder)
	{
		// Game mode (§3.4) plays the same lot with no creation-environment tooling: the 3D view fills
		// the display in its own undocked window, so the editor's docking state survives the trip.
		bool fullscreen = _scene.InGameMode;
		if (fullscreen)
		{
			Vector2 display = builder.GetViewport().GetVisibleRect().Size;
			ImGui.SetNextWindowPos(0f, 0f, ImGui.CondAlways);
			ImGui.SetNextWindowSize(display.X, display.Y, ImGui.CondAlways);
		}
		else
		{
			ImGui.SetNextWindowSize(900f, 560f, ImGui.CondFirstUseEver);
			ImGui.SetNextWindowPos(280f, 40f, ImGui.CondFirstUseEver);
		}

		bool open = fullscreen
			? ImGui.Begin("##GameViewport", GameWindowFlags)
			: ImGui.Begin("Viewport", EditorChrome.PanelWindowFlags);
		if (!open)
		{
			ImGui.End();
			ClearCameraInput();
			return;
		}

		// Scripting status banner: suspension must be visible in the editor, not just in the log.
		if (!fullscreen && LuaManager.Instance.ScriptingSuspended)
		{
			ImGui.PushStyleColor(ImGui.ColText, SuspendedBannerColor);
			ImGui.TextWrapped("SCRIPTING SUSPENDED — " + (LuaManager.Instance.SuspensionReason ?? "reason unknown"));
			ImGui.PopStyleColor(1);
			ImGui.TextDisabled("Scripts stay disabled until the lot is reloaded.");
		}

		SubViewport viewport = _scene.LotViewport;
		Texture2D texture = viewport.GetTexture();
		if (texture != null && texture != _registeredTexture)
		{
			_textureId = ImGui.RegisterTexture(texture);
			_registeredTexture = texture;
		}

		Vector2 avail = ImGui.GetContentRegionAvail();
		bool hasImage = texture != null && _textureId != 0 && avail.X > MinImageSize && avail.Y > MinImageSize;
		if (hasImage)
			ImGui.Image(_textureId, avail);
		else
			ImGui.TextDisabled("3D view unavailable.");

		_imageOrigin = ImGui.GetItemRectMin();
		_imageSize = ImGui.GetItemRectSize();
		// The UI editor clamps every element inside this rect, so it is the canvas the lot UI lives
		// on. SetUiCanvasBounds ignores degenerate sizes, so a collapsed window cannot collapse it.
		_scene.SetUiCanvasBounds(_imageSize);

		// Match the render target to the displayed rect: the camera output always equals the
		// window content, so resizing can never stretch the picture.
		Vector2I wanted = new Vector2I((int)(_imageSize.X + 0.5f), (int)(_imageSize.Y + 0.5f));
		Vector2I current = viewport.Size;
		if (wanted.X > MinImageSize && wanted.Y > MinImageSize
			&& (Math.Abs(wanted.X - current.X) > ResizeHysteresisPixels || Math.Abs(wanted.Y - current.Y) > ResizeHysteresisPixels))
		{
			viewport.Size = wanted;
		}

		// ImGui.Image is a non-interactive item (added with id 0), so while the cursor is over the 3D
		// view ImGui has no active item — and ImGui only begins a window-move drag when nothing is
		// active. Dragging a gizmo handle therefore slid the whole window instead of the object.
		// An invisible button covering the exact image rect claims the press for the viewport and
		// keeps the window still; it draws nothing, so the picture is unchanged, and it does not
		// collide with the image because that item has no id. The gizmo is unaffected either way:
		// it reads raw mouse state (GetMousePos / IsMouseDown), not ImGui item state.
		//
		// Unity ViewportClickHandler parity: this same click clears the selection. It is sampled
		// here, before the gizmo runs, so the decision below can consult WantsMouse.
		bool imageActive = false;
		if (hasImage)
		{
			ImGui.SetCursorScreenPos(_imageOrigin);
			// Claim the press for the viewport so ImGui does not start a window-move drag. The
			// return value is not used: a click is acted on at press time, not at release.
			ImGui.InvisibleButton("##viewport_input", _imageSize);
			// IsItemActive stays true for as long as the press is held, even once the cursor leaves
			// the viewport, so a drag keeps tracking instead of freezing at the edge. This is the
			// same item state the old background-click handler relied on; IsWindowHovered is not
			// used here because the docked Viewport window does not report hover reliably.
			imageActive = ImGui.IsItemActive();
		}

		// Gizmo: updates hover/drag state and draws its handles into this window's draw list, so they
		// land on top of the 3D image and stay clipped to it.
		//
		// In UI mode the left button belongs to the lot UI overlay: the 3D gizmo and part dragging are
		// suspended so nothing competes for the click, while the freecam (right-drag look, WASD) stays
		// live so the background can still be framed.
		if (_scene.InTestMode)
		{
			// Player session (§3.4): no editor tooling. The camera handoff has already moved look
			// input to the player camera; the gizmo, part dragging and selection clicks stay off so
			// nothing competes with the player. The lot's own UI overlay still draws — it belongs to
			// the lot, not to the editor.
			builder.Gizmo.Suspend();
			LotUiRenderer.DrawAll(_scene.LotUIRoot, _imageOrigin, _imageSize, builder.Selection);
		}
		else if (builder.Mode == BuilderMode.UI)
		{
			builder.Gizmo.Suspend();
			LotUiRenderer.DrawAll(_scene.LotUIRoot, _imageOrigin, _imageSize, builder.Selection);
			builder.UiGizmo.Update(_imageOrigin, _imageSize);
		}
		else if (builder.ConstraintPick.IsArmed)
		{
			// The constraint tool owns the click while it is armed: no gizmo, no selection change,
			// no part drag — just the pick the gesture asked for.
			builder.Gizmo.Suspend();
			HandleConstraintPick(builder, imageActive);
			LotUiRenderer.DrawAll(_scene.LotUIRoot, _imageOrigin, _imageSize, builder.Selection);
		}
		else
		{
			builder.Gizmo.Update(_imageOrigin, _imageSize);

			// A click in the 3D view always selects the part under the cursor (Ctrl/Shift toggles
			// membership) and a background click clears the selection. Only Select mode also drags the
			// part; in Translate/Rotate/Scale the gizmo owns the drag, and while one of its handles is
			// hovered or held it owns the click as well.
			if (!builder.Gizmo.WantsMouse)
			{
				// One mode decision, two consumers: the part drag owns Select, the decal face tool
				// owns Decal, and both read it here rather than re-deriving it later.
				GizmoMode mode = builder.Toolbox.CurrentGizmoMode;
				HandlePointerInput(builder, imageActive, mode == GizmoMode.Select, mode == GizmoMode.Decal);
			}

			// Lot UI elements render as a screen-space overlay clipped to this window.
			LotUiRenderer.DrawAll(_scene.LotUIRoot, _imageOrigin, _imageSize, builder.Selection);
		}

		UpdateLook();
		// Resolve the layout-time conditions once, for both cameras: exactly one of these can be
		// true, and only the Current camera consumes the delta handed out by TakeLookDelta().
		bool editorActive, playerActive;
		ResolveCameraAuthority(InputEnabled, ImGui.IsWindowFocused(), _scene.InTestMode, out editorActive, out playerActive);
		EditorCameraActive = editorActive;
		PlayerCameraActive = playerActive;

		// Lot title overlay (stand-in for Unity's TitlePanel).
		if (builder.LotName.Length > 0)
			ImGui.DrawText(_imageOrigin + new Vector2(8f, 6f), new Color(1f, 1f, 1f, 0.85f), builder.LotName);

		ImGui.End();
	}

	/// <summary>
	/// Constraint-tool pointer handling: one queued click ray per left press while the tool is
	/// armed. Like <see cref="HandlePointerInput"/> this only records intent here (the ImGui state
	/// is valid in this pass); the raycast itself runs in the physics step.
	/// </summary>
	private void HandleConstraintPick(Builder builder, bool imageActive)
	{
		if (!ImGui.IsMouseClicked(ImGui.MouseButtonLeft) || !imageActive) return;
		Vector2 mouse = ImGui.GetMousePos();
		ViewportRay.FromScreen(_scene.Freecam, mouse, _imageOrigin, _imageSize, _scene.LotViewport.Size,
			out Vector3 rayOrigin, out Vector3 rayDirection);
		builder.ConstraintPick.QueueClick(rayOrigin, rayDirection);
	}

	/// <summary>
	/// Viewport pointer handling. Only mouse intent is recorded here (the ImGui state is valid in
	/// this layout pass); the pick and the collision-resolved move run in Builder._PhysicsProcess,
	/// where Godot allows space-state queries and kinematic moves.
	/// </summary>
	private void HandlePointerInput(Builder builder, bool imageActive, bool allowDrag, bool decalTool)
	{
		Vector2 mouse = ImGui.GetMousePos();
		bool pressed = ImGui.IsMouseClicked(ImGui.MouseButtonLeft) && imageActive;
		bool released = ImGui.IsMouseReleased(ImGui.MouseButtonLeft);
		bool down = ImGui.IsMouseDown(ImGui.MouseButtonLeft);

		if (pressed || (down && (imageActive || builder.Parts.IsDragging)))
		{
			ViewportRay.FromScreen(_scene.Freecam, mouse, _imageOrigin, _imageSize, _scene.LotViewport.Size,
				out Vector3 rayOrigin, out Vector3 rayDirection);

			if (pressed)
			{
				bool additive = Input.IsPhysicalKeyPressed(Key.Ctrl) || Input.IsPhysicalKeyPressed(Key.Shift);
				builder.Parts.QueuePress(rayOrigin, rayDirection, additive, allowDrag, decalTool);
			}
			else
			{
				builder.Parts.UpdateCursor(rayOrigin, rayDirection);
			}
		}

		if (released)
		{
			builder.Parts.QueueRelease();
		}
	}

	private void UpdateLook()
	{
		// Only a right-press that starts over the viewport begins a look drag (Unity parity);
		// once started it keeps tracking until release, even with the cursor outside the window.
		if (ImGui.IsMouseClicked(ImGui.MouseButtonRight) && ImGui.IsWindowHovered())
		{
			_looking = true;
			_lastDragDelta = Vector2.Zero;
		}
		if (!ImGui.IsMouseDown(ImGui.MouseButtonRight))
			_looking = false;

		if (!_looking)
		{
			_lastDragDelta = Vector2.Zero;
			return;
		}

		// GetMouseDragDelta is cumulative since press; accumulate only the per-frame difference.
		Vector2 drag = ImGui.GetMouseDragDelta(ImGui.MouseButtonRight);
		_pendingLookDelta += drag - _lastDragDelta;
		_lastDragDelta = drag;
	}

	/// <summary>
	/// Hands the accumulated look delta to one consumer and clears it, so a single drag is never
	/// applied twice (the freecam and the player camera share this one source per §3.4).
	/// </summary>
	public Vector2 TakeLookDelta()
	{
		Vector2 delta = _pendingLookDelta;
		_pendingLookDelta = Vector2.Zero;
		return delta;
	}

	/// <summary>
	/// Decides which camera (if any) may read input, from the layout-time conditions. Pure, so the
	/// §3.4 requirement that the player-session gate COMPOSE with the modal gate (rather than
	/// overwrite it) is verifiable without an ImGui frame: a modal denies both cameras, and the
	/// session then decides which one gets the rest.
	/// </summary>
	public static void ResolveCameraAuthority(bool inputEnabled, bool focused, bool playerSession,
		out bool editorActive, out bool playerActive)
	{
		bool viewportOwned = inputEnabled && focused;
		editorActive = viewportOwned && !playerSession;
		playerActive = viewportOwned && playerSession;
	}

	/// <summary>Drops camera authority (window closed): neither camera may read input this frame.</summary>
	private void ClearCameraInput()
	{
		EditorCameraActive = false;
		PlayerCameraActive = false;
	}
}
