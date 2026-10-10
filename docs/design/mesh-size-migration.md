# Mesh-regeneration size migration (authored `Size`, per-part texture mapping, the wedge)

**Status:** implemented — verified headless by `PartSizeSelfTest` + the updated suites (see §5).
**Audience:** whoever maintains the lot object model; the creator-facing half is `docs/part-api.md`.
**Predecessor:** `PartMeshFactory` (the procedural mesh generator with world-unit UVs, verified by
`PartMeshSelfTest`) — this record is the migration that puts it behind `LotObject`.

## 1. What changed and why

Resizing a part used to be node scale: a creator scaled the node and Godot stretched the primitive
mesh with it — taking its texture with it (a 4-unit wall wore the same 1×1 image as a 1-unit wall,
just smeared) and compounding the collider child's own scale. This migration moves resizing onto a
new **authored `Size`** on the part: `SetSize` regenerates the mesh (and the collision hull) at the
requested extents, so a texture keeps a fixed world-unit density on any size, the node scale stays
1, and physics reads the same numbers it always did (see D3). The bundle that shipped with it: the
**wedge** kind, **per-part texture tiling / stretch** (the migration's compatibility valve), and a
**Size tween kind** so scripts can animate an extent.

What deliberately did NOT change: node scale is still a plain `Node3D` transform (`Lot.SetScale`
keeps working exactly as before, and a legacy-scaled part keeps its scale through a resize), the
`scale` field is still written to `lot.json`, and the plane / capsule / decal kinds keep their
shapes (they refuse `SetSize`).

## 2. Decision table

| # | Decision | Where it lives |
|---|---|---|
| D1 | **`Size` is authored state, not a transform.** `LotObject.Size` (a `Vector3` extent, centred on the origin) is the resize vocabulary for the sized kinds — cube, sphere, cylinder, wedge. `HasSize` is false for plane/capsule/decal, and every size verb refuses there with a message rather than silently scaling the node. | `builder/lot/LotObject.cs` |
| D2 | **One write path: `SetSize`.** It clamps through `PartMeshFactory.ClampSize` (magnitude, `MinSize`/`MaxSize`, non-finite → floor), regenerates the mesh from the factory's shared cache, swaps the outline hull's mesh reference, resizes the collision hull in place, re-pads the event sensor, and re-derives every child decal. The gizmo, the Inspector, the undo command, the archive loader, the session snapshot, the duplicate path and Lua all funnel through it — there is no second resize implementation. | `LotObject.SetSize`, `LotObject.RefreshGeneratedGeometry` |
| D3 | **Collision sizing keeps the pre-migration physics arithmetic.** A box carries its size natively (`BoxShape3D.Size`); the round shapes keep the unit resource they always shipped with and carry the size as the **collider child's scale** — exactly the compound node-scale arithmetic every existing lot, test and physics result was built on, so no simulation result changes shape. The wedge has no Godot prism shape, so its hull is a `ConvexPolygonShape3D` built from its six corners (rebuilt on resize). | `LotObject.ResizeCollision`, `LotObject.WedgeHullPoints` |
| D4 | **The event sensor mirrors the collider.** The sensor is a child of the part while a round part's size-as-scale lives on the collider child, so a resize re-pads the sensor shape **and** copies the collider's scale onto the sensor (box/wedge: scale 1). A `ConvexPolygonShape3D` has no pad primitive, so its points are pushed outward from the centroid by `SensorPad` — "touched" keeps meaning "touching the part". | `LotObject.RefreshEventSensor`, `MakeSensorShape` |
| D5 | **Per-part texture mapping: world-unit UVs by default, stretch as the legacy look.** `MeshUvMode.WorldUnits` (1.0 UV = 1 world unit × tiles-per-unit) is what a new part wears. `MeshUvMode.FaceStretch` maps each face/slope/cap to 0..1 — the exact look Godot's primitives gave a pre-migration part. Both are per-part state (`TextureTilesPerUnit` / `TextureStretch`) and both are declared properties, so the Inspector, undo, `lot.json` and Lua share one vocabulary. The factory's cache key carries the mode and the tiling, so a toggle never leaks a mesh between parts. | `PartMeshFactory`, `LotProperty`, `LotObject` |
| D6 | **An old lot loads in stretch mode, per record.** The record's `size` key is written by every new save; a record **without** it was written before this migration, and the loader sets `TextureStretch(true)` on that part instead of applying `One`, so an existing textured lot renders as it was saved (and its node scale still carries the extent, exactly as it did). Untextured, the two modes are indistinguishable — which is why the marker can be the key's presence alone. This is a v1-additive field: no format bump, following §4.1's rule that a new field is additive before any lot ships. | `LotArchive.LotNodeRecord` (`Size` + `HasExplicitSize`), `LotSceneLoad.CreateNode`, `LotSceneWalk` |
| D7 | **Undo carries the size beside the transform.** `TransformCommand` gained an optional `(LotObject, sizeBefore, sizeAfter)` pair (a second constructor keeps every existing call site source-compatible). A gizmo resize writes `Size` plus the compensating origin shift and commits one entry on release; the Inspector's Size row records one entry per edit session. A plain move/rotate entry never touches the size. | `builder/EditorUndo.cs`, `GizmoController`, `InspectorPanel` |
| D8 | **The gizmo's Scale mode is size-aware.** For a target with `HasSize` the same `RunScaleGizmo` math runs, but on the part's extent (`ref size` instead of `ref scale`), and the write is `SetSize` + `GlobalPosition`. The face handles (§2.7) read the live mesh's half extents, so a rotated, non-uniformly-scaled part keeps working; mesh-less targets (model groups) keep the legacy basis-scale path byte-for-byte. **The face drag's half-extent reference must come from the value domain, not the mesh** — `FaceScaleMath.SizeDomainHalfPerUnit` = 0.5 × the node scale on that axis. The first cut fed the live mesh's half, which made the drag size-dependent: a 4-unit part grew a quarter of the cursor's motion while the origin still shifted the full half, so it *slid away* instead of scaling (a unit part cancels the error, which is why it read as working). Found on the first client test, fixed here, and pinned by `PartSizeSelfTest`'s gizmo-domain checks. | `builder/gizmos/GizmoController.cs`, `FaceScaleMath` |
| D9 | **Legacy scale stays legal, everywhere, untouched.** `Lot.SetScale`, `GetScaleX/Y/Z`, the `scale` record field, archive write/read, the drag mover's basis copy and `MassForBody`'s `aabb * Scale` all keep their semantics; `Size` multiplies on top of a legacy scale visually (mesh at `Size`, node scale as authored). Same rule for the session snapshot: it captures `Size` **and** the transform, and the restore re-applies both. | `LotLuaApi`, `BuilderScene.DuplicateNode`, `LotSessionSnapshot` |
| D10 | **`SyncVisuals` copies position and rotation only.** A simulated sized round part's body carries the size as scale; copying the body's whole basis to the visual node would apply that size a second time. This was a latent bug the migration would have turned into a visible one ("the part explodes when you press Play"), so it is fixed here and called out in §6. | `LotConstraintSession.SyncVisuals` |
| D11 | **`TweenSize` rides the existing scheduler.** A new `LotTweenKind.Size` reads `part.Size` (refusing a node without one) and applies through `SetSize`, so the mesh follows every frame of the animation; `TweenScale` is untouched. The verb is sugar over the same `ScheduleTween` plumbing and returns the same cancel token. | `builder/lot/LotScheduler.cs`, `builder/script/LuaBootstrap.cs` |
| D12 | **The scratch VM keeps its minimal vocabulary.** `wedge(x,y,z)` was added to both sugar surfaces (entity sandbox + the `LuaManager` scratch chunk for parity with `cube`/`capsule`), but the `Lot.Tween*` verbs stay entity-sandbox-only, exactly like every other tween verb — the code-editor/command-strip VM never had them. | `LuaBootstrap`, `LuaManager` |
| D13 | **Authority (§1.3).** `Size` is lot state like any transform: a script may set it, and anything other players must see goes through `net.server`. Stated in the creator doc. | `docs/part-api.md` |

---

## 3. Execution flow

* **Editor resize (gizmo).** `GizmoController.RunGizmo` Scale case → the target has `HasSize` →
  `RunScaleGizmo(target, ref size, ref position)` (the unchanged Im3d/face math, now fed the extent)
  → `part.SetSize(size)` + `part.GlobalPosition = position` → on release `CommitGizmoDrag` compares
  the transform *and* the size and pushes one `TransformCommand` carrying both.
* **Editor resize (Inspector).** `Draw3DTransform` shows a `Size` row instead of `Scale` for a sized
  part → `SetSize` per edit frame → `CommitSize` pushes one entry per edit session.
* **Script resize.** `Lot.SetSize(handle, x, y, z)` → `AsPart` → `SetSize` (false ⇒ a `LotLuaApi`
  warning naming the kind).
* **Mesh regeneration.** `SetSize` → clamp → `RefreshGeneratedGeometry` →
  `PartMeshFactory.UpdatePartMesh` (cache key: shape, three rounded axes, tiles, UV mode) → the
  outline swaps its mesh reference → `ResizeCollision` (box native / round scale-as-size / wedge
  hull) → `RefreshEventSensor` (re-pad + mirror the scale) → `RefreshChildDecals` (each patch
  re-derives against the host's new mesh).
* **Save / load.** The walk records `Size` + `HasExplicitSize` for sized kinds; the loader reads the
  key's presence (`size` ⇒ apply `SetSize`, absent ⇒ an older lot ⇒ `SetTextureStretch(true)`);
  tiling/stretch ride the ordinary property bag.
* **Session.** The snapshot captures `Size` per sized entity and the restore re-applies it before the
  property pass, so Test mode's "an instance of the lot" guarantee covers resizing too.
* **Tween.** `Lot.TweenSize(...)` → bootstrap sugar → `ScheduleTween(..., "size", ...)` →
  `TryReadCurrent` snapshots the extent → `Advance` interpolates → `ApplyTween` calls `SetSize`.

## 4. Verification

* `PartSizeSelfTest` — 109 headless checks on detached fixtures: Create defaults per kind (unit
  extent, scale 1, factory mesh, hull type, tiling defaults), `SetSize` per kind (mesh, hull, outline,
  node scale untouched, legacy scale preserved, negative → magnitude), refusals (plane/capsule/decal)
  and clamps (ceiling, floor, non-finite, tiling floor/NaN), mesh sharing (same-size reuse, resize-back
  reuse, tiles/mode in the key), UV spans in both modes, the decal-follows-a-resized-host contract,
  the event sensor (pad delta, scale mirror, wedge hull offset, release), the archive record
  (kind tags both directions, real-encoder round trip, the missing-key migration marker, the capture
  walk), the size-aware `TransformCommand`, the Size tween on a fake scheduler, the two property
  declarations, and every new Lua verb through the real VM.
* Updated in the same change: `LotPropertySelfTest` (the part's declared set is now nine —
  `tilesPerUnit` / `stretchTexture` on a sized kind), `TweenSelfTest` (62 checks: the `TweenSize`
  sugar's token and validation contract on the private sandbox VM).
* Full headless run — see §5.

## 5. Limitations, stated rather than implied

* **Regeneration cost during a drag is measured, not guessed** (this machine, headless, via a
  temporary probe since removed): generating a mesh costs **~950 µs for a 32×16 sphere** (559 final
  vertices after `SurfaceTool.Index()`), **~140 µs for a 32-segment cylinder**, **~17 µs for a block**
  and **~14 µs for a wedge** — i.e. a face-drag of a sphere pays about 1 ms of extra frame time while
  it is being dragged (~6% of a 60 fps budget), while block/wedge drags are already noise. The split
  was measured too: ~65% is the per-vertex `SurfaceTool` interop (three engine calls per vertex),
  ~25% the engine's `Index()` + `GenerateTangents()` passes, ~10% managed math/allocation. Applied
  from that measurement: the generators reuse their grid buffers and one `SurfaceTool` (−10% on
  spheres, −25% on blocks, −9% on wedges, zero behaviour change — `PartMeshSelfTest` and every suite
  re-passed). **Flagged rather than applied:** pushing indexed vertices through `SurfaceTool.AddIndex`
  would roughly halve a sphere's regeneration (~1 ms → ~0.5 ms) but rewrites geometry whose winding,
  UVs and vertex counts are pinned by 76 checks — a deliberate rewrite, not a drive-by; and
  quantizing the *size* a drag writes would cut regenerations to step changes but visibly coarsens
  the mesh mid-drag (snapping, when enabled, already does exactly that).
* **Steady-state cost went down.** The generated meshes are lighter than the Godot primitives they
  replaced: a sphere is 559 vertices (32×16) versus `SphereMesh`'s default 64×32 = 2,145, and a
  cylinder uses 32 segments versus `CylinderMesh`'s 64. Nothing new runs per frame — regeneration
  happens only on an edit or a script's `TweenSize`, which is worth knowing when tweening many sizes
  at once (see `docs/part-api.md`).

* **Tessellation is deliberately coarse.** Generated spheres/cylinders use the factory's defaults
  (32 radial, 16 rings / 32 segments) instead of Godot's own primitive defaults (64 radial, 32 rings),
  because the factory has to build them by hand. Silhouettes stay round at gameplay scale; a
  creator who needs finer tessellation needs the factory to grow a segment parameter per part, which
  is not in this migration.
* **A mirrored host is not compensated for decals** (`DecalMath`'s existing declared limitation), and
  the wedge's face-scale spheres sit on its bounding-box faces (a `+Y` handle floats above the slope).
  Both are pre-existing/known; neither breaks the drag.
* **`FaceStretch` and world-unit UVs are per-part, not per-face** — a creator cannot stretch the top
  face and tile the sides. That granularity is out of scope.
* **A legacy-scaled part keeps its scale**; `Size` and scale therefore compose (visual = mesh ×
  scale). The editor never writes scale for a sized part (the Inspector offers Size), but a script
  can still set both, and nothing stops it.
* **The stretch fallback keys off the record, not the texture.** An old, *untextured* lot loads with
  stretch on for every sized part, so a texture applied later shows stretched — the state it would
  have had when it was saved. Unticking Stretch in the Inspector is the one-click fix and the
  Inspector shows the row.

## 6. Bugs the migration surfaced, fixed here

**`SyncVisuals` scale leak.** `LotConstraintSession.SyncVisuals` puppeted a simulated part by
copying the **whole** body transform onto the visual node. That was invisible while a body's scale
always equaled its part's authored scale. With round parts now carrying the size as the collider
child's scale, the copy would have applied that scale to the mesh node as well — a 3× sphere
rendered 9× the moment a session started. The fix copies position and rotation (the part keeps its
own authored scale), and the gravity probe plus the constraint suite pin that the visual and the
body stay coincident.

**The face-scale drag's half-extent reference (found on the first client test).** The §2.7 drag
arithmetic assumes `visual half-extent = reference × value`: in the old scale domain the reference
was the unit mesh's half (0.5), so one unit of cursor motion changed the value by one unit. The first
cut of the size wiring fed the part's *live mesh half* (e.g. 2 for a 4-unit part) while the value was
the size, so a +1 drag grew the size by only 0.25 while the origin still shifted 0.5 — the part slid
away instead of scaling up, and the effect scaled with the part (a unit part cancels the error, so
default cubes looked fine). `FaceScaleMath.SizeDomainHalfPerUnit` (0.5 × the node scale on that
axis, so a legacy `SetScale` still composes) is now what the controller feeds for sized targets, and
`PartSizeSelfTest` pins the arithmetic numerically: +1 motion → +1 extent, origin shifts by half, the
opposite face stays exactly put, and the old 4.25 value is asserted as wrong.

