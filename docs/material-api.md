# Material API — transparency and luminescence

**Audience:** creators writing lot scripts in Lua.
**Status:** implemented (Milestone 2.9). Both properties are **lot state**: they persist with the
lot, apply in build mode and in play alike, and are editable from the Inspector (under **Surface**)
or from Lua.

---

## 1. Transparency

```lua
local glass = Lot.SpawnCube(0, 4, 0)
Lot.SetTransparency(glass, 0.6)      -- 60% see-through
print(Lot.GetTransparency(glass))    -- 0.6
Lot.SetTransparency(glass, 0)        -- back to a solid part (the default)
```

* **0 is solid, 1 is invisible** (the same direction the Inspector shows). Values are clamped into
  `0..1`, never refused. A brand-new part starts at **0** and renders exactly as it always did.
* At `0` the part stays on Godot's **opaque** render path — nothing about an untouched part changed
  in this milestone. Above `0` the part moves to the alpha pass and its alpha becomes
  `1 - transparency`.
* **An invisible part is still there.** Picking and dragging use the collision shape, not the
  renderer, so a fully transparent part stays selectable in the editor and still blocks (or lets
  things pass, if `CanCollide = false`) in play. `Transparency` and `CanCollide` are independent:
  an invisible wall is a legitimate build.
* **A decal on a transparent host keeps its own surface.** The patch is a separate quad with its
  own `Opacity` property; a host's transparency never changes the patch.
* **Godot's transparent-pass sorting is a real limitation, stated rather than hidden:** alpha-pass
  objects are sorted per object (not per pixel), so overlapping transparent surfaces can sort
  imperfectly depending on the camera angle — and inside a single convex shape, its own back and
  front faces are not drawn back-to-front. Layers of glass thus can look order-dependent. This is
  the engine's behaviour and is not papered over.

## 2. Luminescence

```lua
local lamp = Lot.SpawnCylinder(0, 3, 0)
Lot.SetColor(lamp, 1, 0.85, 0.55)     -- a warm tint...
Lot.SetLuminescence(lamp, 1.5)        -- ...that the glow keeps
print(Lot.GetLuminescence(lamp))      -- 1.5
Lot.SetLuminescence(lamp, 0)          -- off (the default)
```

* **0 is a normal lit part; above 0 it glows.** The value drives both halves of the effect:
  * the part's own surface **emits its current colour** (a lamp keeps its tint — recolour the part
    and the glow follows), scaled linearly by the value;
  * the part contributes **real cast light** to the scene while it is one of the nearest glowing
    parts to the camera.
* **The cast-light pool is bounded, deliberately.** A lot keeps at most **8** real lights (the
  documented budget in `LotGlowLights.MaxActiveLights`) — an unbounded light per glowing part is a
  per-part cost the renderer would pay every frame. When more than 8 parts glow, the **nearest 8 to
  the camera** get a light; every other glowing part stays **self-lit** (its surface still emits)
  and simply casts no light. Nothing is switched off behind your back: far lamps read as glowing
  panels until you walk up to them.
* Lamps cast **no shadows** — that is part of the budget (the sun is the scene's shadow caster).
* Values above 0 are kept as authored (no upper clamp); `0..4` is the practical range, and the
  cast light caps out with the pool, not the value.
* Luminescence needs no session: lamps light in build mode too. A decal ignores both setters — a
  panel is imagery, not a light source.

## 3. Persistence and authority

* Both properties save into the lot's `lot.json` and come back on load; Test mode snapshots and
  restores them like every other authored value (a session that recoloured or dimmed a lamp cannot
  leak into the build lot). Undo/redo covers Inspector edits like any other property.
* **Authority (§1.3):** changing the look of a part other players must see is a gameplay change —
  do it inside `net.server`, exactly like position writes.
* The two properties are declared through the same property registry as every other part property,
  so a colour write (`Lot.SetColor`, the Inspector colour edit, a `TweenColor`) is **RGB-only**: it
  never resets transparency. Tweening transparency itself is not exposed yet — a follow-up
  (`TweenTransparency`) rather than a hidden behaviour.
