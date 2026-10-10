using System;
using System.Collections.Generic;
using NLua;

/// <summary>
/// The Lua side of the router seam (design doc v2 §2 IDispatchTarget, realized in step 3) plus the
/// host of the two bound API methods the bootstrap funnels through, and the net-policy bits the
/// router must not know about:
///
///   - Outbound: InvokeFromLua(handle, direction, name, packedArgs) encodes the args with
///     NetSerializer and hands the call to the router (which owns queueing/routing/limits).
///   - Inbound:  TryDispatch(...) decodes the args payload straight onto the KeraLua stack and
///     calls the bootstrap's __openlot_dispatch. No LuaTable/LuaFunction wrapper is ever retained.
///   - Watchdog policy: every dispatch is an armed execution unit; 3 trips on one entity disable
///     that entity's net handlers for the session (step-3 amendment 1). Milestone 3.7's event
///     deliveries and onFrame run through the same unit / trip / disable policy — one breaker, not
///     two (see TryDispatchEvent / CallOnFrame).
///   - OOM policy: the allocator's OomHit flag is checked after every dispatch — a script's own
///     pcall can swallow the "not enough memory" error, so the flag is the only reliable signal.
///     Rebuilding the VM is deferred to the owner (LuaManager) at a safe point, never mid-dispatch.
///
/// Godot-free like the router and serializer: logging goes through the Warning delegate.
/// </summary>
/// <summary>Outcome of loading one entity script, so callers can distinguish a memory failure
/// (quarantine it, don't retry) from an ordinary error (report it).</summary>
public enum ScriptLoadResult
{
	Loaded,
	Failed,
	OutOfMemory
}

public sealed class LuaNetBridge : IDispatchTarget
{
	private readonly Dictionary<int, int> _tripsByEntity = new Dictionary<int, int>();
	private readonly HashSet<int> _disabledEntities = new HashSet<int>();
	private readonly Dictionary<int, string> _entityScripts = new Dictionary<int, string>();

	private KeraLua.Lua _state;

	/// <summary>Assigned by the owner after construction (the router dispatches into this bridge).</summary>
	public NetRouter Router { get; set; }

	/// <summary>Assigned by the owner; the bridge arms it around every dispatch.</summary>
	public LuaWatchdog Watchdog { get; set; }

	/// <summary>Assigned by the owner; checked after every dispatch (see the class remarks).</summary>
	public LuaMemoryGuard MemoryGuard { get; set; }

	/// <summary>Invoked when OOM was detected; the owner must rebuild the VM at a safe point.</summary>
	public Action OomRecreateRequested { get; set; }

	/// <summary>All warnings flow here (GD.PushWarning in the game, a list in tests).</summary>
	public Action<string> Warning { get; set; }

	// --- Load context: who is spawning right now (F2 owner tagging) ---
	// While a script's INITIAL load runs (chunk + start()), any entity the Lot API spawns belongs to
	// that script. Handler-spawned entities are deliberately NOT owned (they are runtime state; see
	// the design doc's known limitations).

	/// <summary>Handle of the entity whose script is currently loading, or -1 outside a load.</summary>
	public int LoadOwnerHandle { get; private set; } = -1;

	/// <summary>Script file name of the load in progress (paired with LoadOwnerHandle).</summary>
	public string LoadOwnerScript { get; private set; }

	// --- VM lifetime (defense in depth) ---

	private bool _dead;

	/// <summary>True once the owning VM was torn down (see MarkDead).</summary>
	public bool IsDead => _dead;

	/// <summary>
	/// Marks this bridge dead after LuaManager disposed the VM behind it: the cached KeraLua handle
	/// now points at freed memory, so every state-touching entry point throws a clear managed
	/// exception instead of calling lua_* on it (reproduced as a native crash in the 3.2 review).
	/// The reference is also cleared and the per-VM maps are dropped so nothing survives with it.
	/// </summary>
	public void MarkDead()
	{
		_dead = true;
		_state = null;
		_tripsByEntity.Clear();
		_disabledEntities.Clear();
		_entityScripts.Clear();
		Watchdog = null;
		MemoryGuard = null;
		Router = null;
		OomRecreateRequested = null;
	}

