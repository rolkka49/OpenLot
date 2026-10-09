using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// Verification for milestone 3.9 (input API): the action vocabulary and the per-frame snapshot's
/// edge detection (pure), the one engine assumption the milestone rests on — a synthetic physical
/// key drives the InputMap action headless, which every staged probe depends on — and a staged
/// session probe where a real script reads actions while the probe drives the keyboard and a live
/// rebind.
///
/// Follows the project's self-test convention: no framework, prints PASS/FAIL lines, returns the
/// failure count. Runs from <c>BuilderScene._Process</c> after <see cref="TweenSelfTest.ProbeDone"/>
/// — the staged probes drive Test mode one at a time — and the probe is headless-only.
/// </summary>
public static class InputSelfTest
{
	private static int _failures;
	private static int _checks;

	public static int Run(BuilderScene scene)
	{
		_failures = 0;
		_checks = 0;

		Check("harness: the lot scene is available", scene != null);
		if (scene == null)
		{
			GD.Print("[InputSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
			return _failures;
		}

		TestActionTable();
		TestSnapshotEdgeDetection();
		TestMouseSnapshot();
		SpikeSyntheticInputDrivesActions();

		GD.Print("[InputSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
		StartProbe(scene);
		return _failures;
	}

	private static void Check(string name, bool condition)
	{
		_checks++;
		if (condition)
		{
			GD.Print("[InputSelfTest] PASS  " + name);
		}
		else
		{
			_failures++;
			GD.PrintErr("[InputSelfTest] FAIL  " + name);
		}
	}

	private static bool Near(float a, float b, float epsilon = 1e-4f)
	{
		return Mathf.Abs(a - b) <= epsilon;
	}

	// --- the action table and the snapshot ------------------------------------------------------

	private static void TestActionTable()
	{
		Check("actions: the vocabulary is the seven declared names",
			LotInputActions.Names.Length == LotInputActions.ActionCount &&
			LotInputActions.IndexOf("moveForward") == LotInputActions.MoveForward &&
			LotInputActions.IndexOf("jump") == LotInputActions.Jump &&
			LotInputActions.IndexOf("interact") == LotInputActions.Interact);
		Check("actions: unknown names report -1",
			LotInputActions.IndexOf("fly") == -1 && LotInputActions.IndexOf("") == -1 &&
			LotInputActions.IndexOf(null) == -1);
	}

	private static void TestSnapshotEdgeDetection()
	{
		bool[] held = new bool[LotInputActions.ActionCount];
		float[] strength = new float[LotInputActions.ActionCount];
		LotInputActions.Reset();

		held[LotInputActions.MoveForward] = true;
		strength[LotInputActions.MoveForward] = 1f;
		LotInputActions.ApplySnapshotForTest(held, strength);
		Check("snapshot: a fresh press is held and reports pressed-for-one-frame",
			LotInputActions.IsPressed(LotInputActions.MoveForward) &&
			LotInputActions.WasPressed(LotInputActions.MoveForward) &&
			!LotInputActions.WasReleased(LotInputActions.MoveForward));

		LotInputActions.ApplySnapshotForTest(held, strength);
		Check("snapshot: a held key does not re-report pressed",
			!LotInputActions.WasPressed(LotInputActions.MoveForward));

		held[LotInputActions.MoveForward] = false;
		LotInputActions.ApplySnapshotForTest(held, strength);
		Check("snapshot: the release frame reports released exactly once",
			!LotInputActions.IsPressed(LotInputActions.MoveForward) &&
			LotInputActions.WasReleased(LotInputActions.MoveForward));
		LotInputActions.ApplySnapshotForTest(held, strength);
		Check("snapshot: the frame after the release reports nothing",
			!LotInputActions.WasPressed(LotInputActions.MoveForward) &&
			!LotInputActions.WasReleased(LotInputActions.MoveForward));

		held[LotInputActions.Jump] = true;
		strength[LotInputActions.Jump] = 0.7f;
		LotInputActions.ApplySnapshotForTest(held, strength);
		Check("snapshot: analog strength passes through",
			Near(LotInputActions.Strength(LotInputActions.Jump), 0.7f));
		Check("snapshot: an out-of-range index reads false/0",
			!LotInputActions.IsPressed(-1) && !LotInputActions.IsPressed(999) &&
			Near(LotInputActions.Strength(999), 0f));

		LotInputActions.Reset();
		Check("snapshot: Reset clears everything",
			!LotInputActions.IsPressed(LotInputActions.Jump) &&
			!LotInputActions.WasPressed(LotInputActions.Jump));
	}

	private static void TestMouseSnapshot()
	{
		LotInputActions.Reset();
		LotInputActions.AccumulateMouseMotion(3f, -4f);
		LotInputActions.AccumulateWheel(1);
		LotInputActions.AccumulateWheel(1);
		LotInputActions.AccumulateWheel(-1);
		LotInputActions.Poll();
		Check("mouse: the poll snapshots the accumulated motion and wheel",
			Near(LotInputActions.MouseDeltaX, 3f) && Near(LotInputActions.MouseDeltaY, -4f) &&
			LotInputActions.Wheel == 1);
		LotInputActions.Poll();
		Check("mouse: the next poll starts from zero",
			Near(LotInputActions.MouseDeltaX, 0f) && Near(LotInputActions.MouseDeltaY, 0f) &&
			LotInputActions.Wheel == 0);
	}

	private static void SpikeSyntheticInputDrivesActions()
	{
		// Every staged probe drives the game with Input.ParseInputEvent; the action layer sits on
		// Godot's InputMap, so this is the one engine assumption the milestone rests on — checked
		// first, before anything is built on top of it. ParseInputEvent feeds the buffered input
		// system, so the flush is what makes the state visible without waiting for the next frame
		// (the staged probe, reading across frames, does not need it).
		InputEventKey press = new InputEventKey();
		press.Keycode = Key.W;
		press.PhysicalKeycode = Key.W;
		press.Pressed = true;
		Input.ParseInputEvent(press);
		Input.FlushBufferedEvents();
		LotInputActions.Poll();
		bool held = LotInputActions.IsPressed(LotInputActions.MoveForward);

		InputEventKey release = new InputEventKey();
		release.Keycode = Key.W;
		release.PhysicalKeycode = Key.W;
		release.Pressed = false;
		Input.ParseInputEvent(release);
		Input.FlushBufferedEvents();
		LotInputActions.Poll();
		bool releasedAgain = !LotInputActions.IsPressed(LotInputActions.MoveForward);

		Check("spike: a synthetic physical W drives the moveForward action headless (" +
			held + "/" + releasedAgain + ")", held && releasedAgain);
	}

	// --- staged session probe (a script reads actions while the probe drives the keyboard) -------

	private const ulong ProbeExitWaitMsec = 300;
	private const string ProbeScriptDir = "user://Scripts";
	private const string ProbeScriptName = "__inputselftest.lua";

	/// <summary>The fixture script (attached to the lot root): reads the action API from inside a
	/// real session and reports through Lot.Log at three wall-clock checkpoints driven by its own
	/// accumulated delta. It counts `WasActionPressed("jump")` frames, which must total exactly
	/// one for a single press-and-release.</summary>
	private const string ProbeScript =
		"local t = 0\n" +
		"local jumps = 0\n" +
		"local movedForward = false\n" +
		"local midLogged = false\n" +
		"local rebindLogged = false\n" +
		"local endLogged = false\n" +
		"function onFrame(dt)\n" +
		"    t = t + dt\n" +
		"    if Lot.WasActionPressed(\"jump\") then jumps = jumps + 1 end\n" +
		"    if Lot.IsActionPressed(\"moveForward\") then movedForward = true end\n" +
		"    if not midLogged and t >= 0.3 then\n" +
		"        midLogged = true\n" +
		"        Lot.Log(\"IN mid \" .. tostring(movedForward))\n" +
		"        Lot.Log(\"IN strength \" .. tostring(Lot.GetActionStrength(\"moveForward\") > 0))\n" +
		"    end\n" +
		"    if not rebindLogged and t >= 1.15 then\n" +
		"        rebindLogged = true\n" +
		"        Lot.Log(\"IN rebind \" .. tostring(Lot.IsActionPressed(\"moveForward\")))\n" +
		"    end\n" +
		"    if not endLogged and t >= 1.4 then\n" +
		"        endLogged = true\n" +
		"        Lot.Log(\"IN end \" .. tostring(Lot.IsActionPressed(\"moveForward\")) .. \" \" .. jumps)\n" +
		"    end\n" +
		"end\n";

	private static BuilderScene _probeScene;
	private static Node _probeFixture;
	private static float _probePlayerStartZ;
	private static ulong _probeDeadlineMsec;
	private static int _probeStage; // 1..7 drive the timeline, 8 = finished
	private static int _probeChecks;
	private static int _probeFailures;
	private static readonly List<string> _probeFiles = new List<string>();

	/// <summary>Whether the staged probe completed; the scene warns when a run ends too early.</summary>
	public static bool ProbeDone { get { return _probeStage >= 9; } }

	private static void StartProbe(BuilderScene scene)
	{
		if (DisplayServer.GetName() != "headless")
		{
			_probeStage = 9;
			return;
		}
		_probeScene = scene;
		_probeChecks = 0;
		_probeFailures = 0;

		WriteFixture(ProbeScriptName, ProbeScript);
		_probeFixture = AttachFixture(scene.LotRoot, ProbeScriptName);

		_probeStage = 1;
		_probeDeadlineMsec = Time.GetTicksMsec() + 500;
		GD.Print("[InputSelfTest] session probe started");
		scene.EnterTestMode(false);
		_probePlayerStartZ = scene.Player != null ? scene.Player.Position.Z : 0f;
		PressKey(Key.W, true);
	}

	/// <summary>The probe's timeline: hold W, tap Space, rebind to Q, hold Q, then read back what
	/// the script saw.</summary>
	public static void TickProbe()
	{
		if (_probeStage < 1 || _probeStage >= 9) return;
		if (Time.GetTicksMsec() < _probeDeadlineMsec) return;

		switch (_probeStage)
		{
			case 1: // W has been held for 500 ms
				_probeStage = 2;
				PressKey(Key.W, false);
				_probeDeadlineMsec = Time.GetTicksMsec() + 100;
				break;
			case 2:
				_probeStage = 3;
				PressKey(Key.Space, true);
				_probeDeadlineMsec = Time.GetTicksMsec() + 100;
				break;
			case 3:
				_probeStage = 4;
				PressKey(Key.Space, false);
				_probeDeadlineMsec = Time.GetTicksMsec() + 200;
				break;
			case 4:
				_probeStage = 5;
				LotInputActions.Rebind("moveForward", Key.Q);
				_probeDeadlineMsec = Time.GetTicksMsec() + 100;
				break;
			case 5:
				_probeStage = 6;
				PressKey(Key.Q, true);
				_probeDeadlineMsec = Time.GetTicksMsec() + 200;
				break;
			case 6:
				_probeStage = 7;
				PressKey(Key.Q, false);
				_probeDeadlineMsec = Time.GetTicksMsec() + 400;
				break;
			case 7:
				// All three checkpoints have passed (t ~ 1.5 s of session time).
				LotInputActions.Rebind("moveForward", Key.W); // restore the default for later probes
				ProbeCheck("the script saw moveForward held while W was down", HasLog("IN mid true"));
				ProbeCheck("the action's strength is positive while held", HasLog("IN strength true"));
				ProbeCheck("the rebind drives moveForward from Q", HasLog("IN rebind true"));
				ProbeCheck("after all keys are up the action reads false and jump was seen exactly once",
					HasLog("IN end false 1"));
				float playerZ = _probeScene.Player != null ? _probeScene.Player.Position.Z : _probePlayerStartZ;
				ProbeCheck("the character physically walked (z " + _probePlayerStartZ + " -> " + playerZ + ")",
					_probeScene.Player != null && playerZ < _probePlayerStartZ - 1f);

				_probeScene.ExitTestMode();
				_probeStage = 8;
				_probeDeadlineMsec = Time.GetTicksMsec() + ProbeExitWaitMsec;
				break;
			default:
				// Phase 8: out of the session — clean the fixture up.
				_probeStage = 9;
				if (_probeFixture != null && GodotObject.IsInstanceValid(_probeFixture))
					_probeFixture.QueueFree();
				_probeFixture = null;
				for (int i = 0; i < _probeFiles.Count; i++)
				{
					string path = ProbeScriptDir + "/" + _probeFiles[i];
					if (FileAccess.FileExists(path)) DirAccess.RemoveAbsolute(path);
				}
				_probeFiles.Clear();
				_probeScene = null;
				GD.Print("[InputSelfTest] session probe: " + _probeChecks + " checks, " +
					_probeFailures + " failure(s).");
				break;
		}
	}

	private static void ProbeCheck(string name, bool condition)
	{
		_probeChecks++;
		if (condition)
		{
			GD.Print("[InputSelfTest] PASS  (session probe) " + name);
		}
		else
		{
			_probeFailures++;
			GD.PrintErr("[InputSelfTest] FAIL  (session probe) " + name);
		}
	}

	private static void PressKey(Key key, bool pressed)
	{
		InputEventKey keyEvent = new InputEventKey();
		keyEvent.Keycode = key;
		keyEvent.PhysicalKeycode = key;
		keyEvent.Pressed = pressed;
		Input.ParseInputEvent(keyEvent);
	}

	private static void WriteFixture(string fileName, string code)
	{
		if (!DirAccess.DirExistsAbsolute(ProbeScriptDir)) DirAccess.MakeDirRecursiveAbsolute(ProbeScriptDir);
		FileAccess file = FileAccess.Open(ProbeScriptDir + "/" + fileName, FileAccess.ModeFlags.Write);
		if (file != null)
		{
			file.StoreString(code);
			file.Dispose();
		}
		_probeFiles.Add(fileName);
	}

	private static Node AttachFixture(Node parent, string fileName)
	{
		LotScriptNode node = new LotScriptNode();
		node.ScriptPath = ProbeScriptDir + "/" + fileName;
		node.DisplayName = fileName;
		node.Name = fileName.Replace('.', '_');
		parent.AddChild(node);
		return node;
	}

	/// <summary>True when a recent log line equals <paramref name="exact"/>.</summary>
	private static bool HasLog(string exact)
	{
		int start = Math.Max(0, LotLog.Count - 150);
		for (int i = start; i < LotLog.Count; i++)
		{
			if (LotLog.At(i).Text == exact) return true;
		}
		return false;
	}
}