using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// Per-entity script model (Milestone 3.2, step 4; rebuild-safe since the 3.2 review):
/// discovers the lot's <see cref="LotScriptNode"/> placeholders and runs each one in its own
/// sandbox environment.
///
/// Rules implemented here (design doc §1 / decision 1):
///   - An entity is any lot node with a LotScriptNode directly under it. A script directly under
///     the lot root belongs to the LOT ROOT entity, which is handle 0 and runs at lot load.
///   - Each script gets its OWN env; two scripts on one entity cannot collide on globals, and if
///     they declare the same net.server name the router's redeclaration warning names both files.
///   - Entity handles are the node's existing registry handle, or a fresh one assigned during the
///     deterministic tree walk (§9 static handles).
///   - The chunk runs first, then its start() if defined, as ONE watchdog unit under the script
///     instruction budget.
///
/// VM-rebuild safety: the bridge is never cached as a stable dependency — it is resolved through
/// <see cref="Func{TResult}"/> on every tick and load. When the VM is rebuilt (deferred OOM
/// recovery) the resolved bridge changes; the runtime notices, warns, and reloads every script
/// into the fresh VM, because envs and net declarations died with the old state.
/// </summary>
public sealed class ScriptRuntime
{
	/// <summary>Reserved handle of the lot root entity itself (design doc §1).</summary>
	public const int LotRootHandle = 0;

	private readonly Func<LuaNetBridge> _bridgeResolver;
	private readonly Action<string> _warn;
	private readonly Action _afterFrame;
	private readonly Func<bool> _isSuspended;
	private readonly List<string> _loaded = new List<string>();

	// Scripts that failed to load because the VM ran out of memory: key -> fingerprint of the code
	// that failed. Kept HERE (C#, not in the VM) so the record survives VM rebuilds — this object is
	// not rebuilt. A quarantined script is skipped on later loads ONLY while its file is unchanged;
	// editing the file lifts the quarantine on the next reload. Cleared by Stop (a fresh lot).
	private readonly Dictionary<string, long> _quarantined = new Dictionary<string, long>();

	// The last LoadLot arguments, kept so scripts can be reloaded after a VM rebuild.
	private Node _lotRoot;
	private Func<Node, int> _ensureHandle;
	private Func<string, string> _readScript;
	private Action<Node> _destroyEntity;
	private LuaNetBridge _lastBridge;

	public int LoadedScriptCount => _loaded.Count;

	/// <param name="bridgeResolver">Resolves the CURRENT net bridge (production: the one
	/// LuaManager owns right now). A resolver rather than a reference, because an OOM rebuild
	/// replaces the bridge and every stale reference would be dead.</param>
	/// <param name="afterFrame">Frame-boundary hook (LuaManager.ProcessDeferredRecreate in the
	/// game) so a deferred VM rebuild happens after dispatch, never during it.</param>
	/// <param name="isSuspended">True when the rebuild limiter has suspended scripting: the runtime
	/// then stops ticking and never reloads (the lot needs a reload to recover).</param>
	public ScriptRuntime(Func<LuaNetBridge> bridgeResolver, Action<string> warn, Action afterFrame = null,
		Func<bool> isSuspended = null)
	{
		_bridgeResolver = bridgeResolver;
		_warn = warn;
		_afterFrame = afterFrame;
		_isSuspended = isSuspended;
	}

	/// <summary>
	/// Loads every script in the lot: the lot-root entity's scripts first (handle 0), then each
	/// entity in tree order. <paramref name="ensureHandle"/> returns the entity's stable handle;
	/// <paramref name="readScript"/> returns the file contents or null when unreadable. The
	/// arguments are remembered so a VM rebuild can reload the same scripts.
	/// </summary>
	public void LoadLot(Node lotRoot, Func<Node, int> ensureHandle, Func<string, string> readScript,
		Action<Node> destroyEntity = null)
	{
		_lotRoot = lotRoot;
		_ensureHandle = ensureHandle;
		_readScript = readScript;
		_destroyEntity = destroyEntity;
		_lastBridge = Resolve();
		LoadIntoBridge();
	}

