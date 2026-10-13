# Milestone 3.12a — Account identity primitive

**Status:** implemented — verified headless (see §6). This is the design record; the transport half
deliberately waits (the primitive ships first so §3.12 keys per-player data correctly from its first
write).
**Audience:** whoever maintains the net/script layer. The user-facing half (register/login UX) is
§2 here; a creator guide is written when §3.12/§10.1 expose player identity to scripts.
**Related:** `.clinerules` §3.12a · §3.12 per-player data · §10.1 handshake · §10.9 identity model ·
§10.10 data subservers · §5.1 auth gate · §10.12 license tokens · §4.1 archive versioning.

---

## 1. The problem, and the two ways out

**The idea being replaced:** the whole account (identity, profile, "what games know about you") as
one ~50-character string the user carries between devices and presents to a game so the game knows
who is who. **Why it cannot work as a shared secret:** anything a game can *verify* by seeing, it
can *copy and replay*. A bearer string presented to the host is account theft waiting to happen —
and against a technical host the only "prevention" would be an authority watching every login, which
is exactly what OpenLot does not have.

**The two honest escapes:**

1. a central authority (rejected — the decentralization thesis, and it would be the thing that
   intervenes), or
2. **asymmetric proof** — the string is a *private key*; games receive only *public* data and
   *signatures*, and no operation turns a signature back into a key.

This milestone takes (2) — the pattern SSH keys, WireGuard, Nostr and crypto wallets use. The
verifier is the math, run locally by every host; nobody intervenes.

**One correction to the mental model:** a hash cannot be the account (it is one-way — nothing to
reconstruct on another device), and 50 characters cannot *contain* the profile (that needs
hundreds). What fits in exactly 50 characters is the account's **root secret**; the account ID —
and later the display name/avatar — are *derived from* or *signed by* it. The string is the
account; the profile is presentation hanging off it.

## 2. What the user gets

```
> register Ada
Account 8Xk2... created.
Save this key: it IS your account, and it cannot be recovered if lost.
  <the 50-character key>
Run 'login <key>' on another device to use the account there. ...

> logout                       -> locked; the account stays on this device
> login                        -> "Welcome back, Ada. Account 8Xk2..." (unlocks the stored key)
> login <key>                  -> imports the account on another device (same Account ID)
name> Ada                       -> the display name is ASKED FOR (a key carries no profile)
> unregister                   -> names the account that would be deleted, deletes nothing
> unregister confirm           -> deletes this device's account (the lost-key reset)
```

* `register <name>` creates the device's account, stores it, unlocks it, and reveals the key **once**.
* `login` (no argument) unlocks the account stored on this device.
* **A launch signs itself in** when this device holds an account: v1 stores the key plaintext (D6), so
  there is no secret left to ask for and retyping `login` on every start protects nothing. `logout`
  still locks for the rest of the session, and a passphrase is the one thing that would make the
  automatic sign-in stop (`AccountSession.UnlocksAtStartup` is that rule, in one place).
* `login <key>` with **this device's own key** is an unlock, not an import: the key is the account, so
  nothing would be destroyed and the stored display name is kept. Only a key for a *different* account
  hits the no-clobber refusal.
* `login <key> [name]` imports one from its key string — the cross-device move is a paste. **With no
  name the terminal asks for one**, because the key carries the account and never a profile; `-`
  accepts a generated name and an empty line cancels. The name is presentation (D9), so it never has
  to match whatever the account was called on the device it came from.
* `unregister` / `unregister confirm` deletes this device's account — the **lost-key reset** — after
  naming the account it would remove. It is deliberately pre-login: losing a key leaves the device
  locked out, so the reset must be reachable without signing in.
* `logout` locks. Register and import **refuse to clobber** an existing account file (overwriting
  would destroy a key that cannot be re-created); `unregister` is the explicit, confirmed way to start
  over, and a general switch-account flow is still a later surface.
* The §5.1 Unity-parity command list is deliberately re-specified (called out there): `register`
  no longer treats a username as the account; the display name is local presentation.

