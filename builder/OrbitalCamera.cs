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

	// --- Modes (milestone 3.10, design doc) ---
	/// <summary>Where the camera sits and what it looks at. Third-person is the default session
	/// behaviour; scripts drive the others through Lot.SetCameraMode / Lot.FlyCamera.</summary>
	public LotCameraMode Mode { get; private set; } = LotCameraMode.ThirdPerson;

	/// <summary>The pose Fixed mode holds. Each side tracks whether a creator set it, so entering
	/// fixed mode captures whichever side was not set from the current pose ("switch to fixed"
	/// always means "freeze where you are" until told otherwise).</summary>
	public Vector3 FixedPosition;
	public Vector3 FixedTarget;
	private bool _fixedPositionSet;
	private bool _fixedTargetSet;

	// Shot state (Scripted mode): where the shot started and goes, how long it takes, which curve.
	// One shot at a time; a new FlyCamera replaces the running one.
	private bool _shotActive;
	private int _shotId;
	private int _nextShotId = 1;
	private double _shotElapsed;
	private double _shotDuration;
	private LotEasingKind _shotEasing;
	private Vector3 _shotFrom;
	private Vector3 _shotTo;
	private Vector3 _shotFromTarget;
	private Vector3 _shotToTarget;
	private LotCameraMode _modeBeforeShot = LotCameraMode.ThirdPerson;

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

	// --- mode switching (milestone 3.10) --------------------------------------------------------

	/// <summary>Sets the fixed pose's position (Lot.SetCameraPosition).</summary>
	public void SetFixedPosition(Vector3 position)
	{
		FixedPosition = position;
		_fixedPositionSet = true;
	}

	/// <summary>Sets the fixed pose's look target (Lot.SetCameraTarget).</summary>
	public void SetFixedTarget(Vector3 target)
	{
		FixedTarget = target;
		_fixedTargetSet = true;
	}

	/// <summary>
	/// Switches the camera mode (Lot.SetCameraMode). Entering Fixed captures whichever side of the
	/// fixed pose was never set from the current pose, so "switch to fixed" always means "hold
	/// where you are / look where you look" until the creator says otherwise. Scripted is refused
	/// here — FlyCamera is the only way into a shot.
	/// </summary>
	public bool SetMode(LotCameraMode mode)
	{
		if (mode == LotCameraMode.Scripted) return false;
		if (mode == LotCameraMode.Fixed)
		{
			if (!_fixedPositionSet) FixedPosition = GlobalPosition;
			if (!_fixedTargetSet) FixedTarget = CurrentLookPoint();
		}
		_shotActive = false;
		Mode = mode;
		return true;
	}

	/// <summary>Resets to the default third-person mode with no shot — session entry and exit.</summary>
	public void ResetModes()
	{
		_shotActive = false;
		Mode = LotCameraMode.ThirdPerson;
	}

	/// <summary>
	/// Starts a camera shot (Lot.FlyCamera): fly from the current pose to the given pose over the
	/// duration with the easing, then return to the mode that was active. Returns the shot id the
	/// cancel token uses; a new shot replaces whatever is running.
	/// </summary>
	public int StartShot(Vector3 toPosition, Vector3 toTarget, float duration, LotEasingKind easing)
	{
		_modeBeforeShot = Mode == LotCameraMode.Scripted ? _modeBeforeShot : Mode;
		_shotFrom = GlobalPosition;
		_shotFromTarget = CurrentLookPoint();
		_shotTo = toPosition;
		_shotToTarget = toTarget;
		_shotElapsed = 0.0;
		_shotDuration = duration;
		_shotEasing = easing;
		_shotId = _nextShotId++;
		_shotActive = true;
		Mode = LotCameraMode.Scripted;
		return _shotId;
	}

	/// <summary>
	/// Stops the shot with that id, freezing the camera exactly where it is: the current pose
	/// becomes the fixed pose, so the view simply stops ("cancel: the camera stops where it is").
	/// False when that shot is not the running one — the token's harmless no-op path.
	/// </summary>
	public bool StopShot(int id)
	{
		if (!_shotActive || id != _shotId) return false;
		_shotActive = false;
		FixedPosition = GlobalPosition;
		FixedTarget = CurrentLookPoint();
		_fixedPositionSet = true;
		_fixedTargetSet = true;
		Mode = LotCameraMode.Fixed;
		return true;
	}

	/// <summary>The point the camera currently aims at: the orbit focus in third person, the fixed
	/// target in fixed mode, or a forward point from the current facing (the "from" side of a shot
	/// and the frozen target of a cancelled one).</summary>
	public Vector3 CurrentLookPoint()
	{
		if (Mode == LotCameraMode.ThirdPerson) return Target + Vector3.Up * FocusHeight;
		if (Mode == LotCameraMode.Fixed) return FixedTarget;
		return GlobalPosition + -GlobalTransform.Basis.Z * 10f;
	}

	public override void _Process(double delta)
	{
		// Only the Current camera acts: the handoff flips Camera3D.Current, so a non-current player
		// camera must never move or consume look input (the freecam owns it in the editor, and
		// vice versa).
		if (!Current) return;

		// Fixed and scripted modes own their pose without reading input, so they update whenever
		// they are Current — a cutscene or a pinned camera must not freeze because a modal is up
		// (and the headless probes, where the ImGui-derived gate never resolves, can still see
		// them work).
		if (Mode == LotCameraMode.Scripted)
		{
			AdvanceShot(delta);
			return;
		}
		if (Mode == LotCameraMode.Fixed)
		{
			GlobalPosition = FixedPosition;
			LookAt(FixedTarget, Vector3.Up);
			return;
		}

		// Third and first person read the SHARED look input, so their LOOK respects the handoff
		// gate the two cameras arbitrate through (the ImGui layout resolves it once per frame).
		// The pose itself follows regardless: a modal may deny input, but it must not freeze the
		// view or detach it from the character (and the headless probes, where the ImGui-derived
		// gate never resolves, can still see the poses work).
		if (_input != null && _input.PlayerCameraActive)
		{
			Vector2 look = _input.TakeLookDelta();
			if (look != Vector2.Zero)
			{
				_yaw -= look.X * LookSensitivity;
				_pitch = Mathf.Clamp(_pitch - look.Y * LookSensitivity, MinPitchDeg, MaxPitchDeg);
			}
		}

		if (Mode == LotCameraMode.FirstPerson)
		{
			// The same look drives first person — no second input path — but the camera sits at
			// the character's eye and aims straight down the yaw/pitch, so §5.4's sensitivity
			// later applies to both modes at once.
			GlobalPosition = LotCameraMath.FirstPersonPosition(Target, FocusHeight);
			GlobalRotationDegrees = new Vector3(_pitch, _yaw, 0f);
			return;
		}

		ApplyTransform();
	}

	/// <summary>One frame of a running shot: interpolate position and look target with the same
	/// easing engine the tween verbs use, then return to the mode that was active before (design
	/// doc D4).</summary>
	private void AdvanceShot(double delta)
	{
		_shotElapsed += delta;
		float t = _shotDuration <= 0.0 ? 1f : Mathf.Min((float)(_shotElapsed / _shotDuration), 1f);
		float eased = LotEasing.Apply(_shotEasing, t);
		GlobalPosition = _shotFrom.Lerp(_shotTo, eased);
		LookAt(_shotFromTarget.Lerp(_shotToTarget, eased), Vector3.Up);
		if (t >= 1f)
		{
			_shotActive = false;
			Mode = _modeBeforeShot;
		}
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
