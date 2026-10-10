# Milestone 3.12 — Per-player persistent data API

**Status:** implemented — verified headless (see §6). Written **against** the §10.10 subserver frame
(intents, not assertions; deployment-transparent); the ownership subject is §3.12a's Account ID.
**Related:** §3.12a (`docs/design/milestone-3.12a-account-identity.md`) · §10.10 (data subserver) ·
§10.1 (peer→account handshake) · §4.1 archive versioning · §3.6 session snapshot · §3.2 router.

---

## 1. What a creator gets

```lua
local me = Lot.GetLocalPlayer()                    -- this device's player id (22 chars)
Lot.SetPlayerValue(me, "coins", 50)                -- number / string / boolean
local coins = Lot.GetPlayerValue(me, "coins")      -- 50; nil when unset
Lot.RemovePlayerValue(me, "coins")                 -- true when something was removed
```

Values are keyed by the stable identity (§3.12a): the data survives a lot reload and a client
restart, and it is never keyed by a peer id or a display name.

## 2. Decisions

| # | Decision | Where it lives |
|---|---|---|
| D1 | **The player argument is a 22-character player id** (an Account ID, §3.12a, or the device's guest id) — validated by shape. A number (a peer id from an event handler) is refused with guidance: peer ids are session-scoped, and the peer→account map arrives with §10.1. | `LotLuaApi.TryResolvePlayerArg` |
| D2 | **`Lot.GetLocalPlayer()` returns this device's id**: the stored account's Account ID when one exists, otherwise a persisted guest id (`user://guest.cfg`, 16 random bytes, same 22-char shape). Unlock state is a UI concern — identity is the file. Guest data deliberately does not follow the user (claim/merge out of scope). | `BuilderScene.LocalPlayerId`, `GuestIdentity` |
| D3 | **Values are number / string / boolean only.** NaN and infinity are refused (JSON cannot carry them). Caps, all refused readably: 64-char keys, 4 KB strings, 256 keys per player, 256 players, 256 KB total — the total is accounted on every set/remove, so an overwrite or removal frees its budget again. | `PlayerDataStore` |
| D4 | **Storage is the archive's own entry** `playerdata.json` (`{ "version":1, "players": { "<id>": { key: value } } }`), additive to v1 — a lot without the entry loads with an empty store (the same tolerance the script record kind established). Written by the one pipeline manual save and autosave share; read by `ReplaceLotFromArchive` before the scripts restart. A version newer than 1 is a readable failure for that entry, reported, never half-applied. | `LotArchive`, `LotArchiveWriter`, `LotSave`, `BuilderScene` |
| D5 | **Durable ≠ authored.** The store is deliberately NOT in the §3.6 session snapshot: entering/leaving Test mode never rolls it back. Loading a lot resets the store to that lot's own data — a new lot never inherits the previous lot's players. | `BuilderScene.ReplaceLotFromArchive` |
| D6 | **Authority (§1.3).** v1 executes on the local store — the host is the authority trivially, and there is no wire yet. When §10.1 lands, a client's calls route to the host; when §10.10 lands, declared-protected domains move to the data subserver with signed intents. Neither changes this API's vocabulary (the §10.10 D5 rule). | design note |
| D7 | **Failures are readable, never throws.** A missing value reads as `nil` (Lua) / `null` (C#); removing a missing key returns false; every refusal warns through the Output-window path (`LotLuaApi.Warn`). | `LotLuaApi` |

## 3. Flow

* **Save:** `Builder.WriteLot` / `LotAutosave.SaveNow` → `LotSave.WriteScene` → `scene.PlayerData.ToJson()`
  → `LotArchiveWriter.Write` writes the `playerdata.json` entry. Autosave therefore carries player
  data for free, and can never emit a file a manual save would not.
* **Load:** `LotLoad` → `BuilderScene.ReplaceLotFromArchive(document, vfs)` reads the entry through
  `LotVfs`; an unreadable entry logs one `LotLog` warning and starts empty rather than failing the
  whole load (the rest of the archive is valid). No entry clears the store (per-lot isolation).
* **Session:** Test/Game mode runs scripts against the live store; nothing about the session
  boundary touches it (D5).

## 4. What is deliberately deferred (recorded, not implied)

* **Peer→account mapping** — §10.1's handshake records it; until then only the local player's id is
  reachable, and peer ids are refused with a pointer (§D1).
* **Signed intents / protected domains** — §10.10's flagged task (canonical encoding; per-subserver
  subkeys are the recorded candidate). This milestone has no wire.
* **Privacy** — the host owns the archive (and later the subserver), so it can read the data it
  stores; that is inherent to self-hosting and stated rather than implied.
* **A host can still cheat its own game's books** (spawn itself coins) — true of any private server;
  the *account* is unaffected, and sidecar/remote subservers (§10.10) are strictly stronger.

## 5. Verification

`PlayerDataSelfTest` runs from `BuilderScene._Process` (after the physics probe) and consists of:

* **59 synchronous checks** — the id shape (a real §3.12a Account ID passes; 21/23-char,
  non-alphabet, empty/null and non-canonical spellings are refused), store semantics (all three
  kinds, replace, remove-once, isolation, missing reads) and every cap (key length, string length,
  256 players with the 257th refused and a freed slot accepted, the 256 KB budget with overwrite/
  removal freeing it, NaN/infinity), the JSON round trip (all kinds, unicode, empty string,
  accounting rebuilt, the version gate, malformed text, and the lenient skip of unknown kinds and
  malformed ids), the archive entry through the real writer + `LotVfs` (present, parses back,
  omitted for an empty store), and the bound verbs against the live API (round trips; refusals for
  non-primitives, NaN, a peer id — with the §10.1 pointer — a bad id and an oversize string).
* **10 staged probe checks** — a fixture script writes through the real verbs inside a real session
  (the log lines prove the values went through Lua and the refusals fired), the store holds them,
  leaving Test mode does not roll them back (D5), the lot **saves**, **reloads from disk**, and the
  values are read back both from the store and through the bound verb — the milestone's acceptance,
  pinned end to end.

Full headless run: **1,869 checks, 0 failures** (`godot --headless --quit-after 8000
res://builder/builder.tscn`; 1,800 pre-existing + 59 + 10).

## 6. Execution notes

* The store is plain C# + Godot `Dictionary`/`Json` — no scene dependency, so the synchronous suite
  needs no fixtures; only the probe touches the live scene.
* `BuilderScene.LocalPlayerId` resolves lazily and caches per scene (registering happens in the
  Terminal scene, so the next lot load picks it up).
* `LotArchiveWriter.Write` gained a required `playerData` payload; `LotSave.WriteScene` supplies it
  from the scene — the existing archive self-test callers pass `null`.
* The probe's saved lot embeds the probe script; the runtime loads it after restoration and only
  then are the scratch files removed, so the load never races the cleanup.
