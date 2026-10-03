using Godot;

/// <summary>
/// Player-session camera for Test/Game mode (§3.4): a third-person orbital camera that follows a
/// target point with right-drag look. It reads from the same layout-time source as the freecam
/// (<see cref="ICameraInputSource"/>) and only acts while it is the Current camera, so the freecam
/// and the player camera never both respond to the same drag — that is the camera handoff.
///
/// Movement itself lives in <see cref="CapsuleController"/>; this type only decides where the
/// player looks, and exposes <see cref="Yaw"/> so movement can be camera-relative.
/// </summary>
public partial class OrbitalCamera : Camera3D
{
	/// <summary>The point the camera orbits (the character's centre once one is attached).</summary>
	public Vector3 Target;

	public float Distance = 7f;

	/// <summary>Aim this far above the target (Vectors are in world units, so this looks at the
	/// character's chest rather than its feet).</summary>
	public float FocusHeight = 1.0f;

	public float LookSensitivity = 0.15f; // degrees per pixel, matching the freecam's Unity value
	public float MinPitchDeg = -70f;
	public float MaxPitchDeg = 70f;

	private float _yaw;
	private float _pitch;
	private ICameraInputSource _input;

	/// <summary>Horizontal orbit angle in degrees; the basis for camera-relative movement.</summary>
	public float Yaw { get { return _yaw; } }

	/// <summary>Wires the shared input source (look input is resolved by the Viewport window).</summary>
	public void Bind(ICameraInputSource source)
	{
		_input = source;
	}

	/// <summary>
	/// Seeds the orbit angles and target from the camera the player is handed off from, so entering
	/// a player session does not snap the view.
	/// </summary>
	public void SeedOrbit(float yawDegrees, float pitchDegrees, Vector3 target)
	{
		_yaw = yawDegrees;
		_pitch = Mathf.Clamp(pitchDegrees, MinPitchDeg, MaxPitchDeg);
		Target = target;
		ApplyTransform();
	}

	public void SetTarget(Vector3 target)
	{
		Target = target;
	}

	public override void _Process(double delta)
	{
		// Only the Current camera acts: the handoff flips Camera3D.Current, so a non-current player
		// camera must never consume look input (the freecam owns it in the editor, and vice versa).
		if (!Current || _input == null || !_input.PlayerCameraActive) return;

		Vector2 look = _input.TakeLookDelta();
		if (look != Vector2.Zero)
		{
			_yaw -= look.X * LookSensitivity;
			_pitch = Mathf.Clamp(_pitch - look.Y * LookSensitivity, MinPitchDeg, MaxPitchDeg);
		}
		ApplyTransform();
	}

	private void ApplyTransform()
	{
		Vector3 focus = Target + Vector3.Up * FocusHeight;
		Basis basis = Basis.FromEuler(new Vector3(Mathf.DegToRad(_pitch), Mathf.DegToRad(_yaw), 0f));
		// basis.Z points backwards, so this puts the camera behind the focus point along the orbit.
		GlobalPosition = focus + basis.Z * Distance;
		LookAt(focus, Vector3.Up);
	}
}
