# Milestone 3.11 — Physics API

**Status:** implemented — verified headless (see §4).
**Audience:** whoever maintains the scripting layer; the creator-facing half is `docs/physics-api.md`.

## 1. What the creator gets

```lua
-- Raycasts (session-only). Returns nil on a miss, or a table on a hit:
local hit = Lot.Raycast(ox, oy, oz, dx, dy, dz, 100)
if hit ~= nil then
    -- hit.handle   the entity the ray hit (-1 for a non-lot collider)
    -- hit.x/y/z    where it hit      hit.nx/ny/nz  the surface normal
    -- hit.distance origin -> hit
end

-- Forces and velocity on simulated (unanchored, in-session) parts:
Lot.ApplyImpulse(part, 0, 8, 0)     -- one-shot: dv = impulse / mass
Lot.ApplyForce(part, 0, 40, 0)      -- thrust for the NEXT physics step; call each frame
Lot.SetVelocity(part, 6, 0, 0)      -- / Lot.GetVelocityX/Y/Z(part)

-- Material properties (lot state, editable in the Inspector and from Lua alike):
Lot.SetFriction(part, 0.2)          -- 0..inf, default 1
Lot.SetBounce(part, 0.9)            -- 0..1, default 0
Lot.SetMass(part, 4)                -- 0 = auto (mesh volume), positive overrides
```

## 2. Decision table

| # | Decision | Where it lives |
|---|---|---|
| D1 | **`Raycast` returns a real Lua table; `nil` on a miss.** Spike-verified on this project's NLua 1.7.9: a bound method returning a `Dictionary` hands Lua **userdata**, not a table, so the result is built as a `LuaTable` (`Lua.NewTable` + `GetTable`; verified that each call yields a fresh, independent table and older returned tables keep their values). `LotLuaApi` holds the `Lua` instance the way it already holds `Net` (both set in `LuaManager.Initialize`). The fallback — stateful scalar getters — is recorded as the rejected alternative (it breaks under interleaved calls and reads worse). | `LotLuaApi.Raycast`, `LuaManager.Initialize` |
| D2 | **The raycast is the editor's own pick query, widened** — `PartDragController.Raycast` is the same space-state call `Pick` already makes (lot world, bodies not areas, normalized direction), with a caller-chosen distance and the collider returned. One query implementation serves gizmos, the decal tool and Lua. Mask = the editor pick mask: every group plus the no-collide layer, so a `CanCollide = false` part is still hittable (matches what the editor sees) — areas and the player's invisible controller body are not hit; the character's visual collider resolves to the character entity. | `PartDragController.Raycast` |
| D3 | **Queries run inline from the script tick.** Scripts run in `_Process`; Godot 4.4+ allows space-state queries outside the physics step (single-threaded physics here), and the staged probe pins it for real — a script raycasts from `onFrame`; if the engine refused, the probe fails loudly instead of shipping a fantasy. (The drag path's physics-step queueing predates that behaviour and is left as-is.) | `LotLuaApi.Raycast` |
| D5 | **Material properties are declared descriptors** — friction / bounce / mass join `LotPropertyRegistry`, so the Inspector widgets, undo, the session snapshot and `lot.json` serialization all come for free and Lua cannot disagree with the editor about a name or a type. Defaults reproduce the pre-milestone behaviour exactly: friction 1 / bounce 0 (Godot's own defaults) and mass 0 = auto (the §3.6 volume estimate). Clamps: friction >= 0, bounce 0..1, explicit mass >= 0.01 — `0` is always "auto". | `LotProperty.cs`, `LotObject` |
| D6 | **Property verbs are not session-gated; action verbs are.** Friction/bounce/mass are lot state, like anchored and collision group — a load-time script sets them the same way it sets anything else. Raycasting and force/velocity writes need the session's physics world, so they refuse outside a session (the §3.7 D1 gate). | `LotLuaApi` |
| D7 | **Persistence is the property system's, not a second path.** The three values ride the §4.1 walk into `lot.json`, the §3.6 session snapshot, and the load restore — all through the descriptor table; the round-trips are verified rather than assumed. Bodies pick the values up on creation (`SetSimulated`) and live on change (a per-part `PhysicsMaterial` shared by both body forms). | `LotSceneWalk`, `LotSceneLoad`, `LotSessionSnapshot`, `LotObject` |
| D8 | **Authority (§1.3).** Physics is client-local simulation. Anything with enforcement consequences must be decided by the host through `net.server` — the same rule §3.9's input API documents; stated again in the creator doc. | `docs/physics-api.md` |

---

## 3. Execution flow

* A script calls `Lot.Raycast(...)` → validated (zero direction, distance, session, runtime ready) →
  `PartDragController.Raycast` queries the lot world → the collider resolves through
  `BuilderScene.ResolveBodyEntity` (so a root-hosted simulated body and the player both name their
  entity) → a fresh `LuaTable` is filled and returned; a miss is `nil`.
* `ApplyImpulse` / `ApplyForce` / `SetVelocity` resolve the part's live `RigidBody3D`, wake it, apply,
  return true; every refusal path returns false with a warning in the Output window.
* The material setters write the part's fields, refresh the shared `PhysicsMaterial` on whichever body
  is live, and reach the Inspector/Lua through the same descriptor table (the property service remains
  the editor's write path).

## 4. Verification

* `PhysicsSelfTest` — pure: the descriptor declarations (defaults, clamps, the `MassForBody` auto
  rule) and the verb refusals (bad handle, anchored part, outside a session, zero direction); the
  result-table shape on a private VM (field reads, miss = nil) — the NLua mechanism pin; and the
  Inspector's own surface: its draw loop's collection holds the three rows as one `Physics` section
  after `Part`, and the descriptor delegates its Float widgets edit through reach the part.
* Staged session probe (headless): a real script (a) raycasts down at a target and up into the sky,
  reporting handle/point/normal/distance or a miss, (b) applies one impulse to a mass-auto cube and
  to a `SetMass(4)` cube and reports both velocities (the ratio is the mass-property test), (c) sets
  two sliders' velocities and two balls' bounces through the Lua verbs while the probe samples their
  real trajectories — the high-bounce ball must rebound clear of the low-bounce one, and the
  low-friction slider must travel further than the high-friction one. Everything is asserted from
  logged numbers and sampled positions, pinned by the run.
* Run: `godot --headless --quit-after 8000 res://builder/builder.tscn` (the probe chain's wall
  clock; a short run warns "never finished").

