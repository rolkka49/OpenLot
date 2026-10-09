# OpenLot scripting — `net.*` and cross-entity calls

**Audience:** creators writing lot scripts in Lua.
**Status:** implemented for the offline/single-player path (Milestone 3.2). Real peer-to-peer
transport arrives with §7 networking; everything below is already written so creator code does not
change when it lands.

---

## 1. The model

* **One script per entity.** A script is a `.lua` file attached to a lot node. An entity is any lot
  node that has a script under it.
* **The lot root is itself an entity** and always has **handle 0**. Its script runs when the lot
  loads — that is how a new lot spawns its character and camera.
* Every entity has an **integer handle**, assigned in deterministic lot order (lot root = 0, then
  objects in tree order, then UI elements). The script learns its own handle from `this.Handle`.
* Each script runs in its **own sandbox environment**: globals you set are yours, they cannot leak
  into another entity's script.
* Networking is expressed as **plain function declarations and plain function calls**:

```lua
-- Door.lua
net.server.Open = function(who)
    opened = true                 -- authoritative state change, runs on the host only
    net.client.PlayCreak()        -- one-shot effect for every player
end

net.client.PlayCreak = function()
    creakSound = "playing"        -- local-only, no authority implied
end
```

```lua
-- Button.lua (a different entity)
net.server.Press = function()
    Lot.CallServer(1, "Open", "button")   -- 1 = the door's handle
end
```

No remote-event objects, no event names as strings, no `if isServer then ... else ... end` in
creator code.

## 2. Declaring handlers

* Only **functions** can be declared: `net.server.Door = 42` is an error.
* Declaring replaces silently (that is also what makes a script re-run clean).
* If **two scripts on the same entity** declare the same name, the console warns and names both
  files; the last one wins.
* `net` itself is read-only, and `net.server` / `net.client` are namespaces, not callable tables.
* Calling a handler looks like a normal call: `net.server.Open("button")`.

## 3. Authority rules

| Call | Who runs it |
|---|---|
| `net.server.X(...)` | **Only the host.** A client's call is transmitted to the host and runs there. |
| `net.client.X(...)` | The host broadcasts it to connected clients **and also runs it locally** (the host is a player too). A client calling `net.client.X` runs it for its own view only — clients never route to other clients. |

Rules of thumb:
* Put **state changes** in `net.server` — the host is the authority (clinerules §1.3).
* Put **one-shot effects** (sounds, animations, particles) in `net.client`.
* Calls are **fire-and-forget**: return values are not sent back. Offline the call is effectively
  local, so do not write code that depends on a `net.server` function's return value.

## 4. Who called me? `net.sender` and `net.fromPlayer`

Inside a handler:

* `net.sender` — the **peer id of whoever originated the call chain** (a real id; in a local/offline
  session it is `1`). Use it to validate the caller, e.g. "is that player's character near the door?"
  You can trust it: for calls arriving from other peers the host overwrites it from transport
  metadata, never from the payload.
* `net.fromPlayer` — `true` when a **player interaction** originated the chain, `false` for
  everything autonomous.

**`fromPlayer` is `false` for autonomous calls in every mode — including single-player.** Lot load,
  `start()`, timers, and any script logic that just runs all count as autonomous. A call is only
  `true` when a real interaction started it (input handling), which Test mode can simulate with
  `LuaManager.Instance.NetBridge.SimulatePlayerInteraction(...)`. Do not use `fromPlayer` to mean
  "is this a client": use `net.sender` for identity and `net.server` for authority.

Both are `nil` / `false` **outside** a handler, and both are **inherited transitively**: if your
handler triggers another call, that call still carries the original caller's identity.

## 5. Cross-entity calls

```lua
Lot.CallServer(handle, name, ...)   -- run the TARGET entity's net.server.name
Lot.CallClient(handle, name, ...)   -- run the TARGET entity's net.client.name
```

* Same routing rules as §3: a client's `CallServer` is transmitted to the host; the host's
  `CallServer` is queued locally; a host's `CallClient` broadcasts (and runs locally); a client's
  `CallClient` runs locally only.
* Arguments behave exactly like `net.*` arguments (§6) and are validated at the call site.
* Unknown handle or unknown function name → the call is dropped with a console warning. Nothing
  throws into your script; check the console while developing.
* Handles come from the lot itself. The lot root is always `0`; other handles follow lot load order.

