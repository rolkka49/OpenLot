# OpenLot scripting — camera control

**Audience:** creators writing lot scripts in Lua.
**Status:** implemented (Milestone 3.10). Camera modes work inside a player session (Test/Game
mode); outside one every call refuses politely — the camera belongs to the session, and no camera
state ever lands in the saved lot.

---

## 1. The modes

```lua
Lot.SetCameraMode("firstperson")   -- at your character's eye; your own mesh is hidden
Lot.SetCameraMode("thirdperson")   -- the default orbit behind the character
Lot.SetCameraMode("fixed")         -- hold a pose and ignore everyone
Lot.SetCameraMode("scripted")      -- refused directly: Lot.FlyCamera runs shots
Lot.GetCameraMode()                -- "thirdperson" / "firstperson" / "fixed" / "scripted"
```

* **Third person** is what a session starts in — nothing changes unless you ask.
* **First person** uses the same look (mouse drag) as third person, just from the character's eye.
* **Fixed** holds a position you give it, looking at a target you give it:

```lua
Lot.SetCameraPosition(30, 5, 30)
Lot.SetCameraTarget(30, 1, 30)
Lot.SetCameraMode("fixed")
```

  If you switch to fixed without setting a side, that side is captured from wherever the camera
  currently is — so "fixed" first means "hold where you are", never a jump to the origin.

## 2. Cutscene shots

```lua
local shot = Lot.FlyCamera(60, 10, 60, 60, 0, 60, 0.6, "sineInOut")
-- fly to (60,10,60) looking at (60,0,60) over 0.6 s, then control returns to the
-- mode that was active before the shot
shot()   -- cancel: the camera freezes exactly where it is (that pose becomes "fixed")
```

* The easing names are the same set the tween verbs use (`linear`, `sineIn` … `cubicInOut`).
* One shot at a time — starting another replaces it; a token for a shot that already ended is a
  harmless no-op returning `false`.
* A **finished** shot returns to whatever mode was active before it; a **cancelled** shot freezes
  in place instead, so you can leave the camera on a dramatic angle and then decide what happens
  next (`SetCameraMode` away whenever you like).

## 3. What to know

* **Sessions only.** Entering Test/Game mode always starts in third person with the character
  visible; leaving restores the build-mode camera. There is nothing to undo and nothing saved.
* **The character still plays normally** in every mode — first person changes where the camera is,
  not what the character does.
* The camera's FOV, mouse sensitivity and invert-Y options are the client settings milestone's
  territory (they apply to all modes at once, since every mode shares the same camera and look).
