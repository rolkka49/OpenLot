using Godot;

/// <summary>
/// The one place camera controllers read input from (milestone 3.4, camera ownership handoff).
/// ImGui's layout state is only valid inside the layout pass, so the Viewport window resolves the
/// per-frame conditions there (viewport focus, modal state, editor vs. player session) and the
/// cameras consume that answer later, from their own _Process.
///
/// Two cameras share one source but never both act: the build-mode freecam may read input only in
/// the editor, the player camera only during a player session — and on top of that only the
/// Camera3D that is Godot-<c>Current</c> moves at all. The camera handoff is therefore just a
/// <c>Current</c> flip plus the session flag this interface reports.
/// </summary>
public interface ICameraInputSource
{
	/// <summary>True while the editor's freecam is allowed to read input.</summary>
	bool EditorCameraActive { get; }

	/// <summary>True while the player session's camera is allowed to read input.</summary>
	bool PlayerCameraActive { get; }

	/// <summary>
	/// Returns the look delta accumulated during the last layout pass and clears it, so exactly one
	/// consumer acts on a given delta per frame (whichever camera is active).
	/// </summary>
	Vector2 TakeLookDelta();
}