	private void ThrowIfDead()
	{
		if (_dead || _state == null)
			throw new InvalidOperationException("Lua VM was rebuilt: this net bridge belongs to a disposed VM");
	}

	/// <summary>
	/// Binds the bridge to a (possibly freshly rebuilt) Lua state. Clears the per-entity trip and
	/// disable state: a rebuilt VM has no declarations, so the circuit breakers must start over.
	/// </summary>
	public void Attach(Lua state)
	{
		if (state == null) throw new ArgumentNullException(nameof(state));
		_state = state.State;
		_tripsByEntity.Clear();
		_disabledEntities.Clear();
		_entityScripts.Clear();
	}

	/// <summary>Creates the offline (§3.4) session: loopback transport + router over this bridge.</summary>
	public void EnableOfflineNet()
	{
		LoopbackTransport transport = new LoopbackTransport();
		Router = new NetRouter(transport, this);
		Router.Warning = Warn;
	}

	/// <summary>Drops the transport subscription (owner teardown).</summary>
	public void Detach()
	{
		if (Router != null) Router.Detach();
	}

	/// <summary>Registers the owning script's display name, used by the bankruptcy warning.</summary>
	public void SetEntityScript(int handle, string scriptName)
	{
		ThrowIfDead();
		if (string.IsNullOrEmpty(scriptName)) return;
		_entityScripts[handle] = scriptName;
		LuaCall.CallVoidIntString(_state, "__openlot_setEntityScript", handle, scriptName);
	}

	/// <summary>Starts a new frame's instruction accounting (owner calls before Router.Flush()).</summary>
	public void BeginFrame()
	{
		ThrowIfDead();
		Watchdog?.BeginFrame();
	}

	/// <summary>Declaration record from the bootstrap (via LotLuaApi.NetRegister): forwards to the
	/// router's diagnostic registry, which owns the redeclaration warning.</summary>
	public void RegisterHandler(int handle, string direction, string name, string scriptName)
	{
		if (Router == null) return;
		NetDirection parsed;
		if (direction == "server") parsed = NetDirection.Server;
		else if (direction == "client") parsed = NetDirection.Client;
		else return;
		Router.RegisterHandler(handle, parsed, name, scriptName);
	}

	// --- Outbound (bound into Lua as Lot.NetInvoke) ---

	/// <summary>Encodes and routes one net.* call. Serialization failures throw
	/// NetSerializationException, which NLua turns into an error in the calling script.</summary>
	public void InvokeFromLua(int handle, string direction, string name, LuaTable args)
	{
		ThrowIfDead();
		if (args == null) throw new NetSerializationException("net: missing argument table");
		NetDirection parsedDirection;
		if (direction == "server") parsedDirection = NetDirection.Server;
		else if (direction == "client") parsedDirection = NetDirection.Client;
		else throw new NetSerializationException("net: unknown namespace '" + direction + "'");

		byte[] payload;
		try
		{
			int count = Convert.ToInt32(args["n"]);
			payload = NetSerializer.EncodeArgs(args, count);
		}
		finally
		{
			args.Dispose(); // the bootstrap closure hands its packed table over to us
		}

		if (Router == null)
		{
			Warn("[Net] no router is attached; dropped net." + direction + "." + name);
			return;
		}
		Router.InvokeLocal(handle, parsedDirection, name, payload);
	}

	// Creator-facing shapes of the cross-entity entry points (3 args, packed table). Production
	// binds these through LotLuaApi.CallServer/CallClient (the clinerules §1.1 boundary); they
	// exist here too so the self-test can register the same shapes off the bridge, exactly like
	// NetRegister/InvokeFromLua.
	public void CallServer(int handle, string name, LuaTable args)
	{
		InvokeCrossEntity(handle, NetDirection.Server, name, args);
	}

	public void CallClient(int handle, string name, LuaTable args)
	{
		InvokeCrossEntity(handle, NetDirection.Client, name, args);
	}