The key is stored in `user://account.cfg` meanwhile, so a user who misses the reveal is not locked
out of their own device; a gated re-reveal belongs to the §5.4 settings surface (a stated gap).

## 3. The formats (the wire contract)

**Key string** — `AccountKey.KeyString`, exactly 50 characters:

```
version(1 byte) || seed(32 bytes) || SHA-256(version||seed)[0..4] (4 bytes)   -> 37 bytes
-> unpadded Base64Url (A-Z a-z 0-9 - _)                                        -> exactly 50 chars
```

One mistyped character changes the checksum and is refused with a readable message. The encoding is
**canonical** (non-zero padding bits are rejected, so every key has exactly one spelling). A future
version byte refuses cleanly (the suite pins v2-with-a-valid-checksum being rejected by the version
gate).

**Account ID** — `AccountKey.AccountId`, 22 characters:

```
Base64Url( SHA-256("OPENLOT-ACCOUNT-ID-v1" || 0x04 || X(32) || Y(32))[0..16] )
```

A domain-separated, 128-bit-truncated fingerprint of the 65-byte uncompressed public key. Hosts
store this; the full public key travels in the handshake and must hash back to the claimed id
(`AccountKey.VerifyAccountId`).

**Challenge** — `AccountChallenge.Build`, UTF-8 text, fixed field order, no trailing newline:

```
OPENLOT-AUTH-v1
lot:<lot id>
nonce:<base64url of the fresh nonce>
expiry:<unix seconds>
```

The client core signs these bytes (ECDSA P-256 / SHA-256); the host verifies with the public key
and checks the expiry against its clock. Domain separation + the fresh nonce + the expiry are what
make every signature single-host, single-session, single-purpose: a host that captures everything
you send cannot replay it anywhere else, ever.

## 4. Decisions

| # | Decision | Where it lives |
|---|---|---|
| D1 | **The 50-character string is the private key** — `version + seed + checksum` as above — not a serialized profile and not a hash. The profile is presentation (D9). | `AccountKey` |
| D2 | **The account ID is public and derived:** a domain-separated, truncated SHA-256 of the uncompressed public key; the public key itself travels in the handshake and must hash back to the id. | `AccountKey.ComputeAccountId/VerifyAccountId` |
| D3 | **Key foundation: P-256 ECDSA via the .NET BCL** (`System.Security.Cryptography`), chosen by the seed-only-import spike at implementation start (evidence in §6). Zero new dependencies — the same crypto home §10.12's archive encryption already counts on. **Ed25519 via NSec is the recorded fallback candidate** (modern primitive, deterministic seed→keypair; the BCL still ships no Ed25519 — dotnet/runtime#63174 is open): if a target runtime ever fails the spike, the switch is contained to `AccountKey` and enters as a named dependency exception, not a silent one. | `AccountKey` |
| D4 | **Auth is challenge-response over a canonical, domain-separated message** (`OPENLOT-AUTH-v1`, lot, fresh nonce, expiry). The verifier holds only public data; nothing reusable crosses the wire. | `AccountChallenge` |
| D5 | **Signing is a fixed-schema core operation.** The client core signs one kind of thing — a handshake challenge — and Lua gets no signing verb, no seed, no key string. This is the §1.1 boundary extended to the account key, and it kills the "wallet drainer" class of attack (a hostile script tricking the client into signing something meaningful) by there being nothing meaningful to sign. | `AccountKey` doc, `AccountSelfTest` boundary tripwire |
| D6 | **Storage is `user://account.cfg`** (Godot `ConfigFile`, the same format family as the wallpaper setting; §5.4 will adopt it). Plaintext by design for v1, with the warning printed in the flow — the file is the account, like a saved password without a master password. Passphrase protection and the OS keystore are recorded later options, not half-built now. Every operation takes its path explicitly, so tests drive scratch files and can never touch a real account. | `AccountStore` |
| D7 | **No clobbering.** Register and import refuse when the device already holds an account file — including an unreadable one (a corrupted key must not be silently replaced). Deleting/switching is a deliberate later surface. | `AccountSession` |
| D8 | **Terminal semantics change, flagged.** `register <name>` = create; `login` = unlock; `login <key> [name]` = import (no name ⇒ the terminal prompts for one); `unregister [confirm]` = delete this device's account (the lost-key reset, pre-login); `logout` = lock. `Terminal.IsLoggedIn` is now *derived* from the session (the §5.1 gate's input), and the old no-op `InitiatePasswordPrompt` stub is gone. | `Terminal`, `TerminalCommands` |
| D9 | **Identity is the account ID; the display name is presentation.** No authority exists to enforce global name uniqueness — names can collide across lots and are never trusted for identity; a signed profile and per-provider name registries are later, optional layers. | doc + `AccountSession` |
| D10 | **Test mode uses the same local account.** §3.12 keys per-player data by `AccountId` from its first write; session-scoped peer ids map to the stable account at §10.1's handshake when that lands. Nothing about the account is lot state — it never enters `lot.json`. | §3.12 wiring note |

