using Godot;

/// <summary>
/// Build-mode fly camera, ported from Unity's FreecamController: WASD + Space/Ctrl to fly,
/// Shift to sprint, right-drag to look. All input is gated on the Viewport ImGui window having
/// focus (the builder focus rule: click back into the viewport to regain camera control).
/// Look deltas are gathered during the ImGui layout pass (ViewportWindow) and consumed here,
/// because the ImGui wrapper state is only valid inside the layout callback.
///
/// The camera handoff (§3.4) is two conditions: this camera acts only while it is Godot-Current
/// and the shared input source reports an editor session. The player camera checks the mirror
/// conditions, so exactly one of them ever responds to WASD or a look drag.
/// </summary>
public partial class FreecamController : Camera3D
{
	public float MoveSpeed = 12f;
	public float SprintMultiplier = 2.5f;
	public float LookSensitivity = 0.15f; // degrees per pixel of drag (Unity value)

	private ICameraInputSource _inputSource;

	/// <summary>Wired by ViewportWindow's constructor so input can be gated on viewport focus and
	/// on which session is running (editor vs. player).</summary>
	public void Bind(ICameraInputSource source)
	{
		_inputSource = source;
	}

	public override void _Process(double delta)
	{
		// Non-current means the player camera owns the view (§3.4 handoff): never read WASD then.
		if (!Current || _inputSource == null || !_inputSource.EditorCameraActive) return;

		ApplyLookDelta();
		ApplyMovement((float)delta);
	}

	private void ApplyLookDelta()
	{
		Vector2 pending = _inputSource.TakeLookDelta();
		if (pending == Vector2.Zero) return;

		float yaw = RotationDegrees.Y - pending.X * LookSensitivity;
		float pitch = Mathf.Clamp(RotationDegrees.X - pending.Y * LookSensitivity, -89f, 89f);
		RotationDegrees = new Vector3(pitch, yaw, 0f);
	}

	private void ApplyMovement(float delta)
	{
		Basis basis = GlobalTransform.Basis;
		Vector3 move = Vector3.Zero;
		if (Input.IsPhysicalKeyPressed(Key.W)) move += -basis.Z;
		if (Input.IsPhysicalKeyPressed(Key.S)) move += basis.Z;
		if (Input.IsPhysicalKeyPressed(Key.D)) move += basis.X;
		if (Input.IsPhysicalKeyPressed(Key.A)) move += -basis.X;
		if (Input.IsPhysicalKeyPressed(Key.Space)) move += Vector3.Up;
		if (Input.IsPhysicalKeyPressed(Key.Ctrl)) move += Vector3.Down;

		if (move == Vector3.Zero) return;

		float speed = MoveSpeed;
		if (Input.IsPhysicalKeyPressed(Key.Shift)) speed *= SprintMultiplier;
		GlobalPosition += move.Normalized() * speed * delta;
	}
}
