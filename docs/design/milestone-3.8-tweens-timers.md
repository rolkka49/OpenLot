# Milestone 3.8 — Tween & timing library

**Status:** implemented — verified headless (see §5).
**Audience:** whoever maintains the scripting layer; the creator-facing half is `docs/tween-api.md`.

## 1. What the creator gets

```lua
local t1 = Lot.TweenPosition(part, 20, 5, 20, 1.5, "quadInOut")  -- parts and UI elements
local t2 = Lot.TweenColor(part, 1, 0, 0, 1, 0.3)                  -- easing optional (linear)
local t3 = Lot.TweenUIRect(panel, 40, 60, 300, 200, 0.5)
local t4 = After(2, function() ... end)      -- one-shot timer
local t5 = Every(0.5, function() ... end)    -- repeating timer
t1()  -- every call above returns a cancel token, the same shape Subscribe returns

---

## 2. Decision table

| # | Decision | Where it lives |
|---|---|---|
| D1 | **One scheduler, one clock.** `LotScheduler` holds every running tween and timer on a single accumulated-delta clock (simulation time — not wall time), so behaviour is deterministic and pauses exactly with the session. It is the "one scheduler, not two" §3.7 promised: same tick point, same one-watchdog-unit-per-callback policy, same 3-trip breaker. | `builder/LotScheduler.cs`, `ScriptRuntime.Tick` |
| D2 | **Lua never runs per frame.** A tween's interpolation and node writes are pure C#; a timer callback runs as one armed watchdog unit through `LuaNetBridge.CallTimer`. An animating lot therefore costs the Lua VM nothing. | `LotScheduler.Advance`, `LuaNetBridge.CallTimer` |
| D3 | **Tween verbs**: `TweenPosition` / `TweenRotation` / `TweenScale` / `TweenColor` / `TweenUIRect`, each `(handle, values..., duration, easing?)`. `TweenColor` and `TweenUIRect` also work on `LotUIElement`s; transform tweens take any `Node3D`. Invalid target kind or unknown easing raises in the calling script (same as `Subscribe`'s validation). | `LuaBootstrap` Lot overrides, `LotLuaApi` plumbing |
| D4 | **Input is one packed plumbing call.** The five sugar verbs funnel into one bound `ScheduleTween(handle, kind, a, b, c, d, duration, easing)`; the four floats are the value (color alpha included; transforms leave d unused). Kept out of the `Lot` table like `NetInvoke`. | `LotLuaApi`, `LuaManager` exclusion list |
| D5 | **Timers are per-env** (`After` / `Every`), like `Subscribe`: the subscriber identity is the env's handle, so destroying the entity cancels its timers. The callback stays in a Lua-side registry (`timerFnRegistry`) — C# never holds a function wrapper and calls `__openlot_callTimer(handle, id, keep)` at fire time. | `LuaBootstrap.__openlot_newEnv`, `__openlot_callTimer` |
| D6 | **Cancel tokens only.** No creator-facing cancel verb: every call returns a closure that calls the hidden `CancelScheduled(id)`. A token after completion/cancel is a harmless no-op returning false. | `LuaBootstrap`, `LotLuaApi.CancelScheduled` |
| D7 | **Caps.** At most `MaxScheduled = 256` live entries per lot; past the cap the call is refused with one `LotLog` line and the token is a no-op. A repeating timer fires **at most once per frame** even under a large delta (a hitch must not burst N callbacks at once); the next fire is rescheduled one interval from now. | `LotScheduler` |
| D8 | **Session-gated** like §3.7's events and `onFrame`: tweens advance and timers fire only while a player session is active. Leaving the session (or a reload) clears the whole schedule; the Test-mode snapshot restores authored transforms anyway. | `ScriptRuntime.Tick`, `LoadIntoBridge`/`Stop` |
| D9 | **Easing is a fixed pure-math set**: `linear`, `sineIn/Out/InOut`, `quadIn/Out/InOut`, `cubicIn/Out/InOut` (standard formulas, exact endpoints 0 and 1). No new dependency; every curve is hand-verifiable in tests. | `LotEasing` |
| D10 | **Tween ownership is the target handle.** A tween dies when its target node is gone (validity check each advance) or when anything calls `ForgetEntity(handle)` for it — the same hook the entity-destroy walk already calls for events. Timers are owned by their subscriber handle instead, matching D5. | `LotScheduler.ForgetEntity`, `BuilderScene.CleanupRecursive` |

---

## 3. Execution flow (one frame, session active)

1. `ScriptRuntime.Tick(delta)`: `BeginFrame` → events drain → **`Scheduler.Advance(delta)`** (tweens
   write their nodes; due timers fire through `CallTimer`, one unit each) → `onFrame` per entity →
   `Router.Flush()` → after-frame hook.
2. `Advance` reads and writes live nodes; a target that stopped resolving (freed) removes its entry
   silently — no stale references survive (memory-review discipline).
3. Completion removes the entry; tokens remain valid no-ops afterwards.

## 4. Lifecycle

* Created entries live **only in the schedule** — nothing enters `lot.json`; reloads and session
  transitions clear everything (`ClearAll` from `LoadIntoBridge`/`Stop`), exactly like events.
* `BuilderScene.CleanupRecursive` calls `Scheduler.ForgetEntity(handle)` next to the event
  registry's, so destroying an entity cancels what it animated and what it timed.
* The Lua side clears in step: `__openlot_forgetEntity` drops `timerFnRegistry[handle]`;
  `__openlot_resetRegistries` clears the whole registry on reload.

## 5. Verification

* `TweenSelfTest` — pure: every easing curve's endpoints are exact, midpoints hand-computed
  (`quadIn(0.5) = 0.25` …), curves monotonic, every name parses and unknown names refuse.
* Scheduler mechanics with a recording fire-delegate: a 1 s linear tween lands on quarter points
  exactly (0.25 s → mid, 1.0 s → target, entry gone); repeating timer fires once per interval, a
  big delta fires it once (not N times); cancel returns true then false; cap refuses the 257th
  entry with a warning; `ForgetEntity`/`ClearAll` empty the right things; a freed target drops its
  tween.
* Private-VM Lua: verbs bound, tokens cancel, unknown easing raises, timers run their stored
  callbacks through the real bridge (`CallTimer`), a one-shot clears its Lua-side entry, `Every`
  keeps it, `ForgetEntity` clears both sides.
* Staged session probe (headless): a script tweens a part 10 m over 1 s — the probe samples
  across real frames (strictly between the endpoints mid-flight, ≈ target after) — plus a
  one-shot timer logging once, an `Every` timer that cancels itself from inside its own third
  fire (no fourth), an immediate self-cancel that never fires, and a clean session exit (authored
  transform restored, schedule empty).
* Run: `godot --headless --quit-after 6000 res://builder/builder.tscn` (the probe chain's wall
  clock; a short run warns "never finished" instead of fake-greening).
```
