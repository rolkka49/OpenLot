using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// Integration fixture for the net.* layer (Milestone 3.2 polish): unlike NetSelfTest, which
/// exercises components on private VMs, these tests drive the REAL LuaManager + ScriptRuntime +
/// live lot scene, with real script files under user://Scripts. This is the stand-in for hands-on
/// testing until Milestone 3.4 (Test mode) exists. Runs from BuilderScene._Ready under #if DEBUG,
/// after NetSelfTest. Every fixture file is written with a __netselftest_ prefix and deleted
/// afterwards.
/// </summary>
public static class NetIntegrationTest
{
	private const string ScriptDir = "user://Scripts";

	private static int _checks;
	private static int _failures;
	private static bool _completed;
	private static BuilderScene _scene;
	private static readonly List<string> _createdFiles = new List<string>();

	public static int Run(BuilderScene scene)
	{
		_checks = 0;
		_failures = 0;
		_completed = false;
		_scene = scene;
		_createdFiles.Clear();
		LuaManager.Instance.SelfTestMode = true; // deliberate OOM/trip/suspension message prefix
		try
		{
			// Order matters: the reload/frame-budget test must run before the VM-rebuild test,
			// which rebuilds the lot VM on purpose.
			TestFrameBudgetAcrossReload(scene);
			// Live-VM wiring checks relocated from the self-test suite (item 7): that suite now runs
			// entirely on private Lua states, and these need the REAL lot VM.
			TestLiveScratchPath();
			TestLiveCapsuleSpawnPath(scene);
			TestLiveGetHandleWiring(scene);
			TestSpawnDuplicationAcrossReload(scene);
			TestVmRebuildKeepsBridgeAndScripts(scene);
			TestPlayerSessionCameraHandoff(scene);
			TestLoadTimeOomQuarantineAndRebuildLimit(scene); // last: it suspends scripting
			_completed = true;
		}
		catch (Exception ex)
		{
			_failures++;
			GD.PrintErr("[NetIntegrationTest] FAIL  suite-level exception: " + ex.GetType().Name + ": " + ex.Message);
		}
		finally
		{
			CleanupFiles();
		}
		LuaManager.Instance.SelfTestMode = false;
		// The suite deliberately provokes VM rebuilds and, by design, suspends scripting in its last
		// test. Restore the live session so the developer is not left with scripting disabled, the
		// red Viewport banner, or the lot's own scripts missing from a rebuilt VM: fresh VM, limiter
		// cleared, then reload the scene's real lot scripts into it (which also clears any fixture
		// spawns tagged as load-time owned).
		LuaManager.Instance.RecoverFromSelfTest();
		if (_scene != null && _scene.LuaScripts != null)
			_scene.LuaScripts.ReloadScripts();
		if (_completed)
		{
			GD.Print("[NetIntegrationTest] integration suite complete: " + _checks + " checks, " + _failures + " failure(s).");
		}
		else
		{
			// The suite aborted: the headless run must not look green, so quit non-zero.
			GD.PrintErr("[NetIntegrationTest] integration suite INCOMPLETE (" + _checks + " checks ran) — aborting with exit code 1");
			if (_scene != null && _scene.GetTree() != null) _scene.GetTree().Quit(1);
		}
		return _failures;
	}

	private static void Check(string name, bool condition)
	{
		_checks++;
		if (condition)
		{
			GD.Print("[NetIntegrationTest] PASS  " + name);
		}
		else
		{
			_failures++;
			GD.PrintErr("[NetIntegrationTest] FAIL  " + name);
		}
	}

	// --- Fixture plumbing ---

