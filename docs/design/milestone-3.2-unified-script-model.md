# Design index — Milestone 3.2: unified per-entity script model (`net.*`)

**Status:** implemented for the offline / single-player path. Real peer-to-peer transport is §7 work;
nothing here changes when it lands (the transport is a seam, see §4).
**Creator-facing companion doc:** [`docs/net-api.md`](../net-api.md)
**Verified by:** `NetSelfTest` (169 fast checks, all on private Lua states; an opt-in 8-check heavy
set runs with `OPENLOT_HEAVY_TESTS`) plus `GizmoSelfTest` (171 checks), run headless in
`BuilderScene._Ready` under `#if DEBUG`.

---

## 1. Decision record

Settled design calls — change these only with a deliberate revision, because creator-facing behaviour
depends on them.

| # | Decision | Where it lives |
|---|---|---|
| D1 | Each entity gets its own `net` table built by `makeNet(handle)`; the handle is captured in the closures. Router dispatch is keyed by `(handle, direction, name)`. | `LuaBootstrap.Chunk` (`makeNet`, `makeNamespace`, `__openlot_dispatch`) |
| D2 | Reads from `net.server`/`net.client` **always** return a routing closure; raw handler bodies stay in a hidden registry and are reachable only by the dispatcher. Nothing is executed without going through the router. | same |
| D3 | Exactly **two** bound methods form the whole Lua→C# net surface: `NetRegister` (declaration record) and `NetInvoke` (the single call funnel). Both are registered as globals but deliberately excluded from the `Lot` table. | `LotLuaApi`, `LuaManager.Initialize` |
| D4 | **Uniform queueing:** every call — host-local, loopback, or received — is enqueued and dispatched only from `NetRouter.Flush()`, once per frame. No synchronous paths; recursion grows the queue, never the stack. | `NetRouter`, `ScriptRuntime.Tick` |
| D5 | **Authority:** `net.server` bodies run only on the host. On receipt from a remote peer the router overwrites `sender` (from transport metadata) and forces `origin = Player` — only the host originates `System` calls. | `NetRouter.OnMessageReceived` |
| D5a | **Origination (item 4/C):** locally initiated calls are **System** in ALL modes, including loopback — lot load, `start()`, timers and autonomous logic. **Player** only while an explicit interaction scope is open (`NetRouter.BeginPlayerInteraction`/`EndPlayerInteraction`, depth-counted; input handling opens it). Test mode uses `LuaNetBridge.SimulatePlayerInteraction(action)`. The transport no longer supplies a default origin (the old `INetTransport.DefaultLocalOrigin` is gone). | `NetRouter.InvokeLocal` |
| D6 | **Transitive identity:** calls initiated while a dispatch is running inherit that dispatch's sender/origin (`NetRouter` dispatch context). | `NetRouter.InvokeLocal`/`Dispatch` |
| D7 | `net.sender` = real peer id always (loopback = 1); `net.fromPlayer` = player-vs-system origin. Both `nil`/`false` outside a handler, cleared from **both** Lua and C# (the C# clear runs even when a watchdog trip skips Lua cleanup). | `LuaBootstrap.__openlot_dispatch`, `LuaWatchdog.EndUnit` |
| D8 | **Fire-and-forget:** return values do not cross. | `NetRouter.InvokeLocal` |
| D9 | **Validation is Lua-side** (`__openlot_validate`, iterating with `next`, tables-as-keys for exact identity) because wrapper equality in C# is unreliable (`LuaBase.GetHashCode` is per-wrapper). C# keeps depth/node/byte backstops enforced incrementally. | `LuaBootstrap`, `NetSerializer` |
| D10 | **Watchdog:** Lua-installed count hook (managed exceptions must never unwind through native frames). 200k instructions/dispatch, 1M per script load, 4M per frame (each script LOAD begins its own frame accounting, so a reload with many scripts cannot add their counts together; dispatch keeps the frame budget `ScriptRuntime.Tick` begins), 10k-interval granularity. After a trip the hook is re-armed with count 1 and `pcall`/`xpcall` re-throw, so a trip cannot be swallowed; the hook is always cleared **from C#** first. | `LuaBootstrap`, `LuaWatchdog`, `LuaNetBridge` |
| D11 | **Circuit breaker:** 3 trips disable that entity's net handlers for the session, warning with the entity and script file name. | `LuaNetBridge.HandleTrip` |
| D12 | **Memory:** hard cap via `lua_setallocf` (64 MB). `ptr == NULL` ⇒ `osize` is a type tag ⇒ old size 0; used-bytes clamps at 0; OOM sets a flag checked after every dispatch because a script `pcall` can swallow the error; the VM is rebuilt at a frame boundary, never mid-dispatch. | `LuaMemoryGuard`, `LuaNetBridge`, `LuaManager` |
| D13 | **Handles:** lot root = 0 (reserved); every loaded lot node gets a handle in deterministic tree order (option A — needed so one script can address another entity); objects spawned later get handles from the Lua spawn path; negative handles are reserved for client-local visuals and rejected from the wire. | `BuilderScene`, `NetRouter` |
| D14 | **Events ≠ state:** `net.*` is transient; current world state belongs to §7 sync (late joiners miss events by design). | `docs/net-api.md` §8 |
| D15 | **Sandbox:** default-deny whitelist, `__metatable = false` on every shared table (env, string metatable, libraries, `net`), `rawset` removed, `setmetatable` rejects `__gc`, `string.rep` capped and routed through the sealed string metatable, all dangerous globals stripped (`io/os/require/…/luanet`), `_G` aliases the script's own env. | `LuaBootstrap.Chunk` |
| D16 | **Cross-entity calls** are creator-facing sugar (`Lot.CallServer`/`Lot.CallClient`) that shadows the raw table-taking bound methods; packing + validation happen in Lua (C# `params` marshaling would mangle `nil` holes). | `LuaBootstrap`, `LotLuaApi`, `LuaNetBridge` |

## 2. File map

| File | Responsibility |
|---|---|
| `builder/LuaBootstrap.cs` | The Lua chunk: sandbox, `makeNet` + `netRegistry`, dispatcher, validation pre-pass, watchdog hook, env loader, cross-entity sugar. Plus the `LuaCall` push/PCall helper (fetch-by-name, stack restored in `finally`). |
| `builder/LuaWatchdog.cs` | Budget consts, `ArmUnit`/`EndUnit` (clears the native hook first), `BeginFrame`, armed-state queries. |
| `builder/LuaMemoryGuard.cs` | `SetAllocFunction` allocator with the hard byte cap and the `OomHit` flag. |
| `builder/LuaNetBridge.cs` | `IDispatchTarget` over the raw stack; outbound `InvokeFromLua`/`InvokeCrossEntity`; `LoadEntityScript` (env → chunk → `start()`); trip counting / circuit breaker; script-name registry; OOM checks; deferred-recreate callback. |
| `builder/NetRouter.cs` | Transport-agnostic router: envelope codec, uniform queue, drain budget + frame backstop, rate limits, sender/origin authority, transitive context, declaration registry (redeclaration warning). |
| `builder/NetTransport.cs` | `INetTransport` seam + `LoopbackTransport` (`IsHost`, peer 1, Player origin). |
| `builder/NetSerializer.cs` | Argument wire codec (tags, table array/pair sections, budgets) + stack-based decoder; wrapper disposal. |
| `builder/ScriptRuntime.cs` | Entity/script discovery (`LotScriptNode`), per-script envs, handle assignment order, lot-root-at-load, frame wiring (`Tick`), teardown (`Stop`). |
| `builder/LuaManager.cs` | VM lifecycle: guard install, API registration, bootstrap application, bridge/watchdog wiring, scratch-path `RunString` (watchdog-armed), deferred rebuild. |
| `builder/BuilderScene.cs` | Lot world + handle registry, lot-root handle 0, load-order handles, script loading in `_Ready`, `_Process` tick, `_ExitTree` teardown order. |
| `builder/NetSelfTest.cs` | All of the above, verified (serializer, router, sandbox, watchdog, allocator, script runtime, cross-entity, live-scene wiring). |
| `docs/net-api.md` | Creator-facing reference. |

## 3. Execution flow

**VM start-up (once per lot, `BuilderScene._Ready`)**

1. `new NLua.Lua()` → `LuaMemoryGuard.Install` **immediately** (keeps the uncounted baseline small).
2. Reflect `LotLuaApi` public methods into globals; build the `Lot` table (skipping `NetInvoke` /
   `NetRegister`).
3. `LuaBootstrap.Apply` — captures privileged upvalues, strips dangerous globals, builds the sandbox,
   the watchdog hook, `makeNet`, the dispatcher and the env loader.
4. Scratch-path helpers (`cube`/`sphere`/`cylinder`/`log` globals) for the command line / code editor.
5. `LuaWatchdog` + `LuaNetBridge` + `LoopbackTransport` + `NetRouter` wired; `LotLuaApi.Net` set.

**Lot load**

6. Lot root registered at handle 0 → `AssignLoadOrderHandles()` walks `LotRoot` then `LotUIRoot`.
7. `ScriptRuntime.LoadLot` runs the lot-root script first (handle 0), then each entity's scripts in
   tree order. Per script: `SetEntityScript` → read the file → `LoadEntityScript` (env → chunk →
   `start()`), one watchdog unit at the script budget. Failures (syntax, runtime, limit, OOM,
   unreadable file) are reported, never fatal.

**Frame (`BuilderScene._Process` → `ScriptRuntime.Tick`)**

8. `LuaWatchdog.BeginFrame()` → `NetRouter.Flush()` (≤256 envelopes, 8 ms backstop) →
   `LuaManager.ProcessDeferredRecreate()`.

**A call's path**

9. `net.server.Foo(a, b)` → routing closure → packs `{ n = count, ... }` → `__openlot_validate`
   (types, metatables, cycles, NaN, keys) → `NetInvoke(handle, "server", "Foo", packed)`.
10. `LotLuaApi.NetInvoke` → `LuaNetBridge.InvokeFromLua` → `NetSerializer.EncodeArgs` → dispose the
    packed table → `NetRouter.InvokeLocal(targetHandle, …)` → enqueue (or `SendToHost` when a client).
11. Receipt (from another peer): rate limit → size → envelope decode → negative-handle check →
    **overwrite sender/origin** → queue.
12. Drain: `LuaNetBridge.TryDispatch` → arm the dispatch budget → decode args **onto the Lua stack**
    (no retained wrappers) → `PCall(__openlot_dispatch, …)` → handler body with `net.sender` /
    `net.fromPlayer` set → clear the hook and dispatch context → trip/OOM bookkeeping.

**Teardown (`_ExitTree`)**

13. `ScriptRuntime.Stop()` → `LuaManager.Shutdown()` (bridge detach → state dispose → guard dispose —
    in that order, because the native allocator outlives every Lua allocation).

## 4. Seams for later milestones

* **§7 networking:** implement `INetTransport` (ENet/P2P) and swap it in — the router, queue,
  rate limits, authority rules and all creator code stay unchanged. Host identity comes from the
  transport, never a constant.
* **§3.4 Test/Game mode:** `LoopbackTransport` is already the offline answer; wiring Test mode means
  starting/stopping the runtime around the mode switch and calling `Tick` from the play loop.
* **§7 state sync:** add the snapshot channel; the event/state split is already documented (D14).
* **§3.4 capsule character:** a lot-root script spawning a capsule needs only `Lot.Spawn*` plus the
  controller API — the load-time root script path already exists (D1/D13).

## 5. Verification

* `NetSelfTest` (169 fast checks) + `GizmoSelfTest` (171 checks), all green, `0` failures, run
  error-clean. The whole self-test suite runs on **private Lua states only** (never the lot VM) and
  the fast set prints its wall-clock. A separate **heavy set** (20k-encode loop, 8 MB out-of-memory,
  ~10 MB allocation) is opt-in via the `OPENLOT_HEAVY_TESTS` environment variable, so it stays off
  the always-on path.
* Suites: serializer (round-trips, budgets, malformed payloads, stack hygiene, wrapper-disposal
  flat-growth), router (ordering, recursion bound, queue cap, token buckets on an injected clock,
  sender/origin overwrite, negative handles, malformed envelopes, redeclaration, transitivity),
  bootstrap sandbox (metatable sealing, read-only libraries, `rawset` absence, `__gc` rejection,
  `string.rep` caps, cross-env isolation), watchdog (plain and `pcall`-shielded loops, budget wiring,
  frame budget, circuit breaker naming), allocator (exponential growth capped + flag + fresh VM),
  script runtime (lot-root at load, `start()`, error reporting, per-script envs, handles in walk
  order), cross-entity (button → door, `CallClient`, validation, unknown target), live wiring.
* `NetIntegrationTest` (59 checks) drives the REAL `LuaManager` + `ScriptRuntime` against the live
  (it also owns the live-VM checks relocated from `NetSelfTest`: the command-line/code-editor scratch
  path and live `Lot.GetHandle` wiring)
  lot scene with real script files under `user://Scripts`: frame budget across a reload (calibrated by
  **measuring** instruction counts via `__openlot_unitUsed`, never by estimating), load-time OOM
  quarantine + rebuild limiter, and post-rebuild bridge/script recovery. It runs a few physics frames
  after `_Ready` (see limitation 11) and prints
  `[NetIntegrationTest] integration suite complete: N checks, M failure(s).`; if it aborts it prints
  `integration suite INCOMPLETE …` and quits with exit code 1 so a headless run cannot look green
  while testing nothing. BuilderScene warns if the run ends before the suite ever ran.
* **How to run headless** (mono editor is required; the plain Arch `godot` build cannot load C#
  scripts, and the native Lua library is not packaged yet) — capture BOTH streams (`> file 2>&1`):
  Godot prints `PrintErr` lines (including this suite's FAILs) on stderr, so a stdout-only capture
  hides them:

```bash
cd /home/rolkka/openlot
LD_LIBRARY_PATH=/tmp/nluaspike/native \
  ~/Atsiuntimai/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64 \
  --headless --path . res://builder/builder.tscn --quit-after 5 2>&1 | grep SelfTest
```

## 6. Known limitations & deferred items

**Capability gaps that affect creators today**

1. **Scripts run at lot load only.** Attaching or editing a script while in build mode does not run it
   until the lot reloads. Hot re-attach belongs with §3.4 (Test mode) — the reload semantics are the
   reason re-declaration is silent for the same file (D-records in `NetRouter.RegisterHandler`).
2. **Editor-spawned objects have no handle until something registers them.** Option A covers
   everything present at lot load; a node created afterwards (toolbox spawn, duplicate) is addressed
   by Lua only once the spawn path registers it — i.e. Lua-created objects always have handles, but
   editor-created-after-load objects may not. Revisit with §3.4/§8 (the `.lot` format should also
   persist the next-handle watermark).
3. **Coroutines are unavailable** inside scripts (they would escape the per-coroutine count hook).
   Reintroducing them means wrapping `coroutine` with per-thread budgets.
4. **Single-builtin blocking calls cannot be preempted.** The watchdog counts Lua instructions; a
   pathological `string.find` pattern still blocks the frame. `string.rep` is capped; the general
   fix (custom allocator hard-fail is in place, off-thread state is not) is post-MLP hardening.
5. **`nan`-style precision:** numbers cross as doubles (documented); values above 2^53 lose bits.
6. **VM rebuild loses script state:** an OOM rebuild reloads scripts at the frame boundary; in-memory
   script globals are lost (documented rather than hidden).
7. **No state sync yet (D14):** late joiners miss events *and* world state until §7.

**Packaging / environment notes**

8. **Native `lua54` is not packaged.** `KeraLua` P/Invokes `lua54`; nothing in the repo ships it, so
   Lua only runs when a compatible `liblua54.so` is on the library path (the headless command above
   points `LD_LIBRARY_PATH` at a symlink to the system `liblua5.4.so`). This must be resolved before
   Test mode ships (bundle the native library per platform and load it explicitly; `LuaManager`
   degrades to "runtime unavailable" when it is missing, so the failure is visible, not silent).
9. **Headless runs need the mono Godot build** (`godot` from Arch is not .NET-enabled) and the
   project's tests were verified on the .NET 10 runtime with net8.0-targeted NLua assemblies.
10. **Verification gaps:** the `LotLuaApi` forwarding hop for the net methods is exercised by
    inspection rather than an automated check (the self-test binds the same shapes off the bridge);
    interactive play (ImGui build mode with scripts present) has not been exercised, only headless.
11. **Physics smoke-test timing sensitivity (F4, deferred — do NOT fix by weakening the assertion).**
    `GizmoSelfTest`'s physics drag check (`A part resting on a surface still slides along it`) depends
    on how many physics steps elapse during startup: heavy DEBUG work in `_Ready` flips it (control-run
    verified: integration suite off ⇒ pass, on ⇒ fail). The integration suite therefore runs a few
    physics frames later. The proper fix is to make those smoke assertions step-count based instead of
    timing sensitive — scheduled separately.
12. **Load-time spawns are owned; handler spawns are not (F2, implemented).** A node spawned while
    a script's initial load runs (chunk + `start()`) is tagged `openlot_spawn_owner` /
    `openlot_spawn_owner_script` by `LotLuaApi`, and a reload removes owned nodes (recursively,
    through the single `BuilderScene.DestroyEntity` path, which forgets handles and clears the
    VM/router entries) before re-running scripts — so spawning the capsule/camera from a root script
    does not duplicate them. **Handler-spawned entities are deliberately unowned**: they are runtime
    state, so after a rebuild they remain but the scripts that would drive them are gone; re-create
    them from the load path if they matter. Subtlety: cleanup uses `QueueFree`, so for the remainder
    of that frame the old nodes still exist next to the new ones (the tests count
    "not queued for deletion" accordingly).
13. **Suspension is terminal for the session and visible in the editor.** Past the rebuild limiter,
    scripting stays disabled until the lot is reloaded (by design). It shows as a red
    **"SCRIPTING SUSPENDED — <reason>"** banner in the **Viewport** window (above the 3D image),
    plus one error-level log line; `LuaManager.SuspensionReason` carries the text for tests/tools.
14. **Quarantine is content-keyed.** An OOM-at-load script is skipped only while its file is
    unchanged; editing the file lifts the quarantine on the next reload (FNV-1a fingerprint of the
    source).