## 6. Arguments: what crosses the wire

**Allowed:** `nil`, booleans, numbers, strings, and tables of those (nesting up to 8 levels) whose
keys are strings or integers.

**Rejected at the call site** (nothing is transmitted), with a clear error:
functions, userdata/threads, cyclic tables, tables that carry a metatable, `NaN`, non-integral
number keys (e.g. `t[1.5]`), boolean/table keys.

**Limits:**

| Limit | Value |
|---|---|
| Arguments per call | 16 |
| Payload | 16 KB |
| One string | 8 KB |
| Table nesting | 8 levels |
| Values per call (node budget) | 4096 |
| Pairs per table | 256 |

### `nil` holes are preserved

`net.server.Foo(1, nil, 3)` arrives as **three** arguments, with the second one `nil`. To *count*
arguments in a handler, use a vararg signature — otherwise you cannot tell "3 args with a hole"
from "2 args":

```lua
net.server.Foo = function(a, b, c, ...)
    local count = select("#", a, b, c, ...)   -- 3
end
```

### Numbers cross as doubles

Every number is transmitted as an IEEE double, so a value that started as the integer `3` arrives as
the float `3.0`:

```lua
net.server.Foo = function(n)
    log(tostring(n))    -- prints "3.0"
    log(n == 3)         -- prints "true"
end
```

`print` works too and is the same call: it joins its arguments with tabs, exactly like standard Lua
`print`, so `print("x", 1)` shows `x    1` in the Output window.

Comparisons and arithmetic behave normally. Precision is exact up to 2^53. `±math.huge` is allowed;
`NaN` is rejected (it is almost always a bug, and NaN ≠ NaN breaks reasoning).

## 7. What happens when a script misbehaves

* **Instruction budget:** 200,000 instructions per dispatched handler, 1,000,000 per script load
  (chunk + `start()`), 4,000,000 per frame across all handlers. Exceeding it raises
  *script execution limit exceeded* / *frame execution limit exceeded*.
* A limit trip is reported as a **warning** naming the chunk (not an engine error): the watchdog did
  its job, and the editor's Run status line shows the message next to the buffer. Genuine script
  errors (syntax/runtime) are still reported as errors.
* `pcall`/`xpcall` **cannot** swallow a limit trip.
* **Circuit breaker:** after 3 limit trips, that entity's `net` handlers are disabled for the
  session and the console warning names the entity and its script file.
* **Memory cap:** the lot's Lua VM is capped (64 MB). Exceeding it raises *not enough memory*, and
  the VM is rebuilt at the frame boundary (scripts reload; in-memory script state is lost).
* **Queue and rate limits:** 1024 queued calls; the host drains at most 256 per frame, and remote
  peers are rate-limited (120 calls/s, 64 KB/s each) — excess is dropped with a throttled warning.

See `docs/design/milestone-3.2-unified-script-model.md` for the exact constants.

## 8. Events are not state (late joiners)

`net.*` calls are **transient events**. A player who joins *after* `net.client.PlayCreak()` ran will
never see it, and that is correct. Keep current world state (object existence, positions, colors,
"is the door open") out of one-shot calls and let state sync (§7) replicate it. The pattern:

```lua
net.server.Open = function()
    doorOpen = true              -- state (replicated by §7 sync when it lands)
    net.client.PlayCreak()       -- event (late joiners simply miss it)
end
```

## 9. The sandbox in one look

**Available:** `math`, `string`, `table`, `utf8` (read-only), `pairs`, `ipairs`, `next`, `type`,
`tostring`, `tonumber`, `select`, `error`, `pcall`, `xpcall`, `rawget`, `rawequal`, `rawlen`,
`setmetatable`, `getmetatable`, `unpack`, `log`, `Lot.*`, `net.*`, `this`.

**Not available (deliberately):** `io`, `os`, `require`, `dofile`, `loadfile`, `package`, `debug`,
`load`, `collectgarbage`, `coroutine`, `print` (use `log`), `rawset`, `_G` beyond your own env.

**Guards:** libraries are read-only (`math.floor = nil` errors), `string.rep` output is capped at
1 MB (also via `("x"):rep`), metatables containing `__gc` are refused, `_G` *is* your script's own
environment, and `this` / `net` are read-only.

**Coming later, not yet available:** real networking (§7), state sync, coroutines.