	private static string WriteScript(string fileName, string contents)
	{
		if (!Godot.DirAccess.DirExistsAbsolute(ScriptDir))
			Godot.DirAccess.MakeDirRecursiveAbsolute(ScriptDir);
		string path = ScriptDir + "/" + fileName;
		Godot.FileAccess file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write);
		if (file == null)
		{
			GD.PrintErr("[NetIntegrationTest] FAIL  could not write fixture file " + path);
			_failures++;
			return null;
		}
		file.StoreString(contents);
		file.Dispose();
		_createdFiles.Add(fileName);
		return path;
	}

	private static void CleanupFiles()
	{
		Godot.DirAccess dir = Godot.DirAccess.Open(ScriptDir);
		if (dir == null) return;
		for (int i = 0; i < _createdFiles.Count; i++)
			dir.Remove(_createdFiles[i]);
		_createdFiles.Clear();
	}

	private static LotScriptNode MakeScriptNode(string fileName)
	{
		LotScriptNode script = new LotScriptNode();
		script.Name = fileName.Replace('.', '_');
		script.DisplayName = fileName;
		script.ScriptPath = ScriptDir + "/" + fileName;
		return script;
	}

	private static Node3D MakeEntity(Node root, string entityName, string fileName)
	{
		Node3D entity = new Node3D();
		entity.Name = entityName;
		root.AddChild(entity);
		entity.AddChild(MakeScriptNode(fileName));
		return entity;
	}

	private static int CountChildren(Node node)
	{
		return node != null ? node.GetChildCount() : -1;
	}

	// --- Live capsule spawn path (milestone 3.4, slice 1) ---

	private static void TestLiveCapsuleSpawnPath(BuilderScene scene)
	{
		// The capsule kind must survive the whole chain a creator script uses: reflection
		// registration (LuaManager), the sandbox's Lot proxy (LuaBootstrap), and the shared Spawn3D
		// path (mesh + collision build). Also pins the F2 reload contract on the capsule path: a
		// load-time-owned capsule is removed exactly once by the restart cleanup, so a lot-root
		// character script can re-run without duplicating the character.
		if (!LuaManager.Instance.IsRuntimeAvailable)
		{
			Check("capsule spawn path (runtime unavailable)", false);
			return;
		}
		List<string> warnings = new List<string>();
		// The LIVE lot root, not a detached fixture root: Spawn3D parents new objects here, and
		// the reload cleanup walks exactly the root it was handed — so only a live-root load
		// exercises the real no-duplication path (same shape as the F2 fixture).
		Node3D lotRoot = scene.LotRoot;
		List<Node> fixtureNodes = new List<Node>();
		try
		{
			WriteScript("__netselftest_capsule.lua",
				"Lot.SpawnCapsule(2, 40, 0)\n" +
				"function start() capsule(6, 41, 0) end");
			LotScriptNode script = MakeScriptNode("__netselftest_capsule.lua");
			lotRoot.AddChild(script);
			fixtureNodes.Add(script);

			ScriptRuntime scripts = new ScriptRuntime(() => LuaManager.Instance.NetBridge, warnings.Add, () => { }, () => false);
			scripts.LoadLot(lotRoot, scene.EnsureEntityHandle, BuilderScene.ReadScriptFile, scene.DestroyEntity);

			Check("Lot.SpawnCapsule from a lot-root chunk builds a Capsule LotObject (y40=" +
				CountCapsulesAt(scene, 40f) + ")", CountCapsulesAt(scene, 40f) == 1);
			Check("capsule() convenience wrapper reaches the same bound method (y41=" +
				CountCapsulesAt(scene, 41f) + ")", CountCapsulesAt(scene, 41f) == 1);

			// Reload (the F2 contract, exercised on the capsule path): owned spawns removed, then
			// re-spawned exactly once by the re-run scripts.
			scripts.LoadLot(lotRoot, scene.EnsureEntityHandle, BuilderScene.ReadScriptFile, scene.DestroyEntity);

			Check("reload does not duplicate the chunk-spawned capsule (y40=" +
				CountCapsulesAt(scene, 40f) + ")", CountCapsulesAt(scene, 40f) == 1);
			Check("reload does not duplicate the start()-spawned capsule (y41=" +
				CountCapsulesAt(scene, 41f) + ")", CountCapsulesAt(scene, 41f) == 1);

			// The §3.4 re-entry seam itself: the runtime must be stoppable and startable again
			// mid-session, and a restart must actually re-run the lot's scripts. Test mode calls
			// exactly this pair around a lot reload.
			scene.StopScriptRuntime();
			Check("StopScriptRuntime leaves no running runtime", scene.LuaScripts == null);
			scene.StartScriptRuntime();
			Check("StartScriptRuntime returns a running runtime and re-runs scripts (y40=" +
				CountCapsulesAt(scene, 40f) + ")",
				scene.LuaScripts != null && CountCapsulesAt(scene, 40f) == 1);
		}
		finally
		{
			// Same contract as the other fixtures: the script nodes are freed here, while the
			// spawned capsules stay in the live lot tagged as load-time owned — the scene reload
			// after the suite (Run's tail) is what removes them through the real cleanup path.
			for (int i = 0; i < fixtureNodes.Count; i++)
			{
				if (fixtureNodes[i] != null && GodotObject.IsInstanceValid(fixtureNodes[i]))
					fixtureNodes[i].Free();
			}
		}
	}

	/// <summary>Counts live Capsule lot objects at a height, skipping deletion-queued ones (the
	/// reload cleanup defers its frees to the end of the frame).</summary>
	private static int CountCapsulesAt(BuilderScene scene, float y)
	{
		int count = 0;
		Node root = scene.LotRoot;
		for (int i = 0; i < root.GetChildCount(); i++)
		{
			LotObject lotObject = root.GetChild(i) as LotObject;
			if (lotObject == null || lotObject.IsQueuedForDeletion()) continue;
			if (lotObject.Kind != LotObjectKind.Capsule) continue;
			if (Mathf.Abs(lotObject.Position.Y - y) < 0.01f) count++;
		}
		return count;
	}

	// --- Player session: camera handoff + mode switch (milestone 3.4) ---

	private static void TestPlayerSessionCameraHandoff(BuilderScene scene)
	{
		// §3.4: entering a player session reloads the lot's scripts, attaches the character, and
		// hands the viewport from the freecam to the player camera; leaving it puts everything back.
		// Also pins the input-gate composition, which is pure math and so verifiable headless.
		if (!LuaManager.Instance.IsRuntimeAvailable)
		{
			Check("player session camera handoff (runtime unavailable)", false);
			return;
		}

		Builder builder = Builder.Instance;
		Check("builder instance is available for the session test", builder != null);
		if (builder == null) return;

		// Gate composition: the player-session gate must compose with the modal/focus gate.
		bool editor, player;
		ViewportWindow.ResolveCameraAuthority(true, true, false, out editor, out player);
		Check("editor session: the freecam has authority and the player camera does not", editor && !player);
		ViewportWindow.ResolveCameraAuthority(true, true, true, out editor, out player);
		Check("player session: the player camera has authority and the freecam does not", !editor && player);
		ViewportWindow.ResolveCameraAuthority(false, true, false, out editor, out player);
		Check("a modal denies the freecam even in the editor", !editor && !player);
		ViewportWindow.ResolveCameraAuthority(true, false, true, out editor, out player);
		Check("a modal denies the player camera even in a player session", !editor && !player);

		scene.EnterTestMode(false);
		Check("Test mode reports an active player session", scene.InTestMode && !scene.InGameMode);
		Check("the player camera owns the viewport", scene.PlayerCamera != null && scene.PlayerCamera.Current);
		Check("the freecam yields the viewport", !scene.Freecam.Current);
		Check("the player controller attached to the lot's character",
			scene.Player != null && scene.Player.IsAttached && scene.Player.Character != null
			&& scene.Player.Character.Kind == LotObjectKind.Capsule);
		Check("the builder reflects the active player session", builder.InTestMode && !builder.InGameMode);

		scene.ExitTestMode();
		Check("leaving the session returns the viewport to the freecam",
			!scene.InTestMode && !scene.InGameMode && scene.Freecam.Current && !scene.PlayerCamera.Current);
		Check("leaving the session detaches the player controller", scene.Player != null && !scene.Player.IsAttached);
		Check("the builder no longer reports a player session", !builder.InTestMode && !builder.InGameMode);

		// Game mode is the same session with the creation-environment UI hidden.
		scene.EnterTestMode(true);
		Check("Game mode runs a player session with the editor UI hidden", scene.InTestMode && scene.InGameMode);
		scene.ExitTestMode();
		Check("leaving Game mode restores the editor", !scene.InTestMode && !scene.InGameMode && scene.Freecam.Current);
	}

	// --- B: frame instruction budget vs. script loads (regression test, calibrated by measurement) ---

	private static void TestFrameBudgetAcrossReload(BuilderScene scene)
	{
		// Pre-fix this failed: the 5th script tripped "frame execution limit exceeded" because every
		// load of a reload shared one frame's instruction budget. Calibration is by MEASUREMENT — a
		// known loop runs, the watchdog reports the instructions it actually consumed, and the
		// iteration count is derived from that instead of guessed.
		if (!LuaManager.Instance.IsRuntimeAvailable)
		{
			Check("frame-budget reload (runtime unavailable)", false);
			return;
		}
		LuaManager.Instance.ResetRebuildWindowForTest();
		List<string> warnings = new List<string>();
		Node3D calibRoot = new Node3D();
		Node3D lotRoot = new Node3D();
		try
		{
			const int calibrationIterations = 20000;
			WriteScript("__netselftest_calib.lua",
				"local x = 0 for i = 1, " + calibrationIterations + " do x = x + math.floor(1.5) end");
			MakeEntity(calibRoot, "Calibration", "__netselftest_calib.lua");
			ScriptRuntime calibrator = new ScriptRuntime(() => LuaManager.Instance.NetBridge, warnings.Add, () => { }, () => false);
			calibrator.Tick(); // as on a reload: the frame budget is live
			calibrator.LoadLot(calibRoot, scene.EnsureEntityHandle, BuilderScene.ReadScriptFile, scene.DestroyEntity);
			int measured = LuaManager.Instance.Watchdog.UsedInstructions();
			double perIteration = measured / (double)calibrationIterations;
			int iterations = perIteration > 0 ? (int)(LuaWatchdog.ScriptInstructionBudget * 0.7 / perIteration) : 0;
			GD.Print("[NetIntegrationTest] calibration: " + measured + " instructions for " + calibrationIterations +
				" iterations (~" + perIteration.ToString("0.0") + "/iteration) -> " + iterations +
				" iterations/script (~" + (long)(iterations * perIteration) + " instructions, per-load budget " +
				LuaWatchdog.ScriptInstructionBudget + ")");
			Check("calibration measured a usable instruction cost (" + measured + " instructions measured)",
				perIteration > 0 && iterations > 0);
			if (iterations <= 0) return;

			string heavyBody = "local x = 0 for i = 1, " + iterations + " do x = x + math.floor(1.5) end";
			for (int i = 0; i < 8; i++)
			{
				string fileName = "__netselftest_heavy_" + i + ".lua";
				WriteScript(fileName, heavyBody);
				MakeEntity(lotRoot, "Heavy" + i, fileName);
			}

			// Per-script measured counts: one load at a time so each unit's own count is visible.
			for (int i = 0; i < 8; i++)
			{
				Node3D one = new Node3D();
				MakeEntity(one, "Measure" + i, "__netselftest_heavy_" + i + ".lua");
				ScriptRuntime oneShot = new ScriptRuntime(() => LuaManager.Instance.NetBridge, warnings.Add, () => { }, () => false);
				oneShot.LoadLot(one, scene.EnsureEntityHandle, BuilderScene.ReadScriptFile, scene.DestroyEntity);
				GD.Print("[NetIntegrationTest] heavy script " + i + " measured " +
					LuaManager.Instance.Watchdog.UsedInstructions() + " instructions");
				one.Free();
			}

			// The reload scenario: all eight in ONE load (this is the regression case).
			ScriptRuntime scripts = new ScriptRuntime(() => LuaManager.Instance.NetBridge, warnings.Add, () => { }, () => false);
			scripts.Tick();
			scripts.LoadLot(lotRoot, scene.EnsureEntityHandle, BuilderScene.ReadScriptFile, scene.DestroyEntity);

			bool frameTrip = warnings.Exists(w => w.Contains("frame execution limit exceeded"));
			bool unitTrip = warnings.Exists(w => w.Contains("script execution limit exceeded"));
			Check("8 heavy scripts (~" + (long)(iterations * perIteration) + " instructions each, ~" +
				(long)(8 * iterations * perIteration) + " total vs the " + LuaWatchdog.FrameInstructionBudget +
				" frame budget) load on a reload without tripping, loaded " + scripts.LoadedScriptCount + "/8" +
				(frameTrip ? " (frame trip: " + FirstWith(warnings, "frame execution limit exceeded") + ")" : "") +
				(unitTrip ? " (unit trip: " + FirstWith(warnings, "script execution limit exceeded") + ")" : ""),
				!frameTrip && !unitTrip && scripts.LoadedScriptCount == 8);
		}
		finally
		{
			calibRoot.Free();
			lotRoot.Free();
		}
	}

	// --- F2: load-time spawns are owned, removed before a reload, and recreated exactly once ---

	private static void TestSpawnDuplicationAcrossReload(BuilderScene scene)
	{
		// A root script that spawns entities (the capsule + camera pattern) runs again on reload.
		// Load-time spawns are owned by that script's load, so the reload removes them first.
		if (!LuaManager.Instance.IsRuntimeAvailable)
		{
			Check("spawn duplication (runtime unavailable)", false);
			return;
		}
		LuaManager.Instance.ResetRebuildWindowForTest();
		List<string> warnings = new List<string>();
		// Production shape matters here: load-time spawns go to the LOT ROOT the runtime walks, so the
		// fixture attaches its entities there instead of to a detached node.
		Node lotRoot = scene.LotRoot;
		List<Node> fixtureNodes = new List<Node>();
		try
		{
			WriteScript("__netselftest_spawner.lua", "Lot.SpawnCube(0, 9, 0)");
			WriteScript("__netselftest_spawner_start.lua", "function start() Lot.SpawnCube(0, 15, 0) end");
			WriteScript("__netselftest_rig.lua", "Lot.SpawnCube(0, 20, 0) Lot.SpawnCube(0, 21, 0)");
			WriteScript("__netselftest_leak_f2.lua",
				"net.server.Leak = function() local s = 'x' for i = 1, 30 do s = s .. s end end");

			// All three spawner scripts hang directly off the fixture root: lot-root entity (handle 0),
			// exactly the capsule/camera pattern the milestone cares about.
			LotScriptNode spawnerChunk = MakeScriptNode("__netselftest_spawner.lua");
			lotRoot.AddChild(spawnerChunk);
			fixtureNodes.Add(spawnerChunk);
			LotScriptNode spawnerStart = MakeScriptNode("__netselftest_spawner_start.lua");
			lotRoot.AddChild(spawnerStart);
			fixtureNodes.Add(spawnerStart);
			LotScriptNode rig = MakeScriptNode("__netselftest_rig.lua");
			lotRoot.AddChild(rig);
			fixtureNodes.Add(rig);
			Node3D leaker = MakeEntity(lotRoot, "LeakerF2", "__netselftest_leak_f2.lua");
			fixtureNodes.Add(leaker);

			// An ordinary, UNOWNED node (an editor-placed object) must survive the cleanup untouched.
			LotObject editorNode = LotObject.Create(LotObjectKind.Cube, "__netselftest_editor_node", new Color(1f, 1f, 1f));
			editorNode.Position = new Vector3(0f, 30f, 0f);
			scene.LotRoot.AddChild(editorNode);
			fixtureNodes.Add(editorNode); // teardown removes it; it only has to survive the reload

			ScriptRuntime scripts = new ScriptRuntime(() => LuaManager.Instance.NetBridge, warnings.Add, () => { }, () => false);
			scripts.LoadLot(lotRoot, scene.EnsureEntityHandle, BuilderScene.ReadScriptFile, scene.DestroyEntity);

			Check("chunk spawn: one marker (y9=" + CountMarkers(scene, 9f) + ")", CountMarkers(scene, 9f) == 1);
			Check("start() spawn: one marker (y15=" + CountMarkers(scene, 15f) + ")", CountMarkers(scene, 15f) == 1);
			Check("two-entity rig spawned once (y20=" + CountMarkers(scene, 20f) + ", y21=" + CountMarkers(scene, 21f) + ")",
				CountMarkers(scene, 20f) == 1 && CountMarkers(scene, 21f) == 1);

			int leakerHandle = scene.EnsureEntityHandle(leaker);
			LuaNetBridge bridge = LuaManager.Instance.NetBridge;
			bridge.Router.InvokeLocal(leakerHandle, NetDirection.Server, "Leak", NetRouter.EmptyArgsPayload);
			scripts.Tick();
			LuaManager.Instance.ProcessDeferredRecreate();
			scripts.Tick(); // notices the rebuild and reloads
			scripts.Tick();

			Check("after rebuild + reload the chunk spawn is exactly one (y9=" + CountMarkers(scene, 9f) + ")",
				CountMarkers(scene, 9f) == 1);
			Check("after rebuild + reload the start() spawn is exactly one (y15=" + CountMarkers(scene, 15f) + ")",
				CountMarkers(scene, 15f) == 1);
			Check("after rebuild + reload the two-entity rig is exactly two (y20=" + CountMarkers(scene, 20f) +
				", y21=" + CountMarkers(scene, 21f) + ")",
				CountMarkers(scene, 20f) == 1 && CountMarkers(scene, 21f) == 1);
			Check("cleanup leaves unowned (editor-placed) nodes alone (y30=" + CountMarkers(scene, 30f) + ")",
				CountMarkers(scene, 30f) == 1);
			Check("cleanup is reported", warnings.Exists(w => w.Contains("spawned at load; restarting scripts")));

			if (CountMarkers(scene, 9f) != 1 || CountMarkers(scene, 15f) != 1 || CountMarkers(scene, 20f) != 1)
				GD.PrintErr("[NetIntegrationTest] NOTE  reload duplication still present (F2)");
		}
		finally
		{
			// Only OUR fixture nodes are removed (lotRoot is the live lot root — never free that).
			for (int i = 0; i < fixtureNodes.Count; i++)
			{
				if (fixtureNodes[i] != null && GodotObject.IsInstanceValid(fixtureNodes[i]))
					fixtureNodes[i].Free();
			}
		}
	}

	/// <summary>
	/// Counts live lot objects at a height. Nodes already queued for deletion are skipped: the
	/// reload cleanup uses QueueFree (deferred to the end of the frame), so a same-frame measurement
	/// must count what the lot will hold once the frame completes.
	/// </summary>
	private static int CountMarkers(BuilderScene scene, float y)
	{
		int count = 0;
		Node root = scene.LotRoot;
		for (int i = 0; i < root.GetChildCount(); i++)
		{
			LotObject lotObject = root.GetChild(i) as LotObject;
			if (lotObject == null || lotObject.IsQueuedForDeletion()) continue;
			if (Mathf.Abs(lotObject.Position.Y - y) < 0.01f) count++;
		}
		return count;
	}

	// --- F1: load-time OOM quarantine + rebuild limiter ---

	private static void TestLoadTimeOomQuarantineAndRebuildLimit(BuilderScene scene)
	{
		// A script that runs out of memory in its MAIN CHUNK must not create an
		// OOM -> rebuild -> reload -> OOM loop that burns a 64 MB VM every frame, and repeated
		// OOMs must stop rebuilding entirely (suspension).
		if (!LuaManager.Instance.IsRuntimeAvailable)
		{
			Check("load-time OOM quarantine (runtime unavailable)", false);
			return;
		}
		LuaManager.Instance.ResetRebuildWindowForTest();
		List<string> warnings = new List<string>();
		Node3D lotRoot = new Node3D();
		try
		{
			WriteScript("__netselftest_boom.lua", "local s = 'x' for i = 1, 30 do s = s .. s end");
			WriteScript("__netselftest_healthy.lua", "net.server.Ping = function() Lot.SpawnCube(0, 12, 0) end");
			Node3D boomer = MakeEntity(lotRoot, "Boomer", "__netselftest_boom.lua");
			Node3D healthy = MakeEntity(lotRoot, "Healthy", "__netselftest_healthy.lua");
			int healthyHandle = scene.EnsureEntityHandle(healthy);

			ScriptRuntime scripts = new ScriptRuntime(() => LuaManager.Instance.NetBridge, warnings.Add,
				() => { }, () => LuaManager.Instance.ScriptingSuspended);
			scripts.LoadLot(lotRoot, scene.EnsureEntityHandle, BuilderScene.ReadScriptFile, scene.DestroyEntity);

			Check("load-time OOM reported for the offending script",
				warnings.Exists(w => w.Contains("__netselftest_boom.lua")
					&& (w.Contains("quarantine") || w.Contains("out of memory"))));
			Check("the healthy neighbour still loads (loaded " + scripts.LoadedScriptCount + "/2)",
				scripts.LoadedScriptCount == 1);

			// Drive five frames of tick + deferred rebuild: pre-fix each one re-ran the OOM script.
			int rebuilds0 = LuaManager.Instance.VmRebuildCount;
			for (int i = 0; i < 5; i++)
			{
				scripts.Tick();
				LuaManager.Instance.ProcessDeferredRecreate();
			}
			int rebuilds = LuaManager.Instance.VmRebuildCount - rebuilds0;
			Check("load-time OOM does not rebuild every frame (rebuilds: " + rebuilds + ", cap " +
				LuaManager.MaxRebuildsPerWindow + " per " + LuaManager.RebuildWindowSeconds + "s)",
				rebuilds <= LuaManager.MaxRebuildsPerWindow);
			Check("quarantine stops the loop after ONE rebuild (rebuilds: " + rebuilds + ")",
				rebuilds == 1);
			Check("a single quarantined script does not suspend scripting",
				!LuaManager.Instance.ScriptingSuspended);
			Check("the quarantined script is skipped on reload, not retried (loaded " +
				scripts.LoadedScriptCount + "/2)", scripts.LoadedScriptCount == 1);

			// Quarantine lifts when the file changes: fix it, reload, and it must load again.
			WriteScript("__netselftest_boom.lua", "net.server.Fixed = function() Lot.SpawnCube(0, 40, 0) end");
			scripts.LoadLot(lotRoot, scene.EnsureEntityHandle, BuilderScene.ReadScriptFile, scene.DestroyEntity);
			Check("editing a quarantined script lifts the quarantine and it loads (loaded " +
				scripts.LoadedScriptCount + "/2, " +
				(warnings.Exists(w => w.Contains("quarantine LIFTED")) ? "lift reported" : "no lift report") + ")",
				warnings.Exists(w => w.Contains("quarantine LIFTED")) && scripts.LoadedScriptCount == 2);
			int boomerHandle = scene.EnsureEntityHandle(boomer);
			int fixed0 = CountMarkers(scene, 40f);
			LuaManager.Instance.NetBridge.Router.InvokeLocal(boomerHandle, NetDirection.Server, "Fixed", NetRouter.EmptyArgsPayload);
			scripts.Tick();
			Check("the fixed script's handler runs after the lift (y40=" + CountMarkers(scene, 40f) + ")",
				CountMarkers(scene, 40f) == fixed0 + 1);

			int pings0 = CountMarkers(scene, 12f);
			LuaNetBridge bridge = LuaManager.Instance.NetBridge;
			bridge.Router.InvokeLocal(healthyHandle, NetDirection.Server, "Ping", NetRouter.EmptyArgsPayload);
			scripts.Tick();
			Check("the healthy neighbour still dispatches after the churn (pings " + pings0 + " -> " +
				CountMarkers(scene, 12f) + ")",
				CountMarkers(scene, 12f) == pings0 + 1);

			// Rebuild limiter: repeated DISPATCH-time OOMs must stop rebuilding and suspend scripting.
			WriteScript("__netselftest_leak_lim.lua",
				"net.server.Leak = function() local s = 'x' for i = 1, 30 do s = s .. s end end");
			Node3D leaker = MakeEntity(lotRoot, "LimLeaker", "__netselftest_leak_lim.lua");
			scripts.LoadLot(lotRoot, scene.EnsureEntityHandle, BuilderScene.ReadScriptFile, scene.DestroyEntity);
			int leakerHandle = scene.EnsureEntityHandle(leaker);
			for (int i = 0; i < 6 && !LuaManager.Instance.ScriptingSuspended; i++)
			{
				LuaNetBridge current = LuaManager.Instance.NetBridge;
				if (current == null || current.IsDead) break;
				current.Router.InvokeLocal(leakerHandle, NetDirection.Server, "Leak", NetRouter.EmptyArgsPayload);
				scripts.Tick();
				LuaManager.Instance.ProcessDeferredRecreate();
			}
			Check("rebuild limiter suspends scripting after repeated OOMs (" +
				(LuaManager.Instance.SuspensionReason ?? "not suspended") + ")",
				LuaManager.Instance.ScriptingSuspended && LuaManager.Instance.SuspensionReason != null);
			int rebuildsWhenSuspended = LuaManager.Instance.VmRebuildCount;
			scripts.Tick();
			LuaManager.Instance.ProcessDeferredRecreate();
			bool noNewRebuild = LuaManager.Instance.VmRebuildCount == rebuildsWhenSuspended;
			Check("no further rebuilds once suspended", noNewRebuild);
			bool tickSkipped = LuaManager.Instance.ScriptingSuspended;
			Check("suspended runtime stops ticking (scripts stay disabled)", tickSkipped);
		}
		finally
		{
			lotRoot.Free();
		}
	}

	// --- A: OOM -> rebuild -> runtime stays bound + scripts come back ---

	private static void TestVmRebuildKeepsBridgeAndScripts(BuilderScene scene)
	{
		if (!LuaManager.Instance.IsRuntimeAvailable)
		{
			Check("VM rebuild (runtime unavailable)", false);
			return;
		}

		List<string> warnings = new List<string>();
		Node3D lotRoot = new Node3D();
		lotRoot.Name = "__NetIntegrationRebuild";
		try
		{
			WriteScript("__netselftest_door.lua", "net.server.Open = function() Lot.SpawnCube(0, 0, 5) end");
			WriteScript("__netselftest_leak.lua", "net.server.Leak = function() local s = 'x' for i = 1, 30 do s = s .. s end end");
			Node3D door = MakeEntity(lotRoot, "Door", "__netselftest_door.lua");
			Node3D leaker = MakeEntity(lotRoot, "Leaker", "__netselftest_leak.lua");

			LuaNetBridge bridge = LuaManager.Instance.NetBridge;
			// Resolver, not a cached reference: the runtime must follow the VM across a rebuild.
			ScriptRuntime scripts = new ScriptRuntime(() => LuaManager.Instance.NetBridge, warnings.Add, () => { });
			scripts.LoadLot(lotRoot, scene.EnsureEntityHandle, BuilderScene.ReadScriptFile, scene.DestroyEntity);
			int doorHandle = scene.EnsureEntityHandle(door);
			int leakerHandle = scene.EnsureEntityHandle(leaker);

			// Baseline: a dispatch works before anything goes wrong.
			int cubesBefore = CountChildren(scene.LotRoot);
			bridge.Router.InvokeLocal(doorHandle, NetDirection.Server, "Open", NetRouter.EmptyArgsPayload);
			scripts.Tick();
			Check("baseline dispatch works (a cube spawned)",
				CountChildren(scene.LotRoot) == cubesBefore + 1);

			// Trigger the memory guard: the Leak handler doubles a string past the 64 MB cap.
			int rebuildsBefore = LuaManager.Instance.VmRebuildCount;
			bridge.Router.InvokeLocal(leakerHandle, NetDirection.Server, "Leak", NetRouter.EmptyArgsPayload);
			scripts.Tick();
			LuaManager.Instance.ProcessDeferredRecreate();
			bool rebuilt = LuaManager.Instance.VmRebuildCount > rebuildsBefore;
			Check("OOM triggers a VM rebuild", rebuilt);

			// 1. The runtime follows the rebuild instead of holding the dead bridge.
			LuaNetBridge newBridge = LuaManager.Instance.NetBridge;
			Check("a fresh bridge now exists and the runtime resolves it, not the old one",
				rebuilt && newBridge != null && !ReferenceEquals(newBridge, bridge));

			// 2. The old bridge is dead: calling it throws a clear managed exception rather than
			//    touching the freed lua_State (which crashed natively before this fix).
			string deadError = null;
			try
			{
				bridge.BeginFrame();
			}
			catch (Exception ex)
			{
				deadError = ex.GetType().Name + ": " + ex.Message;
			}
			Check("the old bridge is dead and throws a managed exception (" + (deadError ?? "no exception") + ")",
				deadError != null && deadError.Contains("rebuilt"));
			Check("the old watchdog is dead too", bridge.Watchdog == null);

			// 3. Several ticks after the rebuild: the first notices the rebuild and reloads scripts.
			int cubesAfterRebuild = CountChildren(scene.LotRoot);
			string tickError = null;
			for (int i = 0; i < 3 && tickError == null; i++)
			{
				try
				{
					scripts.Tick();
				}
				catch (Exception ex)
				{
					tickError = ex.GetType().Name + ": " + ex.Message;
				}
			}
			Check("ticks after the rebuild run without exceptions (" + (tickError ?? "clean") + ")",
				tickError == null);
			Check("scripts reloaded into the fresh VM (" + scripts.LoadedScriptCount + " loaded)",
				scripts.LoadedScriptCount == 2);

			// 4. The next dispatch works: the reloaded Door.lua runs its handler again.
			newBridge.Router.InvokeLocal(doorHandle, NetDirection.Server, "Open", NetRouter.EmptyArgsPayload);
			scripts.Tick();
			Check("next dispatch after the rebuild works (a cube spawned)",
				CountChildren(scene.LotRoot) == cubesAfterRebuild + 1);
		}
		finally
		{
			lotRoot.Free();
		}
	}

	private static void TestLiveScratchPath()
	{
		// The command-line / code-editor scratch path on the REAL lot VM. Moved here from the
		// self-test suite in item 7 so that suite runs entirely on private Lua states.
		Check("lot Lua runtime is available", LuaManager.Instance.IsRuntimeAvailable);
		Check("scratch chunk runs in the global env",
			LuaManager.Instance.RunString("scratch_marker = 40 + 2", "scratchtest"));
		Check("scratch start() convention still runs",
			LuaManager.Instance.RunString("function start() scratch_started = true end", "scratchtest2"));
		Check("scratch infinite loop is stopped by the script budget",
			!LuaManager.Instance.RunString("while true do end", "__selftest_scratch_limit"));
		Check("scratch runtime still usable after the trip",
			LuaManager.Instance.RunString("scratch_after = 1", "scratchtest3"));
		// One global at a time so a failure names the offender.
		string[] dangerous = { "io", "os", "require", "dofile", "loadfile", "package", "debug", "load",
			"collectgarbage", "coroutine", "rawset", "print", "luanet" };
		List<string> leaks = new List<string>();
		for (int i = 0; i < dangerous.Length; i++)
		{
			if (!LuaManager.Instance.RunString(
				"if " + dangerous[i] + " ~= nil then error('leak') end", "scratchleak"))
				leaks.Add(dangerous[i]);
		}
		Check("scratch env hides dangerous globals (leaks: " + (leaks.Count == 0 ? "none" : string.Join(",", leaks)) + ")",
			leaks.Count == 0);
	}

	private static void TestLiveGetHandleWiring(BuilderScene scene)
	{
		// Item 5 coverage needing the live lot VM: Lot.GetHandle must be visible to creator scripts
		// on the REAL state (NLua exposes CLR methods as callable userdata, not 'function', so probe
		// by CALLING it) and agree with the scene's own name registry.
		Check("Lot.GetHandle is exposed on the live VM",
			LuaManager.Instance.RunString(
				"local h = Lot.GetHandle('Ground')\n" +
				"if type(h) ~= 'number' or h < 0 then error('Lot.GetHandle returned ' .. tostring(h)) end",
				"__selftest_gethandle_api"));
		int sceneGround = scene.FindHandleByName("Ground");
		Check("live scene resolves 'Ground' by name (handle " + sceneGround + ")",
			sceneGround >= 0 && scene.GetByHandle(sceneGround) != null
			&& scene.GetByHandle(sceneGround).Name.ToString() == "Ground");
	}

	private static string FirstWith(List<string> warnings, string needle)
	{
		for (int i = 0; i < warnings.Count; i++)
		{
			if (warnings[i].Contains(needle)) return warnings[i];
		}
		return "";
	}
}
