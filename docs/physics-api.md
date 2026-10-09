# OpenLot scripting — physics

**Audience:** creators writing lot scripts in Lua.
**Status:** implemented (Milestone 3.11). Raycasts and forces work while a player session
(Test/Game mode) is running; material properties are lot state and work everywhere, exactly like
`SetAnchored`.

---

## 1. Raycasts

```lua
local hit = Lot.Raycast(ox, oy, oz, dx, dy, dz, maxDistance)
if hit ~= nil then
    -- hit.handle    the entity the collider belongs to (-1 if it belongs to no lot object)
    -- hit.x/y/z     where the ray met the surface
    -- hit.nx/ny/nz  the surface normal (points out of the surface)
    -- hit.distance  origin -> hit, in metres
end
```

* The **direction does not need normalizing** — it is normalized for you, so `maxDistance` is
  always metres along the ray. `0, -1, 0` cast straight down.
* A miss returns **`nil`** (never an empty table), so `if hit then ... end` is the idiom.
* What the ray can hit is **what the editor can see**: every collision group, including parts with
  `CanCollide = false` (they stay hittable by design — a trigger plate is exactly the thing you
  want to see). It never hits event sensors, and it never hits the player's own invisible
  controller body; the character's capsule still counts, as the entity it is.
* Raycasts need a **running session**: outside Test/Game mode there is no live physics world, so
  the call reports it in the Output window and returns `nil`.

## 2. Forces and velocity

```lua
Lot.ApplyImpulse(part, 0, 8, 0)   -- one-shot kick: the velocity change is impulse / mass
Lot.ApplyForce(part, 0, 40, 0)    -- thrust: applies to the NEXT physics step — call it every frame
Lot.SetVelocity(part, 6, 0, 0)    -- set the velocity outright
local vx = Lot.GetVelocityX(part) -- / GetVelocityY / GetVelocityZ
```

* These act on **simulated parts only**: unanchored parts, while a session runs (that is the
  rigid body the session gives them). An anchored part, or any call outside a session, returns
  `false` with a message in the Output window.
* The body is **woken before every push**, so a part that had gone to sleep still reacts.
* `ApplyImpulse` is the "kick it" verb — **the velocity change is the impulse divided by the
  part's mass**, which makes `SetMass` the weight knob (the same kick moves a mass-4 part a
  quarter as far as a mass-1 part).
* **Timing to know about:** `ApplyImpulse`/`ApplyForce` reach the engine's physics step, so the
  velocity changes from the **next step** — do not read `GetVelocityX` in the same frame and
  expect the new number (read it on a later frame). `SetVelocity` applies immediately.
* `ApplyForce` follows the engine's own rule: a force lasts for **one physics step**, so
  sustained thrust means calling it every frame (from `onFrame`, an `Every` timer, or a
  `touched` handler).

## 3. Material properties

```lua
Lot.SetFriction(part, 0.7)   -- how grippy: 0 = slippery, 1 = default, higher = grippier
Lot.SetBounce(part, 0.9)     -- how bouncy: 0 = dead, 1 = fully elastic
Lot.SetMass(part, 4)         -- 0 = auto (from the part's size), a positive number overrides
local f = Lot.GetFriction(part)  -- / GetBounce / GetMass
```

* These are **lot state, not session actions**: they persist with the lot, appear in the
  Inspector's Physics section, survive save/load and are restored when a Test-mode session ends.
  Scripts may set them at any time — no session needed.
* **Defaults reproduce what every part did before this existed**: friction 1, bounce 0 (the
  engine's own defaults) and mass = auto (derived from the part's size). Setting mass to `0`
  returns a part to auto.
* Values are clamped: friction never goes below 0, bounce stays in 0..1, and an explicit mass is
  floored at a tiny positive minimum. An unknown handle returns `false` (setters) or `0` (getters)
  with a message.
* The engine combines the two surfaces' materials at a contact, so a bouncy ball on a dead floor
  reads as the ball's bounce, and a slippery slider on a grippy floor slides far.

## 4. Authority (§1.3)

Physics is **client-local simulation**. A push or a material change that must count for everyone
belongs in a `net.server` handler on the host — never trust a client's copy of where things are.

## 5. Limitations, stated plainly

* Raycasts, impulses, forces and velocity are **session-only**; materials are not.
* The hit table is a **fresh table per hit** (safe to keep, never shared with other results).
* `hit.handle` is `-1` when the collider is not a lot entity (nothing in the standard flow, but
  honest about the edge).
* Raycasts test the world as of the **last physics step** — normal for queries between steps.