## 5. Security posture

**What each actor gets:**

| Actor | What they obtain | What it buys them |
|---|---|---|
| The host (the original worry) | the public Account ID + signatures over *its own* challenges | Nothing reusable: it cannot authenticate as the user anywhere else, ever. Within its own lot it can lie about or kick a player — true of any host, never account theft |
| Another host / an eavesdropper | signatures from other sessions | Rejected — they are bound to a different nonce, lot and expiry |
| A lot's Lua scripts | nothing; no signing verb or key access exists (tripwire-tested) | — |
| A thief with the device file | the key (plaintext for v1) | Full theft — the password-manager analogy; at-rest protection is a recorded later option |
| Anyone the user pastes the key to | the key | Full theft, by design. The user rule is deliberately clean: **nothing any game ever needs your key for; anything that asks is a scam** |
| A Tier-3 plugin / a modified client | process memory | "You installed malware" — the stated boundary, not pretended away |

**Stated limitations (not implied):**

* **No recovery.** A lost key cannot be restored — no custodian exists by design. The reveal is the
  v1 export flow (plus §5.4's gated re-reveal); social recovery or provider escrow would be optional
  later layers, never core.
* **Free alt accounts.** A banned player can generate a new key; bans are per-account-per-host.
  Without an authority this is inherent, and providers can layer attestations later (§15).
* **Display names can collide** and are never identity. Show the short Account ID where it matters.
* **Active on-path interception** could relay one live join handshake and occupy that session's seat
  — a session-level concern for §10.9's DTLS work later, not account theft (a new session is a new
  nonce; nothing carries over between sessions).
* **The key is not transport encryption.** Identity only; confidentiality is §10.9's axis.
* **The suite drives `AccountSession` directly, not `Terminal`**, because the Terminal node cannot be
  instantiated in suites (it subscribes ImGui layout state in `_Ready` — the `ConsoleScrollback`
  precedent). The terminal shell itself is compile-checked and eye-checked, the project's usual
  split.

## 6. Verification

`AccountSelfTest` runs with the other root suites from `BuilderScene._Ready` (headless, `#if DEBUG`).
Sections:

* **The spike** — a P-256 keypair rebuilt from its 32-byte seed alone (`ImportParameters` with
  Curve+D, no Q): the derived public key must match the originally generated one, and sign/verify
  must round-trip. Runs *every suite run*, so a runtime that cannot do it fails loudly instead of
  producing unimportable accounts.
* **The key string** — exactly 50 characters; base64url alphabet; string round-trip (same id);
  null/empty/49/51 refusals; non-alphabet character; swapped-character checksum refusal; the
  canonical-final-character pin (exactly 4 sextets decode, exactly 1 passes the checksum); a v2 key
  with a *valid* checksum refused by the version gate; an altered seed refused by the checksum.
* **The account ID** — 22 chars, base64url, verifies against its public key, differs between keys,
  a tampered public key no longer matches, stable across a string round-trip.
