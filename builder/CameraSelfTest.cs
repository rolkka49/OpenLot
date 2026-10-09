using System;
using System.Collections.Generic;
using Godot;
using NLua;

/// <summary>
/// Verification for milestone 3.10 (camera API): mode parsing and the first-person eye math
/// (pure), the Lua shot sugar on a private VM (token cancel, validation), and a staged session
/// probe that drives a real script through first-person, fixed and a cancelled cutscene shot —
/// asserting the camera's actual pose, the character's visibility and the clean return of control.
///
/// Follows the project's self-test convention: no framework, prints PASS/FAIL lines, returns the
/// failure count. Runs from <c>BuilderScene._Process</c> after <see cref="InputSelfTest.ProbeDone"/>
/// — the staged probes drive Test mode one at a time — and the probe is headless-only.
/// </summary>
public static class CameraSelfTest
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
			GD.Print("[CameraSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
			return _failures;
		}

		TestModeParsing();
		TestFirstPersonMath();
		TestLuaSugar();

		GD.Print("[CameraSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
		StartProbe(scene);
		return _failures;
	}

	private static void Check(string name, bool condition)
	{
		_checks++;
		if (condition)
		{
			GD.Print("[CameraSelfTest] PASS  " + name);
		}
		else
		{
			_failures++;
			GD.PrintErr("[CameraSelfTest] FAIL  " + name);
		}
	}

	private static bool Near(float a, float b, float epsilon = 1e-4f)
	{
		return Mathf.Abs(a - b) <= epsilon;
	}

	// --- pure: mode names and first-person math -------------------------------------------------

	private static void TestModeParsing()
	{
		LotCameraMode mode;
		Check("modes: every name parses",
			LotCameraMath.TryParseMode("thirdperson", out mode) && mode == LotCameraMode.ThirdPerson &&
			LotCameraMath.TryParseMode("firstperson", out mode) && mode == LotCameraMode.FirstPerson &&
			LotCameraMath.TryParseMode("fixed", out mode) && mode == LotCameraMode.Fixed &&
			LotCameraMath.TryParseMode("scripted", out mode) && mode == LotCameraMode.Scripted);
		Check("modes: unknown names are refused",
			!LotCameraMath.TryParseMode("orbit", out mode) && !LotCameraMath.TryParseMode("", out mode) &&
			!LotCameraMath.TryParseMode(null, out mode));
		Check("modes: names round-trip",
			LotCameraMath.ModeName(LotCameraMode.ThirdPerson) == "thirdperson" &&
			LotCameraMath.ModeName(LotCameraMode.FirstPerson) == "firstperson" &&
			LotCameraMath.ModeName(LotCameraMode.Fixed) == "fixed" &&
			LotCameraMath.ModeName(LotCameraMode.Scripted) == "scripted");
	}

	private static void TestFirstPersonMath()
	{
		Vector3 eye = LotCameraMath.FirstPersonPosition(new Vector3(4f, 1f, 4f), 1.0f);
		Check("first person: the eye sits 1.6 above the character centre (" + eye + ")",
			Near(eye.X, 4f) && Near(eye.Z, 4f) && Near(eye.Y, 1f + 1.0f + LotCameraMath.FirstPersonEyeAboveFocus));
	}

	// --- the Lua shot sugar on a private VM -----------------------------------------------------

	private sealed class FlyStub
	{
		public int NextId = 41;
		public int LastCancelled = -1;
		public string LastEasing = "";

		public int Fly(float x, float y, float z, float tx, float ty, float tz, float duration, string easing)
		{
			LastEasing = easing ?? "";
			return NextId;
		}

		public bool Cancel(int id)
		{
			LastCancelled = id;
			return true;
		}
	}

	private static void TestLuaSugar()
	{
		List<string> logs = new List<string>();
		Lua state = new Lua();
		FlyStub stub = new FlyStub();
		try
		{
			state.RegisterFunction("__testLotLog", logs, typeof(List<string>).GetMethod("Add"));
			state.DoString("Lot = { Log = function(message) __testLotLog(tostring(message)) end, " +
				"SpawnCube = function() return 0 end, SpawnSphere = function() return 0 end, " +
				"SpawnCylinder = function() return 0 end }", "testlot");
			LuaBootstrap.Apply(state);
			state.RegisterFunction("FlyCamera", stub, typeof(FlyStub).GetMethod("Fly"));
			state.RegisterFunction("CancelCameraShot", stub, typeof(FlyStub).GetMethod("Cancel"));
			state.DoString(@"
__testSlotEnvs = {}
function __testSlot(handle, slot)
    local env = __testSlotEnvs[slot]
    if env == nil then env = __openlot_newEnv(handle); __testSlotEnvs[slot] = env end
    return env
end
", "testhelpers");

			bool ran = RunChunk(state, "__testSlot(5, 'a')", "t = Lot.FlyCamera(1, 2, 3, 4, 5, 6, 0.5, 'quadOut')");
			Check("lua: Lot.FlyCamera schedules a shot and returns a token", ran && stub.LastEasing == "quadOut");
			Check("lua: the token cancels that shot",
				RunChunkBool(state, "__testSlot(5, 'a')", "return t()") && stub.LastCancelled == 41);

			logs.Clear();
			RunChunk(state, "__testSlot(5, 'a')",
				"local ok = pcall(function() Lot.FlyCamera(1, 2, 3, 4, 5, 6, 0.5, 'bounce') end) Lot.Log('bounce ' .. tostring(ok))");
			Check("lua: an unknown easing raises", logs.Count == 1 && logs[0] == "bounce false");
			logs.Clear();
			RunChunk(state, "__testSlot(5, 'a')",
				"local ok = pcall(function() Lot.FlyCamera(1, 2, 3, 4, 5, 6, -1) end) Lot.Log('negative ' .. tostring(ok))");
			Check("lua: a negative duration raises", logs.Count == 1 && logs[0] == "negative false");
		}
		finally
		{
			state.Dispose();
		}
	}

	private static bool RunChunk(Lua state, string envExpression, string chunk)
	{
		string script = "local env = " + envExpression + "\n" +
			"local fn, err = __openlot_load([=[" + chunk + "]=], 'testchunk', env)\n" +
			"if fn == nil then return false, err end\n" +
			"return pcall(fn)";
		try
		{
			object[] results = state.DoString(script, "cameraselftest");
			return results != null && results.Length > 0 && results[0] is bool ok && ok;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static bool RunChunkBool(Lua state, string envExpression, string chunk)
	{
		string script = "local env = " + envExpression + "\n" +
			"local fn, err = __openlot_load([=[" + chunk + "]=], 'testchunk', env)\n" +
			"if fn == nil then return false end\n" +
			"local ok, value = pcall(fn)\n" +
			"if not ok then return false end\n" +
			"return value == true";
		try
		{
			object[] results = state.DoString(script, "cameraselftest");
			return results != null && results.Length > 0 && results[0] is bool value && value;
		}
		catch (Exception)
		{
			return false;
		}
	}

	// --- staged session probe (a script drives the camera through all its modes) -----------------

	private const ulong ProbeExitWaitMsec = 400;
	private const string ProbeScriptDir = "user://Scripts";
	private const string ProbeScriptName = "__cameraselftest.lua";

	/// <summary>The fixture script (attached to the lot root): walks the camera through
	/// first-person → fixed → a cutscene shot → a cancelled shot, reporting the mode it sees at
	/// each step through Lot.Log. `flyTok` is declared before onFrame so the closure sees the
	/// local (a `local flyTok =` at the assignment would leave the closure reading a global).</summary>
	private const string ProbeScript =
		"local t = 0\n" +
		"local phase = 0\n" +
		"local flyTok = nil\n" +
		"function onFrame(dt)\n" +
		"    t = t + dt\n" +
		"    if phase == 0 and t >= 0.2 then\n" +
		"        phase = 1\n" +
		"        Lot.SetCameraMode(\"firstperson\")\n" +
		"        Lot.Log(\"CAM fp \" .. Lot.GetCameraMode())\n" +
		"    elseif phase == 1 and t >= 0.8 then\n" +
		"        phase = 2\n" +
		"        Lot.SetCameraPosition(30, 5, 30)\n" +
		"        Lot.SetCameraTarget(30, 1, 30)\n" +
		"        Lot.SetCameraMode(\"fixed\")\n" +
		"        Lot.Log(\"CAM fixed \" .. Lot.GetCameraMode())\n" +
		"    elseif phase == 2 and t >= 1.4 then\n" +
		"        phase = 3\n" +
		"        flyTok = Lot.FlyCamera(60, 10, 60, 60, 0, 60, 0.6)\n" +
		"        Lot.Log(\"CAM fly \" .. Lot.GetCameraMode())\n" +
		"    elseif phase == 3 and t >= 2.3 then\n" +
		"        phase = 4\n" +
		"        Lot.Log(\"CAM after \" .. Lot.GetCameraMode())\n" +
		"        flyTok = Lot.FlyCamera(0, 5, 0, 0, 0, 0, 1.0)\n" +
		"    elseif phase == 4 and t >= 2.5 then\n" +
		"        phase = 5\n" +
		"        flyTok()\n" +
		"        Lot.Log(\"CAM cancel \" .. Lot.GetCameraMode())\n" +
		"    end\n" +
		"end\n";

	private static BuilderScene _probeScene;
	private static Node _probeFixture;
	private static ulong _probeDeadlineMsec;
	private static int _probeStage; // 1..6 drive the timeline, 7 = finished
	private static int _probeChecks;
	private static int _probeFailures;
	private static Vector3 _probeFrozenPosition;
	private static readonly List<string> _probeFiles = new List<string>();

	/// <summary>Whether the staged probe completed; the scene warns when a run ends too early.</summary>
	public static bool ProbeDone { get { return _probeStage >= 7; } }

	private static void StartProbe(BuilderScene scene)
	{
		if (DisplayServer.GetName() != "headless")
		{
			_probeStage = 7;
			return;
		}
		_probeScene = scene;
		_probeChecks = 0;
		_probeFailures = 0;

		WriteFixture(ProbeScriptName, ProbeScript);
		_probeFixture = AttachFixture(scene.LotRoot, ProbeScriptName);

		_probeStage = 1;
		_probeDeadlineMsec = Time.GetTicksMsec() + 600;
		GD.Print("[CameraSelfTest] session probe started");
		scene.EnterTestMode(false);
	}

	private static void ProbeCheck(string name, bool condition)
	{
		_probeChecks++;
		if (condition)
		{
			GD.Print("[CameraSelfTest] PASS  (session probe) " + name);
		}
		else
		{
			_probeFailures++;
			GD.PrintErr("[CameraSelfTest] FAIL  (session probe) " + name);
		}
	}

	/// <summary>The probe's checkpoints, aligned with the fixture's wall-clock phases (t ≈ 0.2 fp,
	/// 0.8 fixed, 1.4–2.0 shot 1, 2.3 shot 2, 2.5 cancel).</summary>
	public static void TickProbe()
	{
		if (_probeStage < 1 || _probeStage >= 7) return;
		if (Time.GetTicksMsec() < _probeDeadlineMsec) return;

		OrbitalCamera camera = _probeScene.PlayerCamera;
		switch (_probeStage)
		{
			case 1: // first person is active
				_probeStage = 2;
				ProbeCheck("the script switched to first person", HasLog("CAM fp firstperson"));
				ProbeCheck("the camera sits at the character's eye (" + camera.GlobalPosition + ")",
					camera.Mode == LotCameraMode.FirstPerson &&
					camera.GlobalPosition.DistanceTo(
						LotCameraMath.FirstPersonPosition(_probeScene.Player.Position, camera.FocusHeight)) < 0.6f);
				ProbeCheck("the character's own mesh is hidden in first person",
					!_probeScene.PlayerCharacterMeshVisible());
				_probeDeadlineMsec = Time.GetTicksMsec() + 550;
				break;
			case 2: // fixed mode holds the given pose
				_probeStage = 3;
				ProbeCheck("the script switched to a fixed camera", HasLog("CAM fixed fixed"));
				ProbeCheck("the camera holds the fixed position (" + camera.GlobalPosition + ")",
					camera.Mode == LotCameraMode.Fixed &&
					camera.GlobalPosition.DistanceTo(new Vector3(30f, 5f, 30f)) < 0.01f);
				ProbeCheck("the character is visible again outside first person",
					_probeScene.PlayerCharacterMeshVisible());
				_probeDeadlineMsec = Time.GetTicksMsec() + 750;
				break;
			case 3: // mid-flight of the first shot
				_probeStage = 4;
				ProbeCheck("the shot reports scripted mode", HasLog("CAM fly scripted"));
				ProbeCheck("the camera is mid-flight, strictly between the endpoints (x " +
					camera.GlobalPosition.X + ")",
					camera.Mode == LotCameraMode.Scripted &&
					camera.GlobalPosition.X > 30.2f && camera.GlobalPosition.X < 59.8f);
				_probeDeadlineMsec = Time.GetTicksMsec() + 650;
				break;
			case 4: // the shot finished; the second one was cancelled
				_probeStage = 5;
				ProbeCheck("the finished shot returned to the previous mode", HasLog("CAM after fixed"));
				ProbeCheck("the cancelled shot froze in place", HasLog("CAM cancel fixed"));
				_probeFrozenPosition = camera.GlobalPosition;
				ProbeCheck("the frozen camera is neither at the shot's origin nor its target (" +
					_probeFrozenPosition + ")",
					camera.Mode == LotCameraMode.Fixed &&
					_probeFrozenPosition.X > 1f && _probeFrozenPosition.X < 59.5f);
				_probeDeadlineMsec = Time.GetTicksMsec() + 350;
				break;
			case 5: // the freeze is stable
				_probeStage = 6;
				ProbeCheck("the frozen camera holds its pose (" + camera.GlobalPosition + ")",
					camera.GlobalPosition.DistanceTo(_probeFrozenPosition) < 0.01f);
				_probeScene.ExitTestMode();
				_probeDeadlineMsec = Time.GetTicksMsec() + ProbeExitWaitMsec;
				break;
			default: // out of the session: control came back cleanly
				_probeStage = 7;
				ProbeCheck("the freecam owns the view again after the session",
					_probeScene.Freecam.Current && !_probeScene.PlayerCamera.Current);
				ProbeCheck("the next session starts from third person with the character visible",
					_probeScene.PlayerCamera.Mode == LotCameraMode.ThirdPerson &&
					_probeScene.PlayerCharacterMeshVisible());

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
				GD.Print("[CameraSelfTest] session probe: " + _probeChecks + " checks, " +
					_probeFailures + " failure(s).");
				break;
		}
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