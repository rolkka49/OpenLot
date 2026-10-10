using System;
using KeraLua;

/// <summary>
/// Instruction-count watchdog for the lot's Lua VM (design doc v4 A1 + step-3 amendments).
/// The hook itself is installed FROM LUA (bootstrap chunk) because error() raised inside a
/// Lua-installed count hook unwinds through Lua's own machinery — throwing a managed exception
/// through native frames risks killing the process (spike D verified the Lua-side pattern).
/// C# only ever arms a budget, and clears the hook with lua_sethook(NULL) afterwards.
///
/// Budget model:
///   - Each execution unit (one net dispatch, one chunk run, one start()) is armed with a budget.
///   - A per-frame total budget spans all units in a frame (owner calls BeginFrame once per frame).
///   - The Lua hook counts in fixed intervals, so budgets are effective to the interval's
///     granularity (WATCHDOG_INTERVAL in the bootstrap chunk).
///   - After a trip the hook is re-armed with count 1, so every subsequent instruction throws —
///     creator pcall cannot swallow a trip (bootstrap also wraps pcall/xpcall). Clearing the hook
///     therefore MUST happen from C# before any Lua call is made again: EndUnit() does that first.
/// </summary>
public sealed class LuaWatchdog
{
	public const int DispatchInstructionBudget = 200_000;
	public const int ScriptInstructionBudget = 1_000_000;   // initial chunk run and start()
	public const int FrameInstructionBudget = 4_000_000;    // total across all dispatches in a frame
	public const int MaxTripsPerEntity = 3;                 // then the entity's net handlers are disabled

	private KeraLua.Lua _state;
	private bool _dead;

	public LuaWatchdog(NLua.Lua state)
	{
		_state = state.State;
	}

	/// <summary>True once the owning VM was torn down (see MarkDead).</summary>
	public bool IsDead => _dead;

	/// <summary>
	/// Marks this watchdog dead: the VM behind it has been disposed, so its native handle points at
	/// freed memory. Every entry point then throws a clear managed exception instead of calling into
	/// lua_* on that memory (the stale-bridge crash of the milestone-3.2 review).
	/// </summary>
	public void MarkDead()
	{
		_dead = true;
		_state = null;
	}

	private void ThrowIfDead()
	{
		if (_dead || _state == null)
			throw new InvalidOperationException("Lua VM was rebuilt: this watchdog belongs to a disposed VM");
	}

	/// <summary>Starts a new frame's instruction accounting (owner calls before router.Flush()).</summary>
	public void BeginFrame()
	{
		ThrowIfDead();
		LuaCall.CallVoidInt(_state, "__openlot_beginFrame", FrameInstructionBudget);
	}

	/// <summary>Arms the count hook for one execution unit.</summary>
	public void ArmUnit(int instructionBudget)
	{
		ThrowIfDead();
		LuaCall.CallVoidInt(_state, "__openlot_armWatchdog", instructionBudget);
	}

	/// <summary>
	/// Ends an execution unit: clears the native hook FIRST (after a trip the count-1 hook throws
	/// on any Lua instruction, including the cleanup calls themselves), then resets the Lua-side
	/// trip flag and dispatch context. Returns true when this unit tripped the watchdog.
	/// </summary>
	public bool EndUnit()
	{
		ThrowIfDead();
		_state.SetHook(null, LuaHookMask.Disabled, 0);
		bool tripped = LuaCall.CallBool(_state, "__openlot_takeTripped");
		LuaCall.CallVoid(_state, "__openlot_resetDispatch");
		return tripped;
	}

	/// <summary>Budget the Lua side currently holds — NetSelfTest pins the const wiring with it.</summary>
	public int ArmedBudget()
	{
		ThrowIfDead();
		return (int)LuaCall.CallNumber(_state, "__openlot_unitBudget");
	}

	public bool IsArmed()
	{
		ThrowIfDead();
		return LuaCall.CallBool(_state, "__openlot_isArmed");
	}

	/// <summary>Instructions consumed by the current/last execution unit (hook-interval granularity).
	/// Measurement hook for the self-tests' heavy-script calibration.</summary>
	public int UsedInstructions()
	{
		ThrowIfDead();
		return (int)LuaCall.CallNumber(_state, "__openlot_unitUsed");
	}
}
