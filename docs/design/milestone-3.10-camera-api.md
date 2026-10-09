# Milestone 3.10 — Camera API

**Status:** implemented — verified headless (see §4).
**Audience:** whoever maintains the scripting layer; the creator-facing half is `docs/camera-api.md`.

## 1. What the creator gets

```lua
Lot.SetCameraMode("firstperson")          -- "thirdperson" (default) / "firstperson" / "fixed"
Lot.SetCameraPosition(30, 5, 30)          -- where the fixed camera sits
Lot.SetCameraTarget(30, 1, 30)            -- what it looks at
local shot = Lot.FlyCamera(60, 10, 60, 60, 0, 60, 0.6, "sineInOut")  -- a cutscene move

---

## 2. Decision table

| # | Decision | Where it lives |
|---|---|---|
| D1 | **Modes on the existing camera, not a second stack.** The player session keeps one `OrbitalCamera`; the mode decides where it sits and what it looks at. Third-person is byte-for-byte today's behaviour. | `OrbitalCamera`, `builder/LotCameraMath.cs` |
| D2 | **First person shares the same look input** — the existing yaw/pitch drag — so there is no second input path and §5.4's sensitivity later applies to both modes at once. The camera parks at `target + 1.6` (eye height above the 2.0-tall capsule's centre) and the character's own mesh is hidden while the mode is on (you must not see the inside of your head); the physics body is untouched. | `OrbitalCamera`, `BuilderScene.ApplyCameraModeVisibility` |
| D3 | **Fixed means fixed**: the camera holds a creator-set position, looking at a creator-set target, and follows nobody. Entering fixed without setting a side captures that side from the current pose ("switch to fixed" always means "hold where you are" first). | `OrbitalCamera.SetMode/SetFixedPosition/SetFixedTarget` |
| D4 | **Shots reuse the tween easing engine** (§3.8's `LotEasing`) — same curves, one engine, two customers. `Lot.FlyCamera(x,y,z, tx,ty,tz, duration, easing?)` flies from the current pose to the given one and returns to the mode that was active when it ends. `duration 0` completes on the next frame. | `OrbitalCamera.StartShot/AdvanceShot` |
| D5 | **Cancel freezes in place.** The shot token's cancel stops the interpolation and converts the current pose into the fixed pose — "the camera simply stops" — rather than snapping anywhere. A natural end returns to the previous mode instead. One shot at a time; a new shot replaces the running one; a stale token's cancel is a harmless false. | `OrbitalCamera.StopShot` |
| D6 | **Session-only.** Camera modes live with the player session: entering Test/Game mode always starts in third person with the character visible, leaving resets everything, and no camera state enters `lot.json` (it is a view, not an edit — the §2.5 principle). Every API call outside a session refuses with a readable message. | `BuilderScene.EnterTestMode/ExitTestMode`, `LotLuaApi` |
| D7 | **`"scripted"` is entered only by FlyCamera.** `SetCameraMode("scripted")` is refused with a message pointing at FlyCamera — a shot with no path would be a mode with no meaning. | `LotLuaApi.SetCameraMode` |
| D8 | **The look gate denies input, not the pose.** The ImGui-resolved `PlayerCameraActive` gate still arbitrates who reads look input (the freecam/camera handoff), but the pose now updates whenever the camera is `Current` — a modal must not freeze the view or detach it from the character, and the headless probes (where the layout-derived gate never resolves) can still verify every mode. This is a deliberate, documented behaviour refinement of §3.4's handoff (the §3.4 tests still pin the authority truth table; nothing there asserted "pose frozen behind a modal"). | `OrbitalCamera._Process` |

---

## 3. Execution flow

* One frame, session active: the script (or `onFrame`) calls the verbs; `LotLuaApi` validates and
  routes to the camera; the camera's `_Process` expresses the mode — pose always, look input only
  through the shared gate.
* A running shot advances on the frame delta with the tower's easing, exactly like a tween, then
  hands the mode back.

## 4. Verification

* `CameraSelfTest` — pure: mode-name parse/refuse/round-trip, the first-person eye math.
* Private-VM Lua: `Lot.FlyCamera` schedules through the bound method and returns a token; the
  token cancels that shot; unknown easing and negative durations raise.
* Staged session probe (headless): a script switches to first person (camera at the eye, mesh
  hidden) → fixed (camera exactly at the given pose, mesh back) → flies a shot (scripted mode,
  strictly between the endpoints mid-flight) → the shot ends back in fixed → a second shot is
  cancelled mid-flight and the camera **freezes where it is** (stable across frames) → leaving the
  session returns the freecam and the next entry starts from third person with the character
  visible.
* Run: `godot --headless --quit-after 6000 res://builder/builder.tscn` (the probe chain's wall
  clock; a short run warns "never finished").
shot()                                    -- cancel: the camera freezes exactly where it is
Lot.GetCameraMode()                       -- "thirdperson" / "firstperson" / "fixed" / "scripted"
```
