using System.Collections.Generic;
using Godot;

/// <summary>
/// Verification for milestone 3.6 (welds and hinges): the record registry, the Roblox anchor rule,
/// the hinge axis basis, the dynamic-body swap and joint build/teardown, the `.lot` round trip of
/// the `constraints` block, the Float property kind the hinge editor introduced, and the bound Lua
/// verbs.
///
/// Follows the project's self-test convention: no framework, prints PASS/FAIL lines, returns the
/// failure count. Runs from <c>BuilderScene._Process</c> after the <c>_Ready</c> suites, because it
/// needs a live lot world and a Lua VM, and because its scratch Float descriptor must not shift the
/// property counts the earlier suites assert (it is unregistered again afterwards).
/// </summary>
public static class ConstraintSelfTest
{
	private static int _failures;
	private static int _checks;

	/// <summary>Scratch lot path under user:// (a headless run has a writable data dir); removed at
	/// the end so a test never leaves a stray file.</summary>
	private const string ScratchLotPath = "user://__constraint_selftest.lot";

	public static int Run(BuilderScene scene)
	{
		_failures = 0;
		_checks = 0;

		// The table is process-wide state: start and end clean so no test link leaks into the world.
		LotConstraints.Clear();
		// Calibration: the Run call itself is the integration point — a null scene means the caller
		// contract broke, which is a failure, not a skip.
		Check("harness: the lot scene is available", scene != null);
		if (scene == null)
		{
			GD.Print("[ConstraintSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
			return _failures;
		}

		TestBasis();
		TestPlayerPushMath();
		TestRegistry();
		TestAnchorRule();
		TestChainGauge();
		TestPersistence(scene);
		TestSession(scene);
		TestFullRoundTrip(scene);
		TestFloatKind(scene);
		TestLuaBindings();

		LotConstraints.Clear();
		GD.Print("[ConstraintSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
		// The one claim a synchronous suite cannot make — that the engine actually steps the session
		// and moves the parts — is verified across frames by the staged probe (TickGravityProbe).
		StartGravityProbe(scene);
		return _failures;
	}

	// --- staged gravity probe (runs across physics frames; driven by BuilderScene._Process) --------

	/// <summary>
	/// How long the probe waits before checking, in milliseconds. Wall-clock, not physics frames on
	/// purpose: headless main-loop iterations run far faster than real time, so a frame count can
	/// pass before physics has stepped even once — but physics itself advances with real time, so
	/// 900 ms guarantees ~54 steps (free fall ≈ 3.9 m, far past the 1 m assertion margin).
	/// </summary>
	private const ulong GravityProbeWaitMsec = 900;

	private static BuilderScene _probeScene;

	// The scenario: one free-falling part, an anchored/unanchored hinge pair, a weld frozen by its
	// anchored side, and a weld between two free parts.
	private static LotObject _probeFall;
	private static LotObject _probeHingeAnchor;
	private static LotObject _probeHingeSwing;
	private static Vector3 _probeHingePivot;
	private static LotObject _probeWeldFrozenA;
	private static LotObject _probeWeldFrozenB;
	private static LotObject _probeWeldFallA;
	private static LotObject _probeWeldFallB;

	// Starting poses, so "moved" is measured, not guessed.
	private static float _probeFallStartY;
	private static Vector3 _probeHingeAnchorStart;
	private static Vector3 _probeHingeSwingStart;
	private static Vector3 _probeWeldFrozenAStart;
	private static Vector3 _probeWeldFrozenBStart;
	private static Vector3 _probeWeldFallAStart;
	private static Vector3 _probeWeldFallBStart;

	// The player-push scenario: a floor the character can stand on, a loose part in its path, and
	// a synthetic W key that drives the walk exactly like a player's (see PressProbeKey).
	private static LotObject _probePushFloor;
	private static LotObject _probePushPart;
	private static float _probePushStartZ;
	private static float _probePlayerStartZ;

	private static ulong _probeDeadlineMsec;
	private static int _probeStage; // 0 = not started, 1 = waiting for time, 2 = finished
	private static int _probeChecks;
	private static int _probeFailures;

	/// <summary>Whether the staged probe completed; the scene warns when a run ends too early.</summary>
	public static bool GravityProbeDone { get { return _probeStage == 2; } }

	/// <summary>
	/// Starts the probe: it walks the REAL session entry path (EnterTestMode — the exact thing a
	/// creator presses) with an unanchored part in the lot, then waits for physics to run. This is
	/// the acceptance criterion "an unanchored object is affected by gravity", and it is also the
	/// regression for the first cut's bug: the physics body fell, but the part node never followed
	/// it, so nothing moved on screen.
	///
	/// Headless-only on purpose: the probe drives Test mode by itself, which would hijack an
	/// interactive debug session right after startup. Interactive verification is by hand.
	/// </summary>
	private static void StartGravityProbe(BuilderScene scene)
	{
		if (DisplayServer.GetName() != "headless")
		{
			_probeStage = 2;
			return;
		}

		_probeScene = scene;
		_probeChecks = 0;
		_probeFailures = 0;

		_probeFall = NewScratchPart(scene, "ConstraintProbeFall", new Vector3(190f, 25f, 190f));
		_probeFall.SetAnchored(false);

		// Hinge: an anchored post and a free arm, pivot 1 m along the post's +X. The axis comes from
		// the picker's own default rule (horizontal, perpendicular to the parts' line) — the
		// zero-torque vertical-axis case reads as "the hinge does nothing" but is a legitimate
		// equilibrium, so the probe pins the default that avoids it.
		_probeHingeAnchor = NewScratchPart(scene, "ConstraintProbeHingeAnchor", new Vector3(198f, 25f, 190f));
		_probeHingeSwing = NewScratchPart(scene, "ConstraintProbeHingeSwing", new Vector3(200f, 25f, 190f));
		_probeHingeSwing.SetAnchored(false);

		// A weld frozen by its anchored side (the Roblox anchor rule), and a weld between two free
		// parts falling as one.
		_probeWeldFrozenA = NewScratchPart(scene, "ConstraintProbeWeldFrozenA", new Vector3(192f, 25f, 190f));
		_probeWeldFrozenB = NewScratchPart(scene, "ConstraintProbeWeldFrozenB", new Vector3(194f, 25f, 190f));
		_probeWeldFrozenB.SetAnchored(false);
		_probeWeldFallA = NewScratchPart(scene, "ConstraintProbeWeldFallA", new Vector3(204f, 25f, 190f));
		_probeWeldFallA.SetAnchored(false);
		_probeWeldFallB = NewScratchPart(scene, "ConstraintProbeWeldFallB", new Vector3(206f, 25f, 190f));
		_probeWeldFallB.SetAnchored(false);

		scene.RegisterHandle(_probeFall);
		int hingeAnchorHandle = scene.RegisterHandle(_probeHingeAnchor);
		int hingeSwingHandle = scene.RegisterHandle(_probeHingeSwing);
		int frozenAHandle = scene.RegisterHandle(_probeWeldFrozenA);
		int frozenBHandle = scene.RegisterHandle(_probeWeldFrozenB);
		int fallAHandle = scene.RegisterHandle(_probeWeldFallA);
		int fallBHandle = scene.RegisterHandle(_probeWeldFallB);

		_probeHingePivot = new Vector3(199f, 25f, 190f);
		LotConstraints.Add(new LotConstraintRecord
		{
			Kind = LotConstraintKind.Hinge,
			A = hingeAnchorHandle,
			B = hingeSwingHandle,
			Pivot = _probeHingeAnchor.GlobalTransform.AffineInverse() * _probeHingePivot,
			Axis = ConstraintPicker.DefaultHingeAxis(_probeHingeAnchor, _probeHingeSwing)
		});
		LotConstraints.Add(new LotConstraintRecord { Kind = LotConstraintKind.Weld, A = frozenAHandle, B = frozenBHandle });
		LotConstraints.Add(new LotConstraintRecord { Kind = LotConstraintKind.Weld, A = fallAHandle, B = fallBHandle });

		_probeFallStartY = _probeFall.GlobalPosition.Y;
		_probeHingeAnchorStart = _probeHingeAnchor.GlobalPosition;
		_probeHingeSwingStart = _probeHingeSwing.GlobalPosition;
		_probeWeldFrozenAStart = _probeWeldFrozenA.GlobalPosition;
		_probeWeldFrozenBStart = _probeWeldFrozenB.GlobalPosition;
		_probeWeldFallAStart = _probeWeldFallA.GlobalPosition;
		_probeWeldFallBStart = _probeWeldFallB.GlobalPosition;

		// The player-push scenario (the default character script spawns the capsule at (4, 1, 4)):
		// a floor big enough to stand and walk on (an anchored, uniformly-scaled cube whose top is
		// y = 0) and a loose part 1.4 m ahead in the -Z walk direction, resting on that floor.
		_probePushFloor = NewScratchPart(scene, "ConstraintProbePushFloor", new Vector3(4f, -4f, 3f));
		_probePushFloor.Scale = new Vector3(8f, 8f, 8f);
		_probePushPart = NewScratchPart(scene, "ConstraintProbePushPart", new Vector3(4f, 0.5f, 2.6f));
		_probePushPart.SetAnchored(false);
		scene.RegisterHandle(_probePushFloor);
		scene.RegisterHandle(_probePushPart);
		_probePushStartZ = _probePushPart.GlobalPosition.Z;

		_probeDeadlineMsec = Time.GetTicksMsec() + GravityProbeWaitMsec;
		_probeStage = 1;
		GD.Print("[ConstraintSelfTest] gravity probe started (waiting " + GravityProbeWaitMsec + " ms)");
		scene.EnterTestMode(false);

		// Drive the character: face -Z deterministically (the orbit seeds from the freecam's yaw,
		// which the probe must not depend on), remember where it started, and hold W for the whole
		// window — the same physical-key state a player produces.
		if (scene.PlayerCamera != null) scene.PlayerCamera.SeedOrbit(0f, 10f, scene.Player != null ? scene.Player.Position : Vector3.Zero);
		_probePlayerStartZ = scene.Player != null ? scene.Player.Position.Z : 0f;
		PressProbeKey(Key.W, true);
	}

	/// <summary>Advances the staged probe; called every frame from BuilderScene._Process.</summary>
	public static void TickGravityProbe()
	{
		if (_probeStage != 1) return;
		if (Time.GetTicksMsec() < _probeDeadlineMsec) return;
		_probeStage = 2;

		ProbeCheck("the session swaps an unanchored part to a simulated body",
			_probeFall != null && _probeFall.IsSimulated);

		float fallY = _probeFall.GlobalPosition.Y;
		ProbeCheck("the free part's visual actually fell (" + _probeFallStartY + " -> " + fallY + ")",
			fallY < _probeFallStartY - 1f);

		float anchorMoved = _probeHingeAnchor.GlobalPosition.DistanceTo(_probeHingeAnchorStart);
		ProbeCheck("a hinge's anchored side does not move (moved " + anchorMoved + ")", anchorMoved < 0.05f);

		float swingMoved = _probeHingeSwing.GlobalPosition.DistanceTo(_probeHingeSwingStart);
		ProbeCheck("a hinged free part swings under gravity (moved " + swingMoved + ")", swingMoved > 0.3f);

		float pivotDistance = _probeHingeSwing.GlobalPosition.DistanceTo(_probeHingePivot);
		ProbeCheck("the hinge holds its pivot distance (" + pivotDistance + " vs 1.0)",
			Mathf.Abs(pivotDistance - 1f) < 0.35f);

		float frozenMovedA = _probeWeldFrozenA.GlobalPosition.DistanceTo(_probeWeldFrozenAStart);
		float frozenMovedB = _probeWeldFrozenB.GlobalPosition.DistanceTo(_probeWeldFrozenBStart);
		ProbeCheck("a weld to an anchored part freezes the island (moved " + frozenMovedA + "/" + frozenMovedB + ")",
			frozenMovedA < 0.1f && frozenMovedB < 0.1f);

		float weldfallA = _probeWeldFallA.GlobalPosition.DistanceTo(_probeWeldFallAStart);
		float weldfallB = _probeWeldFallB.GlobalPosition.DistanceTo(_probeWeldFallBStart);
		float separation = _probeWeldFallA.GlobalPosition.DistanceTo(_probeWeldFallB.GlobalPosition);
		ProbeCheck("two welded free parts fall as one (moved " + weldfallA + "/" + weldfallB + ", gap " + separation + ")",
			weldfallA > 0.5f && weldfallB > 0.5f && Mathf.Abs(separation - 2f) < 0.35f);

		// Stop the walk and check what it did: the player must have advanced, and the loose part in
		// its path must have been pushed ahead of it — the direct regression for "the part just
		// freezes when the player touches it".
		PressProbeKey(Key.W, false);
		float playerZ = _probeScene.Player != null ? _probeScene.Player.Position.Z : _probePlayerStartZ;
		ProbeCheck("the player walked forward in Test mode (z " + _probePlayerStartZ + " -> " + playerZ + ")",
			playerZ < _probePlayerStartZ - 1f);
		float pushZ = _probePushPart.GlobalPosition.Z;
		ProbeCheck("walking into a loose part pushes it (z " + _probePushStartZ + " -> " + pushZ + ")",
			pushZ < _probePushStartZ - 0.5f);

		// Leaving the session restores the authored transforms — the direct regression for "test mode
		// results save into the lot". Then the probe's own parts have to go.
		_probeScene.ExitTestMode();
		float restoredY = _probeFall.GlobalPosition.Y;
		ProbeCheck("leaving Test mode restored the part's transform (" + _probeFallStartY + " -> " + restoredY + ")",
			Mathf.Abs(restoredY - _probeFallStartY) < 0.01f);
		_probeScene.DestroyEntity(_probeFall);
		_probeScene.DestroyEntity(_probeHingeAnchor);
		_probeScene.DestroyEntity(_probeHingeSwing);
		_probeScene.DestroyEntity(_probeWeldFrozenA);
		_probeScene.DestroyEntity(_probeWeldFrozenB);
		_probeScene.DestroyEntity(_probeWeldFallA);
		_probeScene.DestroyEntity(_probeWeldFallB);
		_probeScene.DestroyEntity(_probePushFloor);
		_probeScene.DestroyEntity(_probePushPart);
		_probeFall = null;
		_probeHingeAnchor = null;
		_probeHingeSwing = null;
		_probeWeldFrozenA = null;
		_probeWeldFrozenB = null;
		_probeWeldFallA = null;
		_probeWeldFallB = null;
		_probePushFloor = null;
		_probePushPart = null;
		_probeScene = null;
		LotConstraints.Clear();
		GD.Print("[ConstraintSelfTest] gravity probe: " + _probeChecks + " checks, "
			+ _probeFailures + " failure(s).");
	}

	/// <summary>
	/// Feeds one physical key press/release into Godot's input state — the exact state
	/// <see cref="CapsuleController"/> reads — so the headless probe drives the character like a
	/// player does instead of reaching around the controller.
	/// </summary>
	private static void PressProbeKey(Key key, bool pressed)
	{
		InputEventKey keyEvent = new InputEventKey();
		keyEvent.Keycode = key;
		keyEvent.PhysicalKeycode = key;
		keyEvent.Pressed = pressed;
		Input.ParseInputEvent(keyEvent);
	}

	private static void ProbeCheck(string name, bool condition)
	{
		_probeChecks++;
		if (condition)
		{
			GD.Print("[ConstraintSelfTest] PASS  (gravity probe) " + name);
		}
		else
		{
			_probeFailures++;
			GD.PrintErr("[ConstraintSelfTest] FAIL  (gravity probe) " + name);
		}
	}

	/// <summary>
	/// The hinge axis convention: a Godot HingeJoint3D rotates around the joint node's local Z, so
	/// BasisForAxis must aim that column at the requested world direction and stay a right-handed
	/// frame — for the world axes and, since the orientation pass, for an arbitrary one.
	/// </summary>
	private static void TestBasis()
	{
		Basis x = LotConstraintSession.BasisForAxis(Vector3.Right);
		Basis y = LotConstraintSession.BasisForAxis(Vector3.Up);
		Basis z = LotConstraintSession.BasisForAxis(Vector3.Back);

		Check("basis: X aims the joint's local Z at world X", x.Z.IsEqualApprox(Vector3.Right));
		Check("basis: Y aims the joint's local Z at world Y", y.Z.IsEqualApprox(Vector3.Up));
		Check("basis: Z aims the joint's local Z at world Z", z.Z.IsEqualApprox(Vector3.Back));

		Check("basis: X frame is orthonormal", IsOrthonormal(x));
		Check("basis: Y frame is orthonormal", IsOrthonormal(y));
		Check("basis: Z frame is orthonormal", IsOrthonormal(z));
		Check("basis: X frame is right-handed", x.Determinant() > 0f);
		Check("basis: Y frame is right-handed", y.Determinant() > 0f);

		// The orientation pass: an angled axis must land on the Z column too.
		Vector3 diagonal = new Vector3(1f, 1f, 0f).Normalized();
		Basis diagonalBasis = LotConstraintSession.BasisForAxis(diagonal);
		Check("basis: an angled axis lands on the Z column", diagonalBasis.Z.IsEqualApprox(diagonal));
		Check("basis: the angled frame is orthonormal", IsOrthonormal(diagonalBasis));
		Check("basis: the angled frame is right-handed", diagonalBasis.Determinant() > 0f);

		TestAxisMath();
	}

	/// <summary>
	/// The player's push arithmetic (pure): walking into a body closes the speed gap along the
	/// contact normal, mass-scaled and capped; standing still, backing away, or a body already
	/// keeping up pushes nothing. This is the fix for "the loose part just freezes when the player
	/// touches it" — a kinematic character transfers nothing to a rigid body by itself.
	/// </summary>
	private static void TestPlayerPushMath()
	{
		Vector3 impulse;

		// Walking -Z into a body whose +Z face faces the player: the push goes -Z, capped at the
		// push speed (8) and scaled by the body's mass (2).
		bool pushes = CapsuleController.TryComputePush(new Vector3(0f, 0f, -9f), Vector3.Back,
			Vector3.Zero, 2f, out impulse);
		Check("push: walking into a body produces an impulse", pushes);
		Check("push: the impulse points away from the contact and is capped and mass-scaled",
			pushes && impulse.IsEqualApprox(new Vector3(0f, 0f, -16f)));

		Check("push: standing still does not push",
			!CapsuleController.TryComputePush(Vector3.Zero, Vector3.Back, Vector3.Zero, 2f, out impulse));
		Check("push: backing away does not push",
			!CapsuleController.TryComputePush(new Vector3(0f, 0f, 9f), Vector3.Back, Vector3.Zero, 2f, out impulse));
		Check("push: a body already keeping up is not pushed again",
			!CapsuleController.TryComputePush(new Vector3(0f, 0f, -4f), Vector3.Back,
				new Vector3(0f, 0f, -4f), 2f, out impulse));
		Check("push: a slow body gets only the speed deficit",
			CapsuleController.TryComputePush(new Vector3(0f, 0f, -4f), Vector3.Back,
				new Vector3(0f, 0f, -3f), 1f, out impulse)
			&& impulse.IsEqualApprox(new Vector3(0f, 0f, -1f)));
	}

	/// <summary>The sharp axis tools: exact presets and the 45° steps, pure math.</summary>
	private static void TestAxisMath()
	{
		Check("axis math: vertical is world up", HingeAxisMath.VerticalAxis.IsEqualApprox(Vector3.Up));

		Check("axis math: horizontal flattens a tilted axis, keeping its heading",
			HingeAxisMath.Horizontal(new Vector3(1f, 2f, 0f), Vector3.Back).IsEqualApprox(Vector3.Right));
		Check("axis math: horizontal falls back when the axis is vertical",
			HingeAxisMath.Horizontal(Vector3.Up, Vector3.Back).IsEqualApprox(Vector3.Back));

		Vector3 tilt = HingeAxisMath.Tilt(Vector3.Right);
		Check("axis math: tilt steps 45 degrees off the horizontal",
			Mathf.Abs(tilt.AngleTo(Vector3.Right) - Mathf.DegToRad(45f)) < 1e-3f);
		Check("axis math: tilt keeps the axis unit", Mathf.Abs(tilt.Length() - 1f) < 1e-4f);
		Check("axis math: tilt of a vertical axis stays unit (world X stands in as the pivot)",
			Mathf.Abs(HingeAxisMath.Tilt(Vector3.Up).Length() - 1f) < 1e-4f);

		Vector3 yaw = HingeAxisMath.Yaw(Vector3.Right);
		Check("axis math: yaw turns 45 degrees about the vertical",
			Mathf.Abs(yaw.Y) < 1e-4f && Mathf.Abs(yaw.AngleTo(Vector3.Right) - Mathf.DegToRad(45f)) < 1e-3f);
		Check("axis math: yaw leaves a vertical axis alone", HingeAxisMath.Yaw(Vector3.Up).IsEqualApprox(Vector3.Up));

		Vector3 normalized;
		Check("axis math: a zero axis is refused", !HingeAxisMath.TryNormalize(Vector3.Zero, out normalized));
		Check("axis math: a non-unit axis normalizes",
			HingeAxisMath.TryNormalize(new Vector3(0f, 0f, 5f), out normalized)
			&& normalized.IsEqualApprox(Vector3.Back));
	}

	private static bool IsOrthonormal(Basis b)
	{
		const float epsilon = 1e-4f;
		if (Mathf.Abs(b.X.Length() - 1f) > epsilon) return false;
		if (Mathf.Abs(b.Y.Length() - 1f) > epsilon) return false;
		if (Mathf.Abs(b.Z.Length() - 1f) > epsilon) return false;
		if (Mathf.Abs(b.X.Dot(b.Y)) > epsilon) return false;
		if (Mathf.Abs(b.X.Dot(b.Z)) > epsilon) return false;
		return Mathf.Abs(b.Y.Dot(b.Z)) <= epsilon;
	}

	/// <summary>The record table: ids, pair canonicalisation, lookup, removal by part, and the
	/// part-delete cleanup contract.</summary>
	private static void TestRegistry()
	{
		LotConstraints.Clear();
		Check("registry: starts empty", LotConstraints.Count == 0);

		LotConstraintRecord weld = LotConstraints.Add(new LotConstraintRecord
		{
			Kind = LotConstraintKind.Weld,
			A = 7,
			B = 3
		});
		Check("registry: add assigns an id", weld.Id > 0);
		Check("registry: find returns the stored record", LotConstraints.Find(weld.Id) == weld);

		// The table does not reorder endpoints — the caller canonicalises (Lua and the picker do).
		Check("registry: FindWeld is pair-order independent", LotConstraints.FindWeld(3, 7) == weld);
		Check("registry: IsWelded sees the enabled weld", LotConstraints.IsWelded(7, 3));

		weld.Enabled = false;
		Check("registry: IsWelded ignores a disabled weld", !LotConstraints.IsWelded(7, 3));
		weld.Enabled = true;

		LotConstraintRecord hinge = LotConstraints.Add(new LotConstraintRecord
		{
			Kind = LotConstraintKind.Hinge,
			A = 7,
			B = LotConstraintRecord.WorldHandle
		});
		Check("registry: FindWeld ignores a hinge on the same part", LotConstraints.FindWeld(7, 3) == weld);
		Check("registry: a hinge to the world is stored with the world handle",
			hinge.B == LotConstraintRecord.WorldHandle);

		int removed = LotConstraints.RemoveFor(7);
		Check("registry: RemoveFor drops every link touching the part", removed == 2 && LotConstraints.Count == 0);
		Check("registry: Remove of an unknown id is false", !LotConstraints.Remove(999));

		LotConstraints.Clear();
		Check("registry: clear empties the table", LotConstraints.Count == 0);
	}

	/// <summary>
	/// The one piece of policy v1 adopts from Roblox: one anchored part freezes its island, and a
	/// weld joining two anchored islands cannot hold (it goes inactive). A hinge is inactive only
	/// while both of its sides are anchored.
	/// </summary>
	private static void TestAnchorRule()
	{
		LotConstraints.Clear();
		HashSet<int> anchored = new HashSet<int>();
		System.Collections.Generic.Dictionary<int, bool> activity = new System.Collections.Generic.Dictionary<int, bool>();
		System.Func<int, bool> isAnchored = h => anchored.Contains(h);

		LotConstraintRecord weldAB = LotConstraints.Add(new LotConstraintRecord { Kind = LotConstraintKind.Weld, A = 1, B = 2 });
		Check("anchor: a weld between two free parts is active", Active(activity, isAnchored, weldAB.Id));

		anchored.Add(1);
		Check("anchor: anchoring one side keeps the weld active (the island simply freezes)",
			Active(activity, isAnchored, weldAB.Id));

		anchored.Add(3);
		LotConstraintRecord weldBC = LotConstraints.Add(new LotConstraintRecord { Kind = LotConstraintKind.Weld, A = 2, B = 3 });
		Check("anchor: a weld joining two anchored islands goes inactive", !Active(activity, isAnchored, weldBC.Id));

		LotConstraintRecord weldAC = LotConstraints.Add(new LotConstraintRecord { Kind = LotConstraintKind.Weld, A = 1, B = 3 });
		Check("anchor: a weld directly between two anchored parts is inactive", !Active(activity, isAnchored, weldAC.Id));

		anchored.Clear();
		LotConstraintRecord hingeWorld = LotConstraints.Add(new LotConstraintRecord
		{
			Kind = LotConstraintKind.Hinge,
			A = 4,
			B = LotConstraintRecord.WorldHandle
		});
		Check("anchor: a hinge to the world with a free part is active", Active(activity, isAnchored, hingeWorld.Id));
		anchored.Add(4);
		Check("anchor: a hinge to the world with an anchored part is inactive", !Active(activity, isAnchored, hingeWorld.Id));

		anchored.Clear();
		LotConstraintRecord hingeFree = LotConstraints.Add(new LotConstraintRecord { Kind = LotConstraintKind.Hinge, A = 5, B = 6 });
		Check("anchor: a hinge between two free parts is active", Active(activity, isAnchored, hingeFree.Id));
		anchored.Add(5);
		Check("anchor: a hinge with one anchored side stays active", Active(activity, isAnchored, hingeFree.Id));
		anchored.Add(6);
		Check("anchor: a hinge between two anchored parts is inactive", !Active(activity, isAnchored, hingeFree.Id));

		anchored.Clear();
		weldAB.Enabled = false;
		Check("anchor: a disabled record reports inactive", !Active(activity, isAnchored, weldAB.Id));
		weldAB.Enabled = true;

		LotConstraints.Clear();
	}

	private static bool Active(System.Collections.Generic.Dictionary<int, bool> buffer, System.Func<int, bool> isAnchored, int id)
	{
		LotConstraints.ComputeActivity(isAnchored, buffer);
		bool value;
		return buffer.TryGetValue(id, out value) && value;
	}

	/// <summary>
	/// The chain-length gauge the session warns on (decision: warn above eight active links in one
	/// island, never cap): welds merge islands, a hinge counts toward its side without merging, and a
	/// disabled link neither merges nor counts.
	/// </summary>
	private static void TestChainGauge()
	{
		LotConstraints.Clear();
		HashSet<int> disabled = new HashSet<int>();
		System.Func<int, bool> isActive = id => !disabled.Contains(id);

		// A 9-link chain (welds (0,1) through (8,9)): the island carries every link.
		AddWeldChain(0, 9);
		int chainAll = LotConstraints.LargestLinkIsland(isActive);
		Check("chain: nine welded links report as nine (got " + chainAll + ")", chainAll == 9);
		Check("chain: the threshold is the warn line, not a cap", LotConstraints.ChainWarnThreshold == 8);

		// A separate island does not add to the largest.
		LotConstraints.Add(new LotConstraintRecord { Kind = LotConstraintKind.Weld, A = 20, B = 21 });
		LotConstraints.Add(new LotConstraintRecord { Kind = LotConstraintKind.Weld, A = 21, B = 22 });
		int chainSeparate = LotConstraints.LargestLinkIsland(isActive);
		Check("chain: a separate island does not add to the largest (got " + chainSeparate + ")",
			chainSeparate == 9);

		// Disabling three welds leaves two islands of three links each.
		DisableWeld(disabled, 3, 4);
		DisableWeld(disabled, 4, 5);
		DisableWeld(disabled, 8, 9);
		int chainSplit = LotConstraints.LargestLinkIsland(isActive);
		Check("chain: disabling welds splits the island (got " + chainSplit + ")", chainSplit == 3);

		// A hinge counts toward its first side's island without merging anything: handle 2's island
		// goes from three links to four while its neighbours stay at three.
		LotConstraints.Add(new LotConstraintRecord
		{
			Kind = LotConstraintKind.Hinge,
			A = 2,
			B = LotConstraintRecord.WorldHandle
		});
		int chainHinge = LotConstraints.LargestLinkIsland(isActive);
		Check("chain: a hinge counts toward its side's island (got " + chainHinge + ")", chainHinge == 4);

		disabled.Add(LotConstraints.All[LotConstraints.Count - 1].Id);
		int chainHingeOff = LotConstraints.LargestLinkIsland(isActive);
		Check("chain: a disabled hinge does not count (got " + chainHingeOff + ")", chainHingeOff == 3);

		Check("chain: an empty table reports zero", EmptyIsland() == 0);
		LotConstraints.Clear();
	}

	private static void AddWeldChain(int first, int last)
	{
		for (int handle = first; handle < last; handle++)
		{
			LotConstraints.Add(new LotConstraintRecord
			{
				Kind = LotConstraintKind.Weld,
				A = handle,
				B = handle + 1
			});
		}
	}

	/// <summary>Marks the weld between two handles inactive in the test's activity set.</summary>
	private static void DisableWeld(HashSet<int> disabled, int a, int b)
	{
		for (int i = 0; i < LotConstraints.Count; i++)
		{
			LotConstraintRecord record = LotConstraints.All[i];
			if (record.Kind == LotConstraintKind.Weld && record.A == a && record.B == b)
			{
				disabled.Add(record.Id);
				return;
			}
		}
	}

	private static int EmptyIsland()
	{
		LotConstraints.Clear();
		int result = LotConstraints.LargestLinkIsland(id => true);
		LotConstraints.Clear();
		return result;
	}

	/// <summary>
	/// The `constraints` block: handle-to-index encoding, the real JSON encoder/parser (a Vector3
	/// becomes a string there), the restore path, and the clean-skip rules for an unknown kind or an
	/// index that no longer exists.
	/// </summary>
	private static void TestPersistence(BuilderScene scene)
	{
		LotConstraints.Clear();
		LotObject partA = NewScratchPart(scene, "ConstraintPersistA", new Vector3(150f, 5f, 150f));
		LotObject partB = NewScratchPart(scene, "ConstraintPersistB", new Vector3(152f, 5f, 150f));
		int handleA = scene.RegisterHandle(partA);
		int handleB = scene.RegisterHandle(partB);

		LotConstraints.Add(new LotConstraintRecord
		{
			Kind = LotConstraintKind.Weld,
			A = handleA,
			B = handleB
		});
		LotConstraints.Add(new LotConstraintRecord
		{
			Kind = LotConstraintKind.Hinge,
			A = handleA,
			B = LotConstraintRecord.WorldHandle,
			Pivot = new Vector3(0.5f, 0f, -0.25f),
			Axis = Vector3.Back,
			LimitsEnabled = true,
			LowerDeg = -30f,
			UpperDeg = 60f,
			Motor = HingeMotorMode.Spin,
			MotorVelocity = 45f,
			MotorMaxPush = 12f
		});

		List<Node> nodes = new List<Node>();
		List<LotArchive.LotNodeRecord> records = new List<LotArchive.LotNodeRecord>();
		LotSceneWalk.CaptureChildren(scene.LotRoot, records, nodes);
		Godot.Collections.Array encoded = LotConstraints.ToJson(nodes);
		Check("persist: both records encode", encoded.Count == 2);

		// Through the real encoder and parser: this is where a Vector3 becomes the string "(x, y, z)".
		string text = Json.Stringify(new Godot.Collections.Dictionary { { "constraints", encoded } });
		Variant parsed = Json.ParseString(text);
		Godot.Collections.Array roundTripped = parsed.AsGodotDictionary()["constraints"].AsGodotArray();
		Check("persist: the array survives the JSON encoder and parser", roundTripped.Count == 2);

		LotConstraints.Clear();
		List<string> warnings = new List<string>();
		LotConstraints.FromJson(roundTripped, nodes, HandleOfNode, warnings);
		Check("persist: both records restore", LotConstraints.Count == 2 && warnings.Count == 0);

		LotConstraintRecord restoredWeld = LotConstraints.FindWeld(handleA, handleB);
		Check("persist: the weld restores with both endpoints", restoredWeld != null && restoredWeld.Enabled);
		LotConstraintRecord restoredHinge = FindHinge(handleA);
		Check("persist: the hinge restores", restoredHinge != null);
		Check("persist: the hinge axis survives", restoredHinge != null && restoredHinge.Axis.IsEqualApprox(Vector3.Back));
		Check("persist: the hinge limits survive",
			restoredHinge != null && restoredHinge.LimitsEnabled &&
			Mathf.IsEqualApprox(restoredHinge.LowerDeg, -30f) && Mathf.IsEqualApprox(restoredHinge.UpperDeg, 60f));
		Check("persist: the hinge motor survives",
			restoredHinge != null && restoredHinge.Motor == HingeMotorMode.Spin &&
			Mathf.IsEqualApprox(restoredHinge.MotorVelocity, 45f) && Mathf.IsEqualApprox(restoredHinge.MotorMaxPush, 12f));
		Check("persist: the world second side survives",
			restoredHinge != null && restoredHinge.B == LotConstraintRecord.WorldHandle);
		Check("persist: the pivot survives the string encoding",
			restoredHinge != null && restoredHinge.Pivot.IsEqualApprox(new Vector3(0.5f, 0f, -0.25f)));

		// A newer lot may carry a kind, an index or an axis this build cannot read: all are skipped
		// with a warning instead of half-loading.
		Godot.Collections.Array bad = new Godot.Collections.Array();
		bad.Add(new Godot.Collections.Dictionary { { "kind", "spring" }, { "a", 0 }, { "b", 1 } });
		bad.Add(new Godot.Collections.Dictionary { { "kind", "weld" }, { "a", 999 }, { "b", 1 } });
		bad.Add(new Godot.Collections.Dictionary { { "kind", "hinge" }, { "a", 0 }, { "b", 1 }, { "axis", "w" } });
		LotConstraints.Clear();
		warnings.Clear();
		LotConstraints.FromJson(bad, nodes, HandleOfNode, warnings);
		Check("persist: an unknown kind, a stale index and a bad axis are all skipped with warnings",
			LotConstraints.Count == 0 && warnings.Count == 3);

		// The axis formats: a v1 letter still reads, and an angled axis survives as a vector.
		// The endpoints are looked up by node, because index 0/1 belong to whatever the lot happens
		// to hold first (the smoke-test probes carry no handle).
		int axisIndexA = NodeIndexOf(nodes, partA);
		int axisIndexB = NodeIndexOf(nodes, partB);
		Godot.Collections.Array oldStyle = new Godot.Collections.Array();
		oldStyle.Add(new Godot.Collections.Dictionary
		{
			{ "kind", "hinge" }, { "a", axisIndexA }, { "b", axisIndexB }, { "axis", "y" }, { "pivot", "(0, 0, 0)" }
		});
		LotConstraints.Clear();
		warnings.Clear();
		LotConstraints.FromJson(oldStyle, nodes, HandleOfNode, warnings);
		LotConstraintRecord restoredLetter = LotConstraints.Count == 1 ? LotConstraints.All[0] : null;
		Check("persist: a v1 axis letter still reads (\"y\")",
			restoredLetter != null && restoredLetter.Axis.IsEqualApprox(Vector3.Up) && warnings.Count == 0);

		LotConstraints.Clear();
		Vector3 angledAxis = new Vector3(0f, 1f, 1f).Normalized();
		LotConstraints.Add(new LotConstraintRecord
		{
			Kind = LotConstraintKind.Hinge,
			A = handleA,
			B = LotConstraintRecord.WorldHandle,
			Pivot = Vector3.Zero,
			Axis = angledAxis
		});
		Godot.Collections.Array angled = LotConstraints.ToJson(nodes);
		string angledText = Json.Stringify(new Godot.Collections.Dictionary { { "constraints", angled } });
		Godot.Collections.Array angledParsed = Json.ParseString(angledText).AsGodotDictionary()["constraints"].AsGodotArray();
		LotConstraints.Clear();
		warnings.Clear();
		LotConstraints.FromJson(angledParsed, nodes, HandleOfNode, warnings);
		LotConstraintRecord restoredAngled = FindHinge(handleA);
		Check("persist: an angled axis survives as a vector",
			restoredAngled != null && restoredAngled.Axis.IsEqualApprox(angledAxis) && warnings.Count == 0);

		// A destroyed part drops its links through the single entity-destroy walk, and only its own.
		// (The restore checks above were about the file; this re-establishes the pair on the live
		// table, because the deliberate bad-parse above cleared it.)
		LotConstraints.Add(new LotConstraintRecord { Kind = LotConstraintKind.Weld, A = handleA, B = handleB });
		LotObject partC = NewScratchPart(scene, "ConstraintPersistC", new Vector3(154f, 5f, 150f));
		int handleC = scene.RegisterHandle(partC);
		LotConstraints.Add(new LotConstraintRecord { Kind = LotConstraintKind.Weld, A = handleA, B = handleC });
		scene.DestroyEntity(partC);
		Check("persist: destroying a part drops every link that touched it",
			LotConstraints.FindWeld(handleA, handleC) == null);
		Check("persist: a neighbour's deletion leaves other links alone",
			LotConstraints.FindWeld(handleA, handleB) != null);

		scene.DestroyEntity(partA);
		scene.DestroyEntity(partB);
		LotConstraints.Clear();
	}

	/// <summary>
	/// The session: the body swap for unanchored parts, the joints built for the active links (and
	/// only those), and the teardown that restores the build-mode world. Built directly rather than
	/// through EnterTestMode, because entering a real session would reload the live lot under the
	/// other suites; the parts this test creates are the only unanchored ones in the world, so the
	/// swap touches nothing else.
	/// </summary>
	private static void TestSession(BuilderScene scene)
	{
		LotConstraints.Clear();
		LotObject freeA = NewScratchPart(scene, "ConstraintSessFreeA", new Vector3(160f, 5f, 160f));
		LotObject freeB = NewScratchPart(scene, "ConstraintSessFreeB", new Vector3(162f, 5f, 160f));
		LotObject anchorC = NewScratchPart(scene, "ConstraintSessAnchorC", new Vector3(164f, 5f, 160f));
		LotObject anchorD = NewScratchPart(scene, "ConstraintSessAnchorD", new Vector3(166f, 5f, 160f));
		freeA.SetAnchored(false);
		freeB.SetAnchored(false);
		int hA = scene.RegisterHandle(freeA);
		int hB = scene.RegisterHandle(freeB);
		int hC = scene.RegisterHandle(anchorC);
		int hD = scene.RegisterHandle(anchorD);

		LotConstraintRecord weldAB = LotConstraints.Add(new LotConstraintRecord { Kind = LotConstraintKind.Weld, A = hA, B = hB });
		LotConstraints.Add(new LotConstraintRecord { Kind = LotConstraintKind.Weld, A = hB, B = hC });
		LotConstraintRecord weldCD = LotConstraints.Add(new LotConstraintRecord { Kind = LotConstraintKind.Weld, A = hC, B = hD });
		LotConstraintRecord hingeWorld = LotConstraints.Add(new LotConstraintRecord
		{
			Kind = LotConstraintKind.Hinge,
			A = hA,
			B = LotConstraintRecord.WorldHandle,
			Pivot = new Vector3(0f, 0.5f, 0f),
			Axis = Vector3.Up,
			LimitsEnabled = true,
			LowerDeg = -90f,
			UpperDeg = 90f,
			Motor = HingeMotorMode.Spin,
			MotorVelocity = 45f,
			MotorMaxPush = 10f
		});

		LotConstraintSession session = new LotConstraintSession(scene);
		session.Build(null);

		Check("session: an unanchored part swaps to a rigid body", freeA.IsSimulated && freeA.CollisionBody is RigidBody3D);
		Check("session: the swap keeps the collision shape with the body",
			freeA.CollisionShape != null && freeA.CollisionShape.GetParent() == freeA.CollisionBody);
		Check("session: the group layer survives the swap",
			freeA.CollisionBody.CollisionLayer == LotCollisionGroups.BitForName(freeA.CollisionGroup));
		Check("session: an anchored part keeps its static body", !anchorC.IsSimulated && anchorC.CollisionBody is StaticBody3D);
		Check("session: the swap leaves sleeping enabled (resting islands cost nothing)",
			freeA.CollisionBody is RigidBody3D rigid && rigid.CanSleep);

		// Four records, one of them joining two anchored islands: exactly three joints.
		Check("session: only the active links build joints", session.JointCount == 3);
		Check("session: the split weld builds nothing", scene.LotRoot.GetNodeOrNull("Weld_" + weldCD.Id) == null);

		Generic6DofJoint3D weldJoint = scene.LotRoot.GetNodeOrNull("Weld_" + weldAB.Id) as Generic6DofJoint3D;
		Check("session: the weld joint exists as a sibling of the parts", weldJoint != null);
		Check("session: the weld locks its linear axes",
			weldJoint != null && weldJoint.GetFlagY(Generic6DofJoint3D.Flag.EnableLinearLimit) &&
			Mathf.IsEqualApprox(weldJoint.GetParamY(Generic6DofJoint3D.Param.LinearUpperLimit), 0f));
		Check("session: the weld locks its angular axes",
			weldJoint != null && weldJoint.GetFlagY(Generic6DofJoint3D.Flag.EnableAngularLimit) &&
			Mathf.IsEqualApprox(weldJoint.GetParamY(Generic6DofJoint3D.Param.AngularLowerLimit), 0f));
		Check("session: the weld excludes its two bodies from colliding",
			weldJoint != null && weldJoint.ExcludeNodesFromCollision);
		Check("session: the weld's node path resolves to the swapped body",
			weldJoint != null && weldJoint.GetNodeOrNull(weldJoint.NodeA) == freeA.CollisionBody);

		HingeJoint3D hinge = scene.LotRoot.GetNodeOrNull("Hinge_" + hingeWorld.Id) as HingeJoint3D;
		Check("session: the hinge joint exists", hinge != null);
		Check("session: the hinge frame aims its local Z at the chosen axis",
			hinge != null && hinge.GlobalTransform.Basis.Z.IsEqualApprox(Vector3.Up));
		Check("session: the hinge pivot is the stored part-local point",
			hinge != null && hinge.GlobalPosition.IsEqualApprox(freeA.GlobalTransform * new Vector3(0f, 0.5f, 0f)));
		Check("session: the hinge limits reach the joint (radians)",
			hinge != null && hinge.GetFlag(HingeJoint3D.Flag.UseLimit) &&
			Mathf.IsEqualApprox(hinge.GetParam(HingeJoint3D.Param.LimitLower), Mathf.DegToRad(-90f)) &&
			Mathf.IsEqualApprox(hinge.GetParam(HingeJoint3D.Param.LimitUpper), Mathf.DegToRad(90f)));
		Check("session: the hinge motor reaches the joint",
			hinge != null && hinge.GetFlag(HingeJoint3D.Flag.EnableMotor) &&
			Mathf.IsEqualApprox(hinge.GetParam(HingeJoint3D.Param.MotorTargetVelocity), Mathf.DegToRad(45f)));
		Check("session: a hinge to the world leaves node B empty", hinge != null && hinge.NodeB.IsEmpty);

		// A live parameter edit lands on the built joint.
		hingeWorld.LowerDeg = -15f;
		Check("session: a live hinge edit reports applied", session.ApplyHingeParams(hingeWorld.Id));
		Check("session: a live hinge edit reaches the joint",
			hinge != null && Mathf.IsEqualApprox(hinge.GetParam(HingeJoint3D.Param.LimitLower), Mathf.DegToRad(-15f)));

		session.Teardown();
		Check("session: teardown frees every joint", session.JointCount == 0);
		Check("session: teardown restores the static body", !freeA.IsSimulated && freeA.CollisionBody is StaticBody3D);
		Check("session: teardown restores the anchored part too", !anchorC.IsSimulated && anchorC.CollisionBody is StaticBody3D);

		scene.DestroyEntity(freeA);
		scene.DestroyEntity(freeB);
		scene.DestroyEntity(anchorC);
		scene.DestroyEntity(anchorD);
		LotConstraints.Clear();
	}

	/// <summary>
	/// The acceptance path at file level: save the live lot with links in it, read the archive back,
	/// restore it into fresh containers the same way <c>LotLoad</c> does, then resolve the constraint
	/// indices against the restored objects — proving the round trip, not just the encoder.
	/// </summary>
	private static void TestFullRoundTrip(BuilderScene scene)
	{
		LotConstraints.Clear();
		LotObject partA = NewScratchPart(scene, "ConstraintRoundTripA", new Vector3(170f, 5f, 170f));
		LotObject partB = NewScratchPart(scene, "ConstraintRoundTripB", new Vector3(172f, 5f, 170f));
		int handleA = scene.RegisterHandle(partA);
		int handleB = scene.RegisterHandle(partB);
		LotConstraints.CanonicalPair(handleA, handleB, out int first, out int second);
		LotConstraints.Add(new LotConstraintRecord { Kind = LotConstraintKind.Weld, A = first, B = second });
		LotConstraints.Add(new LotConstraintRecord
		{
			Kind = LotConstraintKind.Hinge,
			A = handleA,
			B = LotConstraintRecord.WorldHandle,
			Pivot = new Vector3(0.25f, 0f, 0f),
			Axis = Vector3.Right,
			LimitsEnabled = true,
			LowerDeg = -45f,
			UpperDeg = 45f
		});

		LotArchive.LotManifest manifest = LotSave.BuildManifest(scene.LotRoot, "ConstraintSelfTest", 0);
		List<string> missing = new List<string>();
		bool wrote = LotSave.Write(scene.LotRoot, scene.LotUIRoot, scene.Environment, ScratchLotPath, manifest, missing, out string error);
		Check("roundtrip: the lot saves (" + error + ")", wrote);

		if (wrote)
		{
			bool read = LotArchiveReader.Read(ScratchLotPath, out _, out LotArchive.LotDocument document, out error);
			Check("roundtrip: the archive reads back (" + error + ")", read);
			Check("roundtrip: the constraints block is present",
				read && document.Constraints != null && document.Constraints.Count == 2);

			LotVfs vfs = new LotVfs();
			try
			{
				Check("roundtrip: the archive opens for streaming", vfs.Open(ScratchLotPath, out error));

				Node3D restoredRoot = new Node3D();
				Node restoredUi = new Node();
				LotSceneLoad.LoadReport report = new LotSceneLoad.LoadReport();
				List<Node> objectsByIndex = new List<Node>();
				LotSceneLoad.Restore(restoredRoot, restoredUi, document, vfs, report, objectsByIndex);
				Check("roundtrip: the scene restores with both parts", report.PartsCreated >= 2);

				// The same handle issuance the scene performs after a load (AssignLoadOrderHandles),
				// reduced to the meta the constraint load resolves against.
				for (int i = 0; i < objectsByIndex.Count; i++)
				{
					if (objectsByIndex[i] != null) objectsByIndex[i].SetMeta(BuilderScene.HandleMeta, 1000 + i);
				}
				LotConstraints.Clear();
				List<string> warnings = new List<string>();
				LotConstraints.FromJson(document.Constraints, objectsByIndex, HandleOfNode, warnings);
				Check("roundtrip: both links resolve against the restored objects (" + warnings.Count + " warnings)",
					LotConstraints.Count == 2 && warnings.Count == 0);

				Node restoredA = FindByName(objectsByIndex, "ConstraintRoundTripA");
				Node restoredB = FindByName(objectsByIndex, "ConstraintRoundTripB");
				Check("roundtrip: the restored parts are both there", restoredA != null && restoredB != null);
				if (restoredA != null && restoredB != null)
				{
					int restoredHandleA = HandleOfNode(restoredA);
					int restoredHandleB = HandleOfNode(restoredB);
					Check("roundtrip: the weld's endpoints are the restored parts",
						LotConstraints.FindWeld(restoredHandleA, restoredHandleB) != null);
					LotConstraintRecord restoredHinge = FindHinge(restoredHandleA);
					Check("roundtrip: the hinge's payload survived the file", restoredHinge != null &&
						restoredHinge.Axis.IsEqualApprox(Vector3.Right) && restoredHinge.LimitsEnabled &&
						Mathf.IsEqualApprox(restoredHinge.LowerDeg, -45f) &&
						restoredHinge.Pivot.IsEqualApprox(new Vector3(0.25f, 0f, 0f)));
				}

				restoredRoot.Free();
				restoredUi.Free();
			}
			finally
			{
				vfs.Dispose();
			}
		}

		scene.DestroyEntity(partA);
		scene.DestroyEntity(partB);
		LotConstraints.Clear();
		if (Godot.FileAccess.FileExists(ScratchLotPath)) DirAccess.RemoveAbsolute(ScratchLotPath);
	}

	/// <summary>The backing value of the scratch Float descriptor.</summary>
	private static float _scratchFloatValue;

	/// <summary>
	/// The Float property kind the hinge editor introduced: one scratch descriptor exercises the
	/// whole chain — declaration, walk capture, the real JSON encoder/parser, the document decode —
	/// and the service's live-apply/history split. The descriptor is unregistered again so a debug
	/// run cannot leave a test property in the editor or in saved lots.
	/// </summary>
	private static void TestFloatKind(BuilderScene scene)
	{
		const string id = "selftestFloat";
		LotPropertyRegistry.Register(new LotPropertyDescriptor
		{
			Id = id,
			DisplayName = "Self-Test Float",
			Category = "Self-Test",
			Doc = "A scratch Float property declared by ConstraintSelfTest.",
			Kind = LotPropertyKind.Float,
			AppliesTo = n => n is LotObject,
			GetFloat = n => _scratchFloatValue,
			SetFloat = (n, v) => _scratchFloatValue = v,
			Speed = 0.25f
		});
		Check("float: the descriptor registers", LotPropertyRegistry.Find(id) != null);

		LotObject part = NewScratchPart(scene, "ConstraintFloatPart", new Vector3(180f, 5f, 180f));
		scene.RegisterHandle(part);
		_scratchFloatValue = 3.25f;

		List<Node> nodes = new List<Node>();
		List<LotArchive.LotNodeRecord> records = new List<LotArchive.LotNodeRecord>();
		LotSceneWalk.CaptureChildren(scene.LotRoot, records, nodes);
		LotArchive.LotNodeRecord record = FindRecord(records, nodes, part);
		Check("float: the walk captures a Float property", record != null && record.Properties.ContainsKey(id));
		Check("float: the captured value is the live number",
			record != null && Mathf.IsEqualApprox((float)record.Properties[id].AsDouble(), 3.25f));

		// The real encoder/parser, then the document decode — the same path a save takes.
		LotArchive.LotDocument doc = new LotArchive.LotDocument();
		if (record != null) doc.Objects.Add(record);
		LotArchive.LotDocument decoded = LotArchive.LotDocument.FromJson(Json.ParseString(Json.Stringify(doc.ToJson())));
		Check("float: the number survives JSON and the document decode",
			decoded.Objects.Count == 1 && decoded.Objects[0].Properties.ContainsKey(id) &&
			Mathf.IsEqualApprox((float)decoded.Objects[0].Properties[id].AsDouble(), 3.25f));

		// The service: live apply plus the one-entry-per-session history, with undo restoring the value.
		Builder builder = Builder.Instance;
		Check("float: the service reads the descriptor", builder != null &&
			Mathf.IsEqualApprox(builder.Properties.ReadFloat(part, LotPropertyRegistry.Find(id)), 3.25f));
		if (builder != null)
		{
			builder.Properties.PushFloatEdit(part, LotPropertyRegistry.Find(id), 3.25f, 4.5f);
			Check("float: an edit session applies through the history", Mathf.IsEqualApprox(_scratchFloatValue, 4.5f));
			builder.History.Undo();
			Check("float: undo restores the number", Mathf.IsEqualApprox(_scratchFloatValue, 3.25f));
		}

		Check("float: the scratch descriptor unregisters", LotPropertyRegistry.Unregister(id));
		Check("float: the registry no longer declares it", LotPropertyRegistry.Find(id) == null);

		scene.DestroyEntity(part);
		_scratchFloatValue = 0f;
	}

	/// <summary>
	/// The bound Lua verbs (the §1.1 boundary): welding, unwelding, creating a hinge to the world,
	/// limits, the motor including the deliberately-unknown mode, and removal — all through the real
	/// VM, with the parts spawned and destroyed through the same API.
	/// </summary>
	private static void TestLuaBindings()
	{
		LuaManager lua = LuaManager.Instance;
		if (lua == null || !lua.IsRuntimeAvailable)
		{
			// No Lua runtime in this environment (the native library is not packaged yet), so the
			// binding cannot be exercised. Reported rather than silently skipped.
			Check("lua: constraint verbs are bound and behave (runtime unavailable, skipped)", true);
			return;
		}

		const string script =
			"local a = Lot.SpawnCube(60, 6, 60)\n" +
			"local b = Lot.SpawnCube(62, 6, 60)\n" +
			"assert(type(a) == 'number' and type(b) == 'number', 'spawn failed')\n" +
			"assert(Lot.IsWelded(a, b) == false, 'a fresh pair is not welded')\n" +
			"local w = Lot.WeldParts(a, b)\n" +
			"assert(type(w) == 'number' and w >= 0, 'WeldParts failed')\n" +
			"assert(Lot.IsWelded(a, b) == true and Lot.IsWelded(b, a) == true, 'the weld should hold both ways')\n" +
			"assert(Lot.WeldParts(a, b) == w, 're-welding the same pair returns the same link')\n" +
			"assert(Lot.WeldParts(a, a) == -1, 'a part cannot be welded to itself')\n" +
			"local h = Lot.CreateHinge(a, -1, 'y', 60, 6, 60)\n" +
			"assert(type(h) == 'number' and h >= 0, 'CreateHinge failed')\n" +
			"assert(Lot.CreateHinge(a, b, 'sideways', 0, 0, 0) == -1, 'an unknown axis is refused')\n" +
			"assert(Lot.SetHingeLimits(h, -30, 30, true) == true, 'limits should set')\n" +
			"assert(Lot.SetHingeLimits(h, 10, -10, true) == true, 'reversed limits should still set')\n" +
			"assert(Lot.SetHingeLimits(w, 0, 0, true) == false, 'a weld id is not a hinge')\n" +
			"assert(Lot.SetHingeMotor(h, 'spin', 45, 10) == true, 'the spin motor should set')\n" +
			"assert(Lot.SetHingeMotor(h, 'angle', 0, 0) == false, 'servo mode is not in this version')\n" +
			"assert(Lot.SetHingeAxis(h, 0, 1, 0) == true, 'SetHingeAxis should set')\n" +
			"assert(Lot.SetHingeAxis(h, 10, 0, 10) == true, 'an angled axis should set')\n" +
			"assert(Lot.SetHingeAxis(h, 0, 0, 0) == false, 'a zero axis must be refused')\n" +
			"assert(Lot.SetHingeAxis(w, 0, 1, 0) == false, 'a weld id is not a hinge')\n" +
			"assert(Lot.RemoveConstraint(h) == true, 'RemoveConstraint should work')\n" +
			"assert(Lot.RemoveConstraint(h) == false, 'removing twice reports false')\n" +
			"assert(Lot.Unweld(a, b) == true, 'Unweld should work')\n" +
			"assert(Lot.IsWelded(a, b) == false, 'the weld should be gone')\n" +
			"assert(Lot.Unweld(a, b) == false, 'unwelding twice reports false')\n" +
			"Lot.DestroyObject(a)\n" +
			"Lot.DestroyObject(b)";

		bool ran = lua.RunString(script, "constraint_selftest");
		Check("lua: constraint verbs are bound and behave" +
			(ran ? "" : " (" + lua.LastError + ")"), ran);
	}

	// --- helpers -----------------------------------------------------------------------------------

	private static LotObject NewScratchPart(BuilderScene scene, string name, Vector3 at)
	{
		LotObject part = LotObject.Create(LotObjectKind.Cube, name, new Color(0.9f, 0.6f, 0.6f));
		part.Position = at;
		scene.LotRoot.AddChild(part);
		return part;
	}

	private static int HandleOfNode(Node node)
	{
		if (node == null || !GodotObject.IsInstanceValid(node) || !node.HasMeta(BuilderScene.HandleMeta)) return -1;
		return node.GetMeta(BuilderScene.HandleMeta).AsInt32();
	}

	private static LotConstraintRecord FindHinge(int handle)
	{
		IReadOnlyList<LotConstraintRecord> all = LotConstraints.All;
		for (int i = 0; i < all.Count; i++)
		{
			if (all[i].Kind == LotConstraintKind.Hinge && all[i].A == handle) return all[i];
		}
		return null;
	}

	/// <summary>The record a captured node produced, via its index in the index-aligned walk list.</summary>
	private static LotArchive.LotNodeRecord FindRecord(List<LotArchive.LotNodeRecord> records, List<Node> nodes, Node target)
	{
		for (int i = 0; i < nodes.Count && i < records.Count; i++)
		{
			if (nodes[i] == target) return records[i];
		}
		return null;
	}

	/// <summary>The index a captured node sits at in the walk list, or -1.</summary>
	private static int NodeIndexOf(List<Node> nodes, Node target)
	{
		for (int i = 0; i < nodes.Count; i++)
		{
			if (nodes[i] == target) return i;
		}
		return -1;
	}

	private static Node FindByName(List<Node> nodes, string name)
	{
		for (int i = 0; i < nodes.Count; i++)
		{
			if (nodes[i] != null && nodes[i].Name.ToString() == name) return nodes[i];
		}
		return null;
	}

	private static void Check(string name, bool condition)
	{
		_checks++;
		if (condition)
		{
			GD.Print("[ConstraintSelfTest] PASS  " + name);
		}
		else
		{
			_failures++;
			GD.PrintErr("[ConstraintSelfTest] FAIL  " + name);
		}
	}
}
