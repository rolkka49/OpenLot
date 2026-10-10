# Part API — size, texture mapping and the wedge

Creator-facing reference for the mesh-regeneration size migration. The decision record behind it is
`docs/design/mesh-size-migration.md`; the property/tween/transform verbs it composes with live in the
milestone docs (`tween-api.md`, `physics-api.md`, `net-api.md`).

## Sizes

A part's **size** is its extent in world units, centred on its origin. It is authored state on the
part (not the node's scale), so resizing regenerates the mesh — which is what keeps a texture at a
fixed density when a part grows.

```lua
local wall = Lot.SpawnCube(0, 2, 0)
Lot.SetSize(wall, 8, 4, 0.5)      -- 8 wide, 4 tall, half a unit thick
print(Lot.GetSizeX(wall))         -- 8
print(Lot.GetSizeY(wall))         -- 4
print(Lot.GetSizeZ(wall))         -- 0.5
```

Which kinds have a size:

| Kind | Size? | Notes |
|---|---|---|
| Cube | yes | |
| Sphere | yes | An ellipsoid extent; the collision shape follows it exactly. |
| Cylinder | yes | Elliptical cross-section if X and Z differ. |
| Wedge | yes | Right-angle ramp: X wide, Y tall at the back, Z deep. |
| Plane, Capsule | no | Fixed shapes; `SetSize` warns and does nothing. |
| Decal | no | Its geometry is a per-face patch, not an extent. |

Rules the verbs follow:

* Values are **magnitudes** — a negative axis takes its absolute value rather than mirroring the part.
* Values are clamped: each axis into `0.0001 .. 100000`, and a non-finite value falls back to the
  minimum. `SetSize` on a kind without a size warns (the Output window) and returns without a change.
* `GetSizeX/Y/Z` on such a kind warns and returns `0`.
* `Lot.SetScale` still exists and still works (it is a plain transform), but `Size` is what the
  editor's gizmo and Inspector write, and what keeps textures consistent. Prefer `SetSize` in scripts.
* A part built by an old OpenLot version loads with its size carried by `scale`, exactly as it was
  saved; resizing it with `SetSize` from then on keeps that scale multiplying the mesh.

## Texture mapping

Two per-part properties control how a texture sits on a part's generated mesh — settable from the
Inspector (under **Part**) or from Lua:

```lua
Lot.SetTilesPerUnit(part, 2)      -- 2 repeats per world unit; default 1
print(Lot.GetTilesPerUnit(part))  -- 2

Lot.SetTextureStretch(part, true) -- stretch the image across each face (0..1) instead
print(Lot.GetTextureStretch(part))
```

* `Tiles / unit` (tilesPerUnit) — repeats per world unit. On a 4×2 wall with `1`, an image covers one
  unit and repeats 4 times across; with `2` it repeats 8 times. Values ≤ 0 clamp to a small floor.
* `Stretch` (stretchTexture) — the pre-migration look: each face, cap and slope shows the whole image
  once. Use it to reproduce a lot built before world-unit UVs.
* Both apply to the sized kinds only (their UVs are baked into generated meshes); the verbs warn and
  do nothing elsewhere.
* **Old lots load with Stretch on.** A lot saved before this feature has no tiling information, so
  its parts render stretched exactly as they were authored. Untick **Stretch** on a part (or call
  `Lot.SetTextureStretch(part, false)`) to switch it to world-unit tiling.

## The wedge

A right-angle ramp: `size.X` wide, `size.Y` tall at the back, `size.Z` deep. The vertical wall faces
**-Z**; the slope descends from the wall's top edge toward the **+Z** bottom edge. Its collision is an
exact convex hull of its six corners.

```lua
local ramp = Lot.SpawnWedge(0, 1, 0)   -- bound method
local ramp2 = wedge(4, 1, 0)           -- sugar, same thing
Lot.SetSize(ramp, 6, 2, 4)             -- 6 wide, 2 tall, 4 deep ramp
```

There is also a **Wedge** button in the Toolbox's Create Part section.

## Animating a size

```lua
-- Smoothly grow a platform from its current size to 12 x 1 x 12 over two seconds:
local token = Lot.TweenSize(platform, 12, 1, 12, 2.0, 'quadOut')

-- token() cancels it early:
token()
```

`TweenSize(handle, x, y, z, duration, easing)` interpolates the part's size — the mesh (and its
world-unit texture density) follows every step — and returns a cancel token like the other tween
verbs. It refuses a node without a size, which is reported in the Output window. `TweenScale` remains
available and untouched.

> **Performance note.** A size tween regenerates the part's mesh each frame it runs: about 0.95 ms for
> a 32×16 sphere, 0.14 ms for a cylinder and under 0.02 ms for a box or wedge (measured headless —
> see `docs/design/mesh-size-migration.md` §5). Tweening one or two sizes is comfortably within a
> frame; tweening dozens of spheres at once is the case to watch, and animating position or colour
> (which never regenerates geometry) is the cheaper choice for bulk motion.

## Sessions, persistence and authority

* **Size is lot state.** It saves into the lot's `lot.json` (with the texture mapping) and is restored
  on load; Test mode snapshots and restores it like every other authored value.
* **In play**, size is only physics-relevant the way it always was: an unanchored part's body wears the
  same extents the mesh does, so friction/bounce/mass behave exactly as before the migration.
* **Authority (§1.3).** Resizing a part other players must see is a gameplay change: do it inside
  `net.server` so the host decides it, exactly like position writes.
