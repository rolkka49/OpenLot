using Godot;

/// <summary>
/// Title-bar-only movement for an ImGui window begun with <see cref="ImGui.WindowNoMove"/>.
///
/// Why this exists: ImGui's own "move from title bar only" switch is an <c>io</c> config value, and
/// the vendored addon exposes no IO (see the note on <see cref="EditorChrome"/>, which documents the
/// same limitation for dock-node flags). Without it, a window can be dragged by its *body* — so a
/// panel that needs the body for its own mouse gestures (the hierarchy's marquee) cannot have both.
/// Turning movement off with <see cref="ImGui.WindowNoMove"/> and re-adding it here for the title bar
/// only is the version that works with the API the addon actually exposes.
///
/// Usage: <see cref="BeforeBegin"/> immediately before <c>ImGui.Begin</c>, then <see cref="AfterBegin"/>
/// immediately after it.
/// </summary>
public sealed class TitleBarWindowDrag
{
	/// <summary>Where the window currently is, in screen space. Known only once ImGui has laid the
	/// window out at least once, which is what keeps the panel's own first-use default working.</summary>
	private Vector2 _position;
	private bool _positionKnown;

	/// <summary>Window position when the current title-bar drag started, so the drag is computed from
	/// the press point instead of accumulating per-frame deltas.</summary>
	private Vector2 _dragOrigin;
	private bool _dragging;

	/// <summary>
	/// Pins the window to its tracked position, or applies <paramref name="initialPosition"/> the first
	/// time (as a normal first-use default, so a saved layout still wins on the very first frame).
	/// </summary>
	public void BeforeBegin(Vector2 initialPosition)
	{
		if (!_positionKnown)
		{
			ImGui.SetNextWindowPos(initialPosition.X, initialPosition.Y, ImGui.CondFirstUseEver);
			return;
		}
		ImGui.SetNextWindowPos(_position.X, _position.Y, ImGui.CondAlways);
	}

	/// <summary>
	/// Re-syncs the tracked position from ImGui (so a layout file's position is honoured) and runs the
	/// title-bar drag. <paramref name="windowOpen"/> is what <c>ImGui.Begin</c> returned: a closed window
	/// is not laid out, so its reported position must not be trusted.
	/// </summary>
	public void AfterBegin(bool windowOpen)
	{
		if (!windowOpen)
		{
			_dragging = false;
			return;
		}

		_position = ImGui.GetWindowPos();
		_positionKnown = true;

		// ImGui's title bar is one frame-height tall, starting at the window's top-left.
		Vector2 mouse = ImGui.GetMousePos();
		Vector2 windowSize = ImGui.GetWindowSize();
		bool overTitleBar = mouse.X >= _position.X && mouse.X <= _position.X + windowSize.X
			&& mouse.Y >= _position.Y && mouse.Y <= _position.Y + ImGui.GetFrameHeight();

		if (!_dragging && overTitleBar && ImGui.IsMouseClicked(ImGui.MouseButtonLeft))
		{
			_dragging = true;
			_dragOrigin = _position;
		}

		if (_dragging && ImGui.IsMouseDown(ImGui.MouseButtonLeft))
			_position = _dragOrigin + ImGui.GetMouseDragDelta(ImGui.MouseButtonLeft);
		else
			_dragging = false;
	}
}