	/// <summary>
	/// Re-runs the previously loaded lot in the current VM. Called automatically when a rebuild is
	/// detected; also the entry point Test mode (§3.4) will use for an explicit restart.
	/// </summary>
	public bool ReloadScripts()
	{
		if (Suspended) return false;
		LuaNetBridge bridge = Resolve();
		if (bridge == null) return false;
		_lastBridge = bridge;
		LoadIntoBridge();
		return true;
	}

	private bool Suspended => _isSuspended != null && _isSuspended();

	private void LoadIntoBridge()
	{
		_loaded.Clear();
		if (_lotRoot == null || !GodotObject.IsInstanceValid(_lotRoot)) return; // lot gone: nothing to load
		LuaNetBridge bridge = Resolve();
		if (bridge == null) return;
		// Item 6: every load is a clean slate — clear the declaration/trip/disabled/script registries
		// so a re-run cannot see the previous load's net state (stale redeclaration warnings, a
		// still-disabled entity, or a trip blamed on the wrong script).
		bridge.ClearRegistries();
		// F2: every load is a restart, so the entities the previous load spawned are removed first —
		// that is what keeps a root script spawning the capsule/camera from duplicating them.
		CleanupOwnedSpawns();
		LoadScriptsUnder(bridge, _lotRoot, LotRootHandle, _readScript);
		WalkEntities(bridge, _lotRoot, _ensureHandle, _readScript);
	}

	/// <summary>
	/// Deletes every node tagged as a LOAD-TIME spawn (LotLuaApi.LoadSpawnOwnerMeta) before scripts
	/// re-run, recursively (a spawned subtree goes whole) and through the single destroy path the
	/// owner supplies, which forgets handles and clears the VM/router entries for them.
	/// </summary>
	private void CleanupOwnedSpawns()
	{
		List<Node> owned = new List<Node>();
		CollectOwnedSpawns(_lotRoot, owned);
		if (owned.Count == 0) return;
		if (_destroyEntity == null)
		{
			Warn("[Lua] " + owned.Count + " load-time spawn(s) cannot be cleaned up: no entity-destroy hook wired");
			return;
		}
		for (int i = 0; i < owned.Count; i++)
		{
			Node node = owned[i];
			if (node == null || !GodotObject.IsInstanceValid(node)) continue;
			Warn("[Lua] removing " + node.Name + " (spawned at load; restarting scripts)");
			_destroyEntity(node);
		}
	}

