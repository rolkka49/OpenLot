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

	/// <summary>The session's starting orbit distance — the value every session entry and exit
	/// returns to (milestone 3.14: zoom is a view, never lot state).</summary>
	public const float DefaultDistance = 7f;

	/// <summary>The damped orbit distance. Player zoom glides this toward <see cref="ZoomTarget"/>;
	/// scripts and the fixed/scripted modes leave it alone (those modes are zoom-proof).</summary>
	public float Distance = DefaultDistance;

	// --- player zoom (milestone 3.14) -----------------------------------------------------------
	/// <summary>The wheel's target: <see cref="AddZoom"/> moves it, <see cref="Distance"/> glides
	/// toward it, and <see cref="ResetModes"/> returns both to <see cref="DefaultDistance"/>.</summary>
	public float ZoomTarget { get; private set; } = DefaultDistance;

	/// <summary>Closest the wheel can pull the orbit in. At this distance the blend below is fully
	/// first person, so it doubles as the scripted first-person pose (see <see cref="AddZoom"/>).</summary>
	public float ZoomMin = 1.0f;

	/// <summary>Farthest the wheel can push the orbit out.</summary>
	public float ZoomMax = 14f;

	/// <summary>How much one wheel notch moves the target, in metres.</summary>
	public float ZoomStep = 1.0f;

	/// <summary>Glide rate for <see cref="Distance"/> toward the target (1/seconds, the exponential
	/// <see cref="LotCameraMath.Damp"/> contract). Exposed data so §5.4's camera settings can own it.</summary>
	public float ZoomDampRate = 14f;

	/// <summary>Distance at which the orbit pose starts blending toward the eye; between here and
	/// <see cref="ZoomMin"/> the view is a smooth mix of the two (pure math:
	/// <see cref="LotCameraMath.BlendFactor"/> + <see cref="LotCameraMath.Smoothstep"/>), so
	/// "zoom in far enough" becomes first person with no seam.</summary>
	public float BlendStart = 3.5f;

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

	/// <summary>Resets to the default third-person mode with no shot — session entry and exit.
	/// The zoom resets with it (milestone 3.14): a session starts at the default distance and
	/// leaving cannot leak a player's viewing distance into the next one.</summary>
	public void ResetModes()
	{
		_shotActive = false;
		Mode = LotCameraMode.ThirdPerson;
		Distance = DefaultDistance;
		ZoomTarget = DefaultDistance;
	}

	/// <summary>
	/// The wheel's entry point (milestone 3.14): called by <c>_Process</c> when the player may zoom
	/// (a running session, the camera handoff active, mode third-person or first-person) and
	/// directly by the self-test — one entry point, so the effect has one implementation.
	///
	/// Positive <paramref name="steps"/> (wheel up) pulls the orbit in; negative pushes it out,
	/// clamped to [<see cref="ZoomMin"/>, <see cref="ZoomMax"/>]. Fixed and scripted cameras are
	/// zoom-proof. In first person, any outward step is how the player takes the view back: the
	/// mode switches to third person at the band's inner end, so the hand-off is seamless.
	/// </summary>
	public void AddZoom(int steps)
	{
		if (steps == 0) return;
		if (Mode == LotCameraMode.FirstPerson)
		{
			if (steps > 0) return; // already fully in — there is no closer
			Mode = LotCameraMode.ThirdPerson;
			Distance = ZoomMin;
			ZoomTarget = Mathf.Clamp(ZoomMin - steps * ZoomStep, ZoomMin, ZoomMax);
			return;
		}
		if (Mode != LotCameraMode.ThirdPerson) return;
		ZoomTarget = Mathf.Clamp(ZoomTarget - steps * ZoomStep, ZoomMin, ZoomMax);
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
			AimAt(FixedTarget);
			return;
		}

		// Third and first person read the SHARED look input, so their LOOK respects the handoff
		// gate the two cameras arbitrate through (the ImGui layout resolves it once per frame).
		// The pose itself follows regardless: a modal may deny input, but it must not freeze the
		// view or detach it from the character (and the headless probes, where the ImGui-derived
		// gate never resolves, can still see the poses work).
		// The wheel (milestone 3.14) is gated the same way: a modal denies zoom exactly as it
		// denies look, and AddZoom is the one entry point the gate funnels into.
		if (_input != null && _input.PlayerCameraActive)
		{
			Vector2 look = _input.TakeLookDelta();
			if (look != Vector2.Zero)
			{
				_yaw -= look.X * LookSensitivity;
				_pitch = Mathf.Clamp(_pitch - look.Y * LookSensitivity, MinPitchDeg, MaxPitchDeg);
			}
			int wheel = LotInputActions.Wheel;
			if (wheel != 0) AddZoom(wheel);
		}

		if (Mode == LotCameraMode.FirstPerson)
		{
			// The same look drives first person — no second input path — but the camera sits at
			// the character's eye and aims straight down the yaw/pitch, so §5.4's sensitivity
			// later applies to both modes at once. The zoom is pinned fully in, so the scripted
			// first person and the wheel's inner end are the same pose (one seamless contract).
			Distance = ZoomMin;
			GlobalPosition = LotCameraMath.FirstPersonPosition(Target, FocusHeight);
			GlobalRotationDegrees = new Vector3(_pitch, _yaw, 0f);
			return;
		}

		// The zoom glide: the wheel moved the target, the actual distance eases toward it
		// (exponential damping — pure math in LotCameraMath — snapping when close so the
		// "lands exactly on the previous orbit" contract holds).
		Distance = LotCameraMath.Damp(Distance, ZoomTarget, ZoomDampRate, (float)delta);

		if (Distance < BlendStart)
		{
			// Inside the band the pose is a smooth mix of the orbit and the eye: zooming all the
			// way in *becomes* first person, and the same math runs in reverse on the way out.
			float t = LotCameraMath.Smoothstep(LotCameraMath.BlendFactor(Distance, BlendStart, ZoomMin));
			Vector3 focus = Target + Vector3.Up * FocusHeight;
			Basis basis = Basis.FromEuler(new Vector3(Mathf.DegToRad(_pitch), Mathf.DegToRad(_yaw), 0f));
			Vector3 orbitPosition = focus + basis.Z * Distance;
			Vector3 eyePosition = LotCameraMath.FirstPersonPosition(Target, FocusHeight);
			Vector3 eyeLook = eyePosition + basis * Vector3.Forward * 10f;
			GlobalPosition = orbitPosition.Lerp(eyePosition, t);
			AimAt(focus.Lerp(eyeLook, t));
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
		AimAt(_shotFromTarget.Lerp(_shotToTarget, eased));
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
		AimAt(focus);
	}

	/// <summary>
	/// Aims the camera at a world point through <see cref="LotCameraMath.LookBasis"/>: identical
	/// to <c>LookAt(point, Vector3.Up)</c> for every ordinary pose, but a straight-down or
	/// straight-up aim resolves through the colinear fallback silently instead of Godot's
	/// per-frame "Target and up vectors are colinear" warning (a fixed camera pointing straight
	/// down used to log it every frame). One aim path for every mode, so no call site can
	/// reintroduce the warning.
	/// </summary>
	private void AimAt(Vector3 target)
	{
		GlobalTransform = new Transform3D(LotCameraMath.LookBasis(target - GlobalPosition, Vector3.Up), GlobalPosition);
	}
}
