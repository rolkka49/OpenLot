# OpenLot scripting — spatial queries

**Audience:** creators writing lot scripts in Lua.
**Status:** implemented (milestone 3.13) — the query verbs, and the placeable **query-volume
object** with its `entered`/`exited` events (a trigger you drop in the editor).

---

## 1. The verbs

```lua
local hits = Lot.QuerySphere(0, 2, 0, 6)         -- centre + radius
local inBox = Lot.QueryBox(0, 2, 0, 4, 4, 4)     -- centre + size
local swept = Lot.SweepSphere(0, 2, 0, 1, 0, 0, 1.5, 12)  -- from + direction + radius + distance
local moved = Lot.Explode(0, 1, 0, 8, 120)       -- centre + radius + power
```

| Call | Returns |
|---|---|
| `Lot.QuerySphere(x, y, z, radius)` | An array of hits (see below), or `nil` on bad input |
| `Lot.QueryBox(x, y, z, sx, sy, sz)` | Same |
| `Lot.SweepSphere(x, y, z, dx, dy, dz, radius, maxDistance)` | Same — a sphere cast along a direction (melee swings) |
| `Lot.Explode(x, y, z, radius, power)` | How many simulated parts were pushed |

Each hit is a table `{ handle = ..., x = ..., y = ..., z = ... }` — the entity's handle and where
its collider currently is. One call, one list: there is never a call per hit.

## 2. Rules

* **Session-only**, like `Lot.Raycast`: there is a live physics world only inside Test/Game mode;
  outside it the calls return `nil` (or `0`) with a warning in the Output window.
* **What they can see:** every part in every collision group — *including* `CanCollide = false`
  parts — but never event sensors or the invisible controller body. The character's capsule is a
  normal part and shows up.
* **Bounded:** at most 64 hits per call, deduped to one entry per entity, ordered by handle
  (deterministic), with a truncation warning when the cap is hit.
* **Sweeps report the colliders along the path**, at their current positions — not the exact entry
  point on the surface (a recorded refinement). The sweep is sampled (spacing at most half the
  radius) so it catches everything the swing passes through, up to long ranges where samples space
  out (see the design doc).
* **Explosions push only simulated parts** (unanchored ones in a session): each gets an impulse
  scaled by distance — full power at the centre, fading to zero at the rim — and is woken first.
  Static/anchored parts are reported by the query but not pushed. Physics is client-local; an
  explosion other players must agree on belongs in a `net.server` handler (§1.3).

## 3. Typical uses

```lua
-- Melee hitbox: everything in front of the swing.
local hits = Lot.SweepSphere(px, py, pz, facingX, 0, facingZ, 1.2, 3)

-- Proximity check each frame (from onFrame), then act on the handles.
local nearby = Lot.QuerySphere(px, py, pz, 5)
if nearby ~= nil then
    for i = 1, #nearby do
        -- nearby[i].handle ...
    end
end

-- A blast that throws loose parts (and reports how many it woke).
local moved = Lot.Explode(10, 1, 10, 8, 150)
```

## 4. The query volume object (a placed trigger)

The same query as a thing you drop into the lot:

1. Toolbox → **Gameplay → Query Volume**. Move and resize it like any object; the translucent
   preview is build-mode only (it hides while a session runs).
2. Inspector rows: **Shape** (`Sphere` / `Box`), **Radius** (used by spheres, 0.1 m minimum —
   kept while the shape is Box, so switching back loses nothing) and the standard **Size** row
   (box extents — a rotated volume samples its rotated shape).
3. Its script subscribes to the region itself:

```lua
Subscribe(this.Handle, "entered", function(other, player)
    Lot.Log("entered " .. tostring(other))
end)
Subscribe(this.Handle, "exited", function(other)
    Lot.Log("left " .. tostring(other))
end)
```

* **Sessions only**, evaluated once per frame. At session start everything already inside fires
  `entered` (the baseline is "empty"), and the player's own character carries `player` /
  `net.fromPlayer` like a touch does — `docs/event-api.md` §3 has the details.
* **Placing it is enough:** every volume gets an identity at session start whether or not it has a
  script, so a region left unscripted still evaluates (and can be found by rays and queries); the
  script is what makes it *do* something.
* A volume never collides: you cannot walk into it, block on it or hit it with physics — it is
  purely a query. In the editor it stays selectable and draggable.
* The two verbs families cover both halves: `Lot.QuerySphere`/`QueryBox` when a script wants a
  one-off answer, a volume when the region itself must watch continuously.
