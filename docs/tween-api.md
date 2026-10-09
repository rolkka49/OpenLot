# OpenLot scripting — tweens and timers

**Audience:** creators writing lot scripts in Lua.
**Status:** implemented (Milestone 3.8). Tweens and timers run while a player session (Test/Game
mode) is active; outside a session they are recorded but idle, and entering or leaving a session
re-runs your scripts, so nothing scheduled survives a reload.

---

## 1. Tweens

```lua
local t = Lot.TweenPosition(part, 20, 5, 20, 1.5, "quadInOut")
t()  -- cancel: the part stays where it is
```

* `Lot.TweenPosition(handle, x, y, z, duration, easing?)` — moves a part (or a group node).
* `Lot.TweenRotation(handle, x, y, z, duration, easing?)` — degrees.
* `Lot.TweenScale(handle, x, y, z, duration, easing?)`.
* `Lot.TweenColor(handle, r, g, b, a, duration, easing?)` — parts and UI elements.
* `Lot.TweenUIRect(handle, x, y, w, h, duration, easing?)` — UI elements; the same clamping as
  `SetUIRect` applies, so a tween can never leave the picture frame.

Each returns a **cancel token** (call it to stop the tween where it is). `duration` is in seconds;
`0` finishes on the next frame. The starting point is the target's value when you call — there is
no "from" parameter.

**Easing names:** `linear` (default), `sineIn`, `sineOut`, `sineInOut`, `quadIn`, `quadOut`,
`quadInOut`, `cubicIn`, `cubicOut`, `cubicInOut`. A typo raises in your script immediately.

## 2. Timers

```lua
local once = After(2, function() ... end)        -- fires once, two seconds from now
local loop = Every(0.5, function() ... end)      -- fires every half second
once()   -- cancel (returns true if it was still scheduled)
loop()   -- cancel from anywhere, even from inside the callback itself
```

The callback runs as its own watchdog unit, exactly like a `net.*` handler: one runaway timer
trips alone and three trips disable that entity's handlers for the session.

## 3. What to know

* **No per-frame Lua.** Tweens are interpolated in C# — an animating lot costs your scripts
  nothing, and nothing stops you combining tweens with `onFrame` for your own logic.
* **Cancel tokens are safe to call late**: after a tween completes or a timer fires, calling its
  token is a harmless `false`.
* **Destroying an entity** cancels what it animated and what it timed; leaving the session clears
  everything.
* **A cap of 256 live tweens+timers** applies per lot (a runaway script cannot grow without
  bound); past it the call is logged and the token is a no-op.
* A repeating timer fires **at most once per frame** even if the frame was long, so a lag spike
  never bursts a pile of callbacks at once.
