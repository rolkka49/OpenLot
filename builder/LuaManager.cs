using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using NLua;

/// <summary>
/// Entry seam for the Lua scripting layer — the NLua runtime (roadmap milestone 3.1).
/// Lifecycle is scene-owned: BuilderScene.Initialize()s this when a lot opens and Shutdown()s it
/// when the lot exits (every object handle the API vends dies with the scene, so the VM must too).
/// Call sites (command line, code editor, future script runner) keep calling RunString unchanged.
/// Sandbox boundary (clinerules 1.1): scripts only ever reach the registered LotLuaApi object —
/// the CLR bridge (LoadCLRPackage) is never opened and io/os/require are stripped at init.
/// </summary>
public class LuaManager
{
	// Creator-convenience globals mapped onto the registered Lot API in Lua itself (no extra C#
	// surface): keeps the default script template `function start() cube() end` working verbatim.
	private const string BootstrapChunk =
		"function cube(x, y, z) return Lot.SpawnCube(x or 0, y or 0, z or 0) end\n" +
		"function sphere(x, y, z) return Lot.SpawnSphere(x or 0, y or 0, z or 0) end\n" +
		"function cylinder(x, y, z) return Lot.SpawnCylinder(x or 0, y or 0, z or 0) end\n" +
		"function capsule(x, y, z) return Lot.SpawnCapsule(x or 0, y or 0, z or 0) end\n" +
		"function decal(host) return Lot.SpawnDecal(host or 0) end\n" +
		"function log(msg) Lot.Log(tostring(msg)) end\n" +
		// print is reinstated for the scratch path (code editor and command strip): LuaBootstrap
		// strips the native one just above, so this re-defines it as a varargs alias of log with
		// Lua's own tab-joined formatting. Defined AFTER LuaBootstrap.Apply, which is what makes
		// the ordering safe.
		"function print(...)\n" +
		"    local n = select('#', ...)\n" +
		"    local parts = {}\n" +
		"    for i = 1, n do parts[i] = tostring((select(i, ...))) end\n" +
		"    Lot.Log(table.concat(parts, '\\t'))\n" +
		"end\n";

	public static LuaManager Instance { get; } = new LuaManager();

	private NLua.Lua _state;
	private LotLuaApi _api;
	private LuaMemoryGuard _memoryGuard;
	private LuaWatchdog _watchdog;
	private LuaNetBridge _bridge;
	private bool _recreatePending;

	public string LastError { get; private set; } = "";
	public bool IsRuntimeAvailable { get; private set; }

	/// <summary>The net.* bridge (null while the runtime is unavailable). Owner wires Router.Flush()
	/// to a per-frame call and ProcessDeferredRecreate() to the frame boundary.</summary>
	public LuaNetBridge NetBridge => _bridge;

	/// <summary>Instruction watchdog of the current VM (null while unavailable).</summary>
	public LuaWatchdog Watchdog => _watchdog;

	/// <summary>Set by the self-tests while they deliberately provoke errors (watchdog trips, OOM,
	/// suspension): those messages get a "[expected]" prefix so `grep ERROR | grep -v expected`
	/// shows only real problems.</summary>
	internal bool SelfTestMode { get; set; }

	private string MarkExpected(string message)
	{
		return SelfTestMode ? "[expected] " + message : message;
	}

	/// <summary>Hard-cap allocator of the current VM (null while unavailable).</summary>
	public LuaMemoryGuard MemoryGuard => _memoryGuard;

	private LuaManager() { }

