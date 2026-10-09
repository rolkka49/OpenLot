# Milestone 3.7 — Event system (script-subscribed events + `onFrame`)

**Status:** implemented (physics-side half) — see §7 for the blocked/deferred list.
**Audience:** whoever maintains the scripting layer; the creator-facing half is `docs/event-api.md`.

This is the decision record for milestone 3.7, written before the code at the same bar as
`milestone-3.2-unified-script-model.md`. It covers the registration sugar, touch semantics, the
delivery spine, the cleanup contract, the attribution rules and the deliberate exclusions.

---

## 1. What the creator gets

```lua
-- A part's script. `Subscribe` is per-env (see D2); it returns a cancel token.
local token = Subscribe(this.Handle, "touched", function(other, player)
    -- `other`  = the other entity's handle (a number)
    -- `player` = the originating peer id when the touch came from the player's own
    --            character, nil for a physics-caused touch
end)

Subscribe(this.Handle, "touchEnded", function(other) ... end)

-- Every script may define this like start(): it runs once per frame in a session.
function onFrame(delta) ... end
```

Nothing else is public. There is no event table, no manual registration ids, no `if isServer`
branching — the same shape `net.*` already has.

---

## 2. Decision table

| # | Decision | Where it lives |
|---|---|---|
| D1 | **Session-gated.** `touched`/`touchEnded` and `onFrame` fire only while a player session is active (Test or Game mode). Sensors are session artifacts (built on entry, torn down on exit), like §3.6's joints. Rationale: touch deliveries are physics-driven and simulated interaction only exists in a session; build-mode editor drags must not fire touch storms, and per-frame script motion must not fight the gizmo/undo. Matches the acceptance wording "runs every frame and stops at session exit". | `BuilderScene.EnterTestMode/ExitTestMode`, `ScriptRuntime.Tick` |
| D2 | **`Subscribe(subject, eventName, fn)` is per-env**, created in `__openlot_newEnv` exactly like `net` and `this` — NOT a method on the shared read-only `Lot` table, because that one proxy cannot know which entity is calling. The subscriber identity is the env's handle; per-script identity for D3 is a fresh `owner` table per env. | `LuaBootstrap.__openlot_newEnv`, `makeSubscribe` |
| D3 | **Redeclaration replaces within one script, appends across scripts.** The Lua-side registry is keyed `(subject, event)` holding an ordered array of `{owner, subscriber, fn}`. Same `owner` re-subscribing replaces the handler in place; a second script on the same entity appends. | `LuaBootstrap` event registry |
| D4 | **One plumbing pair, excluded from the `Lot` table.** `SubscribeEvent(handle, subject, event)` / `UnsubscribeEvent(...)` are bound methods called by the sugar by name — the same treatment as `NetInvoke`/`NetRegister` (`LuaManager` registration loop skips them from `Lot`). They mirror the Lua-side registry into the C# `LotEventRegistry`; no callback ever crosses into C# as a wrapper. | `LotLuaApi`, `LuaManager` |
| D5 | **Dispatch is stack-based**, `__openlot_dispatchEvent(subject, event, subscriber, other, playerId, fromPlayer)`, mirroring `__openlot_dispatch`: current `sender`/`fromPlayer` are set and restored around each handler so `net.fromPlayer` and the transitive-identity rule work inside event handlers. Arguments are primitives (handles + a peer id); no serializer, no tables, no per-delivery allocation. | `LuaBootstrap`, `LuaNetBridge.TryDispatchEvent` |
| D6 | **Queue, then dispatch from `ScriptRuntime.Tick`** — the shape `NetRouter.Flush()` already has (D4 of the 3.2 doc). Sensors enqueue during the physics step; Lua never runs inside a physics callback. Deliveries drain **before** `Router.Flush()`, so a handler's `net.server` call dispatches the same frame. | `ScriptRuntime.Tick` |
| D7 | **One watchdog unit per delivery** (`ArmUnit(DispatchInstructionBudget)` + `EndUnit`), trips counted in the existing per-entity `_tripsByEntity` breaker: three trips disable the entity's handlers (net and events alike) with the existing "disabled" mechanics. | `LuaNetBridge.TryDispatchEvent/CallOnFrame` |
| D8 | **Ordering: subject handle ascending, then enqueue sequence; within one (subject, event) the subscriber list order (declaration order).** Deterministic for tests. | `LotEventRegistry.Drain` |
| D9 | **Bounds:** duplicate `(subject, event, other)` deliveries within one frame coalesce; at most `MaxDeliveriesPerFrame = 128` queue entries dispatch per frame, the overflow is dropped with ONE throttled log line (the `UnknownHandlerStat` + `WarnThrottleSeconds` pattern). | `LotEventRegistry` |
| D10 | **Touch is overlap-based, not collision-based.** Subscribing a sensor-driven event opts the subject into an `Area3D` (internal-marked, `Monitorable = false`, monitoring bodies only — never other areas, so two sensored parts cannot double-report). The sensor's mask is `LotCollisionGroups.EventSensorMask` (all groups + no-collide + the player's event bit), deliberately independent of the §3.5 matrix — the matrix governs physics and must not swallow events. A `CanCollide = false` trigger plate therefore works by design. | `LotObject.EnsureEventSensor`, `LotCollisionGroups` |
| D11 | **Sensor shapes are padded copies of the part's collision shape** (`SensorPad = 0.15f` local units). Physics stops a moving body exactly at contact, which is NOT an overlap — a resting ball or a character pressing a wall would never enter an exact-size volume. The pad makes "touch" mean "in contact or within 0.15 m". The pad scales with the part (known limitation, §6). | `LotObject.MakeSensorShape` |
| D12 | **The player is detectable via a dedicated event-only layer bit.** The player body is layer 0 by design (never a collision target, never a pick hit), so an area could never see it. It now wears `CharacterBodyBit = 1<<9` instead: no matrix row, no pick mask and no drag mask includes that bit, so every existing interaction is unchanged; only `EventSensorMask` includes it. | `LotCollisionGroups`, `CapsuleController` |
| D13 | **Two-ended cleanup.** Destroying an entity removes what it registered as a subscriber AND what others registered on it as a subject, through the existing single path: `DestroyEntity` → `CleanupRecursive` → `bridge.ForgetEntity` (Lua side) + `Events.ForgetEntity` (C# side). A reload (`ClearRegistries`) clears both sides — scripts re-subscribe when they re-run. A destroyed entity's handler can never fire afterwards; every subscription has a cancel token. | `BuilderScene.CleanupRecursive`, `ScriptRuntime`, `LuaBootstrap.__openlot_forgetEntity` |
| D14 | **Attribution.** A touch delivered with `other` = the local player's character enters the same `BeginPlayerInteraction` scope `net.*` uses, so inside the handler `net.fromPlayer` is true and `player` carries the local peer id (loopback = 1, resolved through the transport — never hardcoded). A physics-caused touch is System: `player` is nil, `fromPlayer` false. Remote-player attribution waits on §8.1. | `BuilderScene`, `NetRouter.LocalPeerId` |
| D15 | **`onFrame` is declared like `start()`**: a top-level function, captured at script load (`__openlot_captureFrame`), one execution unit per entity per frame, and it stops with the session (D1). One `onFrame` per entity: two scripts on one entity defining one means the last load wins — the same collision rule `net.*` uses. | `LuaNetBridge.LoadEntityScript`, `ScriptRuntime.Tick` |
| D16 | **Subscriptions ship inside the lot** as ordinary Lua in the archive's `scripts/` — nothing new enters `lot.json`; a loaded lot re-establishes subscriptions exactly the way it re-declares `net.*`. | (no code — a property of the whole design) |
| D17 | **`destroyed`, `clicked`, `entered`, `exited`, `playerJoined`, `playerLeft` are reserved vocabulary.** The names are validated and accepted by `Subscribe` from today, but only `touched`/`touchEnded` fire in this half. `destroyed` firing needs a re-entrancy-safe dispatch point (its delivery must outlive the destroyed entity's own registration clean-up) — deferred rather than half-built. `clicked` waits on the shared §5.2/§5.3 in-session input path; `entered`/`exited` on §3.13's query volumes; `playerJoined`/`playerLeft` on §8.1. | `LotEventRegistry.KnownEvents` |

---

## 3. Execution flow (one frame)

1. **Physics step** — a sensor's `body_entered`/`body_exited` callback resolves the other entity's
   handle and player-ness, then calls `LotEventRegistry.Enqueue(subject, event, other, fromPlayer,
   playerId)`. Nothing runs in Lua here.
2. **`BuilderScene._Process` → `ScriptRuntime.Tick(delta)`**:
   a. `bridge.BeginFrame()` — one frame budget across events, frames and net.
   b. While a session is active: `Events.Drain()` — pending sensors attach (a subject spawned after
      its subscription resolves here), then the queue is sorted (subject ascending, then enqueue
      sequence) and each entry resolves its subscribers in declaration order; disabled entities are
      skipped; each subscriber call is one armed watchdog unit (`LuaNetBridge.TryDispatchEvent`);
      anything past the per-frame cap is dropped with one throttled line.
   c. `TickFrames` — per entity with a captured `onFrame`, `bridge.CallOnFrame(handle, delta)`, one
      unit each; a handler that vanished (destroyed/reloaded) prunes the list.
   d. `Router.Flush()` (unchanged) and the after-frame hook (deferred VM rebuild).
3. **Inside `__openlot_dispatchEvent`** — `currentSender`/`currentFromPlayer` are set for the
   duration of the call and restored after, the handler runs under `raw_pcall`, and a trip
   propagates to C# where it counts against the entity's breaker.

## 4. Lifecycle

* **Load** — the chunk runs, `Subscribe(...)` writes the Lua-side registry (replace-within-script /
  append-across-scripts) and calls the `SubscribeEvent` plumbing once per (subject, event).
* **Session entry** — `EnterTestMode` (after the script reload and the constraint build) calls
  `Events.OnSessionStarted()`: every subject is marked pending and the first drain attaches sensors.
* **Session exit** — `Events.OnSessionEnded()` detaches every attached sensor and clears the pending
  list and the queue (including deliveries queued during the last frame).
* **Reload** (VM rebuild, Test-mode round trip) — `ScriptRuntime.LoadIntoBridge` clears both
  registries and detaches sensors; the re-run scripts re-subscribe.
* **Destroy** — `DestroyEntity` → `bridge.ForgetEntity` (Lua: what this entity registered anywhere
  and what others registered on it) + `Events.ForgetEntity` (the same removal on the C# side); the
  last unsubscribe or destroy of a subject detaches its sensor.
* **Cancel token** — removes exactly the entry its env created (a replaced handler's old token still
  cancels the newer one: the token matches on the script's identity, not the function).

## 5. Verification

* `EventSelfTest` — synchronous suite: registry semantics (subscribe/unsubscribe, replace vs
  append, token cancel, ordering, coalescing, the per-frame cap and its one throttled line,
  `ForgetEntity` both ends, `ClearAll`, the sensor attach/retry/refuse protocol) plus the Lua sugar
  on a private VM (replace vs append across two envs, cancel, delivered arguments, `net.fromPlayer`
  inside a player-caused delivery, disabled-entity silence, `onFrame` capture / has / call,
  unknown-event refusal).
* Staged session probe (headless only, after `ConstraintSelfTest.GravityProbeDone`): a falling ball
  touches a plate (`other` = the ball's handle, `player` nil, `net.fromPlayer` false) and fires
  exactly one `touched` while it rests; the character walking into a wall delivers `player` = the
  local peer id with `net.fromPlayer` true; lifting the ball away fires `touchEnded`; `onFrame`
  advances and then stops at session exit; destroying the subject drops its subscriptions and its
  sensor; every sensor is gone after exit.
* Run: `godot --headless --quit-after 2500 res://builder/builder.tscn` — the probes need wall-clock
  time after the suites (the constraint probe ~1.4 s, this one ~3 s more), so a short run ends with
  "never finished" warnings instead of a verdict. The verification run: **EventSelfTest 47 checks
  + session probe 11 checks, 0 failures**, alongside every other suite green.

**What the run caught that the synchronous checks could not:** the session probe's walk exposed a
latent §3.6 leak — `LotConstraintSession.ApplySimulation` simulated parts that were *queued for
deletion* but still in the tree for the rest of the frame, and a part's own deletion cannot release
a root-hosted body (it is a sibling, not a child). The orphaned rigid body then sat in the next
session's walk path and blocked the character, and it touched the wall's sensor as a ghost entity.
`ApplySimulation` now skips `IsQueuedForDeletion()` nodes; the probe is the regression. This is
exactly the class of bug the milestone wanted a staged probe for — invisible to any synchronous
test, visible to a character walking a lot.

## 6. Known limitations

* The sensor pad is in local units, so it scales with the part.
* Overlap is shape-approximate on curved parts (a sphere or cylinder sensor pads the primitive
  shape, not an exact curve).
* A simulated subject's sensor is a child of the visual part, so it is one physics step behind the
  body it puppets.
* One `onFrame` per entity (last-loaded script wins), matching the `net.*` same-name collision rule.
* Reserved events (`clicked`, `entered`, `exited`, `destroyed`, `playerJoined`, `playerLeft`) are
  accepted by `Subscribe` and never fire yet — silently, by design D17.
* A subscription to a handle that never resolves stays pending for the session (bounded to one int
  per subject).

## 7. Blocked / deferred (stated, not implied)

* `clicked` and lot-UI press attribution → §5.2/§5.3 (the shared in-session input path, Phase 2).
* `playerJoined`/`playerLeft` and remote-player attribution → §8.1's real transport.
* `destroyed` firing → needs a delivery that outlives the destroyed entity's registration
  clean-up; reserved now, not half-built.
* `entered`/`exited` → §3.13's query volumes, which raise them through this registry.
* Timer callbacks → §3.8's milestone plugs into this same spine (one scheduler, not two).