## 10. Where scripts run, and when

Scripts are loaded **once, when the lot loads** (in this order per script: the file's top-level
code, then its `start()` if defined). Attaching a script while editing does not run it until the lot
reloads — Test mode (§3.4) will make that hand-off explicit.

**Entities you spawn while your script is loading belong to it.** If the lot ever restarts scripts
(an out-of-memory recovery rebuild, or a later Test-mode reload), the entities your load spawned are
removed first and your script runs again — so spawning the character and camera at the top of a root
script is safe and will not duplicate them. Entities spawned later (inside a handler) are **not**
owned: they survive a restart but their script state does not, so re-create them from the load path
if they matter.

**When something goes wrong:** a script that runs out of memory while loading is *quarantined* (it is
skipped until you edit the file or reload the lot), and repeated out-of-memory rebuilds *suspend
scripting* for the session — the Viewport window then shows a red **SCRIPTING SUSPENDED** banner with
the reason, and scripts stay disabled until the lot is reloaded.

## 11. Part properties (`Lot.*`, milestone 2.3)

The Inspector edits these; the same state is readable and writable from a script. Everything is
addressed by the object's handle, so nothing Godot-side ever reaches your code.

```lua
Lot.SetAnchored(handle, false)   -- unanchored: affected by gravity in a player session
Lot.SetCanCollide(handle, false) -- solid things pass through it
Lot.SetTexture(handle, assetId)  -- apply an image by asset id
Lot.SetCollisionGroup(handle, "Terrain")
```

| Call | What it does |
|---|---|
| `Lot.SetAnchored(handle, bool)` | Anchored parts stay in place; unanchored parts are affected by gravity in a player session. |
| `Lot.IsAnchored(handle)` | Returns whether the part is anchored. |
| `Lot.SetCanCollide(handle, bool)` | When off, solid things pass through the part. It stays selectable and draggable in the editor. |
| `Lot.GetCanCollide(handle)` | Returns whether the part blocks other objects. |
| `Lot.SetTexture(handle, assetId)` | Applies an image; returns `true` on success and `false` for an unknown id. `""` clears it. |
| `Lot.GetTexture(handle)` | Returns the part's texture asset id, or `""`. |
| `Lot.SetCollisionGroup(handle, group)` | Puts the part in a named collision group (milestone 3.5). An unknown name is ignored (with a warning), so a typo cannot drop the part off the physics layers. |
| `Lot.GetCollisionGroup(handle)` | Returns the part's group name, or `""`. |
| `Lot.SetGroupsCollidable(a, b, bool)` | Turns interaction between two groups on or off for the whole lot; returns `false` for an unknown name. Host-authoritative (§1.3): over a network send it through `net.server`. |
| `Lot.GetGroupsCollidable(a, b)` | Returns whether two groups interact; `false` for an unknown name. |

Notes:

* **Anchoring is about gravity, not who may move a part.** `SetPosition`/`SetRotationDeg` still work
  on an anchored part — it simply will not fall afterwards.
* **Asset ids are opaque.** Do not parse them. Read one from `Lot.GetTexture`, or pick one from the
  Inspector's Texture dropdown; `"Import..."` there loads a new file.
* **Supported images:** PNG, JPEG, WebP, BMP and TGA. The format is detected from the file's content,
  not its name, so an image saved under the wrong extension still loads.
* **Gravity applies in a player session.** Unanchored parts become simulated bodies when Test/Game
  mode starts, so they fall, collide and follow their welds and hinges; in the creation environment
  (Build mode) both states stay put so the part remains editable. A part's body form is fixed for a
  session — changing `Anchored` while a session runs applies when the session next starts.
* **Collision groups name a vocabulary, the matrix decides its meaning.** The groups are `Default`,
  `Terrain`, `Character`, `Prop`, `Decoration`, `Trigger`, `Projectile` and `Effect`; which of them
  interact is the lot-wide matrix edited under **View -> Collision Groups** (or with
  `SetGroupsCollidable`). By default everything collides, so a lot that never touches groups is
  unchanged. `CanCollide = false` still wins over the group — that part passes through everything.
* Every one of these verbs becomes available to scripts automatically once declared in
  `LotPropertyRegistry`/`LotLuaApi`; the registry is the single place a property is defined.

## 12. Welds and hinges (`Lot.*`, milestone 3.6)

