# Milestone 3.13 — Spatial queries (primitives + the query volume)

**Status:** both halves landed and verified headless — the primitives (`Lot.QuerySphere` etc.) and
the placeable **query-volume object** with its `entered`/`exited` event wiring.
**Related:** §3.7 (the event spine `entered`/`exited` ride) · §3.11 (raycast, the same
mask/session contract) · §3.6 (explosion pushes ride the dynamic-body layer).

## 1. What a creator gets

```lua
local hits = Lot.QuerySphere(0, 2, 0, 6)          -- every entity overlapping the sphere
for i = 1, #hits do
    Lot.Log(hits[i].handle .. " at " .. hits[i].x .. "," .. hits[i].y .. "," .. hits[i].z)
end
local inBox = Lot.QueryBox(0, 2, 0, 4, 4, 4)      -- centred, sized
local swept = Lot.SweepSphere(0, 2, 0, 1, 0, 0, 1.5, 12)  -- melee: a sphere cast along +X
Lot.Explode(0, 1, 0, 8, 120)                      -- falloff-scaled push; returns how many moved
```

## 2. Decisions

| # | Decision | Where it lives |
|---|---|---|
| D1 | **One primitive.** Overlap (sphere/box) and the swept sphere all run the same implementation; the explosion is the radius query plus a pure falloff function — no one-off query paths. | `SpatialQuery` |
| D2 | **The physics server IS the broad phase.** A shape query is filtered by the Jolt BVH through the collision mask; the mask is the editor pick mask, so every part in every group (non-collidable included) is visible — the same contract §3.11's raycast defines for gameplay-facing queries. | `SpatialQuery.Collect` |
| D3 | **One Lua crossing per query, none per hit.** Results arrive as one 1-based array of `{handle, x, y, z}`, deduped to one entry per entity, sorted handle-ascending (deterministic) and capped at 64 with a readable truncation warning. | `LotLuaApi.HitsToLua`, `SpatialQuery.MaxHits` |
| D4 | **Session-gated** like the raycast: outside a running session there is no live world, so the verbs warn and return nil/0. | `LotLuaApi.QueryUnavailable` |
| D5 | **The explosion**: linear falloff from full power at the centre to zero at the rim, along the outward direction; no direction exists exactly at the centre, so that point receives nothing. Only simulated bodies are pushed (static parts have nowhere to carry it), each woken first. Authority note: a world-changing explosion belongs in `net.server` (§1.3). | `SpatialQuery.ExplosionImpulse/Explode` |
| D6 | **Sweep semantics stated:** the swept sphere is a **sampled** sweep (shape evaluated along the path, spacing ≤ half the radius, ≤ 16 gaps — Jolt's own shape cast reports only the nearest collision, which is not what a hitbox wants); coverage is continuous while maxDistance ≤ 32 × radius, and results are the colliders at their origins, not exact entry points — both recorded refinements. | `SpatialQuery.SweepSphere` |
| D7 | **The volume object landed:** `LotObjectKind.QueryVolume` (Toolbox **Gameplay → Query Volume**, the same spawn/undo path a part takes), property rows `query_shape` (Choice) and `query_radius` (Float — inert for a box, whose extents are the standard **Size** row, but offered and persisted either way: the capture follows `CollectFor`, so a hidden row would silently drop its value on save — found by the probe's save/reload round trip), with the part-property split extended so a volume's Inspector is exactly those. No part geometry and no gameplay collision: a translucent preview (build-mode only, hidden for a session) and a pick proxy pinned to the no-collide layer, so the editor selects and drags it while every gameplay mask never sees it. `RefreshCollision` pins that layer whatever a script sets, and the constraint session refuses to simulate the kind. | `LotObject.cs`, `LotPropertyRegistry`, `ToolboxPanel`, `BuilderScene.DuplicateNode` |
| D8 | **Evaluation semantics:** one query per volume per frame from `_Process` (queries outside the physics step are legal — §3.11's pinned rule), the volume's own handle filtered out, and the frame-to-frame difference computed by one pure `Diff` over reused sets (no per-frame allocation). The baseline is **empty**, so a session's starting contents fire `entered` on the first evaluation — "everything in the region has entered" always holds. Box volumes query with the volume's full transform, so a rotated region samples its real orientation; the Lua `QueryBox` verb keeps its axis-aligned form. **Every placed volume is ensured a handle at Build** — an editor-placed node carries one only once a registration path touches it (§3.2 option A) and the runtime's walk resolves script-bearing entities only, so without this an unscripted toolbox-placed volume would silently evaluate nothing (found by the probe; placement alone is enough, a script on the volume is the common case, not a requirement). | `QueryVolumeSession`, `SpatialQuery.OverlapBox` |
| D9 | **Events ride the reserved vocabulary:** deliveries go through `LotEventRegistry.Enqueue`, so coalescing, the 128-per-frame cap, the one-watchdog-unit rule and two-ended cleanup apply unchanged; attribution is by handle (the local character's handle ⇒ `player` and `net.fromPlayer`, the transport's peer id — never hardcoded), the same rule the sensor path applies. | `BuilderScene.EnqueueEntityContact` |

## 3. Verification

`SpatialQuerySelfTest`: the pure falloff math (half power at half the radius, nothing at the rim/
outside/centre, magnitude = power × falloff, refusals for non-positive inputs) and the four verb
refusals outside a session run synchronously; a staged probe then drives the real queries against
live geometry inside a session — a sphere finds the near part and not the far one, a box finds the
far one and not the off-axis one, a swept sphere finds the part in its path (melee) and not the
off-axis one, bad arguments refuse with nil, and an explosion pushes two same-size free cubes at
1 m and 3 m from a 4 m blast with the expected ~3:1 velocity ratio (read one physics step later,
per §3.11's Jolt note).

**Result:** `SpatialQuerySelfTest` — 11 synchronous checks + a 7-check staged probe, 0 failures:
the probe found the near part in the sphere and not the far one, the far one in the box and not the
off-axis one, the part in the swept path (melee) and not the off-axis one, the in-session argument
refusals, and a blast whose two free cubes measured 43.64 vs 14.36 m/s — a 3.04 ratio, the
predicted ~3:1 falloff.

The **object layer** is verified by `QueryVolumeSelfTest` — 21 synchronous checks + a 20-check
staged probe, 0 failures. Synchronous: the kind tag round-trip, the shape vocabulary, the
declaration split (a volume's list is Shape/Radius/Size and none of the part rows; a cube's list is
unchanged; a box keeps Radius — stored, inert), the proxy and preview following shape/radius/size,
the radius floor and the no-collide layer pin — plus the pure `Diff` hand-set contract. The probe
places THREE volumes exactly like the toolbox does (no handles pre-assigned) in a real session — a
scripted box volume, a scripted sphere volume on the character's spawn, and an unscripted volume:
every one gets an identity through the session's own paths (the runtime's walk / Build's ensure —
the first cut silently skipped an unscripted volume, found by this probe and fixed), the editor's
pick ray finds the volume through its proxy (click-select), the part already inside the box fires
`entered` exactly once and player-unattributed (the empty baseline), the session's own character
standing in a region fires `entered` with `player`/`net.fromPlayer` true, a part moved in fires
`entered` and out fires `exited` (once each; nothing that stayed reported an exit, and the region
never reported its own handle), the previews hide in-session and return on exit, and the volumes
survive the Test-mode round trip. The lot then saves and RELOADS for real, with the volume coming
back as a query volume carrying its box shape, size and radius — the capture follows what applies,
which is how the probe caught a hidden Radius row dropping its value on save (the row is offered
for every volume now).
Riding along: `LotArchiveSelfTest`'s kind array grew `Wedge` and `QueryVolume` (the round-trip
check it always claimed covers every kind now really does). One product fix rode along too, found
by chaining these suites: `ScriptRuntime`'s entity walk, `HasScript` and `LoadScriptsUnder` now
skip nodes (and script nodes) queued for free — the §3.6 deletion-window rule applied to script
loading, because a teardown and the next session entry can land in the same frame, and the old
code loaded a destroyed fixture and warned about the file its cleanup had just deleted. Full
headless run: **1,949 checks, 0 failures** (1,907 pre-existing + 21 + 20 here + `PlayerDataSelfTest`'s
new settle-frame orphan check, added after this suite's session entry surfaced that probe's
leftover fixture node — the fix pins the §3.6 deletion-window rule for test cleanup too). The run
is warning-clean: the only warnings left are the intentional ones (the documented owned-spawn
reload notices, the player-data refusal probes, and an engine `look_at` advisory).