	/// <summary>Creates the VM, binds the API surface, and installs sandbox/net/watchdog/allocator.
	/// Idempotent.</summary>
	public void Initialize(LotLuaApi api)
	{
		if (IsRuntimeAvailable) return;
		_api = api;
		try
		{
			Lua state = new Lua();

			// Cap the VM's memory before anything else allocates (v4 A4): the pre-install
			// baseline stays small, so the cap is meaningful.
			_memoryGuard = new LuaMemoryGuard();
			_memoryGuard.Install(state.State, LuaMemoryGuard.MemoryCapBytes);

			// Binding strategy, both choices deliberate:
			//  - Every method is registered with the instance captured at registration time
			//    (RegisterFunction with a captured target). Assigning state["Lot"] = api instead
			//    uses NLua userdata method dispatch, which resolved the MethodInfo but invoked it
			//    with a null target ("instance method 'SpawnCube' requires a non null target").
			//  - Single-segment global names only: dotted RegisterFunction paths walk the parent
			//    table in native Lua and hard-crashed (unprotected lua error, no managed
			//    exception) when the table did not exist yet. Lua itself builds the Lot table.
			MethodInfo[] methods = typeof(LotLuaApi).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
			System.Text.StringBuilder lotTable = new System.Text.StringBuilder("Lot = {}\n");
			for (int i = 0; i < methods.Length; i++)
			{
				state.RegisterFunction(methods[i].Name, api, methods[i]);
				// NetInvoke/NetRegister, the event plumbing (SubscribeEvent/UnsubscribeEvent) and
				// the tween/timer plumbing (milestone 3.8) are bootstrap sugar called by name from
				// the chunk, not creator API: registered as globals above, deliberately kept out of
				// the Lot table.
				if (methods[i].Name == "NetInvoke" || methods[i].Name == "NetRegister" ||
					methods[i].Name == "SubscribeEvent" || methods[i].Name == "UnsubscribeEvent" ||
					methods[i].Name == "ScheduleTween" || methods[i].Name == "ScheduleTimer" ||
					methods[i].Name == "CancelScheduled") continue;
				lotTable.Append("Lot.").Append(methods[i].Name).Append(" = ").Append(methods[i].Name).Append('\n');
			}

			// The Lot table must exist before the bootstrap builds its read-only proxy over it.
			state.DoString(lotTable.ToString(), "openlot_api_rebind");
			// Sandbox + per-entity net tables + validation pre-pass + watchdog. Strips io/os/
			// require/dofile/loadfile/package/debug/load/collectgarbage/coroutine/rawset/print/
			// luanet, so this replaces the old ad-hoc strip chunk.
			LuaBootstrap.Apply(state);
			// Scratch-path conveniences (cube/sphere/cylinder/log globals) for the code editor and
			// command line; entity scripts get their own copies inside the sandbox table.
			state.DoString(BootstrapChunk, "openlot_bootstrap");

			_watchdog = new LuaWatchdog(state);
			_bridge = new LuaNetBridge();
			_bridge.Attach(state);
			_bridge.Watchdog = _watchdog;
			_bridge.MemoryGuard = _memoryGuard;
			_bridge.Warning = GD.PushWarning;
			_bridge.OomRecreateRequested = RequestRecreate;
			_bridge.EnableOfflineNet();
			if (api != null) api.Net = _bridge;

			_state = state;
			LastError = "";
			IsRuntimeAvailable = true;
		}
		catch (Exception ex)
		{
			// A failing native Lua call must degrade to "runtime unavailable" instead of
			// taking the whole process down with an unprotected native error.
			if (_bridge != null) { _bridge.Detach(); _bridge = null; }
			if (_state != null) { _state.Dispose(); _state = null; }
			if (_memoryGuard != null) { _memoryGuard.Dispose(); _memoryGuard = null; }
			_watchdog = null;
			IsRuntimeAvailable = false;
			LastError = "Lua init failed: " + ex.Message;
			LotLog.Error("lua", LastError);
			GD.PushError("[LuaManager] " + LastError);
		}
	}

