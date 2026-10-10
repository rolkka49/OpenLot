# Design record — §10 Networking: transport strategy & protection posture

**Status:** research baseline recorded (Oct 2026), and the red-dot research pass is done too (results in §6 — every
§10 red is now decomposed into 🟠/🟡 work). The decisions below are settled and the §10.1–§10.12 milestones are
written against them; this file becomes the §10.1 execution decision record as the transport lands — it is
updated, not rewritten.
**Engine pinned:** Godot 4.7.2 (.NET). 4.6/4.7 shipped no networking headline changes, so the APIs below are the
stable 4.x surface.
**Related:** `.clinerules` §10 (plan of record) · §4.1 archive versioning · §3.2 script model
(`docs/design/milestone-3.2-unified-script-model.md`) · §3.12 per-player data · §15 providers.

---

## 1. What's tamed vs what's built

| Layer | Godot 4.7 provides | OpenLot decision |
|---|---|---|
| UDP transport | `ENetMultiplayerPeer` (connection lifecycle, Godot-managed peer ids, server = 1); `ENetConnection` / `ENetPacketPeer` low-level wrappers | Primary route: peer + raw packets. `ENetConnection`-direct reserved for §10.2 mesh/punch paths |
| Raw bytes, no RPCs | `SceneMultiplayer.send_bytes` out; `peer_packet` signal in; manual polling via `SceneTree.multiplayer_poll = false` + `MultiplayerPeer.Poll()` / `GetPacketPeer()` / `GetPacketMode()` | The §3.2 router envelope stays the wire format; own message-kind byte, because the receive path cannot report the channel (#12907) |
| Delivery classes | `TransferMode` (reliable / unreliable / unreliable-ordered), `transfer_channel` | Per-message class: transforms unreliable, state milestones reliable (§10.6) |
| Encryption | DTLS through the peer's connection: `host.dtls_server_setup` / `dtls_client_setup` (TLSOptions + certificates) | Opt-in for v1, default-on once the certificate UX is settled (§10.9) |
| Stats, timeouts, limits | `ENetPacketPeer` RTT/loss statistics, `set_timeout`, `bandwidth_limit`, throttling | Feed §10.5's load-test table and a debug overlay (§10.6) |
| UPnP | `UPNP` class (methods block — thread them) | §10.2 Tier A |
| WebSocket | `WebSocketMultiplayerPeer` with TLS (`wss://`) | The browser path only; TCP latency makes it not primary |
| WebRTC | Built in for web exports; native needs the official `webrtc-native` GDExtension | Not taken — the dependency is a deliberate no |
| Mesh | `create_mesh` / `add_mesh_peer` | Toolbox for direct client-to-client links (§10.3), not the star session's transport |
| Dedicated server | `dedicated_server` export tag ⇒ `--headless`, resource stripping; `application/run/flush_stdout_on_print` | §10.5 |
| Console | `OS.read_string_from_stdin()` (blocking) | §10.5, on a dedicated thread; Linux is the guaranteed target |
| Signal handling | — (godot-proposals #1361 open) | §10.5: best-effort .NET shim + idempotent boot + crash marker |
| NAT traversal | Primitives only: `ENetConnection.socket_send` is documented *for* punching; needs a STUN-derived endpoint; **never works behind symmetric NAT** | §10.2 Tiers B/C are ours (rendezvous / relay) |
| Rendezvous, relay, lobby | — | §10.2 / §15 |
| Interest management | `MultiplayerSynchronizer` per-peer visibility filters (1350-byte sync packets) — reference only | Ours, handle-based (§10.6) |
| Content crypto | `Crypto` is asymmetric-only (RSA sign/verify, HMAC-SHA256, self-signed certs); **no AES-GCM** | `.lot` v2 crypto in .NET (`System.Security.Cryptography`) — no new dependency (§10.12) |

## 2. Settled decisions (Oct 2026 planning pass)

| # | Decision | Consequence |
|---|---|---|
| D1 | **Transport route:** `ENetMultiplayerPeer` + raw packets primary; `ENetConnection`-direct for §10.2 mesh/punch paths | §10.1: lifecycle/auth/timeouts come free; DTLS reachable through the host connection |
| D2 | **Protection default: open.** New lots are plaintext and moddable; protection is opt-in presets (`open / authorized hosts / private-group / provider-only`) | §10.12: matches the open-source ethos; encryption is a per-lot choice |
| D3 | **Movement trust default: client-authoritative.** Host validation clamps are opt-in per lot | §10.6 / §10.10: movement cheats on a private instance hurt no one; hosting stays cheap |
| D4 | **Private-server authorization: creator decides per lot.** `open` lots always permit private hosting | §10.11 |
| D5 | **Subserver routing: deployment detail of existing `net.server` handlers** — no new creator vocabulary in §3.2 | §10.10: embedded → sidecar → remote with no creator-code change |

## 3. Facts to design around

- **Raw receive cannot report the ENet channel** (godot-proposals #12907, open) → OpenLot frames carry a message-kind byte; channels are for ENet-level sequencing, not demux.
- **No engine-level signal handling** (godot-proposals #1361, open) → the runner ships a best-effort .NET shim (candidate: `PosixSignalRegistration` / `Console.CancelKeyPress`) and treats hard kills as survivable: idempotent boot + the §4.1 crash marker + autosave.
- **`--lot` bootstrapping lives in §10.5** (moved out of §10.1, which is transport-only).
- ENet `create_server` defaults: 32 max clients (documented ceiling 4095, realistically lower); `maxPlayers` always comes from the manifest.
- **Hole punching needs a third party:** `socket_send` reaches a *known* public endpoint (STUN-style), and symmetric NAT / CGNAT cannot be punched — Tier C relay is the honest fallback (§10.2).
- **DTLS certificate UX is the gating cost:** self-signed/own-CA + pinning, renewal and client updates — why encryption is opt-in first (§10.9).
- **Godot's high-level protocol "is an implementation detail... may change without notice"** → both ends are ours and §10.1 version-gates the handshake; our wire format stays the router's.
- **Godot's own protection stance** (docs): no out-of-the-box anti-copy; DRM is the implementer's responsibility; PCK encryption is a key-in-binary speed bump that requires self-compiled templates. §10.12 therefore does not lean on engine obfuscation at all.

## 4. Still to decide inside §10.1's execution

- exact frame layout (kind-byte values, headers) and the MTU number;
- peer-id mapping rules for `ENetConnection`-direct paths (ENet exposes peers, not our ids — the primary route avoids this);
- auth-hook usage (`auth_callback` / `complete_auth`) vs a token field in OpenLot's own handshake;
- whether the snapshot channel ever goes `ENetConnection`-direct (channel visibility) or stays raw-packet with the kind byte;
- the host↔subserver link for sidecar/remote deployments (§10.10) — loopback transport or process pipes;
- the SSH client choice for §10.4: SSH.NET (MIT, streaming) as a named dependency exception, vs the system `ssh` binary.

## 5. References

- **Godot docs:** `ENetMultiplayerPeer`, `ENetConnection`, `ENetPacketPeer`, `MultiplayerPeer`, `SceneMultiplayer`, `MultiplayerSynchronizer`, `UPNP`, `WebSocketMultiplayerPeer`, `WebRTCMultiplayerPeer`, `Crypto`; tutorials: High-level multiplayer, WebRTC, Exporting for dedicated servers, Compiling with PCK encryption key, Exporting packs/patches/mods.
- **Proposals & issues:** godot-proposals #12907 (`send_bytes` channel receive gap, open) · godot-proposals #1361 (signal handling, open) · godot #2970 (P2P across NAT — closed via UPnP support) · godot #119564 (script-encryption export field silently broken on stock templates; closed as duplicate, "implemented by #115723") · godot PR #115723 (export-time key injection).
- **Articles:** "Multiplayer in Godot 4.0: ENet wrappers, WebRTC" (mesh + DTLS-at-ENetConnection) · "War on NAT" (hole-punch/relay decision tree) · "Making peer-to-peer multiplayer seamless with Godot" (rendezvous + match codes) · "Networking tutorial 6: Encrypted connections using DTLS" (server/client setup calls).
- **Red-dot research pass (§6):** ProbeLab, "Can libp2p punch through NATs?" (Aug 2026) + Trautwein et al., IMC '26 (70% ± 7.1%) · Tailscale, "How NAT traversal works" · Valve GameNetworkingSockets `README_P2P.md` · Project Fika wiki + `NatPunchServer.cs` · Gaffer On Games, "Snapshot Interpolation" · SSH.NET API docs · Denuvo TraceMark announcements.

## 6. Research results (Oct 2026) — what de-risked the §10 reds

**NAT traversal (§10.2 B/C).** Large-scale live measurements of IPFS/libp2p DCUtR (IMC '26 + ProbeLab, Aug 2026): aggregate hole-punch success **70% ± 7.1%**; by NAT class **port-restricted cone 82.9%** (99.3% genuine simultaneous-open), **symmetric ≈ 40%** (small sample, partly misclassification — treat as unreliable), full-cone ~52%; ~36% of punch-advertising peers never reached the attempt stage. NAT behaviour classifies on the RFC 4787 mapping × filtering axes (RFC 5780-style probes). Mechanics per Tailscale's write-up: candidate exchange, simultaneous probe sprays, keepalives; port prediction / birthday-paradox spraying is a best-effort bonus only. **Reference implementation:** Project Fika's NAT punch server is ~150 lines of C# (GUID registry, token parse, `NatIntroduce`, 30-second stale cleanup) on LiteNetLib's `NatPunchModule`; flow = host registers → client requests introduction → both sides punch. Valve's GameNetworkingSockets confirms the canonical shape (signaling + STUN + relay) and notes that **relayed traffic hides host IPs**; its relay is the only costly service, hence fallback-not-default. Conclusion: engineering with a measured success envelope — the relay is the coverage answer.

**Movement & interpolation (§10.6).** Gaffer's measured rule: interpolation delay ≈ **3× the send interval** at 2–5% loss (10 Hz ⇒ 300 ms; 20–30 Hz ⇒ 150–300 ms; 60 Hz ⇒ ~85 ms), plus a frame or two for jitter. Extrapolation is only safe short (50–250 ms) and mostly-linear; physics mispredicts. **Decision D3 removes the classic hard part** — with no server-side player body there is no reconciliation/rubber-banding loop on the default path; host clamps are opt-in per lot, and lag compensation/rewind is explicitly out of scope for v1.

**SSH host setup (§10.4).** SSH.NET (MIT) covers the client: `ShellStream.DataReceived` for live output, SFTP for uploads, command exec — or drive the system `ssh` binary for zero dependencies (decision recorded in §4). Remote ops scope: Linux + systemd (unit template, `systemctl`, `journalctl -f`), key auth first — the red was aggregate breadth, now scoped away.

**Subserver plumbing & audit (§10.10).** Sidecar mode dogfoods the existing loopback transport (same binary, `--subserver`, local token); remote mode is the same protocol over TLS + token; **provider multi-tenancy is deferred to §15**. Audit is a solved pattern: append-only JSONL, hash-chained for tamper-evidence; intent idempotency is ordinary API design.

**Watermarking (§10.12).** The commercial bar is Denuvo TraceMark-class forensic watermarking (builds + footage) — a specialist product, not a checklist item. What serves the stated goal: per-licensee tokens as the trace, per-delivery canaries, delivery records, and the existing "unverified copy" degrade mode. Robust invisible asset watermarking is parked as optional, non-blocking, droppable research.