	private static void CollectOwnedSpawns(Node parent, List<Node> owned)
	{
		int count = parent.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			Node child = parent.GetChild(i);
			if (child.HasMeta(LotLuaApi.LoadSpawnOwnerMeta))
			{
				owned.Add(child); // the subtree is deleted with it, so do not descend
				continue;
			}
			CollectOwnedSpawns(child, owned);
		}
	}

	/// <summary>
	/// One lot frame: pick up a VM rebuild if one happened, start the frame's instruction
	/// accounting, drain the net queue, then let the owner process a deferred VM rebuild. Called
	/// from BuilderScene._Process.
	/// </summary>
	public void Tick()
	{
		// The rebuild limiter can suspend scripting (a script that OOMs every load or every call):
		// then nothing ticks, nothing reloads, and the lot needs a reload to recover.
		if (Suspended) return;

		LuaNetBridge bridge = Resolve();
		if (bridge == null) return;

		if (!ReferenceEquals(bridge, _lastBridge))
			HandleRebuild(bridge);

		bridge.BeginFrame();
		if (bridge.Router != null) bridge.Router.Flush();
		if (_afterFrame != null) _afterFrame();

		// The after-frame hook is where a deferred OOM rebuild happens. Pick the new VM up in the
		// same tick rather than spending a frame with no handlers loaded.
		LuaNetBridge after = Resolve();
		if (after != null && !ReferenceEquals(after, bridge))
			HandleRebuild(after);
	}

	private void HandleRebuild(LuaNetBridge bridge)
	{
		_lastBridge = bridge;
		Warn("[Lua] Lua VM was rebuilt; reloading " + _loaded.Count + " lot script(s)");
		ReloadScripts();
	}

	private LuaNetBridge Resolve()
	{
		return _bridgeResolver != null ? _bridgeResolver() : null;
	}

	private void WalkEntities(LuaNetBridge bridge, Node parent, Func<Node, int> ensureHandle, Func<string, string> readScript)
	{
		int count = parent.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			Node child = parent.GetChild(i);
			if (child is LotScriptNode) continue; // owned by this parent's pass
			// Internal implementation children (outline hulls, collision bodies) are not entities.
			if (child.HasMeta(LotObject.InternalChildMeta)) continue;

			if (HasScript(child))
				LoadScriptsUnder(bridge, child, ensureHandle(child), readScript);
			WalkEntities(bridge, child, ensureHandle, readScript);
		}
	}

	private static bool HasScript(Node entity)
	{
		int count = entity.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			if (entity.GetChild(i) is LotScriptNode) return true;
		}
		return false;
	}

	private void LoadScriptsUnder(LuaNetBridge bridge, Node entity, int handle, Func<string, string> readScript)
	{
		int count = entity.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			LotScriptNode script = entity.GetChild(i) as LotScriptNode;
			if (script == null) continue;

			string displayName = !string.IsNullOrEmpty(script.DisplayName)
				? script.DisplayName
				: System.IO.Path.GetFileName(script.ScriptPath);
			// Registered before running so a trip inside this script names the file (circuit-breaker
			// warning) rather than "unknown script".
			bridge.SetEntityScript(handle, displayName);

			string code = readScript(script.ScriptPath);
			if (code == null)
			{
				Warn("[Lua] " + displayName + ": script file could not be read (" + script.ScriptPath + ")");
				continue;
			}

			// Quarantine is keyed to the CONTENT that ran out of memory: fixing the file lifts it on
			// the next reload instead of punishing the entity forever.
			string quarantineKey = handle + "|" + displayName;
			long fingerprint = Fingerprint(code);
			long failing;
			if (_quarantined.TryGetValue(quarantineKey, out failing))
			{
				if (failing == fingerprint)
				{
					Warn("[Lua] " + displayName + ": skipped (quarantined after running out of memory; file unchanged)");
					continue;
				}
				_quarantined.Remove(quarantineKey);
				Warn("[Lua] " + displayName + ": quarantine LIFTED (the script changed since it ran out of memory)");
			}

			string error;
			ScriptLoadResult result = bridge.LoadEntityScript(handle, code, displayName, out error);
			if (result == ScriptLoadResult.Loaded)
			{
				_loaded.Add(displayName);
			}
			else if (result == ScriptLoadResult.OutOfMemory)
			{
				_quarantined[quarantineKey] = fingerprint;
				Warn("[Lua] " + displayName + ": QUARANTINED after running out of memory while loading; " +
					"it will be skipped until the lot is reloaded (stops the rebuild loop)");
			}
			else
			{
				Warn("[Lua] " + displayName + " failed: " + error);
			}
		}
	}

	/// <summary>Drops the loaded-script bookkeeping and the load context, so a later rebuild does
	/// not reload a lot that is closing. The VM itself is torn down by LuaManager
	/// (BuilderScene._ExitTree), so this is the script-layer half of a lot closing.</summary>
	public void Stop()
	{
		_loaded.Clear();
		_quarantined.Clear(); // fresh lot: quarantine is per-lot state
		_lotRoot = null;
		_ensureHandle = null;
		_readScript = null;
		_destroyEntity = null;
		_lastBridge = null;
	}

	/// <summary>FNV-1a over the script text: a stable content fingerprint for quarantine (no
	/// dependency, no allocations beyond the loop).</summary>
	private static long Fingerprint(string code)
	{
		unchecked
		{
			long hash = unchecked((long)14695981039346656037UL);
			for (int i = 0; i < code.Length; i++)
			{
				hash ^= code[i];
				hash *= 1099511628211L;
			}
			return hash;
		}
	}

	private void Warn(string message)
	{
		Action<string> warn = _warn;
		if (warn != null) warn(message);
	}
}
