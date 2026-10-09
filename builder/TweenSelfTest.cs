using System;
using System.Collections.Generic;
using Godot;
using NLua;

/// <summary>
/// Verification for milestone 3.8 (tweens and timers): the easing curves (endpoints exact,
/// midpoints hand-computed, monotonic), the scheduler mechanics on a fake clock (quarter-point
/// interpolation, timer firing, the one-fire-per-frame rule, cancels, the cap, two-ended
/// cleanup), the Lua sugar on a private VM (tokens, validation, callbacks through the real
/// bridge), and a staged session probe that runs a real tween and real timers across physics
/// frames.
///
/// Follows the project's self-test convention: no framework, prints PASS/FAIL lines, returns the
/// failure count. Runs from <c>BuilderScene._Process</c> after <see cref="EventSelfTest.ProbeDone"/>
/// — the staged probes drive Test mode one at a time — and the probe is headless-only.
/// </summary>
public static class TweenSelfTest
{
	private static int _failures;
	private static int _checks;

	public static int Run(BuilderScene scene)
	{
		_failures = 0;
		_checks = 0;

		Check("harness: the lot scene is available", scene != null);
		Check("harness: the scene's scheduler is wired", scene != null && scene.Scheduler != null);
		if (scene == null)
		{
			GD.Print("[TweenSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
			return _failures;
		}

		TestEasingMath();
		TestSchedulerMechanics(scene);
		TestLuaSugar();

		GD.Print("[TweenSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
		StartProbe(scene);
		return _failures;
	}

	private static void Check(string name, bool condition)
	{
		_checks++;
		if (condition)
		{
			GD.Print("[TweenSelfTest] PASS  " + name);
		}
		else
		{
			_failures++;
			GD.PrintErr("[TweenSelfTest] FAIL  " + name);
		}
	}

	private static bool Near(float a, float b, float epsilon = 1e-4f)
	{
		return Mathf.Abs(a - b) <= epsilon;
	}

	// --- easing math ----------------------------------------------------------------------------

	private static void TestEasingMath()
	{
		LotEasingKind[] all = (LotEasingKind[])Enum.GetValues(typeof(LotEasingKind));
		bool endpointsExact = true;
		bool monotonic = true;
		for (int k = 0; k < all.Length; k++)
		{
			endpointsExact &= Near(LotEasing.Apply(all[k], 0f), 0f) && Near(LotEasing.Apply(all[k], 1f), 1f);
			float previous = -1f;
			for (int i = 0; i <= 20; i++)
			{
				float value = LotEasing.Apply(all[k], i / 20f);
				if (value < previous - 1e-6f) monotonic = false;
				previous = value;
			}
		}
		Check("easing: every curve maps 0 to 0 and 1 to 1 exactly", endpointsExact);
		Check("easing: every curve is monotonic", monotonic);

		Check("easing: linear(0.5) = 0.5", Near(LotEasing.Apply(LotEasingKind.Linear, 0.5f), 0.5f));
		Check("easing: quadIn(0.5) = 0.25", Near(LotEasing.Apply(LotEasingKind.QuadIn, 0.5f), 0.25f));
		Check("easing: quadOut(0.5) = 0.75", Near(LotEasing.Apply(LotEasingKind.QuadOut, 0.5f), 0.75f));
		Check("easing: quadInOut(0.5) = 0.5", Near(LotEasing.Apply(LotEasingKind.QuadInOut, 0.5f), 0.5f));
		Check("easing: cubicIn(0.5) = 0.125", Near(LotEasing.Apply(LotEasingKind.CubicIn, 0.5f), 0.125f));
		Check("easing: cubicOut(0.5) = 0.875", Near(LotEasing.Apply(LotEasingKind.CubicOut, 0.5f), 0.875f));
		Check("easing: sineIn(0.5) = 1 - cos(pi/4)",
			Near(LotEasing.Apply(LotEasingKind.SineIn, 0.5f), 1f - Mathf.Cos(Mathf.Pi * 0.25f)));
		Check("easing: cubicInOut(0.5) = 0.5", Near(LotEasing.Apply(LotEasingKind.CubicInOut, 0.5f), 0.5f));

		LotEasingKind parsed;
		Check("easing: every name parses",
			LotEasing.TryParse("linear", out parsed) && parsed == LotEasingKind.Linear &&
			LotEasing.TryParse("sineInOut", out parsed) && parsed == LotEasingKind.SineInOut &&
			LotEasing.TryParse("cubicOut", out parsed) && parsed == LotEasingKind.CubicOut);
		Check("easing: unknown names are refused",
			!LotEasing.TryParse("bounce", out parsed) && !LotEasing.TryParse("", out parsed) &&
			!LotEasing.TryParse(null, out parsed));
	}

	// --- scheduler mechanics (a fresh instance; Advancing by hand is the fake clock) -------------

	private static void TestSchedulerMechanics(BuilderScene scene)
	{
		List<string> warns = new List<string>();
		LotScheduler scheduler = new LotScheduler(warns.Add);
		scheduler.ResolveTarget = scene.GetByHandle;
		List<string> fires = new List<string>();
		scheduler.FireTimer = (handle, id, keep) => { fires.Add(handle + "|" + id + "|" + keep); return true; };

		LotObject part = NewScratchPart(scene, "TweenMechPart", new Vector3(200f, 5f, 200f));
		part.Color = new Color(0f, 0f, 1f, 1f);
		int partHandle = scene.RegisterHandle(part);

		// A 1 s linear position tween lands on its quarter points exactly.
		int tweenId = scheduler.ScheduleTween(partHandle, "position", 210f, 5f, 200f, 0f, 1f, "linear");
		Check("tween: schedules on a valid target (id " + tweenId + ")",
			tweenId > 0 && scheduler.Count == 1 && scheduler.Has(tweenId));
		scheduler.Advance(0.25);
		Check("tween: a quarter of the time is a quarter of the distance (" + part.Position.X + ")",
			Near(part.Position.X, 202.5f, 1e-3f));
		scheduler.Advance(0.25);
		Check("tween: halfway", Near(part.Position.X, 205f, 1e-3f));
		scheduler.Advance(0.25);
		Check("tween: three quarters", Near(part.Position.X, 207.5f, 1e-3f));
		scheduler.Advance(0.25);
		Check("tween: lands on the target and leaves the schedule",
			Near(part.Position.X, 210f, 1e-3f) && scheduler.Count == 0 && !scheduler.Has(tweenId));

		// Easing changes the path, not the endpoint (the first tween left the part at 210).
		scheduler.ScheduleTween(partHandle, "position", 200f, 5f, 200f, 0f, 1f, "quadIn");
		scheduler.Advance(0.5);
		Check("tween: easing bends the path (quadIn at half: " + part.Position.X + ", from 210 to 200)",
			Near(part.Position.X, 207.5f, 1e-3f));
		scheduler.Advance(0.5);
		Check("tween: an eased tween still lands exactly", Near(part.Position.X, 200f, 1e-3f));

		// Zero duration completes on the first advance.
		scheduler.ScheduleTween(partHandle, "position", 205f, 5f, 200f, 0f, 0f, "linear");
		scheduler.Advance(0.016);
		Check("tween: zero duration applies and completes on the next advance",
			Near(part.Position.X, 205f, 1e-3f) && scheduler.Count == 0);

		// Refusals (each with a warning, never a silent schedule).
		warns.Clear();
		Check("tween: an unknown easing is refused", scheduler.ScheduleTween(partHandle, "position", 0f, 0f, 0f, 0f, 1f, "bounce") == -1);
		Check("tween: an unknown property is refused", scheduler.ScheduleTween(partHandle, "squish", 0f, 0f, 0f, 0f, 1f, "linear") == -1);
		Check("tween: a negative duration is refused", scheduler.ScheduleTween(partHandle, "position", 0f, 0f, 0f, 0f, -1f, "linear") == -1);
		Check("tween: a missing target is refused", scheduler.ScheduleTween(9999, "position", 0f, 0f, 0f, 0f, 1f, "linear") == -1);
		Check("tween: a UI-rect tween on a part is refused", scheduler.ScheduleTween(partHandle, "rect", 0f, 0f, 0f, 0f, 1f, "linear") == -1);
		Check("tween: every refusal warned (" + warns.Count + ")", warns.Count == 5 && scheduler.Count == 0);

		// Color interpolates through the part's own property (blue to red over one second).
		scheduler.ScheduleTween(partHandle, "color", 1f, 0f, 0f, 1f, 1f, "linear");
		scheduler.Advance(0.5);
		Check("tween: color interpolates (" + part.Color + ")",
			Near(part.Color.R, 0.5f, 0.01f) && Near(part.Color.B, 0.5f, 0.01f));
		scheduler.Advance(0.6); // finish it, so the timer tests start from an empty schedule
		Check("tween: the color tween completed (" + scheduler.Count + ")", scheduler.Count == 0);

		// A UI element's rect goes through the owner's clamped path.
		LotUIElement element = scene.SpawnUIElement(LotUIKind.Frame, new Vector2(20f, 20f), new Vector2(100f, 100f), "tween");
		int elementHandle = scene.RegisterHandle(element);
		scheduler.ApplyUiRect = (el, p, s) =>
		{
			Vector2 pos = p;
			Vector2 sz = s;
			UiGizmoMath.ClampRect(ref pos, ref sz, scene.UiCanvasBounds);
			el.Position = pos;
			el.Size = sz;
		};
		scheduler.ScheduleTween(elementHandle, "rect", 10000f, 10000f, 100f, 100f, 0f, "linear");
		scheduler.Advance(0.016);
		Check("tween: a UI rect tween clamps into the picture frame (" + element.Position + ")",
			element.Position.X <= scene.UiCanvasBounds.X && element.Position.Y <= scene.UiCanvasBounds.Y);

		// Timers: due exactly on time, one fire per frame, repeating keeps itself alive.
		fires.Clear();
		scheduler.ScheduleTimer(partHandle, 0.5, false, 0.0);
		scheduler.Advance(0.4);
		Check("timer: not due yet", fires.Count == 0 && scheduler.Count == 1);
		scheduler.Advance(0.2);
		Check("timer: fires once when due", fires.Count == 1 && scheduler.Count == 0);

		fires.Clear();
		int repeatingId = scheduler.ScheduleTimer(partHandle, 0.5, true, 0.5);
		scheduler.Advance(2.0);
		Check("timer: a big delta fires a repeating timer exactly once (" + fires.Count + ")",
			fires.Count == 1 && scheduler.Has(repeatingId));
		scheduler.Advance(0.5);
		Check("timer: the next interval fires next", fires.Count == 2);

		scheduler.FireTimer = (handle, id, keep) => false;
		scheduler.Advance(1.0);
		Check("timer: a repeating timer drops when its callback is gone", !scheduler.Has(repeatingId));
		scheduler.FireTimer = (handle, id, keep) => { fires.Add(handle + "|" + id + "|" + keep); return true; };

		int cancelId = scheduler.ScheduleTimer(partHandle, 5.0, false, 0.0);
		Check("timer: cancel removes it", scheduler.Cancel(cancelId) && !scheduler.Has(cancelId));
		Check("timer: cancelling twice reports false", !scheduler.Cancel(cancelId));

		// Cap.
		warns.Clear();
		for (int i = 0; i < LotScheduler.MaxScheduled; i++) scheduler.ScheduleTimer(partHandle, 100.0, false, 0.0);
		int overflow = scheduler.ScheduleTimer(partHandle, 1.0, false, 0.0);
		Check("cap: " + LotScheduler.MaxScheduled + " entries fit", scheduler.Count == LotScheduler.MaxScheduled);
		Check("cap: the next entry is refused with a warning",
			overflow == -1 && warns.Count > 0 && warns[warns.Count - 1].Contains("full"));
		scheduler.ClearAll();
		Check("cap: ClearAll empties the schedule", scheduler.Count == 0);

		// Two-ended cleanup.
		int ownerTimer = scheduler.ScheduleTimer(7, 1.0, false, 0.0);
		int otherTimer = scheduler.ScheduleTimer(8, 1.0, false, 0.0);
		scheduler.ForgetEntity(7);
		Check("cleanup: ForgetEntity drops that owner's entries only",
			!scheduler.Has(ownerTimer) && scheduler.Has(otherTimer));
		scheduler.ClearAll();

		LotObject doomed = NewScratchPart(scene, "TweenMechDoomed", new Vector3(210f, 5f, 200f));
		int doomedHandle = scene.RegisterHandle(doomed);
		scheduler.ScheduleTween(doomedHandle, "position", 220f, 5f, 200f, 0f, 5f, "linear");
		doomed.Free();
		scheduler.Advance(0.1);
		Check("cleanup: a freed target drops its tween", scheduler.Count == 0);

		scene.DestroyEntity(part);
		scene.DestroyEntity(element);
	}

	// --- the Lua sugar on a private VM (never the lot's) ----------------------------------------

	private static void TestLuaSugar()
	{
		List<string> logs = new List<string>();
		Lua state = new Lua();
		LuaNetBridge bridge = new LuaNetBridge();
		bridge.Attach(state);
		bridge.Watchdog = new LuaWatchdog(state);
		LotScheduler scheduler = new LotScheduler(message => { });
		scheduler.ResolveTarget = handle => null; // no scene here: tween scheduling refuses
		scheduler.FireTimer = (handle, id, keep) => bridge.CallTimer(handle, id, keep);

		try
		{
			state.RegisterFunction("__testLotLog", logs, typeof(List<string>).GetMethod("Add"));
			state.DoString("Lot = { Log = function(message) __testLotLog(tostring(message)) end, " +
				"SpawnCube = function() return 0 end, SpawnSphere = function() return 0 end, " +
				"SpawnCylinder = function() return 0 end }", "testlot");
			LuaBootstrap.Apply(state);
			state.RegisterFunction("ScheduleTween", scheduler, typeof(LotScheduler).GetMethod("ScheduleTween"));
			state.RegisterFunction("ScheduleTimer", scheduler, typeof(LotScheduler).GetMethod("ScheduleTimer"));
			state.RegisterFunction("CancelScheduled", scheduler, typeof(LotScheduler).GetMethod("Cancel"));
			state.DoString(@"
__testSlotEnvs = {}
function __testSlot(handle, slot)
    local env = __testSlotEnvs[slot]
    if env == nil then env = __openlot_newEnv(handle); __testSlotEnvs[slot] = env end
    return env
end
", "testhelpers");

			// A tween whose target does not resolve still returns a token (a harmless no-op).
			bool ran = RunChunk(state, "__testSlot(5, 'a')", "t = Lot.TweenPosition(9, 1, 2, 3, 1.0)");
			Check("lua: a tween on an unresolvable target schedules nothing", ran && scheduler.Count == 0);
			Check("lua: the no-op token returns false", !RunChunkBool(state, "__testSlot(5, 'a')", "return t()"));

			// Validation raises in the calling script.
			logs.Clear();
			RunChunk(state, "__testSlot(5, 'a')",
				"local ok = pcall(function() Lot.TweenPosition(9, 1, 2, 3, 1.0, 'bounce') end) Lot.Log('bounce ' .. tostring(ok))");
			Check("lua: an unknown easing raises", logs.Count == 1 && logs[0] == "bounce false");
			logs.Clear();
			RunChunk(state, "__testSlot(5, 'a')",
				"local ok = pcall(function() Lot.TweenPosition('x', 1) end) Lot.Log('badhandle ' .. tostring(ok))");
			Check("lua: a non-number target raises", logs.Count == 1 && logs[0] == "badhandle false");
			logs.Clear();
			RunChunk(state, "__testSlot(5, 'a')",
				"local ok = pcall(function() After(-1, function() end) end) Lot.Log('negtimer ' .. tostring(ok))");
			Check("lua: a negative timer delay raises", logs.Count == 1 && logs[0] == "negtimer false");

			// Timers: scheduled, fired through the real bridge, one-shot clears itself.
			logs.Clear();
			bool afterRan = RunChunk(state, "__testSlot(5, 'a')",
				"After(0.5, function() Lot.Log('after') end)");
			Check("lua: After schedules a timer", afterRan && scheduler.Count == 1);
			scheduler.Advance(0.6);
			Check("lua: the callback runs through the bridge", logs.Count == 1 && logs[0] == "after");
			scheduler.Advance(1.0);
			Check("lua: a one-shot fires once", logs.Count == 1 && scheduler.Count == 0);

			// Repeating: fires each interval; its token cancels it.
			logs.Clear();
			RunChunk(state, "__testSlot(5, 'a')", "e = Every(0.5, function() Lot.Log('every') end)");
			scheduler.Advance(0.5);
			scheduler.Advance(0.5);
			Check("lua: a repeating timer fires every interval", logs.Count == 2 && scheduler.Count == 1);
			Check("lua: the token cancels it", RunChunkBool(state, "__testSlot(5, 'a')", "return e()") && scheduler.Count == 0);
			scheduler.Advance(1.0);
			Check("lua: nothing fires after the cancel", logs.Count == 2);

			// Destroying the owner clears both sides (bridge + scheduler) in one sweep.
			logs.Clear();
			RunChunk(state, "__testSlot(9, 'd')", "Every(0.5, function() Lot.Log('ghost') end)");
			Check("lua: the timer registered", scheduler.Count == 1);
			bridge.ForgetEntity(9);
			scheduler.ForgetEntity(9);
			scheduler.Advance(1.0);
			Check("lua: a destroyed entity's timer does not fire", logs.Count == 0 && scheduler.Count == 0);

			Check("lua: calling a cancelled timer reports false", !bridge.CallTimer(9, 12345, true));
		}
		finally
		{
			state.Dispose();
		}
	}

	/// <summary>Runs one chunk inside the named env expression; true when it ran without error.</summary>
	private static bool RunChunk(Lua state, string envExpression, string chunk)
	{
		string script = "local env = " + envExpression + "\n" +
			"local fn, err = __openlot_load([=[" + chunk + "]=], 'testchunk', env)\n" +
			"if fn == nil then return false, err end\n" +
			"return pcall(fn)";
		try
		{
			object[] results = state.DoString(script, "tweenselftest");
			return results != null && results.Length > 0 && results[0] is bool ok && ok;
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>Runs one chunk and reports its first return value as a bool (false on any error) —
	/// the token-call shape: `return t()`.</summary>
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
			object[] results = state.DoString(script, "tweenselftest");
			return results != null && results.Length > 0 && results[0] is bool value && value;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static LotObject NewScratchPart(BuilderScene scene, string name, Vector3 at)
	{
		LotObject part = LotObject.Create(LotObjectKind.Cube, name, new Color(0.7f, 0.7f, 0.9f));
		part.Position = at;
		scene.LotRoot.AddChild(part);
		return part;
	}

	// --- staged session probe (real tweens and timers across physics frames) --------------------

	/// <summary>Mid-flight checkpoint: the tween runs 1 s, so 700 ms in it is visibly on its way.</summary>
	private const ulong ProbeMidWaitMsec = 700;

	/// <summary>Final checkpoint: by 1.5 s the tween, the one-shot timer and the three-tick
	/// repeating timer have all resolved.</summary>
	private const ulong ProbeFinalWaitMsec = 800;

	/// <summary>Wait after leaving the session, to prove the authored transform came back.</summary>
	private const ulong ProbeExitWaitMsec = 400;

	private const string ProbeScriptDir = "user://Scripts";
	private const string ProbeScriptName = "__tweenselftest.lua";

	/// <summary>The fixture script, attached to the probe part: a 10 m tween over 1 s (quadOut), a
	/// one-shot timer, a repeating timer that cancels itself from inside its own third fire, and an
	/// immediately self-cancelled timer that must never fire. Note the `local tk; tk = Every(...)`
	/// shape — `local tk = Every(...)` would leave the closure reading a global.</summary>
	private const string ProbeScript =
		"local me = this.Handle\n" +
		"Lot.TweenPosition(me, 160, 0.5, 150, 1.0, \"quadOut\")\n" +
		"After(0.5, function() Lot.Log(\"TWS after\") end)\n" +
		"local n = 0\n" +
		"local tk\n" +
		"tk = Every(0.3, function()\n" +
		"    n = n + 1\n" +
		"    Lot.Log(\"TWS every \" .. n)\n" +
		"    if n >= 3 then tk() end\n" +
		"end)\n" +
		"local late = After(5, function() Lot.Log(\"TWS late\") end)\n" +
		"late()\n";

	private static BuilderScene _probeScene;
	private static LotObject _probePart;
	private static ulong _probeDeadlineMsec;
	private static int _probeStage; // 0 = not started, 1 = mid-flight, 2 = final, 3 = exited, 4 = finished
	private static int _probeChecks;
	private static int _probeFailures;
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

		WriteFixture(ProbeScriptName, ProbeScript);
		_probePart = NewScratchPart(scene, "TweenProbePart", new Vector3(150f, 0.5f, 150f));
		scene.RegisterHandle(_probePart);
		AttachFixture(_probePart, ProbeScriptName);

		_probeDeadlineMsec = Time.GetTicksMsec() + ProbeMidWaitMsec;
		_probeStage = 1;
		GD.Print("[TweenSelfTest] session probe started");
		scene.EnterTestMode(false);
	}

	private static void ProbeCheck(string name, bool condition)
	{
		_probeChecks++;
		if (condition)
		{
			GD.Print("[TweenSelfTest] PASS  (session probe) " + name);
		}
		else
		{
			_probeFailures++;
			GD.PrintErr("[TweenSelfTest] FAIL  (session probe) " + name);
		}
	}

	/// <summary>Advances the staged probe across real frames (called every frame from
	/// BuilderScene._Process): mid-flight → final → exited-and-cleaned.</summary>
	public static void TickProbe()
	{
		if (_probeStage == 0 || _probeStage >= 4) return;
		if (Time.GetTicksMsec() < _probeDeadlineMsec) return;

		if (_probeStage == 1)
		{
			_probeStage = 2;
			float x = _probePart.GlobalPosition.X;
			ProbeCheck("the tween is mid-flight, strictly between the endpoints (x " + x + ")",
				x > 150.2f && x < 159.8f);
			ProbeCheck("the one-shot timer fired", HasLog("TWS after"));
			ProbeCheck("the repeating timer fired at least once (" + CountLogPrefix("TWS every ") + ")",
				CountLogPrefix("TWS every ") >= 1);
			_probeDeadlineMsec = Time.GetTicksMsec() + ProbeFinalWaitMsec;
			return;
		}

		if (_probeStage == 2)
		{
			_probeStage = 3;
			float endX = _probePart.GlobalPosition.X;
			ProbeCheck("the tween landed on its target (x " + endX + ")",
				Mathf.Abs(endX - 160f) < 0.05f);
			int everyCount = CountLogPrefix("TWS every ");
			ProbeCheck("the repeating timer fired exactly three times (got " + everyCount + ")",
				everyCount == 3 && HasLog("TWS every 3") && !HasLog("TWS every 4"));
			ProbeCheck("the self-cancelled timer never fired", !HasLog("TWS late"));
			ProbeCheck("the schedule is empty once everything resolved (" + _probeScene.Scheduler.Count + ")",
				_probeScene.Scheduler.Count == 0);

			_probeScene.ExitTestMode();
			_probeDeadlineMsec = Time.GetTicksMsec() + ProbeExitWaitMsec;
			return;
		}

		// Phase 3: out of the session — the authored transform is back and the probe cleans up.
		_probeStage = 4;
		ProbeCheck("leaving Test mode restored the authored position (x " + _probePart.Position.X + ")",
			Mathf.Abs(_probePart.Position.X - 150f) < 0.01f);
		// The exit re-ran the fixtures in build mode (subscriptions/schedules are recorded but
		// idle there); destroying the animated entity must clear everything it owned.
		_probeScene.DestroyEntity(_probePart);
		ProbeCheck("destroying the animated entity cleared its schedule (" + _probeScene.Scheduler.Count + ")",
			_probeScene.Scheduler.Count == 0);
		_probePart = null;
		for (int i = 0; i < _probeFiles.Count; i++)
		{
			string path = ProbeScriptDir + "/" + _probeFiles[i];
			if (FileAccess.FileExists(path)) DirAccess.RemoveAbsolute(path);
		}
		_probeFiles.Clear();
		_probeScene = null;
		GD.Print("[TweenSelfTest] session probe: " + _probeChecks + " checks, " +
			_probeFailures + " failure(s).");
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

	/// <summary>How many recent log lines start with <paramref name="prefix"/>.</summary>
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
}