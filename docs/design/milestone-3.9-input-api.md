# Milestone 3.9 — Input API

**Status:** implemented — verified headless (see §5).
**Audience:** whoever maintains the scripting layer; the creator-facing half is `docs/input-api.md`.

## 1. What the creator gets

```lua
if Lot.IsActionPressed("jump") then ... end          -- held this frame
if Lot.WasActionPressed("interact") then ... end     -- true for exactly one frame
if Lot.WasActionReleased("moveForward") then ... end
local strength = Lot.GetActionStrength("moveForward") -- 0..1 (gamepad stick included)
local dx = Lot.GetMouseDeltaX()                       -- look delta gathered this frame
local wheel = Lot.GetMouseWheel()
```

Default actions — the same keys the character already uses: `moveForward` / `moveBackward` /
`moveLeft` / `moveRight` (WASD), `jump` (Space), `sprint` (Shift), `interact` (E); gamepad:
left stick = the four move actions, A = jump, X = interact.

## 2. Decision table

| # | Decision | Where it lives |
|---|---|---|
| D1 | **Logical actions on Godot's InputMap.** `LotInputActions` registers the seven action names once (`openlot_moveForward` …) with default events (physical keys + joypad axes/buttons) on first use. Godot's own mapping machinery is the right layer for key mapping — no dependency, no hand-rolled matcher, and §5.5's rebinding UI later has a real store to write to. | `builder/LotInputActions.cs` |
| D2 | **One poll per frame, reads are cache lookups.** The tick reads every action once per frame plus the accumulated mouse deltas/wheel, and computes held / pressed-this-frame / released-this-frame by comparing against last frame. A script read never touches `Input`, and two scripts in one frame can never see different states. Just-pressed/released are true for exactly one frame. | `LotInputActions.Poll`, `ScriptRuntime.Tick` |
| D3 | **Session-gated reads** (consistent with §3.7 D1 / §3.8 D8): outside a player session every read answers `false`/`0`, never an error — while you are editing, your keystrokes belong to the editor, not to scripts. Entering/leaving a session resets the snapshot so no stale "pressed" survives the boundary. | `LotInputActions.Reset`, `BuilderScene` |
| D4 | **Creator API is six reads and nothing else**: `IsActionPressed`, `WasActionPressed`, `WasActionReleased`, `GetActionStrength`, `GetMouseDeltaX/Y`, `GetMouseWheel` — primitives only, unknown action names warn and answer false/0. No new per-frame call path: the reads are what a script asks *about* the cache, not new polling. | `LotLuaApi` |
| D5 | **The two existing controllers move onto the action layer** (`CapsuleController`'s WASD/Space, `FreecamController`'s WASD/Space/Shift) with identical defaults — visually nothing changes today, but from §5.5 on one rebind moves the character, the freecam and every script together. The existing staged probes (which walk the character with synthetic input) are the regression that this refactor didn't change movement. | `CapsuleController`, `FreecamController` |
| D6 | **Rebinding is in-memory now; persistence and the UI land in §5.5.** `LotInputActions.Rebind(name, key)` swaps the action's events; §5.5 wires it to the settings screen and `user://openlot_settings.cfg` (§5.4's file). | `LotInputActions.Rebind` |
| D7 | **Authority rule, decided here (roadmap requirement):** raw input is client-local and scripts may read it freely, but anything with enforcement impact must be decided by host-authoritative `net.server` code — a client's reported input is never trusted for moderation/economy decisions (§1.3). Stated in the creator docs, not just here. | `docs/input-api.md` |
| D8 | **Mouse state comes from raw motion accumulation** (`BuilderScene._Input` collects `InputEventMouseMotion` deltas and wheel events; the poll snapshots and zeroes them). The cameras keep their own ImGui-drag look deltas — scripts get the raw frame motion, which is what a first-person or custom camera script wants. | `BuilderScene._Input` |