	/// <summary>
	/// Cross-entity call (step 5): encodes the packed args and hands the call to the router with
	/// the TARGET handle. Sender/origin come from the router's dispatch context, so a call made
	/// inside a handler keeps the original caller's identity (transitive rule).
	/// </summary>
	public void InvokeCrossEntity(int handle, NetDirection direction, string name, LuaTable args)
	{
		ThrowIfDead();
		if (args == null) throw new NetSerializationException("net: missing argument table");
		byte[] payload;
		try
		{
			payload = NetSerializer.EncodeArgs(args, Convert.ToInt32(args["n"]));
		}
		finally
		{
			args.Dispose(); // the bootstrap closure hands its packed table over to us
		}

		if (Router == null)
		{
			Warn("[Net] no router is attached; dropped cross-entity call " + name + " for entity " + handle);
			return;
		}
		Router.InvokeLocal(handle, direction, name, payload);
	}

	// --- Entity script loading (step 4) ---

	/// <summary>
	/// Creates the entity's sandbox env (__openlot_newEnv), loads the chunk into it
	/// (__openlot_load) and calls its start() if defined — all as ONE watchdog unit under the
	/// script instruction budget (amendment 1). The env lives only on the Lua stack: no C#
	/// reference is kept, so nothing can pin it after the entity is gone (v2 §10a). Returns false
	/// with a message on the first failure (syntax error, runtime error, watchdog trip, OOM).
	/// </summary>
	public ScriptLoadResult LoadEntityScript(int handle, string chunk, string chunkName, out string error)
	{
		ThrowIfDead();
		error = null;
		if (_state == null)
		{
			error = "Lua runtime is not available";
			return ScriptLoadResult.Failed;
		}

		// Each script load gets its OWN frame accounting: a reload with many scripts must not add
		// their instruction counts together against one frame's budget (B). Dispatch keeps the
		// frame budget that ScriptRuntime.Tick begins.
		Watchdog?.BeginFrame();
		// Clear any OOM left over from an earlier unit (whoever caused it already reported it) so an
		// out-of-memory below is attributed to THIS load and never to its innocent neighbour.
		if (MemoryGuard != null) MemoryGuard.ConsumeOomHit();

		bool ok = true;
		bool tripped;
		int top = _state.GetTop();
		Watchdog?.ArmUnit(LuaWatchdog.ScriptInstructionBudget);
		// Everything spawned between here and the finally is owned by this script's load (F2).
		LoadOwnerHandle = handle;
		LoadOwnerScript = chunkName;
		try
		{
			// env
			_state.GetGlobal("__openlot_newEnv");
			_state.PushInteger(handle);
			if (!CallAndCheck(1, 1, "env", ref error)) ok = false;
			else
			{
				int envIndex = _state.GetTop();

				// load(chunk, chunkName, env)
				_state.GetGlobal("__openlot_load");
				_state.PushString(chunk ?? "");
				_state.PushString(chunkName ?? "script");
				_state.PushCopy(envIndex); // KeraLua's lua_pushvalue; the env stays stack-only
				if (!CallAndCheck(3, 1, "load", ref error)) ok = false;
				else if (!CallAndCheck(0, 0, "run", ref error)) ok = false;
				else
				{
					// start(), when the script defines one (same convention as the scratch path).
					_state.GetField(envIndex, "start");
					if (_state.IsFunction(-1))
					{
						if (!CallAndCheck(0, 0, "start", ref error)) ok = false;
					}
					else
					{
						_state.SetTop(_state.GetTop() - 1);
					}

					// onFrame capture (milestone 3.7): the env's onFrame goes to the Lua-side
					// registry via __openlot_captureFrame (nil is fine — the helper ignores it),
					// so a script without one costs a single call and nothing else. Same watchdog
					// unit as the chunk and start().
					if (ok)
					{
						_state.GetGlobal("__openlot_captureFrame");
						_state.PushInteger(handle);
						_state.GetField(envIndex, "onFrame");
						if (!CallAndCheck(2, 0, "onFrame", ref error)) ok = false;
					}
				}
			}
		}
		finally
		{
			LoadOwnerHandle = -1;
			LoadOwnerScript = null;
			tripped = Watchdog != null && Watchdog.EndUnit();
			_state.SetTop(top);
		}

		// Read the OOM flag REGARDLESS of ok: a chunk that dies with "not enough memory" fails
		// (ok == false) AND leaves the flag set, so gating this on ok both lost the attribution and
		// leaked the flag onto the next script.
		bool oom = MemoryGuard != null && MemoryGuard.ConsumeOomHit();
		if (oom)
		{
			error = "out of memory";
			Warn("[Lua] " + chunkName + ": Lua out of memory while loading; the lot VM will be rebuilt");
			OomRecreateRequested?.Invoke();
			return ScriptLoadResult.OutOfMemory;
		}
		if (ok && tripped)
		{
			error = "script execution limit exceeded";
			return ScriptLoadResult.Failed;
		}
		return ok ? ScriptLoadResult.Loaded : ScriptLoadResult.Failed;
	}