* **The challenge** — the exact known-answer bytes; nonce/expiry/lot each change the message;
  sign/verify round-trip; replay rejected across nonce and lot; another key's signature rejected;
  malformed inputs verify false and never throw.
* **The store** — scratch-file save/load round-trip (key string, id, display name, created stamp);
  a missing file loads as "no account"; a corrupt key is refused with a readable message; delete.
* **The session flows** — start locked; unlock-before-register refusal; register requires a name;
  register creates + unlocks + reveals exactly the key string; double-register refusal;
  register/import no-clobber on a device that already holds an account; unlock/lock lifecycle
  (including "kept on disk", double-lock and re-unlock); import validates the key, defaults the
  display name, persists, and unlocks on the next run.
* **The boundary tripwire** — reflection over the bound `LotLuaApi` surface asserts no method name
  signs or exposes key material.

**Result:** `AccountSelfTest` — 65 checks, 0 failures; the full headless run is all suites green
alongside it: **1,800 checks, 0 failures** (the 1,735 pre-existing ones untouched). Run:
`godot --headless --quit-after 8000 res://builder/builder.tscn`. The on-screen side (the reveal
block, colors, the `login` welcome line) is checked by eye in the running client, the project's
usual split.

**The spike's own evidence (recorded, not implied):** a standalone console run on .NET 10 printed
`D round-trips = True`, `Q matches generated = True`, `verify = True`, `tampered verify = False`;
the in-game suite then pins the same contract under the game's own runtime on every run.

## 7. Wiring checklist (what consumes this, later)

* **§10.1 handshake** — replace the "player name/token" sketch with: display name + Account ID +
  public key + a signature over `AccountChallenge.Build(lotId, nonce, expiry)`; the host verifies
  with `AccountKey.Verify` and records the peer→Account ID mapping. The primitive and its format
  are frozen by tests now; the handshake is wiring.
* **The display name reaches a lot's scripts (`Lot.GetLocalPlayerName()`, added in the 3.12a fix
  pass).** `BuilderScene.LocalPlayerName` reads it from the stored account (presentation only, `""`
  for a guest) and `LotLuaApi` exposes it beside `GetLocalPlayer()`. This is the local half only:
  showing a name to *other* players still needs §10.1 to carry it, so the name is currently for
  nameplates/welcome lines a local script draws, never an identity claim.
* **§10.9** — the identity model line: session-scoped peer ids remain the transport view; the stable
  Account ID is mapped onto them at the handshake.
* **§3.12** — per-player data keyed by `AccountId`; the data subserver verifies signed intents with
  the same key. Intent signing is where *semantic* content gets signed, so it needs its own
  canonical-encoding rules (and is a candidate for per-subserver HKDF-derived subkeys so the master
  key signs nothing gameplay-level) — a §10.10 design task, flagged, not built here.
* **§5.1** — the auth gate's `RequiresLogin` field reads `Terminal.IsLoggedIn` (derived from the
  session); the parity list's register/login semantics follow §2's flows.
* **§5.4 settings** — key re-reveal (gated), passphrase protection, and the account file join the
  settings story.
* **Deliberately absent:** recovery, multi-account switching, device management, provider name
  registries, and any Lua exposure of player identity (that is §3.12's surface, and it can only
  expose the public Account ID).

## 8. Alternatives recorded (why not)

* **The presented shared secret** (the original idea) — unfixable without an authority; see §1.
* **Ed25519 / NSec** — the stronger primitive, but a new dependency; kept as the recorded fallback
  candidate (D3) with a clear switch criterion (a target runtime failing the spike).
* **Per-lot derived secrets** (HKDF per host, sent as a password) — solves cross-lot theft but hands
  each host a *bearer credential for its own lot*, which is exactly what §10.10's protected-data
  model must not allow (the host could then mint state changes as the player). Signatures avoid it.
* **A "sign arbitrary bytes" client API for future features** — rejected on purpose: the only
  signing operation is the fixed challenge schema; anything more must be designed as its own
  canonical format first (the D5 rule).
