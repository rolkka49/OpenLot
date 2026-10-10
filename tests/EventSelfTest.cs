using System;
using System.Collections.Generic;
using Godot;
using NLua;

/// <summary>
/// Verification for milestone 3.7 (the event system): the registry semantics (subscriptions,
/// ordering, coalescing, the per-frame cap, two-ended cleanup, the sensor attach protocol) and the
/// Lua sugar on a private VM (replace-within-script vs append-across-scripts, cancel tokens,
/// delivered arguments, net.fromPlayer attribution, disabled entities, the reserved vocabulary),
/// plus a staged session probe that drives the REAL sensors across physics frames.
///
/// Follows the project's self-test convention: no framework, prints PASS/FAIL lines, returns the
/// failure count. Runs from <c>BuilderScene._Process</c> after
/// <see cref="ConstraintSelfTest.GravityProbeDone"/> — both suites drive Test mode, and only one
/// session probe may own the lot at a time. The probe is headless-only, like the gravity probe.
/// </summary>
public static class EventSelfTest
{
	private static int _failures;
	private static int _checks;

	public static int Run(BuilderScene scene)
	{
		_failures = 0;
		_checks = 0;

		Check("harness: the lot scene is available", scene != null);
		Check("harness: the scene's event registry is wired", scene != null && scene.Events != null);
		if (scene == null)
		{
			GD.Print("[EventSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
			return _failures;
		}

		TestRegistrySemantics();
		TestSensorProtocol();
		TestRegistryCleanup();
		TestLuaSugar();

		GD.Print("[EventSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
		// The claim a synchronous suite cannot make — real sensor overlaps across real physics
		// frames — is verified by the staged probe (TickProbe).
		StartProbe(scene);
		return _failures;
	}

	private static void Check(string name, bool condition)
	{
		_checks++;
		if (condition)
		{
			GD.Print("[EventSelfTest] PASS  " + name);
		}
		else
		{
			_failures++;
			GD.PrintErr("[EventSelfTest] FAIL  " + name);
		}
	}

	// --- registry semantics (pure C#, recording dispatch) ---------------------------------------

	private static void TestRegistrySemantics()
	{
		List<string> calls = new List<string>();
		LotEventRegistry registry = new LotEventRegistry(message => { });
		registry.SessionActive = () => false;
		registry.Dispatch = (subject, eventName, subscriber, other, playerId, fromPlayer) =>
		{
			calls.Add(subject + "|" + eventName + "|" + subscriber + "|" + other + "|" + playerId + "|" + fromPlayer);
			return true;
		};

		Check("registry: subscribe adds a subscriber",
			registry.Subscribe(5, 2, "touched") && registry.SubscriberCount(2, "touched") == 1);
		Check("registry: subscribe is idempotent (same subscriber re-subscribes)",
			registry.Subscribe(5, 2, "touched") && registry.SubscriberCount(2, "touched") == 1);
		Check("registry: an unknown event name is refused", !registry.Subscribe(5, 2, "exploded"));
		Check("registry: a negative handle is refused", !registry.Subscribe(-1, 2, "touched"));

		registry.Subscribe(7, 2, "touched");
		Check("registry: a second subscriber appends (declaration order)",
			registry.SubscriberCount(2, "touched") == 2);

		// A subject nobody watches is not queued; a duplicate triple coalesces.
		registry.Enqueue(3, "touched", 9, false, -1);
		Check("registry: a subject with no subscribers is not queued", registry.QueuedCount == 0);
		registry.Enqueue(2, "touched", 9, false, -1);
		registry.Enqueue(2, "touched", 9, false, -1);
		Check("registry: a duplicate (subject, event, other) coalesces",
			registry.QueuedCount == 1 && registry.CoalescedEvents == 1);

		// Ordering: subject handle ascending regardless of enqueue order; subscribers in
		// declaration order within one (subject, event).
		registry.Subscribe(5, 1, "touched");
		registry.Enqueue(1, "touched", 4, false, -1);
		registry.Drain();
		Check("registry: deliveries drain in subject-ascending order",
			calls.Count == 3 &&
			calls[0] == "1|touched|5|4|-1|False" &&
			calls[1] == "2|touched|5|9|-1|False" &&
			calls[2] == "2|touched|7|9|-1|False");

		// Attribution fields pass through untouched.
		calls.Clear();
		registry.Subscribe(5, 12, "touched");
		registry.Enqueue(12, "touched", 99, true, 1);
		registry.Drain();
		Check("registry: player attribution rides the delivery",
			calls.Count == 1 && calls[0] == "12|touched|5|99|1|True");

		// The per-frame cap drops the overflow with ONE throttled line.
		List<string> warns = new List<string>();
		LotEventRegistry capped = new LotEventRegistry(warns.Add, () => 0.0);
		capped.SessionActive = () => false;
		capped.Dispatch = (subject, eventName, subscriber, other, playerId, fromPlayer) => true;
		capped.Subscribe(1, 1, "touched");
		for (int i = 0; i < LotEventRegistry.MaxDeliveriesPerFrame + 5; i++)
			capped.Enqueue(1, "touched", 100 + i, false, -1);
		capped.Drain();
		Check("registry: the per-frame cap holds (delivered " + capped.DeliveredEvents + ")",
			capped.DeliveredEvents == LotEventRegistry.MaxDeliveriesPerFrame);
		Check("registry: the overflow is reported once (" + warns.Count + " line(s))",
			warns.Count == 1 && warns[0].Contains("dropped"));
		Check("registry: the overflow count is recorded (" + capped.DroppedEvents + ")",
			capped.DroppedEvents == 5);
		capped.Drain();
		Check("registry: a second drain inside the throttle window stays silent", warns.Count == 1);
	}

	// --- sensor attach/detach protocol (injected attach delegate) -------------------------------

	private static void TestSensorProtocol()
	{
		List<int> attached = new List<int>();
		List<int> detached = new List<int>();
		List<string> warns = new List<string>();
		bool session = false;
		bool retryFirst = true;
		LotEventRegistry registry = new LotEventRegistry(warns.Add, () => 0.0);
		registry.SessionActive = () => session;
		registry.AttachSensor = subject =>
		{
			if (subject == 4) return SensorAttachResult.Refused;
			if (subject == 6 && retryFirst)
			{
				retryFirst = false;
				return SensorAttachResult.Retry;
			}
			attached.Add(subject);
			return SensorAttachResult.Ok;
		};
		registry.DetachSensor = subject => detached.Add(subject);

		registry.Subscribe(5, 2, "touched");
		Check("sensor: no sensor before a session",
			!registry.HasPendingSensor(2) && !registry.HasAttachedSensor(2));

		session = true;
		registry.OnSessionStarted();
		registry.Drain();
		Check("sensor: session entry attaches the subscribed subject's sensor",
			registry.HasAttachedSensor(2) && attached.Contains(2));
		Check("sensor: attach happened exactly once", CountOf(attached, 2) == 1);

		registry.Subscribe(5, 4, "touched");
		registry.Subscribe(5, 6, "touched");
		registry.OnSessionStarted();
		registry.Drain();
		Check("sensor: an unresolvable subject stays pending (subject 6)",
			registry.HasPendingSensor(6) && !registry.HasAttachedSensor(6));
		Check("sensor: a refused subject is reported once (" + warns.Count + " line(s))",
			!registry.HasPendingSensor(4) && !registry.HasAttachedSensor(4) &&
			warns.Count == 1 && warns[0].Contains("cannot host"));
		registry.Drain();
		Check("sensor: the retry resolves on the next drain", registry.HasAttachedSensor(6));

		registry.Unsubscribe(5, 2, "touched");
		Check("sensor: the last unsubscribe detaches the sensor",
			!registry.HasAttachedSensor(2) && detached.Contains(2));

		registry.Subscribe(7, 2, "touched");
		session = false;
		registry.OnSessionEnded();
		Check("sensor: session end detaches every sensor and clears pending",
			!registry.HasAttachedSensor(6) && detached.Contains(6) && !registry.HasPendingSensor(2));

		registry.Subscribe(5, 8, "clicked");
		Check("sensor: a reserved (non-sensor) event never marks a sensor",
			!registry.HasPendingSensor(8) && !registry.HasAttachedSensor(8));
	}

	// --- two-ended cleanup (design doc D13) -----------------------------------------------------

	private static void TestRegistryCleanup()
	{
		LotEventRegistry registry = new LotEventRegistry(message => { });
		registry.SessionActive = () => false;
		registry.Subscribe(5, 2, "touched");
		registry.Subscribe(6, 2, "touchEnded");
		registry.Subscribe(5, 3, "touched");

		registry.ForgetEntity(5);
		Check("cleanup: a destroyed subscriber goes from every subject it watched",
			registry.SubscriberCount(2, "touched") == 0 && registry.SubscriberCount(3, "touched") == 0);
		Check("cleanup: another subscriber's registration survives",
			registry.SubscriberCount(2, "touchEnded") == 1);

		registry.Subscribe(6, 3, "touched");
		registry.ForgetEntity(3);
		Check("cleanup: a destroyed subject drops what others registered on it",
			!registry.HasSubscription(3, "touched"));

		registry.Subscribe(7, 4, "touched");
		registry.ClearAll();
		Check("cleanup: ClearAll empties every table",
			!registry.HasSubscription(4, "touched") && registry.QueuedCount == 0);
	}

	// --- the Lua sugar on a private VM (never the lot's) ----------------------------------------

	private static void TestLuaSugar()
	{
		List<string> logs = new List<string>();
		Lua state = new Lua();
		LuaNetBridge bridge = new LuaNetBridge();
		bridge.Attach(state);
		bridge.Watchdog = new LuaWatchdog(state);
		LotEventRegistry registry = new LotEventRegistry(message => { });
		registry.SessionActive = () => true;
		registry.Dispatch = (subject, eventName, subscriber, other, playerId, fromPlayer) =>
			bridge.TryDispatchEvent(subject, eventName, subscriber, other, playerId, fromPlayer);

		try
		{
			state.RegisterFunction("__testLotLog", logs, typeof(List<string>).GetMethod("Add"));
			state.DoString("Lot = { Log = function(message) __testLotLog(tostring(message)) end, " +
				"SpawnCube = function() return 0 end, SpawnSphere = function() return 0 end, " +
				"SpawnCylinder = function() return 0 end }", "testlot");
			LuaBootstrap.Apply(state);
			state.RegisterFunction("SubscribeEvent", registry, typeof(LotEventRegistry).GetMethod("Subscribe"));
			state.RegisterFunction("UnsubscribeEvent", registry, typeof(LotEventRegistry).GetMethod("Unsubscribe"));
			state.DoString(@"
__testSlotEnvs = {}
function __testSlot(handle, slot)
    local env = __testSlotEnvs[slot]
    if env == nil then env = __openlot_newEnv(handle); __testSlotEnvs[slot] = env end
    return env
end
", "testhelpers");

			// One script on handle 5 (slot 'a') subscribes on subject 2.
			bool ran = RunChunk(state, "__testSlot(5, 'a')",
				"t = Subscribe(2, 'touched', function(other, player) Lot.Log('a1 ' .. tostring(other) .. ' ' .. tostring(player) .. ' ' .. tostring(net.fromPlayer)) end)");
			Check("lua: a script subscribes through the sugar",
				ran && registry.HasSubscription(2, "touched") && registry.SubscriberCount(2, "touched") == 1);

			logs.Clear();
			bridge.TryDispatchEvent(2, "touched", 5, 9, -1, false);
			Check("lua: a physics delivery arrives with (other, nil, net.fromPlayer=false)",
				logs.Count == 1 && logs[0] == "a1 9 nil false");

			logs.Clear();
			bridge.TryDispatchEvent(2, "touched", 5, 9, 1, true);
			Check("lua: a player delivery arrives with (other, peer id, net.fromPlayer=true)",
				logs.Count == 1 && logs[0] == "a1 9 1 true");

			// Redeclaration within the same script replaces in place; a second script on the same
			// entity appends (its owner identity differs even though the handle is the same).
			RunChunk(state, "__testSlot(5, 'a')",
				"Subscribe(2, 'touched', function() Lot.Log('a2') end)");
			Check("lua: redeclaration within one script replaces (still one subscriber)",
				registry.SubscriberCount(2, "touched") == 1);
			logs.Clear();
			bridge.TryDispatchEvent(2, "touched", 5, 9, -1, false);
			Check("lua: the replaced handler is the one that runs",
				logs.Count == 1 && logs[0] == "a2");

			RunChunk(state, "__testSlot(5, 'b')",
				"t = Subscribe(2, 'touched', function() Lot.Log('b1') end)");
			logs.Clear();
			bridge.TryDispatchEvent(2, "touched", 5, 9, -1, false);
			Check("lua: a second script appends and both run in declaration order",
				registry.SubscriberCount(2, "touched") == 1 &&
				logs.Count == 2 && logs[0] == "a2" && logs[1] == "b1");

			// Cancel: the token removes exactly its own script's entry. While another script on
			// the entity still watches the pair, C# is not told (the registry dedupes by handle).
			RunChunk(state, "__testSlot(5, 'b')", "local ok = t() Lot.Log('cancelB ' .. tostring(ok))");
			Check("lua: cancelling one script's token leaves the pair watched",
				registry.SubscriberCount(2, "touched") == 1);
			logs.Clear();
			bridge.TryDispatchEvent(2, "touched", 5, 9, -1, false);
			Check("lua: the cancelled entry no longer runs", logs.Count == 1 && logs[0] == "a2");

			RunChunk(state, "__testSlot(5, 'a')", "local ok = t() Lot.Log('cancelA ' .. tostring(ok))");
			Check("lua: cancelling the last script's entry unsubscribes in C#" +
				(registry.SubscriberCount(2, "touched") == 0 ? "" :
					" (count " + registry.SubscriberCount(2, "touched") + ")"),
				registry.SubscriberCount(2, "touched") == 0);
			logs.Clear();
			bool stale = bridge.TryDispatchEvent(2, "touched", 5, 9, -1, false);
			Check("lua: nothing runs after the last cancel", !stale && logs.Count == 0);

			// The vocabulary is validated: an unknown event name is refused in the calling script.
			RunChunk(state, "__testSlot(5, 'a')",
				"local ok = pcall(function() Subscribe(2, 'exploded', function() end) end) Lot.Log('refuse ' .. tostring(ok))");
			Check("lua: an unknown event name raises in the script",
				logs.Count == 1 && logs[0] == "refuse false");
			Check("lua: the reserved vocabulary is settled",
				LotEventRegistry.IsKnownEvent("touched") && LotEventRegistry.IsKnownEvent("touchEnded") &&
				LotEventRegistry.IsKnownEvent("clicked") && LotEventRegistry.IsKnownEvent("entered") &&
				LotEventRegistry.IsKnownEvent("exited") && LotEventRegistry.IsKnownEvent("destroyed") &&
				LotEventRegistry.IsKnownEvent("playerJoined") && LotEventRegistry.IsKnownEvent("playerLeft") &&
				!LotEventRegistry.IsKnownEvent("exploded"));

			// A disabled entity's handler does not run (the same circuit breaker as net.*).
			RunChunk(state, "__testSlot(9, 'd')",
				"Subscribe(2, 'touched', function() Lot.Log('disabled') end)");
			state.DoString("__openlot_disableNet(9)", "testdisable");
			logs.Clear();
			bool disabledHandled = bridge.TryDispatchEvent(2, "touched", 9, 3, -1, false);
			Check("lua: a disabled entity's handler does not run", !disabledHandled && logs.Count == 0);

			// onFrame: captured at load, callable per frame, tripping through the same breaker.
			string frameError;
			bridge.LoadEntityScript(30, "function onFrame(dt) Lot.Log('frame ' .. tostring(dt)) end", "Frame.lua", out frameError);
			Check("lua: a script's onFrame is captured at load", bridge.HasFrameHandler(30));
			logs.Clear();
			bridge.CallOnFrame(30, 0.5);
			Check("lua: onFrame runs and receives its delta", logs.Count == 1 && logs[0] == "frame 0.5");
			bridge.LoadEntityScript(31, "plain = 1", "Plain.lua", out frameError);
			Check("lua: a script without onFrame reports none", !bridge.HasFrameHandler(31));

			string tripError;
			bridge.LoadEntityScript(32, "function onFrame(dt) while true do end end", "Trip.lua", out tripError);
			for (int i = 0; i < 3; i++) bridge.CallOnFrame(32, 0.0);
			Check("lua: three onFrame trips disable the entity", bridge.IsEntityDisabled(32));

			// Forget through the bridge: a destroyed subject's Lua-side handlers cannot fire.
			RunChunk(state, "__testSlot(12, 'e')",
				"Subscribe(13, 'touched', function() Lot.Log('ghost') end)");
			Check("lua: the subscription was recorded", registry.HasSubscription(13, "touched"));
			bridge.ForgetEntity(13);
			registry.ForgetEntity(13);
			logs.Clear();
			bool ghost = bridge.TryDispatchEvent(13, "touched", 12, 0, -1, false);
			Check("lua: a destroyed subject's handlers cannot fire", !ghost && logs.Count == 0);
		}
		finally
		{
			state.Dispose();
		}
	}

	private static int CountOf(List<int> list, int value)
	{
		int count = 0;
		for (int i = 0; i < list.Count; i++)
		{
			if (list[i] == value) count++;
		}
		return count;
	}

	/// <summary>Runs one chunk inside the named env expression, through the real env loader and
	/// pcall, so a fixture error is a failed check rather than an exception in the suite.</summary>
	private static bool RunChunk(Lua state, string envExpression, string chunk)
	{
		string script = "local env = " + envExpression + "\n" +
			"local fn, err = __openlot_load([=[" + chunk + "]=], 'testchunk', env)\n" +
			"if fn == nil then return false, err end\n" +
			"return pcall(fn)";
		try
		{
			object[] results = state.DoString(script, "eventselftest");
			return results != null && results.Length > 0 && results[0] is bool ok && ok;
		}
		catch (Exception)
		{
			return false;
		}
	}

	// --- staged session probe (real sensors, real physics frames) -------------------------------

	/// <summary>Wall-clock wait for the scenario: the ball falls ~5.6 m onto the plate and the
	/// character walks ~2 m into the wall — 2 s is comfortably past both.</summary>
	private const ulong ProbeWaitMsec = 2000;

	/// <summary>Wait after lifting the ball, so touchEnded has time to deliver.</summary>
	private const ulong ProbeLiftWaitMsec = 500;

	/// <summary>Wait after leaving the session, to prove onFrame stopped and sensors came down.</summary>
	private const ulong ProbeExitWaitMsec = 400;

	private const string ProbeScriptDir = "user://Scripts";
	private const string ProbeScriptName = "__eventselftest_events.lua";
	private const string ProbeFrameScriptName = "__eventselftest_frame.lua";

	/// <summary>The fixture script, attached to both the plate and the wall: it subscribes its own
	/// entity to the two sensor events and reports every delivery through Lot.Log — script output
	/// is the observable, because envs are stack-only by design.</summary>
	private const string ProbeScript =
		"local me = this.Handle\n" +
		"Subscribe(me, \"touched\", function(other, player)\n" +
		"    Lot.Log(\"EVT touched \" .. me .. \" \" .. tostring(other) .. \" \" .. tostring(player) .. \" \" .. tostring(net.fromPlayer))\n" +
		"end)\n" +
		"Subscribe(me, \"touchEnded\", function(other)\n" +
		"    Lot.Log(\"EVT ended \" .. me .. \" \" .. tostring(other))\n" +
		"end)\n";

	/// <summary>The frame fixture (a second script under the lot root): counts session frames and
	/// reports every 20th, so the probe can see onFrame running and stopping across the session
	/// boundary.</summary>
	private const string ProbeFrameScript =
		"local n = 0\n" +
		"function onFrame(dt)\n" +
		"    n = n + 1\n" +
		"    if n % 20 == 0 then Lot.Log(\"EVT frame \" .. n) end\n" +
		"end\n";

	private static BuilderScene _probeScene;
	private static LotObject _probePlate;
	private static LotObject _probeWall;
	private static LotObject _probeBall;
	private static Node _probeFrameScript;
	private static int _probePlateHandle;
	private static int _probeWallHandle;
	private static int _probeBallHandle;
	private static ulong _probeDeadlineMsec;
	private static int _probeStage; // 0 = not started, 1 = waiting, 2 = ball lifted, 3 = exited, 4 = finished
	private static int _probeChecks;
	private static int _probeFailures;
	private static int _probeFrameCountBeforeExit;
	private static float _probePlayerStartZ;
	private static float _probeMinPlayerZ;
	private static readonly List<string> _probeFiles = new List<string>();

	/// <summary>Whether the staged probe completed; the scene warns when a run ends too early.</summary>
	public static bool ProbeDone { get { return _probeStage >= 4; } }

	private static void StartProbe(BuilderScene scene)
	{
		if (DisplayServer.GetName() != "headless")
		{
			_probeStage = 4;
			return;
		}

		_probeScene = scene;
		_probeChecks = 0;
		_probeFailures = 0;

		// Scratch parts another probe left behind — this frame's queued-for-free nodes included —
		// would sit in the walk path and silently block the character, so clear both prefixes.
		DestroyLeftovers(scene, "ConstraintProbe");
		DestroyLeftovers(scene, "EventProbe");

		WriteFixture(ProbeScriptName, ProbeScript);
		WriteFixture(ProbeFrameScriptName, ProbeFrameScript);

		// Scenario, clear of the smoke test's origin region and the constraint probe's area:
		// the plate catches the falling ball, and the wall sits in the character's -Z walk path
		// (the default character script spawns the capsule at (4, 1, 4)).
		_probePlate = NewScratchPart(scene, "EventProbePlate", new Vector3(120f, 0.2f, 120f));
		_probePlate.Scale = new Vector3(4f, 0.4f, 4f);
		_probeBall = NewScratchPart(scene, "EventProbeBall", new Vector3(120f, 6f, 120f));
		_probeBall.SetAnchored(false);
		_probeWall = NewScratchPart(scene, "EventProbeWall", new Vector3(4f, 1f, 2f));
		_probeWall.Scale = new Vector3(3f, 2f, 0.4f);

		_probePlateHandle = scene.RegisterHandle(_probePlate);
		_probeBallHandle = scene.RegisterHandle(_probeBall);
		_probeWallHandle = scene.RegisterHandle(_probeWall);

		AttachFixture(_probePlate, ProbeScriptName);
		AttachFixture(_probeWall, ProbeScriptName);
		_probeFrameScript = AttachFixture(scene.LotRoot, ProbeFrameScriptName);

		_probeDeadlineMsec = Time.GetTicksMsec() + ProbeWaitMsec;
		_probeStage = 1;
		GD.Print("[EventSelfTest] session probe started (waiting " + ProbeWaitMsec + " ms)");
		// The real entry path a creator presses: this reloads the lot (the fixtures subscribe)
		// and starts the session.
		scene.EnterTestMode(false);

		// Drive the character exactly like a player: face -Z deterministically and hold W.
		if (scene.PlayerCamera != null)
			scene.PlayerCamera.SeedOrbit(0f, 10f, scene.Player != null ? scene.Player.Position : Vector3.Zero);
		_probePlayerStartZ = scene.Player != null ? scene.Player.Position.Z : 0f;
		_probeMinPlayerZ = _probePlayerStartZ;
		PressProbeKey(Key.W, true);
	}

	/// <summary>Removes scratch parts a previous probe left in the tree (a straggler in the walk
	/// path would block the character and turn the player-touch check into a false negative).</summary>
	private static void DestroyLeftovers(BuilderScene scene, string prefix)
	{
		List<Node> doomed = new List<Node>();
		CollectByNamePrefix(scene.LotRoot, prefix, doomed);
		for (int i = 0; i < doomed.Count; i++) scene.DestroyEntity(doomed[i]);
	}

	/// <summary>Diagnostic for a blocked walk: every node (LotObject or not — a leftover physics
	/// body has no LotObject wrapper and would otherwise be invisible) within 4 m of the character,
	/// with its type, name, position and collision layer.</summary>
	private static void LogObstaclesNear(BuilderScene scene, Vector3 center)
	{
		GD.PrintErr("[EventSelfTest] walk blocked at " + center + "; nodes within 4 m:");
		LogNodesNear(scene.LotRoot, center);
	}

	private static void LogNodesNear(Node node, Vector3 center)
	{
		int count = node.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			Node child = node.GetChild(i);
			Node3D node3D = child as Node3D;
			if (node3D != null && node3D.GlobalPosition.DistanceTo(center) <= 4f)
			{
				string layers = "";
				PhysicsBody3D body = child as PhysicsBody3D;
				if (body != null) layers = " layer=" + body.CollisionLayer + " mask=" + body.CollisionMask;
				Area3D area = child as Area3D;
				if (area != null) layers = " (area) layer=" + area.CollisionLayer + " mask=" + area.CollisionMask;
				GD.PrintErr("[EventSelfTest]   " + child.GetType().Name + " '" + child.Name + "' at " +
					node3D.GlobalPosition + layers +
					(child.HasMeta(LotObject.InternalChildMeta) ? " (internal)" : ""));
			}
			LogNodesNear(child, center);
		}
	}

	private static void CollectByNamePrefix(Node node, string prefix, List<Node> found)
	{
		int count = node.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			Node child = node.GetChild(i);
			if (child.Name.ToString().StartsWith(prefix, StringComparison.Ordinal)) found.Add(child);
			else CollectByNamePrefix(child, prefix, found);
		}
	}

	private static void ProbeCheck(string name, bool condition)
	{
		_probeChecks++;
		if (condition)
		{
			GD.Print("[EventSelfTest] PASS  (session probe) " + name);
		}
		else
		{
			_probeFailures++;
			GD.PrintErr("[EventSelfTest] FAIL  (session probe) " + name);
		}
	}

	private static LotObject NewScratchPart(BuilderScene scene, string name, Vector3 at)
	{
		LotObject part = LotObject.Create(LotObjectKind.Cube, name, new Color(0.6f, 0.8f, 0.6f));
		part.Position = at;
		scene.LotRoot.AddChild(part);
		return part;
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

	/// <summary>Feeds one physical key press/release into Godot's input state — the exact state the
	/// capsule controller reads — so the probe walks the character like a player does (the same
	/// technique the §3.6 gravity probe uses).</summary>
	private static void PressProbeKey(Key key, bool pressed)
	{
		InputEventKey keyEvent = new InputEventKey();
		keyEvent.Keycode = key;
		keyEvent.PhysicalKeycode = key;
		keyEvent.Pressed = pressed;
		Input.ParseInputEvent(keyEvent);
	}

	/// <summary>How many recent log lines equal <paramref name="exact"/> (the probe's observable:
	/// script output through Lot.Log). The window is bounded because the ring drops old entries.</summary>
	private static int CountLog(string exact)
	{
		int count = 0;
		int start = Math.Max(0, LotLog.Count - 150);
		for (int i = start; i < LotLog.Count; i++)
		{
			if (LotLog.At(i).Text == exact) count++;
		}
		return count;
	}

	/// <summary>True when a recent log line equals <paramref name="exact"/>.</summary>
	private static bool HasLog(string exact)
	{
		return CountLog(exact) > 0;
	}

	/// <summary>How many recent log lines START with <paramref name="prefix"/> — the counting form
	/// for lines that carry more than one payload shape (e.g. touched lines with any arguments).</summary>
	private static int CountLogPrefix(string prefix)
	{
		int count = 0;
		int start = Math.Max(0, LotLog.Count - 150);
		for (int i = start; i < LotLog.Count; i++)
		{
			if (LotLog.At(i).Text.StartsWith(prefix, StringComparison.Ordinal)) count++;
		}
		return count;
	}

	/// <summary>The highest "EVT frame N" reported so far, or 0 — the onFrame progress gauge.</summary>
	private static int LastFrameNumber()
	{
		int best = 0;
		int start = Math.Max(0, LotLog.Count - 150);
		for (int i = start; i < LotLog.Count; i++)
		{
			string text = LotLog.At(i).Text;
			if (!text.StartsWith("EVT frame ", StringComparison.Ordinal)) continue;
			int value;
			if (int.TryParse(text.Substring("EVT frame ".Length), out value) && value > best) best = value;
		}
		return best;
	}

	/// <summary>
	/// Advances the staged probe across physics frames (called every frame from
	/// BuilderScene._Process). Phase 1 waits for the ball to land and the character to reach the
	/// wall; phase 2 lifts the ball (touchEnded), destroys the wall (two-ended cleanup) and leaves
	/// the session; phase 3 proves onFrame stopped and the sensors came down, then cleans up.
	/// </summary>
	public static void TickProbe()
	{
		if (_probeStage == 0 || _probeStage >= 4) return;
		// Trajectory sample: the minimum Z reached tells "walked then got put back" apart from
		// "never advanced past a blocker".
		if (_probeStage == 1 && _probeScene != null && _probeScene.Player != null)
		{
			float sampledZ = _probeScene.Player.Position.Z;
			if (sampledZ < _probeMinPlayerZ) _probeMinPlayerZ = sampledZ;
		}
		if (Time.GetTicksMsec() < _probeDeadlineMsec) return;

		if (_probeStage == 1)
		{
			_probeStage = 2;
			PressProbeKey(Key.W, false);

			// The ball (physics-caused): one touch while it rests, attributed to System.
			string ballTouch = "EVT touched " + _probePlateHandle + " " + _probeBallHandle + " nil false";
			ProbeCheck("the ball's fall delivers a touched to the plate's script", HasLog(ballTouch));
			int ballTouches = CountLogPrefix("EVT touched " + _probePlateHandle + " " + _probeBallHandle + " ");
			ProbeCheck("a resting contact fires exactly once (edge-triggered, got " + ballTouches + ")",
				ballTouches == 1);

			// The player (input-caused): the character walks into the wall, and the delivery
			// carries the local peer id with net.fromPlayer true inside the handler. The walk
			// progress is its own check so "did not move" reads differently from "moved but the
			// wall never fired".
			float playerZ = _probeScene.Player != null ? _probeScene.Player.Position.Z : float.NaN;
			Vector3 playerPosition = _probeScene.Player != null ? _probeScene.Player.Position : Vector3.Zero;
			ProbeCheck("the character walked toward the wall (z " + _probePlayerStartZ + " -> " + playerZ +
				", closest " + _probeMinPlayerZ + ")",
				_probeScene.Player != null && playerZ < _probePlayerStartZ - 0.5f);
			// A blocked walk needs identification, not guessing: report what sits around it.
			if (_probeScene.Player == null || playerZ >= _probePlayerStartZ - 0.5f)
				LogObstaclesNear(_probeScene, playerPosition);

			Node character = _probeScene.FindCharacter();
			string charHandle = character != null && character.HasMeta(BuilderScene.HandleMeta)
				? character.GetMeta(BuilderScene.HandleMeta).AsInt32().ToString()
				: "?";
			ProbeCheck("walking into the wall delivers a player-attributed touched (character " +
				charHandle + ", wall deliveries " + CountLogPrefix("EVT touched " + _probeWallHandle + " ") + ")",
				HasLog("EVT touched " + _probeWallHandle + " " + charHandle + " 1 true"));

			int frames = LastFrameNumber();
			ProbeCheck("onFrame advanced during the session (last reported frame " + frames + ")",
				frames >= 20);

			// Phase 2: lift the ball away — the sensor must report the exit.
			RigidBody3D body = _probeBall.CollisionBody as RigidBody3D;
			ProbeCheck("the ball simulates while the session runs", body != null);
			if (body != null)
			{
				body.Sleeping = false; // a resting body may be asleep; a teleport must still move it
				body.Position += new Vector3(0f, 10f, 0f);
			}
			_probeDeadlineMsec = Time.GetTicksMsec() + ProbeLiftWaitMsec;
			return;
		}

		if (_probeStage == 2)
		{
			_probeStage = 3;
			string ballEnd = "EVT ended " + _probePlateHandle + " " + _probeBallHandle;
			ProbeCheck("lifting the ball away delivers touchEnded (got " + CountLog(ballEnd) + ")",
				CountLog(ballEnd) == 1);

			// Destroy the wall mid-session: both registries must forget it immediately.
			_probeScene.DestroyEntity(_probeWall);
			_probeWall = null;
			ProbeCheck("destroying the subject drops its subscriptions",
				!_probeScene.Events.HasSubscription(_probeWallHandle, "touched") &&
				!_probeScene.Events.HasSubscription(_probeWallHandle, "touchEnded"));
			ProbeCheck("destroying the subject drops its sensor",
				!_probeScene.Events.HasAttachedSensor(_probeWallHandle));

			_probeFrameCountBeforeExit = LastFrameNumber();
			_probeScene.ExitTestMode();
			_probeDeadlineMsec = Time.GetTicksMsec() + ProbeExitWaitMsec;
			return;
		}

		// Phase 3: the session is over — onFrame must have stopped and every sensor must be gone.
		_probeStage = 4;
		int afterExit = LastFrameNumber();
		ProbeCheck("onFrame stopped at session exit (last frame " + afterExit + ", before exit " +
			_probeFrameCountBeforeExit + ")", afterExit == _probeFrameCountBeforeExit);
		ProbeCheck("every sensor came down with the session", !_probePlate.HasEventSensor);

		// Cleanup so the developer's lot is exactly as it was.
		_probeScene.DestroyEntity(_probePlate);
		_probeScene.DestroyEntity(_probeBall);
		_probePlate = null;
		_probeBall = null;
		if (_probeFrameScript != null && GodotObject.IsInstanceValid(_probeFrameScript))
			_probeFrameScript.QueueFree();
		_probeFrameScript = null;
		for (int i = 0; i < _probeFiles.Count; i++)
		{
			string path = ProbeScriptDir + "/" + _probeFiles[i];
			if (FileAccess.FileExists(path)) DirAccess.RemoveAbsolute(path);
		}
		_probeFiles.Clear();
		_probeScene = null;
		GD.Print("[EventSelfTest] session probe: " + _probeChecks + " checks, " +
			_probeFailures + " failure(s).");
	}
}