	/// <summary>Calls the function currently on the stack (below the pushed arguments) and reports
	/// the Lua error text on failure. On failure the push/pop imbalance is corrected here.</summary>
	private bool CallAndCheck(int argumentCount, int resultCount, string what, ref string error)
	{
		int before = _state.GetTop() - argumentCount;
		KeraLua.LuaStatus status = _state.PCall(argumentCount, resultCount, 0);
		if (status == KeraLua.LuaStatus.OK) return true;
		error = what + ": " + _state.ToString(-1, false);
		_state.SetTop(before); // drop the function and any results
		return false;
	}

	// --- Inbound (IDispatchTarget) ---

	public bool TryDispatch(int handle, NetDirection direction, string name, int senderPeerId, NetOrigin origin, byte[] argsPayload)
	{
		ThrowIfDead();
		if (_disabledEntities.Contains(handle))
		{
			// Already warned at disable time; dropping quietly keeps the log usable.
			return true;
		}

		bool handled;
		bool tripped;
		Watchdog?.ArmUnit(LuaWatchdog.DispatchInstructionBudget);
		try
		{
			handled = InvokeDispatch(handle, direction, name, senderPeerId, origin == NetOrigin.Player, argsPayload);
		}
		finally
		{
			tripped = Watchdog != null && Watchdog.EndUnit();
		}
		if (tripped) HandleTrip(handle, direction, name);

		if (MemoryGuard != null && MemoryGuard.ConsumeOomHit())
		{
			Warn("[Net] Lua out of memory while dispatching net." + DirectionName(direction) + "." + name +
				" on entity " + handle + "; the lot VM will be rebuilt");
			OomRecreateRequested?.Invoke();
			return true;
		}
		return handled;
	}

	private bool InvokeDispatch(int handle, NetDirection direction, string name, int senderPeerId, bool fromPlayer, byte[] argsPayload)
	{
		if (_state == null)
		{
			Warn("[Net] no Lua state is attached; dropped net." + DirectionName(direction) + "." + name);
			return true;
		}

		int top = _state.GetTop();
		try
		{
			// Fetched by name each dispatch instead of holding a LuaFunction reference: wrappers
			// pin registry references, and this global is invisible to sandbox envs anyway.
			_state.GetGlobal("__openlot_dispatch");
			_state.PushInteger(handle);
			_state.PushString(DirectionName(direction));
			_state.PushString(name);
			_state.PushInteger(senderPeerId);
			_state.PushBoolean(fromPlayer);
			NetSerializer.DecodeArgs(_state, argsPayload, 0); // pushes the args table as the 6th argument

			KeraLua.LuaStatus status = _state.PCall(6, 1, 0);
			if (status != KeraLua.LuaStatus.OK)
			{
				string error = _state.ToString(-1, false);
				Warn("[Net] handler net." + DirectionName(direction) + "." + name + " on entity " + handle +
					" errored: " + error);
				return true; // handled-and-failed: the router must not also report "no handler"
			}
			return _state.ToBoolean(-1);
		}
		catch (NetSerializationException ex)
		{
			// Crafted/corrupt args bytes (the receipt path treats them as untrusted).
			Warn("[Net] dropped net." + DirectionName(direction) + "." + name + " for entity " + handle + ": " + ex.Message);
			return true;
		}
		finally
		{
			_state.SetTop(top);
		}
	}

