# Milestone 3.14 — Player camera zoom & first-person blend

**Status:** implemented — verified headless (see §4).
**Related:** §3.10 (the camera modes and the handoff this builds on) · §3.9 (`LotInputActions.Wheel`,
the only input path) · §3.4 (the `PlayerCameraActive` gate) · §2.9 (the transparency material the
mesh fade writes) · §5.4 (will surface the exposed fields as settings).

## 1. What the player gets

Wheel up pulls the orbit in, wheel down pushes it out. Between `BlendStart` (3.5 m) and `ZoomMin`
(1.0 m) the view is a smooth mix of the orbit pose and the eye pose, and the character's own mesh
fades with it — so zooming all the way in *becomes* first person with no snap, and zooming back out
lands on exactly the orbit it left. Nothing about it is script API or lot state: it is player view,
session-only.

## 2. Decisions

| # | Decision | Where it lives |
|---|---|---|
| D1 | **Wheel = one damped target.** `AddZoom(steps)` moves `ZoomTarget` (one notch = `ZoomStep` metres, clamped to [`ZoomMin`, `ZoomMax`]); `Distance` glides toward it through `LotCameraMath.Damp` (exponential, snapping exactly once within 1e-4 — the "lands exactly on the previous orbit" contract). All fields are public data so §5.4's camera settings can own them later. | `OrbitalCamera`, `LotCameraMath.Damp` |
| D2 | **The blend band.** Below `BlendStart`, the pose is `smoothstep(BlendFactor(Distance, BlendStart, ZoomMin))` between the orbit pose and `LotCameraMath.FirstPersonPosition` (position and look point both lerped), so there is no mode change and no seam in either direction. | `OrbitalCamera._Process`, `LotCameraMath` |
| D3 | **First person is the band's inner end.** A script-set `FirstPerson` pins `Distance = ZoomMin` every frame, so the scripted pose and the wheel's innermost pose are the *same pose*; any outward wheel step is the player's documented way back, switching to `ThirdPerson` at the boundary with no jump. | `OrbitalCamera` |
| D4 | **Fixed and scripted cameras are zoom-proof** (`AddZoom` refuses outside third/first person; a running shot owns the pose). | `OrbitalCamera.AddZoom` |
| D5 | **Gating unchanged.** The wheel is read only where the look input is read (`_input != null && _input.PlayerCameraActive`), so a modal denies zoom exactly as it denies look — the §3.4 handoff truth table is untouched. `AddZoom` is the single entry point both `_Process` and the self-test funnel through. | `OrbitalCamera._Process` |
| D6 | **The mesh fade is session-only and direct.** `BuilderScene.ApplyCharacterFade` writes the character's §2.9 transparency each frame (skipping sub-visible changes) from the authored value captured on session entry (`_sessionCharacterTransparency`) toward 1 at full blend — **never through the property service**, so a view effect makes no undo entry and never touches the dirty flag. The session snapshot restores the authored value on exit regardless of where the fade left it. | `BuilderScene` |
| D7 | **Session-only, reset on both boundaries.** `ResetModes` (called on entry and exit) returns mode, `Distance` and `ZoomTarget` to `ThirdPerson`/`DefaultDistance`; nothing enters `lot.json`. | `OrbitalCamera.ResetModes` |

## 3. Scope honestly stated

* **Wheel only** — no gamepad zoom binding ships in the first cut (the roadmap said "decided at
  implementation, shipped off by default if it conflicts with look"; a right-stick-click or
  shoulder-button binding can be added with §16.1 without changing anything here).
* **§5.4's settings rows are not built** (that screen does not exist yet): the data they will own
  is already exposed as public fields with defaults.
* The fade is a material-alpha ramp, not a dithered dissolve — a transparent-sorting artefact at
  extreme angles is possible and is an eye-check item (the project's usual split); no geometry or
  collision changes with it.

## 4. Verification

* `CameraSelfTest` pure additions: the blend factor (0 at/above the band, 1 at/past the inner end,
  halfway in the middle), smoothstep endpoints/midpoint/clamps, and the glide (exact snap, no-op
  on zero delta or rate, monotone approach, exact landing after settling).
* The staged probe gained a second session after the existing one: six wheel notches in → the
  glide settles at exactly `ZoomMin`, the pose is the eye (`< 0.01` from
  `FirstPersonPosition`), the mesh transparency is ≥ 0.95; six notches out → `Distance` is
  **exactly** `DefaultDistance` and the mesh is back to its authored transparency; a fixed camera
  ignores a wheel step; a script-set first person pins fully in and a wheel-out takes the view
  back; three notches out settles at 4.0 with the fade gone; leaving the session resets mode and
  distance and returns the freecam.

**Result:** `CameraSelfTest` — 17 synchronous checks (8 new zoom-math checks) + a 26-check staged
probe (12 new zoom checks across a second session, all passing with exact numbers: settled at 1.0
and exactly 7.0/4.0, the pose at the eye within 0.01 m, mesh transparency 0.999 → 0 → 0.0004).
Full headless run: **1,907 checks, 0 failures** (1,887 pre-existing + 20).
