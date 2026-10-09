# OpenLot scripting — input actions

**Audience:** creators writing lot scripts in Lua.
**Status:** implemented (Milestone 3.9). Input reads work while a player session (Test/Game mode)
is running; while you are building they answer `false`/`0`, so your editor keystrokes never leak
into scripts.

---

## 1. Reading input

```lua
function onFrame(dt)
    if Lot.WasActionPressed("jump") then
        -- exactly one frame per press: the safe place for one-shot reactions
    end
    if Lot.IsActionPressed("moveForward") then
        -- held this frame
    end
    if Lot.WasActionReleased("interact") then ... end
    local push = Lot.GetActionStrength("moveForward")  -- 0..1 (gamepad stick included)
    local dx = Lot.GetMouseDeltaX()   -- raw mouse motion this frame (pixels)
    local dy = Lot.GetMouseDeltaY()
    local wheel = Lot.GetMouseWheel() -- +1 per up notch, -1 per down
end
```

All reads answer from **one snapshot taken each frame**, so two scripts can never disagree about
what was pressed this frame, and the reads are cheap (no engine calls per question).

## 2. The actions and their default keys

| Action | Keyboard | Gamepad |
|---|---|---|
| `moveForward` / `moveBackward` / `moveLeft` / `moveRight` | W / S / A / D | left stick |
| `jump` | Space | A |
| `sprint` | Shift | Left shoulder |
| `interact` | E | X |

These are the **same bindings the character and the build-mode camera already use**: when
rebinding arrives (the settings screen, later milestone), one rebind will move the player, the
camera and every script together — scripts never see a physical key, only the action.

## 3. Rules worth knowing

* **Sessions only.** During build mode every read is `false`/`0` — editor keystrokes belong to
  the editor.
* **Just-pressed / just-released are exactly one frame.** A press that starts and ends between
  two frames is seen as a press for one frame (never lost, never doubled).
* **Unknown action names** are reported in the Output window and answer `false`/`0`, so a typo is
  visible instead of silently doing nothing.
* **Gamepad** currently arrives through these same reads (movement and jump already work);
  full controller support, remapping UI and console-style navigation are their own later
  milestones.

## 4. The authority rule (read this before building on input)

Raw input is **client-local**. A script may read it freely, but anything with consequences —
spending currency, scoring, kicking, granting an item — must be decided by host-authoritative
`net.server` code. A client's reported input is never trusted for those decisions, exactly like
every other client-supplied value. Think of input as "what the player asked for", and the host's
`net.server` handler as "what actually happened".