	/// <summary>Step-3 amendment 1: three trips disable the entity's net handlers for the session.
	/// Item 6: the trip is attributed to the script that DECLARED the handler being dispatched,
	/// resolved from the router's declaration registry — with two scripts on one entity the
	/// last-loaded name is not necessarily the one whose handler tripped.</summary>
	private void HandleTrip(int handle, NetDirection direction, string name)
	{
		int trips;
		_tripsByEntity.TryGetValue(handle, out trips);
		trips++;
		_tripsByEntity[handle] = trips;
		// Resolve the blamed script ONCE, before any UnregisterEntity: the disable path wipes the
		// router registry, so a second lookup would fall back to the (possibly wrong) per-handle name.
		string script = TripScript(handle, direction, name);
		Warn("[Net] entity " + handle + " (" + script +
			") hit the script execution limit (" + trips + "/" + LuaWatchdog.MaxTripsPerEntity + ")");

		if (trips < LuaWatchdog.MaxTripsPerEntity) return;

		_disabledEntities.Add(handle);
		LuaCall.CallVoidInt(_state, "__openlot_disableNet", handle);
		if (Router != null) Router.UnregisterEntity(handle);
		Warn("[Net] entity " + handle + " (" + script +
			"): net handlers disabled after " + trips + " watchdog trips");
	}

	// --- Events (milestone 3.7) --------------------------------------------------------------

	/// <summary>
	/// Delivers one event to one subscriber under the same watchdog policy as a net dispatch: one
	/// armed execution unit, trips counted in the shared per-entity breaker (three disable the
	/// entity's handlers, net and events alike), OOM checked after the call. Returns true when the
	/// subscriber's handler ran (or the entity is disabled); false when the registration is gone
	/// (unsubscribed or reloaded).
	/// </summary>
	public bool TryDispatchEvent(int subjectHandle, string eventName, int subscriber, int otherHandle,
		int playerId, bool fromPlayer)
	{
		ThrowIfDead();
		if (_disabledEntities.Contains(subscriber)) return true;

		bool handled;
		bool tripped;
		Watchdog?.ArmUnit(LuaWatchdog.DispatchInstructionBudget);
		try
		{
			handled = InvokeEventDispatch(subjectHandle, eventName, subscriber, otherHandle, playerId, fromPlayer);
		}
		finally
		{
			tripped = Watchdog != null && Watchdog.EndUnit();
		}
		if (tripped) HandleEventTrip(subscriber, eventName);

		if (MemoryGuard != null && MemoryGuard.ConsumeOomHit())
		{
			Warn("[Events] Lua out of memory while delivering '" + eventName + "' to entity " + subscriber +
				"; the lot VM will be rebuilt");
			OomRecreateRequested?.Invoke();
			return true;
		}
		return handled;
	}

	private bool InvokeEventDispatch(int subjectHandle, string eventName, int subscriber, int otherHandle,
		int playerId, bool fromPlayer)
	{
		if (_state == null)
		{
			Warn("[Events] no Lua state is attached; dropped '" + eventName + "' for entity " + subscriber);
			return true;
		}

		int top = _state.GetTop();
		try
		{
			_state.GetGlobal("__openlot_dispatchEvent");
			_state.PushInteger(subjectHandle);
			_state.PushString(eventName);
			_state.PushInteger(subscriber);
			_state.PushInteger(otherHandle);
			_state.PushInteger(playerId);
			_state.PushBoolean(fromPlayer);

			KeraLua.LuaStatus status = _state.PCall(6, 1, 0);
			if (status != KeraLua.LuaStatus.OK)
			{
				string error = _state.ToString(-1, false);
				Warn("[Events] handler '" + eventName + "' on entity " + subscriber + " errored: " + error);
				return true; // handled-and-failed: nothing else can deliver it either
			}
			return _state.ToBoolean(-1);
		}
		finally
		{
			_state.SetTop(top);
		}
	}

