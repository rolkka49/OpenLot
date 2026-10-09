# OpenLot scripting — events and `onFrame`

**Audience:** creators writing lot scripts in Lua.
**Status:** implemented for the physics-side half (Milestone 3.7). `touched` / `touchEnded` and
`onFrame` work in Test/Game sessions today. `clicked`, `entered` / `exited`, `destroyed` and
`playerJoined` / `playerLeft` are **accepted** by `Subscribe` (the names are API from now on) but
do **not fire yet** — subscribing to them is harmless; §6 says what each is waiting on.

---

## 1. The model

```lua
-- Somewhere a part can see it (top of the script, inside start(), wherever).
local token = Subscribe(this.Handle, "touched", function(other, player)
    Lot.Log("touched by " .. tostring(other))
end)
```

* **You subscribe on an entity** — usually your own (`this.Handle`), because a part's script
  reacting to its own touches is the common case. Any handle works, so one manager script can
  watch many parts.
* **Sessions only.** Events fire while a player session (Test or Game mode) is running. Sensors
  come up when the session starts and come down when it ends — nothing fires while you are just
  building.
* **The part gets a sensor automatically.** Subscribing `touched`/`touchEnded` gives the subject a
  physics volume (a slightly enlarged copy of its collision shape, 0.15 m of padding). That
  padding is deliberate: physics stops a body exactly at contact, which is touching but not
  overlapping — the pad is what makes "resting on it" actually count. It also means a touch can
  register from up to ~0.15 m away.
* **Edge-triggered.** A ball resting on a plate fires `touched` once, not every frame;
  `touchEnded` fires when it leaves.
* **`CanCollide = false` still works.** A non-collidable trigger plate fires touches by design —
  the sensor does not consult the collision matrix.
* **The player counts.** Walking into a trigger with your character fires it, with the character's
  handle as `other`.
* **Cancel with the token.** `token()` removes exactly your script's subscription.

## 2. `touched` / `touchEnded`

```lua
Subscribe(this.Handle, "touched", function(other, player)
    -- other  = the other entity's handle (a number)
    -- player = the originating peer id when the player's own character caused it,
    --          nil for a physics-caused touch (a falling part, something sliding in)
end)

Subscribe(this.Handle, "touchEnded", function(other)
    -- fires when the overlapping body leaves the sensor
end)
```

| Touch | `other` | `player` | `net.fromPlayer` inside the handler |
|---|---|---|---|
| Player's character | the character's handle | the local peer id (1 offline) | `true` |
| Another part (physics) | that part's handle | `nil` | `false` |

A touch caused by the player enters the same attribution scope `net.*` uses, so the handler's own
`net.server` calls are counted as player-originated too.

## 3. `onFrame`

```lua
local elapsed = 0
function onFrame(delta)
    elapsed = elapsed + delta
end
```

* Declared like `start()` — a top-level function, nothing to register.
* Runs once per frame **during a session** (Test/Game), and stops when the session ends.
* `delta` is the frame time in seconds (the same value the runtime ticks with).
* One per entity: if two scripts on one entity both define one, the last-loaded script wins —
  the same collision rule `net.*` declarations use.
* Every frame is one watchdog unit for that entity, so a runaway `onFrame` trips alone.

## 4. Lifetime and cleanup

* **Cancel tokens.** `Subscribe` returns a function; call it to remove that subscription:
  `local token = Subscribe(...)` … `token()`.
* **Re-running a script replaces its own subscriptions** in place; a **second script on the same
  entity appends** alongside it. Both scripts' handlers run, in declaration order.
* **Destroying the subscriber** removes its handlers from every part it watched. **Destroying the
  subject** removes everything anyone registered on it (and its sensor). A destroyed entity's
  handler can never fire afterwards — there is nothing left to fire it from.
* **Reloads clear everything.** Entering or leaving a session re-runs the lot's scripts, so
  subscriptions are re-established the same way `net.*` declarations are — you never have to
  clean up by hand.
* **Subscriptions live in your script**, so they travel inside the `.lot` file like the rest of
  it. Nothing extra to save.

## 5. Deliberate behaviour worth knowing

* One sensor per subscribed subject; overlaps are **bodies only** (a sensor never sees another
  sensor), and internal implementation nodes (collision hulls, decals, joints) are never reported
  as `other`.
* Touch storms are bounded: identical `(subject, event, other)` contacts within one frame are
  coalesced, and at most 128 deliveries dispatch per frame — the rest are dropped with one log
  line in the Output window.
* Each handler runs under the same instruction budget as a `net.*` dispatch; three watchdog trips
  on an entity disable its handlers (net and events alike) for the session.
* A sensor follows the part it belongs to; on a simulated (falling) part it tracks the visual, so
  it is a physics step behind the body.

## 6. Reserved names (accepted today, not firing yet)

| Name | Waiting on |
|---|---|
| `clicked` | the shared in-session input path (§5.2 / §5.3); it will carry the originating player |
| `entered` / `exited` | §3.13's query volumes, which will raise them through this same registry |
| `destroyed` | a re-entrancy-safe dispatch point (a follow-up) |
| `playerJoined` / `playerLeft` | §8.1's real transport (no peers exist offline) |
| timer callbacks | §3.8 — the timer verbs will ride this same event spine |