Two links between parts, in the Roblox mental model — a weld holds two parts in their current
relative pose, a hinge lets them rotate around one axis — with no attachments to manage: the weld
takes the pose the parts already have, and a hinge's pivot is a point you pass in (usually where you
clicked the part).

```lua
local id = Lot.WeldParts(partA, partB)                  -- move as one from now on
Lot.SetAnchored(partA, true)                            -- one anchored side freezes the island
local hinge = Lot.CreateHinge(partB, -1, "y", x, y, z)  -- hinge to the world at a pivot point
Lot.SetHingeLimits(hinge, -90, 90, true)                -- a door that stops at 90 degrees
Lot.SetHingeMotor(hinge, "spin", 45, 10)                -- drive it at 45 deg/s, at most 10 impulse
```

| Call | What it does |
|---|---|
| `Lot.WeldParts(handleA, handleB)` | Welds two parts: they keep the relative position/orientation they have right now and move as one. Returns the link's id, or `-1` for a bad handle. Welding an already-welded pair returns the existing link. |
| `Lot.Unweld(handleA, handleB)` | Removes the weld between two parts (order does not matter). `false` when there is none. |
| `Lot.IsWelded(handleA, handleB)` | Whether an enabled weld holds between them. |
| `Lot.CreateHinge(handleA, handleB, axis, px, py, pz)` | Hinges two parts, or a part to the world (`-1` as the second side), around the world point `(px, py, pz)`. `axis` is `"x"`, `"y"`, `"z"` or their negatives (`"-x"` etc.) in the first part's frame; for any other direction use `Lot.SetHingeAxis`. Returns the link's id, or `-1` for a bad handle or unknown axis. |
| `Lot.SetHingeLimits(id, lowerDeg, upperDeg, enabled)` | Angular limits in degrees, relative to the pose the hinge was built at. Reversed values are swapped. `false` for a non-hinge id. |
| `Lot.SetHingeMotor(id, mode, velocity, maxPush)` | `"off"` (free hinge) or `"spin"` (turn at `velocity` degrees/second, capped by `maxPush`; `0` = unlimited, negative reverses). `false` for an unknown mode or a non-hinge id. |
| `Lot.SetHingeAxis(id, x, y, z)` | Aims a hinge's axis at a world-space direction — any direction, for an angled hinge. The vector is normalized; a zero vector is refused. `false` for a non-hinge id. |
| `Lot.RemoveConstraint(id)` | Removes a weld or a hinge by id. |

Notes:

* **Anchoring is the switch that freezes a link's island.** One anchored part in a welded group
  freezes the whole group; anchoring *both* sides of a weld deactivates that weld (the parts stay
  put, and the Inspector says why) — the Roblox rule, adopted deliberately.
* **A hinge needs one movable side**: part-to-world, or part-to-part where at least one side is
  unanchored. Both sides anchored means nothing to rotate, so the link reports inactive.
* **Changes apply live.** Editing a weld or hinge while a session runs updates the physics
  immediately; outside a session the change applies when the next session starts.
* **The editor does the same thing**: the Toolbox's **Weld** button is two clicks (part, part).
  **Hinge** is three steps — click the first part, click the second part (or empty space for the
  world), then a green hinge-point marker with its axis rod appears: click to move it, then press
  **Confirm** (or **Cancel**) in the Toolbox. A fresh hinge gets a horizontal axis so a hanging
  part swings under gravity; rotate it with the sharp axis buttons — **Vertical**, **Horizontal**,
  **Tilt 45**, **Yaw 45** — during placement and in the Inspector (each press is one undo step).
  A selected part's hinges also show their ball-and-rod in the view outside a session, so the
  orientation is visible as you change it. Welds and hinges are listed in the
  **Scene Hierarchy** under `Links (n)` — click a row to select its first part, right-click to
  select both parts, disable/enable or remove the link. The servo/angle motor mode reports an error
  until the timing system (§3.8) can drive it.
* **Over a network these are host-authoritative** (§1.3): send them through `net.server`.
* Welds and hinges are saved in the lot file, so a lot reopens with its links intact.
* **Test mode is an instance of the lot**: entering it snapshots the world, and leaving restores it
  — parts a session moved, spawns a script made and links the session changed all stay in the
  session. A part a session script *destroyed* is the one thing that does not come back.
