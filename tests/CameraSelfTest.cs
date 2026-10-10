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
		TestZoomMath();
		TestAimBasis(scene);
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

	// --- pure: the zoom glide and the first-person blend (milestone 3.14) ------------------------

	private static void TestZoomMath()
	{
		Check("zoom: the blend is 0 at and above the band's start",
			LotCameraMath.BlendFactor(5f, 3.5f, 1f) == 0f && LotCameraMath.BlendFactor(3.5f, 3.5f, 1f) == 0f);
		Check("zoom: the blend is 1 at and past the inner end",
			LotCameraMath.BlendFactor(1f, 3.5f, 1f) == 1f && LotCameraMath.BlendFactor(0.5f, 3.5f, 1f) == 1f);
		Check("zoom: the blend midpoint is halfway (" + LotCameraMath.BlendFactor(2.25f, 3.5f, 1f) + ")",
			Near(LotCameraMath.BlendFactor(2.25f, 3.5f, 1f), 0.5f));

		Check("zoom: smoothstep pins its endpoints, midpoint and clamps",
			LotCameraMath.Smoothstep(0f) == 0f && LotCameraMath.Smoothstep(1f) == 1f &&
			Near(LotCameraMath.Smoothstep(0.5f), 0.5f) &&
			LotCameraMath.Smoothstep(-3f) == 0f && LotCameraMath.Smoothstep(4f) == 1f);

		Check("zoom: the glide snaps exactly onto its target",
			LotCameraMath.Damp(5.00005f, 5f, 14f, 0.016f) == 5f && LotCameraMath.Damp(5f, 5f, 14f, 0.016f) == 5f);
		Check("zoom: a zero delta or rate leaves the value alone",
			LotCameraMath.Damp(4f, 7f, 14f, 0f) == 4f && LotCameraMath.Damp(4f, 7f, 0f, 0.016f) == 4f);
		float step1 = LotCameraMath.Damp(1f, 7f, 14f, 0.016f);
		float step2 = LotCameraMath.Damp(step1, 7f, 14f, 0.016f);
		Check("zoom: the glide is monotone toward the target (" + step1 + " then " + step2 + ")",
			step1 > 1f && step1 < 7f && step2 > step1 && step2 < 7f);
		float settled = 1f;
		for (int i = 0; i < 400; i++) settled = LotCameraMath.Damp(settled, 7f, 14f, 0.016f);
		Check("zoom: the glide lands exactly on the target", settled == 7f);
	}

	// --- pure: the world-up aim guard (the straight-down LookAt fix) ------------------------------

	private static void TestAimBasis(BuilderScene scene)
	{
		// Non-colinear: identical to the engine's own basis for the same input.
		Basis engineSide = Basis.LookingAt(new Vector3(1f, 0f, 0f), Vector3.Up);
		Basis ours = LotCameraMath.LookBasis(new Vector3(1f, 0f, 0f), Vector3.Up);
		Check("aim: an ordinary view matches the engine's Basis.LookingAt (" +
			ours.Z.DistanceTo(engineSide.Z) + ")",
			ours.X.DistanceTo(engineSide.X) < 1e-6f && ours.Y.DistanceTo(engineSide.Y) < 1e-6f &&
			ours.Z.DistanceTo(engineSide.Z) < 1e-6f);

		// Colinear: the engine's reference is the NATIVE look-at path (Node3D.LookAt) — the
		// managed Basis.LookingAt static throws an ArgumentException on this input, so it is not
		// the reference. The native path logs its warning and falls back to
		// get_any_perpendicular; invoking it here deliberately (twice) IS the proof our port
		// produces the engine-identical basis — the aim path itself never touches it.
		Basis engineDown = NativeLookBasis(scene, new Vector3(0f, -1f, 0f));
		Basis ourDown = LotCameraMath.LookBasis(new Vector3(0f, -1f, 0f), Vector3.Up);
		Check("aim: a straight-down view matches the engine's native fallback (" + ourDown.Y +
			" vs " + engineDown.Y + ")",
			ourDown.X.DistanceTo(engineDown.X) < 1e-6f && ourDown.Y.DistanceTo(engineDown.Y) < 1e-6f &&
			ourDown.Z.DistanceTo(engineDown.Z) < 1e-6f);
		Basis engineUp = NativeLookBasis(scene, new Vector3(0f, 1f, 0f));
		Basis ourUp = LotCameraMath.LookBasis(new Vector3(0f, 1f, 0f), Vector3.Up);
		Check("aim: a straight-up view matches the engine's native fallback too",
			ourUp.X.DistanceTo(engineUp.X) < 1e-6f && ourUp.Y.DistanceTo(engineUp.Y) < 1e-6f &&
			ourUp.Z.DistanceTo(engineUp.Z) < 1e-6f);

		// Determinism, plus the exact convention the probe checks live: straight down shows -X as
		// the camera's up axis (the engine's own fallback roll).
		Basis again = LotCameraMath.LookBasis(new Vector3(0f, -1f, 0f), Vector3.Up);
		Check("aim: the fallback is deterministic (" + again.Y + ")",
			again == ourDown && ourDown.Y.DistanceTo(new Vector3(-1f, 0f, 0f)) < 1e-5f);
	}

	/// <summary>
	/// The engine's native look-at basis for a direction, via a throwaway node — the reference
	/// the colinear checks compare against. Node3D.LookAt is the path the camera used before the
	/// fix (the managed <c>Basis.LookingAt</c> static throws on colinear input, so it cannot be
	/// the reference); it logs its one warning per call by design here. The identity is returned
	/// on any exception so a check fails readably instead of aborting the suite.
	/// </summary>
	private static Basis NativeLookBasis(BuilderScene scene, Vector3 direction)
	{
		Node3D node = new Node3D();
		scene.AddChild(node);
		try
		{
			node.LookAt(node.GlobalPosition + direction, Vector3.Up);
			return node.GlobalTransform.Basis;
		}
		catch (Exception)
		{
			return Basis.Identity;
		}
		finally
		{
			scene.RemoveChild(node);
			node.Free();
		}
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

	private const long ZoomSettleMsec = 900;

	private static BuilderScene _probeScene;
	private static Node _probeFixture;
	private static ulong _probeDeadlineMsec;
	private static int _probeStage; // 1..11 drive the timeline, 12 = finished
	private static int _probeChecks;
	private static int _probeFailures;
	private static Vector3 _probeFrozenPosition;
	private static float _probeFixedDistance;
	private static readonly List<string> _probeFiles = new List<string>();

	/// <summary>Whether the staged probe completed; the scene warns when a run ends too early.</summary>
	public static bool ProbeDone { get { return _probeStage >= 12; } }

	private static void StartProbe(BuilderScene scene)
	{
		if (DisplayServer.GetName() != "headless")
		{
			// 12 = finished. NOT a stage inside the active range: stages 1..11 dereference the
			// session and its camera, so an interactive (non-headless) session must land exactly
			// on the done value — the old skip to 7 (an active zoom stage) NRE'd every frame,
			// because TickProbe then ran against a scene the probe never started.
			_probeStage = 12;
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

	/// <summary>The probe's checkpoints: the fixture's wall-clock phases drive the first session
	/// (t ≈ 0.2 fp, 0.8 fixed, 1.4–2.0 shot 1, 2.3 shot 2, 2.5 cancel), then a second session drives
	/// the wheel through the zoom band (milestone 3.14).</summary>
	public static void TickProbe()
	{
		if (_probeStage < 1 || _probeStage >= 12) return;
		if (Time.GetTicksMsec() < _probeDeadlineMsec) return;
		// Belt and braces: every active stage dereferences the session this probe started, so a
		// probe that never started must stop here even if a future edit reintroduces a skip value
		// inside the active range (the NRE-flood bug this guard now backstops).
		if (_probeScene == null) return;

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
				// The fixed pose aims straight down (30,5,30 -> 30,1,30): the guarded aim must
				// take the engine's own silent fallback basis instead of Godot's per-frame
				// colinear warning.
				ProbeCheck("the straight-down fixed camera took the fallback basis (-X up, " +
					camera.GlobalTransform.Basis.Y + ")",
					camera.GlobalTransform.Basis.Y.DistanceTo(new Vector3(-1f, 0f, 0f)) < 1e-4f);
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
			case 6: // out of the first session: control came back cleanly; start the zoom session
			{
				_probeStage = 7;
				ProbeCheck("the freecam owns the view again after the session",
					_probeScene.Freecam.Current && !_probeScene.PlayerCamera.Current);
				ProbeCheck("the camera came back reset (mode " + _probeScene.PlayerCamera.Mode +
					", distance " + _probeScene.PlayerCamera.Distance + ")",
					_probeScene.PlayerCamera.Mode == LotCameraMode.ThirdPerson &&
					Near(_probeScene.PlayerCamera.Distance, OrbitalCamera.DefaultDistance) &&
					_probeScene.PlayerCharacterMeshVisible());

				// The fixture's scripted mode changes would fight the zoom session, so the fixture
				// leaves before it (immediate Free, not QueueFree — the runtime is about to load
				// the tree and must not find a node whose file is already gone). The default
				// lot's own character script stays.
				if (_probeFixture != null && GodotObject.IsInstanceValid(_probeFixture))
					_probeFixture.Free();
				_probeFixture = null;
				for (int i = 0; i < _probeFiles.Count; i++)
				{
					string path = ProbeScriptDir + "/" + _probeFiles[i];
					if (FileAccess.FileExists(path)) DirAccess.RemoveAbsolute(path);
				}
				_probeFiles.Clear();

				// Session two: the wheel. Six notches in = exactly the minimum distance; let the
				// glide settle.
				_probeScene.EnterTestMode(false);
				_probeScene.PlayerCamera.AddZoom(6);
				_probeDeadlineMsec = Time.GetTicksMsec() + ZoomSettleMsec;
				break;
			}
			case 7: // fully zoomed in: the pose is the eye, the mesh has faded; wheel back out
			{
				_probeStage = 8;
				OrbitalCamera zoomCamera = _probeScene.PlayerCamera;
				ProbeCheck("the glide settled at the innermost distance (" + zoomCamera.Distance + ")",
					Near(zoomCamera.Distance, zoomCamera.ZoomMin, 1e-3f));
				ProbeCheck("the fully zoomed pose is the first-person eye (" + zoomCamera.GlobalPosition + ")",
					zoomCamera.GlobalPosition.DistanceTo(
						LotCameraMath.FirstPersonPosition(_probeScene.Player.Position, zoomCamera.FocusHeight)) < 0.01f);
				ProbeCheck("the character's mesh has faded out at the eye (" +
					_probeScene.Player.Character.Transparency + ")",
					_probeScene.Player.Character.Transparency >= 0.95f);
				zoomCamera.AddZoom(-6);
				_probeDeadlineMsec = Time.GetTicksMsec() + ZoomSettleMsec;
				break;
			}
			case 8: // back out: exactly the previous orbit; then the mode composition rules
			{
				_probeStage = 9;
				OrbitalCamera zoomCamera = _probeScene.PlayerCamera;
				ProbeCheck("the wheel-out landed exactly on the previous orbit (" + zoomCamera.Distance + ")",
					zoomCamera.Distance == OrbitalCamera.DefaultDistance);
				ProbeCheck("the mesh is back to its authored transparency (" +
					_probeScene.Player.Character.Transparency + ")",
					_probeScene.Player.Character.Transparency <= 0.02f);

				// Fixed is zoom-proof: the wheel changes nothing.
				zoomCamera.SetMode(LotCameraMode.Fixed);
				_probeFixedDistance = zoomCamera.Distance;
				zoomCamera.AddZoom(5);
				ProbeCheck("a fixed camera ignores the wheel (" + zoomCamera.Distance + ")",
					Near(zoomCamera.Distance, _probeFixedDistance, 1e-4f));

				// A script-set first person: the wheel out is the player's way back to the orbit.
				zoomCamera.SetMode(LotCameraMode.FirstPerson);
				_probeDeadlineMsec = Time.GetTicksMsec() + 150;
				break;
			}
			case 9: // scripted first person pins fully in; a wheel-out takes the view back
			{
				_probeStage = 10;
				OrbitalCamera zoomCamera = _probeScene.PlayerCamera;
				ProbeCheck("a script-set first person pins the zoom fully in (" + zoomCamera.Distance + ")",
					zoomCamera.Mode == LotCameraMode.FirstPerson && Near(zoomCamera.Distance, zoomCamera.ZoomMin, 1e-3f));
				zoomCamera.AddZoom(-3);
				ProbeCheck("a wheel-out takes the view back from scripted first person (" + zoomCamera.Mode + ")",
					zoomCamera.Mode == LotCameraMode.ThirdPerson);
				_probeDeadlineMsec = Time.GetTicksMsec() + ZoomSettleMsec;
				break;
			}
			case 10: // the wheel-out glide is settling: out of the band, mesh restored, then leave
			{
				_probeStage = 11;
				OrbitalCamera zoomCamera = _probeScene.PlayerCamera;
				ProbeCheck("the wheel-out glide settled three notches out (" + zoomCamera.Distance + ")",
					zoomCamera.Mode == LotCameraMode.ThirdPerson && Near(zoomCamera.Distance, 4f, 0.05f));
				ProbeCheck("the mesh fade is gone once out of the band (" +
					_probeScene.Player.Character.Transparency + ")",
					_probeScene.Player.Character.Transparency <= 0.02f);
				_probeScene.ExitTestMode();
				_probeDeadlineMsec = Time.GetTicksMsec() + ProbeExitWaitMsec;
				break;
			}
			default: // out of the zoom session: everything reset, fixtures already cleaned
				_probeStage = 12;
				ProbeCheck("the freecam owns the view again after the zoom session",
					_probeScene.Freecam.Current && !_probeScene.PlayerCamera.Current);
				ProbeCheck("leaving the session reset the mode and the distance (" +
					_probeScene.PlayerCamera.Mode + ", " + _probeScene.PlayerCamera.Distance + ")",
					_probeScene.PlayerCamera.Mode == LotCameraMode.ThirdPerson &&
					_probeScene.PlayerCamera.Distance == OrbitalCamera.DefaultDistance);
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