	/// <summary>
	/// Tears the VM down. Order matters and is deliberate:
	///   1. detach the bridge from its transport (no more receipts into a dying VM);
	///   2. drop the router's queued envelopes (they can never be dispatched);
	///   3. dispose the Lua state — its allocator callbacks still run during lua_close, so the
	///      memory guard must be alive until this returns;
	///   4. mark the bridge / watchdog / memory guard dead so any stale reference throws a clear
	///      managed exception from now on instead of calling lua_* on the freed state.
	/// There are no long-lived LuaFunction/LuaTable wrappers to release first: every call fetches
	/// what it needs by name and disposes the wrappers it materializes (see LuaCall and
	/// NetSerializer), so the state itself is the only long-lived native reference. (The one
	/// transient exception, RunString's start() wrapper, is disposed at its call site.)
	/// </summary>
	public void Shutdown()
	{
		LuaNetBridge bridge = _bridge;
		LuaWatchdog watchdog = _watchdog;
		LuaMemoryGuard guard = _memoryGuard;

		if (bridge != null)
		{
			bridge.Detach();
			if (bridge.Router != null)
			{
				int dropped = bridge.Router.ClearQueue();
				if (dropped > 0)
					GD.PushWarning("[LuaManager] dropping " + dropped + " queued net call(s) on VM teardown");
			}
		}

		if (_state != null)
		{
			_state.Dispose();
			_state = null;
		}

		// Only after the state is gone: these mark the stale references unusable.
		if (bridge != null) bridge.MarkDead();
		if (watchdog != null) watchdog.MarkDead();
		if (guard != null) guard.MarkDead();

		_bridge = null;
		_watchdog = null;
		_memoryGuard = null;
		_api = null;
		IsRuntimeAvailable = false;

		// A fresh lot starts with a clean rebuild budget and scripting enabled. Skipped while a
		// REBUILD is calling Shutdown/Initialize: otherwise every rebuild would clear its own rate
		// window and the limiter could never trip.
		if (!_rebuilding)
		{
			_rebuildTimes.Clear();
			_scriptingSuspended = false;
			SuspensionReason = null;
		}
	}

	/// <summary>Flags that the VM must be rebuilt (out of memory). The rebuild itself is deferred
	/// to <see cref="ProcessDeferredRecreate"/> so it never happens mid-dispatch or mid-chunk.</summary>
	public void RequestRecreate()
	{
		_recreatePending = true;
	}

	/// <summary>How many times the VM has been rebuilt (OOM recovery). Diagnostics/self-test
	/// observability only.</summary>
	public int VmRebuildCount { get; private set; }

	// --- Rebuild limiter (F1) -------------------------------------------------------------------
	// An OOM can repeat every frame (a script that allocates past the cap at load, or a handler that
	// does it on every call), and each occurrence would otherwise buy a fresh 64 MB VM. Cap the
	// rate: past the limit, scripting is suspended until the lot is reloaded.

	/// <summary>Rebuilds allowed inside <see cref="RebuildWindowSeconds"/>.</summary>
	public const int MaxRebuildsPerWindow = 3;

	/// <summary>Length of the rebuild-rate window, in seconds.</summary>
	public const double RebuildWindowSeconds = 10.0;

	private readonly List<double> _rebuildTimes = new List<double>();
	private bool _scriptingSuspended;
	private bool _rebuilding;

	/// <summary>True when scripting was suspended by the rebuild limiter: scripts stay disabled
	/// (nothing reloads, no dispatch) until the lot is reloaded.</summary>
	public bool ScriptingSuspended => _scriptingSuspended;

	/// <summary>Why scripting was suspended (null while running normally).</summary>
	public string SuspensionReason { get; private set; }

	/// <summary>Clears the rebuild window (self-test support: each integration test starts with a
	/// clean rate budget so one test's rebuilds cannot suspend scripting inside another).</summary>
	internal void ResetRebuildWindowForTest()
	{
		_rebuildTimes.Clear();
		_scriptingSuspended = false;
		SuspensionReason = null;
	}

	/// <summary>Restores a running session after the DEBUG self-test suites. The integration suite
	/// deliberately provokes VM rebuilds and, by design, a scripting suspension in its last test;
	/// left alone that would disable the developer's scripts and pin the red suspension banner on
	/// the Viewport for the rest of the session. This clears the rebuild limiter, drops any pending
	/// rebuild, and forces a fresh VM (the last OOM test leaves the old one at its memory cap). The
	/// caller reloads the lot's own scripts afterward. Test support, not a creator API.</summary>
	internal void RecoverFromSelfTest()
	{
		_rebuildTimes.Clear();
		_scriptingSuspended = false;
		SuspensionReason = null;
		_recreatePending = false;
		if (IsRuntimeAvailable)
		{
			LotLuaApi api = _api;
			Shutdown();
			Initialize(api);
		}
		VmRebuildCount = 0;
	}

