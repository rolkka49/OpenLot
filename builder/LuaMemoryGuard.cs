using System;
using System.Runtime.InteropServices;
using KeraLua;

/// <summary>
/// Hard memory cap for the lot's Lua VM (design doc v4 A4, implementation rulings in the step-3
/// amendments). Installed via KeraLua's SetAllocFunction (lua_setallocf) — verified by spike E and
/// again in-project: allocation failure becomes an ordinary catchable Lua error ("not enough
/// memory"), NOT a native crash. This is the only mechanism that actually stops exponential
/// allocation (s = s .. s); the count-hook memory check from v3 was dropped for exactly that
/// reason.
///
/// This guard does NOT own the memory: at Install time it fetches Lua's OWN allocator with
/// GetAllocFunction (lua_getallocf) and delegates every request to it, only counting bytes. Every
/// block is therefore allocated and freed by the same allocator Lua would always have used — no
/// cross-heap mismatch (the reason Marshal.*HGlobal was unsafe on Windows), and no per-platform
/// P/Invoke of our own. The cap is enforced by refusing (returning NULL) a request that would push
/// the count past the limit; Lua's allocator is never asked to over-allocate.
///
/// Device rules (lua_Alloc contract):
///   - ptr == NULL means "allocate"; in that case Lua's osize argument is a TYPE TAG
///     (LUA_TSTRING/TABLE/FUNCTION/USERDATA/...), not the size of an existing block, so the old
///     size is treated as 0. Verified in-project: the values seen on alloc are all tiny tags.
///   - nsize == 0 means "free": delegate to Lua's allocator (it frees and returns NULL).
///   - The used-bytes counter tracks only what THIS guard counted; it clamps at 0 because blocks
///     allocated before Install are later freed with a size we never counted.
///   - Returning NULL sets OomHit. A script's pcall can swallow the Lua error, so owners MUST check
///     OomHit after every execution unit (LuaNetBridge does after each dispatch, LuaManager after
///     each chunk run) and rebuild the VM instead of trusting the error to have surfaced.
///   - The cap is approximate by the pre-install baseline (install immediately after the state is
///     created to keep that baseline small).
/// </summary>
public sealed class LuaMemoryGuard : IDisposable
{
	public const long MemoryCapBytes = 64L * 1024 * 1024;

	// The native allocator function pointer is taken from this delegate; Lua keeps calling it for
	// the life of the state, so the delegate must stay rooted (KeraLua holds no reference).
	private static LuaAlloc _pinnedAllocator;

	// Lua's own allocator + its opaque userdata, captured at Install time. Every request is
	// delegated to it, so memory is owned/freed by the same allocator Lua would have used anyway.
	private LuaAlloc _original;
	private IntPtr _originalUd;
	private bool _installed;

	private IntPtr _selfHandle;
	private long _capBytes;
	private long _used;
	private volatile bool _oomHit;
	private bool _disposed;
	private volatile bool _dead;

	public bool OomHit => _oomHit;
	public long UsedBytes => _used;

	/// <summary>True once the owning VM was torn down (see MarkDead).</summary>
	public bool IsDead => _dead;

	/// <summary>
	/// Marks the guard dead and releases the GCHandle the allocator receives. MUST run only AFTER
	/// the Lua state has been disposed: lua_close frees its remaining blocks through this allocator,
	/// so releasing the handle first would leave those frees without a guard (and a freed-handle
	/// lookup). Its reads are plain C# state and stay safe to call; Install refuses to run again.
	/// </summary>
	public void MarkDead()
	{
		_dead = true;
		Dispose();
	}

	public void Install(KeraLua.Lua state, long capBytes)
	{
		if (state == null) throw new ArgumentNullException(nameof(state));
		if (_dead) throw new InvalidOperationException("Lua VM was rebuilt: this memory guard belongs to a disposed VM");
		if (_installed) throw new InvalidOperationException("this memory guard is already installed on a Lua state");
		_capBytes = capBytes;
		// Fetch Lua's OWN allocator + userdata BEFORE installing ours, so every request can be
		// delegated to it (see the class remarks). Fetching after installing would return our own
		// wrapper and recurse; the _installed guard above makes that impossible.
		IntPtr originalUd = IntPtr.Zero;
		_original = state.GetAllocFunction(ref originalUd);
		if (_original == null)
			throw new InvalidOperationException("Lua returned no allocator to delegate to; cannot cap memory");
		_originalUd = originalUd;
		// The opaque userdata Lua hands back on every allocation is a GCHandle to this guard, so
		// two VMs (e.g. the lot VM and a test VM) each route to their own accounting.
		_selfHandle = GCHandle.ToIntPtr(GCHandle.Alloc(this));
		_pinnedAllocator = Allocate;
		IntPtr userData = _selfHandle;
		state.SetAllocFunction(_pinnedAllocator, ref userData);
		_installed = true;
	}

	/// <summary>Reads and clears the out-of-memory flag (an OOM is consumed once).</summary>
	public bool ConsumeOomHit()
	{
		bool hit = _oomHit;
		_oomHit = false;
		return hit;
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		if (_selfHandle != IntPtr.Zero)
		{
			GCHandle.FromIntPtr(_selfHandle).Free();
			_selfHandle = IntPtr.Zero;
		}
	}

	private static IntPtr Allocate(IntPtr userData, IntPtr ptr, UIntPtr osize, UIntPtr nsize)
	{
		LuaMemoryGuard guard = null;
		if (userData != IntPtr.Zero)
		{
			try
			{
				guard = GCHandle.FromIntPtr(userData).Target as LuaMemoryGuard;
			}
			catch (Exception)
			{
				// The handle was released (VM torn down). There is no guard to account for and no
				// allocator to delegate through, so fail safe: NULL reads as out-of-memory on an
				// allocation and as a successful free. (Reached only after lua_close, if ever.)
				guard = null;
			}
		}
		if (guard == null || guard._original == null) return IntPtr.Zero;

		long newSize = (long)nsize;
		// ptr == NULL is an allocation request; Lua's osize is a type tag there (LUA_TSTRING, ...),
		// never a size, so the old size is 0.
		long oldSize = ptr == IntPtr.Zero ? 0 : (long)osize;

		if (newSize == 0)
		{
			// Free: Lua's allocator releases the block and returns NULL (per the lua_Alloc contract).
			try { guard._original(guard._originalUd, ptr, osize, nsize); }
			catch (Exception) { return IntPtr.Zero; } // never unwind through native frames
			if (ptr != IntPtr.Zero) guard.AddUsed(-oldSize);
			return IntPtr.Zero;
		}

		// Cap check before touching the allocator, so an over-budget resize leaves the original
		// block intact. Lua turns the NULL into "not enough memory".
		if (guard._used - oldSize + newSize > guard._capBytes)
		{
			guard._oomHit = true;
			return IntPtr.Zero;
		}

		IntPtr result;
		try { result = guard._original(guard._originalUd, ptr, osize, nsize); }
		catch (Exception)
		{
			// Never let a managed exception unwind through native Lua frames.
			guard._oomHit = true;
			return IntPtr.Zero;
		}
		if (result == IntPtr.Zero)
		{
			guard._oomHit = true;
			return IntPtr.Zero;
		}
		guard.AddUsed(newSize - oldSize);
		return result;
	}

	/// <summary>Clamped at 0: pre-install blocks are freed through this allocator with a size we
	/// never counted, so the counter can legitimately go negative without the clamp.</summary>
	private void AddUsed(long delta)
	{
		long updated = _used + delta;
		_used = updated < 0 ? 0 : updated;
	}
}