	/// <summary>The event-side trip handler: the same counter, breaker and disable mechanics as the
	/// net path. The blame is the entity's registered script name — events have no declaration
	/// table to resolve a more precise one from, and the last-loaded name is the honest answer.</summary>
	private void HandleEventTrip(int handle, string eventName)
	{
		int trips;
		_tripsByEntity.TryGetValue(handle, out trips);
		trips++;
		_tripsByEntity[handle] = trips;
		string script;
		if (!_entityScripts.TryGetValue(handle, out script) || string.IsNullOrEmpty(script)) script = "unknown script";
		Warn("[Events] entity " + handle + " (" + script + ") hit the script execution limit (" +
			trips + "/" + LuaWatchdog.MaxTripsPerEntity + ")");

		if (trips < LuaWatchdog.MaxTripsPerEntity) return;

		_disabledEntities.Add(handle);
		LuaCall.CallVoidInt(_state, "__openlot_disableNet", handle);
		if (Router != null) Router.UnregisterEntity(handle);
		Warn("[Events] entity " + handle + " (" + script +
			"): handlers disabled after " + trips + " watchdog trips");
	}

	/// <summary>
	/// Runs one entity's captured onFrame as its own watchdog unit (design doc D15). Returns false
	/// when the entity no longer has a frame handler (destroyed or reloaded), so the caller can
	/// prune its list.
	/// </summary>
	public bool CallOnFrame(int handle, double delta)
	{
		ThrowIfDead();
		if (_disabledEntities.Contains(handle)) return true;

		bool handled;
		bool tripped;
		Watchdog?.ArmUnit(LuaWatchdog.DispatchInstructionBudget);
		try
		{
			handled = InvokeFrame(handle, delta);
		}
		finally
		{
			tripped = Watchdog != null && Watchdog.EndUnit();
		}
		if (tripped) HandleEventTrip(handle, "onFrame");

		if (MemoryGuard != null && MemoryGuard.ConsumeOomHit())
		{
			Warn("[Events] Lua out of memory in onFrame on entity " + handle + "; the lot VM will be rebuilt");
			OomRecreateRequested?.Invoke();
			return true;
		}
		return handled;
	}

	private bool InvokeFrame(int handle, double delta)
	{
		if (_state == null) return false;
		int top = _state.GetTop();
		try
		{
			_state.GetGlobal("__openlot_callFrame");
			_state.PushInteger(handle);
			_state.PushNumber(delta);
			KeraLua.LuaStatus status = _state.PCall(2, 1, 0);
			if (status != KeraLua.LuaStatus.OK)
			{
				string error = _state.ToString(-1, false);
				Warn("[Events] onFrame on entity " + handle + " errored: " + error);
				return true;
			}
			return _state.ToBoolean(-1);
		}
		finally
		{
			_state.SetTop(top);
		}
	}

	/// <summary>True when the entity's script declared onFrame (captured at load, design doc D15).</summary>
	public bool HasFrameHandler(int handle)
	{
		ThrowIfDead();
		return LuaCall.CallBoolInt(_state, "__openlot_hasFrame", handle);
	}

	/// <summary>
	/// Runs one scheduled timer callback as its own watchdog unit (milestone 3.8, design doc D2):
	/// one armed unit, the shared 3-trip breaker, the same OOM policy as net and events. Returns
	/// false when the callback no longer exists (the Lua-side registry lost it), so a repeating
	/// timer can be dropped.
	/// </summary>
	public bool CallTimer(int handle, int id, bool keep)
	{
		ThrowIfDead();
		if (_disabledEntities.Contains(handle)) return true;

		bool handled;
		bool tripped;
		Watchdog?.ArmUnit(LuaWatchdog.DispatchInstructionBudget);
		try
		{
			handled = InvokeTimer(handle, id, keep);
		}
		finally
		{
			tripped = Watchdog != null && Watchdog.EndUnit();
		}
		if (tripped) HandleEventTrip(handle, "timer");

		if (MemoryGuard != null && MemoryGuard.ConsumeOomHit())
		{
			Warn("[Events] Lua out of memory in a timer callback on entity " + handle +
				"; the lot VM will be rebuilt");
			OomRecreateRequested?.Invoke();
			return true;
		}
		return handled;
	}