	/// <summary>Rebuilds the VM when an OOM was flagged. The owner calls this once per frame after
	/// net dispatch; a no-op otherwise. Refuses (and suspends scripting) past the limiter.</summary>
	public void ProcessDeferredRecreate()
	{
		if (!_recreatePending) return;
		_recreatePending = false;
		if (_scriptingSuspended) return;

		double now = Time.GetTicksMsec() / 1000.0;
		_rebuildTimes.RemoveAll(t => now - t > RebuildWindowSeconds);
		if (_rebuildTimes.Count >= MaxRebuildsPerWindow)
		{
			_scriptingSuspended = true;
			SuspensionReason = _rebuildTimes.Count + " VM rebuilds within " + RebuildWindowSeconds +
				"s (limit " + MaxRebuildsPerWindow + "): most likely a script exhausting the memory cap";
			LotLog.Error("script", "scripting suspended — " + SuspensionReason
				+ ". Scripts are disabled and will not reload until the lot is reloaded.");
			GD.PushError(MarkExpected("[LuaManager] scripting SUSPENDED — " + SuspensionReason +
				". Scripts are disabled and will not reload until the lot is reloaded."));
			return;
		}
		_rebuildTimes.Add(now);

		GD.PushWarning(MarkExpected("[LuaManager] rebuilding the lot Lua VM after an out-of-memory event"));
		LotLuaApi api = _api;
		_rebuilding = true;
		try
		{
			Shutdown();
			Initialize(api);
		}
		finally
		{
			_rebuilding = false;
		}
		VmRebuildCount++;
	}

	/// <summary>Runs creator Lua code. If the chunk defined a global <c>start</c> function it is
	/// called afterwards (so `function start() cube() end` behaves like the Unity prototype).</summary>
	/// <returns>True when the code ran without a Lua error.</returns>
	public bool RunString(string code, string chunkName)
	{
		if (!IsRuntimeAvailable)
		{
			LastError = "Lua runtime is not initialized.";
			return false;
		}
		if (string.IsNullOrEmpty(code)) return true;

		bool tripped = false;
		try
		{
			LastError = "";
			// Chunk runs get the larger script budget (v4 A1/amendment 1); the hook is always
			// cleared afterwards, from C# — after a trip the count-1 hook would block any Lua
			// cleanup call of its own.
			_watchdog.ArmUnit(LuaWatchdog.ScriptInstructionBudget);
			try
			{
				_state.DoString(code, chunkName);

				object start = _state["start"];
				LuaFunction startFunction = start as LuaFunction;
				if (startFunction != null)
				{
					// The wrapper pins a Lua registry reference (NLua leaks otherwise), so it is
					// disposed here rather than left to the GC.
					try
					{
						startFunction.Call();
					}
					finally
					{
						startFunction.Dispose();
					}
				}
			}
			finally
			{
				tripped = _watchdog.EndUnit();
			}

			if (tripped)
			{
				LastError = "script execution limit exceeded";
				GD.PushWarning("[LuaManager] " + chunkName + ": " + LastError);
				return false;
			}
			if (_memoryGuard.ConsumeOomHit())
			{
				// The script may have caught "not enough memory" with pcall; the flag is the
				// reliable signal, and the rebuild waits for a safe point.
				GD.PushWarning("[LuaManager] " + chunkName + ": Lua out of memory; rebuilding the VM");
				RequestRecreate();
				return false;
			}
			return true;
		}
		catch (Exception ex)
		{
			LastError = ex.Message;
			if (tripped)
			{
				// A watchdog trip is a controlled outcome — the loop was stopped on purpose — not an
				// engine failure, so it warns (matching how dispatcher trips are reported) instead of
				// spamming an error with a managed stack trace. The chunk name identifies the script;
				// the editor/command line already show LastError next to the Run button.
				GD.PushWarning(MarkExpected("[LuaManager] " + chunkName + ": script execution limit exceeded"));
			}
			else
			{
				GD.PushError("[LuaManager] " + chunkName + " failed: " + ex.Message);
			}
			return false;
		}
	}
}