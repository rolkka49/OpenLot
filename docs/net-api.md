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
