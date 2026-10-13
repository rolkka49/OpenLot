# OpenLot scripting — player data

**Audience:** creators writing lot scripts in Lua.
**Status:** implemented (milestone 3.12). Values are stored per lot, keyed by the player's stable
id; the save/autosave pipeline carries them, and a lot reload keeps them.

---

## 1. The model

* Every player has a **stable id** (22 characters): the account's public id (§3.12a). Data is
  stored **per lot** and **keyed by that id**, so the same person finds their coins after a
  reload, a restart, or on another day — and the id cannot be faked by another player.
* The device's own player is `Lot.GetLocalPlayer()` — the account registered on this device, or a
  local guest id when there is no account yet.
* `Lot.GetLocalPlayerName()` is that player's **display name** — the name entered at
  `register`/`login`. It is presentation only (`""` for a guest): names can collide and are never an
  identity, so key data by `GetLocalPlayer()`, never by the name.
* A value lives under a **key** you choose. Keys are yours; the engine stores what you give it.

## 2. The verbs

```lua
local me = Lot.GetLocalPlayer()
local who = Lot.GetLocalPlayerName()             -- the display name, "" for a guest

Lot.SetPlayerValue(me, "coins", 50)             -- number, string or boolean
local coins = Lot.GetPlayerValue(me, "coins")   -- 50; nil when never set
Lot.RemovePlayerValue(me, "coins")              -- true when something was removed
```

| Call | Returns |
|---|---|
| `Lot.GetLocalPlayer()` | This device's player id (a 22-character string) |
| `Lot.GetLocalPlayerName()` | The local display name (a string; `""` for a guest) |
| `Lot.SetPlayerValue(player, key, value)` | `true` when stored; `false` (with an Output warning) when refused |
| `Lot.GetPlayerValue(player, key)` | The value; `nil` when the key was never set |
| `Lot.RemovePlayerValue(player, key)` | `true` when a value was removed |

## 3. Rules & limits

* **Values:** numbers, strings and booleans. Tables and `nil` are refused (`nil` clears nothing —
  use `RemovePlayerValue`); NaN and infinity are refused.
* **Limits, each refused with a readable message:** keys up to 64 characters, strings up to 4 KB,
  up to 256 keys per player, data for up to 256 players, and 256 KB total per lot.
* **Persistence:** data is saved with the lot — the manual save and the periodic autosave both
  carry it — and restored when the lot loads. Entering/leaving Test mode does **not** reset it:
  play data is durable, not session state.
* **A lot carries its players' data.** A saved `.lot` copied to another machine brings the data
  along; loading a different lot shows that lot's own data only.

## 4. What is not here yet (stated, not implied)

* **Peer ids are not keys.** Event handlers pass a session player id (a number); passing that here
  is refused with a pointer. The peer→account mapping arrives with the networking milestone
  (§10.1), after which a host script can key the touching player's data; until then, scripts
  address the local player.
* **Authority:** v1 runs on this machine — the host is the authority by construction. When
  multiplayer hosting lands, client calls route to the host, and lots that protect their data
  (currency, items) move it to the data subserver (§10.10) with signed operations — the same
  verbs, with no creator-code change.
* **The host can read its lot's data** (it stores it) and can modify its own copy — true of any
  self-hosted server, and it never affects your account anywhere else.