	private bool InvokeTimer(int handle, int id, bool keep)
	{
		if (_state == null) return false;
		int top = _state.GetTop();
		try
		{
			_state.GetGlobal("__openlot_callTimer");
			_state.PushInteger(handle);
			_state.PushInteger(id);
			_state.PushBoolean(keep);
			KeraLua.LuaStatus status = _state.PCall(3, 1, 0);
			if (status != KeraLua.LuaStatus.OK)
			{
				string error = _state.ToString(-1, false);
				Warn("[Events] timer callback on entity " + handle + " errored: " + error);
				return true;
			}
			return _state.ToBoolean(-1);
		}
		finally
		{
			_state.SetTop(top);
		}
	}

	/// <summary>The script to blame for a trip on (handle, direction, name): the declaring script
	/// from the router registry when known, else the entity's registered script, else "unknown".</summary>
	private string TripScript(int handle, NetDirection direction, string name)
	{
		string script;
		if (Router != null && Router.TryGetHandlerScript(handle, direction, name, out script)
			&& !string.IsNullOrEmpty(script))
			return script;
		if (_entityScripts.TryGetValue(handle, out script) && !string.IsNullOrEmpty(script))
			return script;
		return "unknown script";
	}

	public bool IsEntityDisabled(int handle)
	{
		return _disabledEntities.Contains(handle);
	}

	/// <summary>
	/// Drops every trace of a destroyed entity from this VM and the router: Lua-side registries
	/// (declarations, disabled flag, script name), the C# maps and the router's declaration registry.
	/// One cleanup path shared by editor deletion and the F2 reload cleanup.
	/// </summary>
	public void ForgetEntity(int handle)
	{
		_tripsByEntity.Remove(handle);
		_disabledEntities.Remove(handle);
		_entityScripts.Remove(handle);
		if (Router != null) Router.UnregisterEntity(handle);
		if (_dead || _state == null) return; // nothing left to clear Lua-side
		LuaCall.CallVoidInt(_state, "__openlot_forgetEntity", handle);
	}

	/// <summary>
	/// Item 6: wipes every net registry so a (re)load starts from a clean slate — the per-entity
	/// trip/disable/script maps (C#), the router's declaration and unknown-handler state, and the
	/// Lua-side declaration/disabled/script registries. Without this a re-run in the SAME VM would
	/// still see the previous load's declarations (a stale redeclaration warning, a still-disabled
	/// entity, or a trip blamed on a script that is no longer responsible). A VM rebuild already
	/// hands us a fresh bridge; this is what makes an in-place reload (Test mode, §3.4) equivalent.
	/// </summary>
	public void ClearRegistries()
	{
		_tripsByEntity.Clear();
		_disabledEntities.Clear();
		_entityScripts.Clear();
		LoadOwnerHandle = -1;
		LoadOwnerScript = null;
		if (Router != null) Router.ClearRegistries();
		if (_dead || _state == null) return; // nothing left to clear Lua-side
		LuaCall.CallVoid(_state, "__openlot_resetRegistries");
	}

	/// <summary>
	/// Test-mode-ready entry point for player-originated input (item 4): calls made inside the
	/// action are marked as player origination, anything else stays autonomous (System). Real input
	/// handling in §3.4/§5.1 wraps its event handling the same way via the router scope.
	/// </summary>
	public void SimulatePlayerInteraction(Action action)
	{
		if (Router == null) { if (action != null) action(); return; }
		Router.BeginPlayerInteraction();
		try { if (action != null) action(); }
		finally { Router.EndPlayerInteraction(); }
	}

	private static string DirectionName(NetDirection direction)
	{
		return direction == NetDirection.Server ? "server" : "client";
	}

	private void Warn(string message)
	{
		// The one place router warnings pass through, so the Output window sees them without a
		// second call site per message. The assigned callback keeps its Godot-console line for
		// headless runs.
		LotLog.Warn("net", message);
		Action<string> warning = Warning;
		if (warning != null) warning(message);
	}
}
