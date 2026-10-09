using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

/// <summary>Which namespace a net.* call targets. Wire values are fixed: do not reorder.</summary>
public enum NetDirection : byte
{
	Server = 0,
	Client = 1
}

/// <summary>Whether a call chain originated from a real player or from autonomous host logic
/// (design doc v4 A5). Wire values are fixed: do not reorder.</summary>
public enum NetOrigin : byte
{
	Player = 0,
	System = 1
}

/// <summary>
/// The seam between the router and Lua-side dispatch (design doc step-2 seam: the Lua bootstrap
/// and __openlot_dispatch land in step 3, so the router is verified against a fake target until
/// then). Implementations receive one queued envelope's fields; args stay as their wire payload
/// — decoding onto the Lua stack is the dispatch target's job. Return false when no handler
/// exists for (handle, direction, name); the router then warns and drops.
/// </summary>
public interface IDispatchTarget
{
	bool TryDispatch(int handle, NetDirection direction, string name, int senderPeerId, NetOrigin origin, byte[] argsPayload);
}

/// <summary>
/// Host-authoritative router for net.* calls (design doc v4 §5, §6; amendments 1/5). Godot-free
/// and clock-injected so the whole thing is testable in the NetSelfTest style.
///
/// Invariants implemented here:
///   - Uniform queueing: every call — host-local, loopback, or network-received — is enqueued
///     and dispatched only from Flush(), one drain point per frame. No synchronous paths exist,
///     so online and offline ordering are identical and A→B→A recursion grows the queue, never
///     the stack.
///   - Envelope: [targetHandle:int32][direction:u8][origin:u8][senderPeerId:int32]
///               [nameLength:u8][name UTF-8][args payload].
///   - §1.3 trust boundary: on receipt from a REMOTE peer the router overwrites sender (from
///     transport metadata, never the payload) and origin (only the host can originate System
///     calls, so remote envelopes are always Player).
///   - Transitive identity: calls initiated while a dispatch is running inherit that dispatch's
///     sender and origin (the host acts on behalf of the originating peer).
///   - Rate limits apply to REMOTE receipts only; loopback/host-local traffic is bounded by the
///     queue cap and the Lua watchdog instead (a creator rate-limiting themselves is noise).
/// </summary>
public sealed class NetRouter
{
	// Limits (design doc v4 §4/§5 + amendment 1): named consts, tuned later from real usage.
	public const int MaxCallsPerSecondPerPeer = 120;
	public const int CallBurstAllowance = 30;
	public const int MaxBytesPerSecondPerPeer = 64 * 1024;
	public const int MaxQueuedMessages = 1024;
	public const int MaxDrainPerFrame = 256;
	public const double FrameDrainMilliseconds = 8.0;
	public const double WarnThrottleSeconds = 1.0;
	public const int MaxFunctionNameBytes = 255;

	// handle(4) + direction(1) + origin(1) + sender(4) + nameLength(1) + name(≤255).
	private const int MaxEnvelopeOverhead = 4 + 1 + 1 + 4 + 1 + MaxFunctionNameBytes;

	/// <summary>Wire encoding of a zero-argument call (u16 argCount = 0).</summary>
	internal static readonly byte[] EmptyArgsPayload = new byte[] { 0, 0 };

	// Internal (not private) so NetSelfTest can round-trip envelopes through the codec.
	internal struct Envelope
	{
		public int TargetHandle;
		public NetDirection Direction;
		public NetOrigin Origin;
		public int SenderPeerId;
		public string Name;
		public byte[] Args;
	}

	/// <summary>Per-peer inbound rate-limit state (token buckets, refilled from the clock).</summary>
	private sealed class PeerBudget
	{
		public double CallTokens;
		public double ByteTokens;
		public double LastRefill;
		public double LastWarnTime = double.NegativeInfinity;
		public int DroppedSinceWarn;
	}

	private readonly INetTransport _transport;
	private readonly IDispatchTarget _dispatch;
	private readonly Func<double> _nowSeconds;
	private readonly Queue<Envelope> _queue = new Queue<Envelope>();

	// Declaration registry — diagnostic only (the redeclaration warning); dispatch authority is
	// TryDispatch/Lua-side, so this never gates a call (design doc v2 §3 decision A1).
	private readonly Dictionary<(int, NetDirection, string), string> _handlers =
		new Dictionary<(int, NetDirection, string), string>();

	private readonly Dictionary<int, PeerBudget> _peerBudgets = new Dictionary<int, PeerBudget>();

	// Item 6: unknown-handler diagnostics. A missing handler is a normal runtime event (an entity
	// that has not declared a name, or a stale cross-entity call), but a per-frame timer hitting one
	// would otherwise flood the log with one warning per call. Occurrences are therefore aggregated
	// per (handle, direction, name) and reported as ONE summary line per frame; each key is further
	// throttled to at most one report per WarnThrottleSeconds, and its occurrences in between are
	// carried forward so the reported count stays truthful. The map is capped so a hostile flood of
	// distinct names cannot grow it without bound (clinerules memory-leak discipline).
	private sealed class UnknownHandlerStat
	{
		public int Count;
		public double LastWarn = double.NegativeInfinity;
	}

	private const int MaxTrackedUnknownKeys = 256;
	private readonly Dictionary<(int, NetDirection, string), UnknownHandlerStat> _unknownHandlers =
		new Dictionary<(int, NetDirection, string), UnknownHandlerStat>();
	private readonly List<(int, NetDirection, string)> _unknownSummaryKeys = new List<(int, NetDirection, string)>();
	private readonly List<(int, NetDirection, string)> _removableUnknownKeys = new List<(int, NetDirection, string)>();
	private readonly StringBuilder _unknownSummaryBuilder = new StringBuilder();
	private int _unknownOverflow; // occurrences for keys past the tracking cap, since the last report
	private double _unknownLastPrune = double.NegativeInfinity;

	// Dispatch context: set while a handler runs so calls it initiates inherit sender/origin.
	private int _dispatchDepth;
	private int _currentSender;
	private NetOrigin _currentOrigin;

	private bool _flushing;
	private double _lastQueueWarnTime = double.NegativeInfinity;

	// Item 4 (C): autonomous calls — lot load, start(), timers, host logic — are System in ALL
	// modes, including loopback. Player origination exists only while an explicit interaction scope
	// is open (input handling; Test mode's SimulatePlayerInteraction).
	private int _playerInteractionDepth;

	/// <summary>Opens a player-origination scope (nestable). Calls initiated while it is open count
	/// as Player-originated; everything else is System.</summary>
	public void BeginPlayerInteraction()
	{
		_playerInteractionDepth++;
	}

	/// <summary>Closes one player-origination scope.</summary>
	public void EndPlayerInteraction()
	{
		if (_playerInteractionDepth > 0) _playerInteractionDepth--;
	}

	public bool InPlayerInteraction => _playerInteractionDepth > 0;

	/// <summary>All router warnings flow here (assigned to GD.PushWarning by the owner; tests
	/// collect them). Never null-checked per call site — see Warn().</summary>
	public Action<string> Warning;

	/// <summary>Raised (throttled, once per WarnThrottleSeconds per peer) when a remote peer is
	/// rate-limited. Observation hook for future moderation plugins (§1.3) — no policy here.</summary>
	public event Action<int> PeerRateLimited;

	public int QueuedCount => _queue.Count;

	/// <summary>Local peer id from the active transport — the transport owns identity, never a
	/// hardcoded id (§8.2's P2P rule). The event system (§3.7) uses it as the `player` argument
	/// when the local character causes a touch.</summary>
	public int LocalPeerId { get { return _transport.LocalPeerId; } }

	/// <summary>Drops every queued envelope and returns how many were dropped. Used when the owning
	/// VM is torn down: the payloads can no longer be dispatched, and holding them would only keep
	/// memory alive through the discard.</summary>
	public int ClearQueue()
	{
		int dropped = _queue.Count;
		_queue.Clear();
		return dropped;
	}

	public NetRouter(INetTransport transport, IDispatchTarget dispatch, Func<double> nowSeconds = null)
	{
		_transport = transport ?? throw new ArgumentNullException(nameof(transport));
		_dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
		_nowSeconds = nowSeconds ?? DefaultClock;
		_transport.MessageReceived += OnMessageReceived;
	}

	private static double DefaultClock()
	{
		return System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
	}

	/// <summary>Unsubscribes from the transport. Owners must call this before discarding the
	/// router so a torn-down lot leaves no subscription behind (memory-leak discipline).</summary>
	public void Detach()
	{
		if (_transport != null)
			_transport.MessageReceived -= OnMessageReceived;
	}

	// --- Initiation (called from the Lua-bound API surface) ---

	/// <summary>
	/// Initiates a net.* call from the local peer. Routing follows the design doc v2 §5 table:
	/// server-direction executes only on the host (local enqueue when host, SendToHost when a
	/// client); client-direction broadcasts from the host and additionally dispatches host-locally
	/// (the host is also a player in the P2P model), and is local-only on clients (clients never
	/// route to other clients). All paths enqueue — nothing dispatches synchronously.
	/// </summary>
	public void InvokeLocal(int handle, NetDirection direction, string name, byte[] argsPayload)
	{
		if (handle < 0)
		{
			Warn("[Net] net." + DirectionName(direction) + "." + name + ": negative entity handle " + handle + "; dropped");
			return;
		}
		if (argsPayload == null) argsPayload = EmptyArgsPayload;

		int sender;
		NetOrigin origin;
		if (_dispatchDepth > 0)
		{
			// Transitive identity (v4 §3/A5): the host acts on behalf of the originator.
			sender = _currentSender;
			origin = _currentOrigin;
		}
		else
		{
			sender = _transport.LocalPeerId;
			// Autonomous by default (lot load, start(), timers): System even in loopback. Player only
			// while an explicit interaction scope is open (item 4 rule).
			origin = InPlayerInteraction ? NetOrigin.Player : NetOrigin.System;
		}

		Envelope envelope = new Envelope
		{
			TargetHandle = handle,
			Direction = direction,
			Origin = origin,
			SenderPeerId = sender,
			Name = name ?? "",
			Args = argsPayload
		};

		if (direction == NetDirection.Server)
		{
			if (_transport.IsHost)
				Enqueue(envelope);
			else
				_transport.SendToHost(EncodeEnvelope(envelope));
		}
		else
		{
			if (_transport.IsHost)
			{
				Enqueue(envelope); // host is also a player: run the client handler locally too
				_transport.BroadcastToClients(EncodeEnvelope(envelope));
			}
			else
			{
				Enqueue(envelope); // client: local view only, never forwarded
			}
		}
	}

	/// <summary>
	/// Records a net.* declaration for diagnostics (design doc amendment: same-script re-runs
	/// replace silently; a different script redeclaring the same name warns, naming both).
	/// </summary>
	public void RegisterHandler(int handle, NetDirection direction, string name, string scriptName)
	{
		(int, NetDirection, string) key = (handle, direction, name);
		if (_handlers.TryGetValue(key, out string existing) && existing != scriptName)
		{
			Warn("[Net] entity " + handle + ": net." + DirectionName(direction) + "." + name +
				" declared by both " + existing + " and " + scriptName + " — using " + scriptName);
		}
		_handlers[key] = scriptName;
	}

	public bool HasHandler(int handle, NetDirection direction, string name)
	{
		return _handlers.ContainsKey((handle, direction, name));
	}

	/// <summary>Drops all declarations owned by a destroyed entity. Queued envelopes for it are
	/// NOT scanned out — they hit the unknown-handler path at drain (warn + drop), same as any
	/// message arriving for a dead handle.</summary>
	public void UnregisterEntity(int handle)
	{
		_removableKeys.Clear();
		foreach (KeyValuePair<(int, NetDirection, string), string> pair in _handlers)
		{
			if (pair.Key.Item1 == handle)
				_removableKeys.Add(pair.Key);
		}
		for (int i = 0; i < _removableKeys.Count; i++)
			_handlers.Remove(_removableKeys[i]);
		_removableKeys.Clear();
	}

	/// <summary>Script file that declared a (handle, direction, name) handler, or false when no
	/// declaration is on record. Used to attribute a watchdog trip to the RIGHT script when an
	/// entity runs more than one script (item 6): the last-loaded script is not necessarily the one
	/// whose handler tripped.</summary>
	public bool TryGetHandlerScript(int handle, NetDirection direction, string name, out string script)
	{
		return _handlers.TryGetValue((handle, direction, name), out script);
	}

	/// <summary>Drops every declaration and unknown-handler diagnostic. Called on a (re)load so a
	/// re-run starts from a clean slate; the queue is deliberately NOT cleared (it is transport
	/// state, and reload re-declares the same handlers).</summary>
	public void ClearRegistries()
	{
		_handlers.Clear();
		_unknownHandlers.Clear();
		_unknownSummaryKeys.Clear();
		_unknownOverflow = 0;
	}

	private readonly List<(int, NetDirection, string)> _removableKeys = new List<(int, NetDirection, string)>();

	// --- Receipt (untrusted path) ---

	private void OnMessageReceived(int fromPeerId, byte[] payload)
	{
		bool remote = fromPeerId != _transport.LocalPeerId;

		// Rate limit first: it is the cheapest bound on a garbage flood (§4).
		if (remote && !ConsumeBudget(fromPeerId, payload.Length))
			return;

		if (payload.Length > NetSerializer.MaxPayloadBytes + MaxEnvelopeOverhead)
		{
			Warn("[Net] oversized envelope from peer " + fromPeerId + " (" + payload.Length + " bytes); dropped");
			return;
		}
		if (!TryDecodeEnvelope(payload, out Envelope envelope, out string error))
		{
			Warn("[Net] malformed envelope from peer " + fromPeerId + " (" + error + "); dropped");
			return;
		}
		if (envelope.TargetHandle < 0)
		{
			// Negative handles are reserved for client-local visuals (§9) and may never arrive.
			Warn("[Net] negative entity handle " + envelope.TargetHandle + " from peer " + fromPeerId + "; dropped");
			return;
		}
		if (remote)
		{
			// §1.3: never trust client-reported identity. Sender comes from transport metadata;
			// only the host originates System calls, so remote envelopes are always Player.
			envelope.SenderPeerId = fromPeerId;
			envelope.Origin = NetOrigin.Player;
		}
		Enqueue(envelope);
	}

	private bool ConsumeBudget(int peerId, int byteCount)
	{
		double now = _nowSeconds();
		if (!_peerBudgets.TryGetValue(peerId, out PeerBudget budget))
		{
			budget = new PeerBudget
			{
				CallTokens = CallBurstAllowance,
				ByteTokens = MaxBytesPerSecondPerPeer,
				LastRefill = now
			};
			_peerBudgets[peerId] = budget;
		}

		double elapsed = now - budget.LastRefill;
		if (elapsed > 0)
		{
			budget.CallTokens = Math.Min(CallBurstAllowance, budget.CallTokens + elapsed * MaxCallsPerSecondPerPeer);
			budget.ByteTokens = Math.Min(MaxBytesPerSecondPerPeer, budget.ByteTokens + elapsed * MaxBytesPerSecondPerPeer);
			budget.LastRefill = now;
		}

		if (budget.CallTokens >= 1 && budget.ByteTokens >= byteCount)
		{
			budget.CallTokens -= 1;
			budget.ByteTokens -= byteCount;
			return true;
		}

		budget.DroppedSinceWarn++;
		if (now - budget.LastWarnTime >= WarnThrottleSeconds)
		{
			// Throttled: the limiter itself must not become a log-spam channel.
			budget.LastWarnTime = now;
			Warn("[Net] peer " + peerId + " rate-limited (" + budget.DroppedSinceWarn + " call(s) dropped)");
			budget.DroppedSinceWarn = 0;
			PeerRateLimited?.Invoke(peerId);
		}
		return false;
	}

	// --- Drain ---

	private void Enqueue(Envelope envelope)
	{
		if (_queue.Count >= MaxQueuedMessages)
		{
			double now = _nowSeconds();
			if (now - _lastQueueWarnTime >= WarnThrottleSeconds)
			{
				_lastQueueWarnTime = now;
				Warn("[Net] message queue full (" + MaxQueuedMessages + "); dropping net." +
					DirectionName(envelope.Direction) + "." + envelope.Name + " for entity " + envelope.TargetHandle);
			}
			return;
		}
		_queue.Enqueue(envelope);
	}

	/// <summary>
	/// The single dispatch point — called once per frame by the scene owner. Drains at most
	/// MaxDrainPerFrame envelopes, with a wall-clock backstop (checked every 16 dispatches to
	/// keep clock reads off the hot path); leftovers carry to the next frame.
	/// </summary>
	public void Flush()
	{
		if (_flushing) return; // a handler must never re-enter the drain loop
		_flushing = true;
		try
		{
			double startMs = _nowSeconds() * 1000.0;
			int drained = 0;
			while (_queue.Count > 0 && drained < MaxDrainPerFrame)
			{
				Envelope envelope = _queue.Dequeue();
				Dispatch(envelope);
				drained++;
				if ((drained & 15) == 0 && _nowSeconds() * 1000.0 - startMs > FrameDrainMilliseconds)
					break;
			}
			// One throttled summary of any missing handlers seen this frame (item 6).
			ReportUnknownHandlers();
		}
		finally
		{
			_flushing = false;
		}
	}

	/// <summary>Records one missing-handler occurrence for the per-frame summary. New keys are
	/// tracked up to a cap; beyond it occurrences count toward an overflow total so memory stays
	/// bounded under a flood of distinct names.</summary>
	private void RecordUnknownHandler(Envelope envelope)
	{
		(int, NetDirection, string) key = (envelope.TargetHandle, envelope.Direction, envelope.Name);
		UnknownHandlerStat stat;
		if (!_unknownHandlers.TryGetValue(key, out stat))
		{
			if (_unknownHandlers.Count >= MaxTrackedUnknownKeys)
			{
				_unknownOverflow++;
				return;
			}
			stat = new UnknownHandlerStat();
			_unknownHandlers[key] = stat;
		}
		stat.Count++;
	}

	/// <summary>
	/// Emits at most one warning per frame describing the missing handlers seen since the last
	/// report. A key is only reported once its WarnThrottleSeconds window has elapsed; keys still
	/// inside their window keep accumulating and appear in a later summary, so no occurrence is
	/// silently lost while a per-frame hammerer stays throttled.
	/// </summary>
	private void ReportUnknownHandlers()
	{
		if (_unknownHandlers.Count == 0 && _unknownOverflow == 0) return; // fast path, no allocation

		double now = _nowSeconds();
		_unknownSummaryKeys.Clear();
		int reported = 0;
		foreach (KeyValuePair<(int, NetDirection, string), UnknownHandlerStat> pair in _unknownHandlers)
		{
			if (now - pair.Value.LastWarn >= WarnThrottleSeconds)
			{
				_unknownSummaryKeys.Add(pair.Key);
				reported += pair.Value.Count;
			}
		}
		if (_unknownSummaryKeys.Count == 0 && _unknownOverflow == 0) return; // all throttled this frame

		_unknownSummaryKeys.Sort(CompareUnknownKeys);
		StringBuilder builder = _unknownSummaryBuilder;
		builder.Clear();
		builder.Append("[Net] ").Append(reported + _unknownOverflow).Append(" call(s) had no handler this frame:");
		for (int i = 0; i < _unknownSummaryKeys.Count; i++)
		{
			(int, NetDirection, string) key = _unknownSummaryKeys[i];
			UnknownHandlerStat stat = _unknownHandlers[key];
			builder.Append(" no handler for net.").Append(DirectionName(key.Item2)).Append('.').Append(key.Item3)
				.Append(" on entity ").Append(key.Item1).Append(" (").Append(stat.Count).Append("x);");
			stat.LastWarn = now;
			stat.Count = 0;
		}
		if (_unknownOverflow > 0)
			builder.Append(" (+").Append(_unknownOverflow).Append(" for handlers not tracked);");
		_unknownOverflow = 0;
		builder.Append(" dropped");
		Warn(builder.ToString());

		PruneUnknownHandlers(now);
	}

	/// <summary>Deterministic report order (handle, then direction, then name), so a frame's
	/// summary reads the same way every run.</summary>
	private static int CompareUnknownKeys((int, NetDirection, string) a, (int, NetDirection, string) b)
	{
		int compare = a.Item1.CompareTo(b.Item1);
		if (compare != 0) return compare;
		compare = a.Item2.CompareTo(b.Item2);
		if (compare != 0) return compare;
		return string.CompareOrdinal(a.Item3, b.Item3);
	}

	/// <summary>Drops keys that were already reported and saw no new occurrence since, so a one-off
	/// missing handler does not pin memory for the session. Runs at most once per throttle window.</summary>
	private void PruneUnknownHandlers(double now)
	{
		if (now - _unknownLastPrune < WarnThrottleSeconds) return;
		_unknownLastPrune = now;
		_removableUnknownKeys.Clear();
		foreach (KeyValuePair<(int, NetDirection, string), UnknownHandlerStat> pair in _unknownHandlers)
		{
			if (pair.Value.Count == 0 && now - pair.Value.LastWarn >= WarnThrottleSeconds)
				_removableUnknownKeys.Add(pair.Key);
		}
		for (int i = 0; i < _removableUnknownKeys.Count; i++)
			_unknownHandlers.Remove(_removableUnknownKeys[i]);
		_removableUnknownKeys.Clear();
	}

	private void Dispatch(Envelope envelope)
	{
		int previousSender = _currentSender;
		NetOrigin previousOrigin = _currentOrigin;
		_currentSender = envelope.SenderPeerId;
		_currentOrigin = envelope.Origin;
		_dispatchDepth++;
		try
		{
			bool handled;
			try
			{
				handled = _dispatch.TryDispatch(envelope.TargetHandle, envelope.Direction, envelope.Name,
					envelope.SenderPeerId, envelope.Origin, envelope.Args);
			}
			catch (Exception ex)
			{
				// Last-resort guard: a throwing handler must not kill the drain loop. The step-3
				// Lua dispatch target catches Lua errors itself; this catches everything else.
				Warn("[Net] handler for net." + DirectionName(envelope.Direction) + "." + envelope.Name +
					" on entity " + envelope.TargetHandle + " threw: " + ex.Message);
				handled = true; // already reported; don't double-report as unknown
			}
			if (!handled)
			{
				// Item 6: aggregate rather than warn per call; Flush emits one throttled summary.
				RecordUnknownHandler(envelope);
			}
		}
		finally
		{
			_dispatchDepth--;
			_currentSender = previousSender;
			_currentOrigin = previousOrigin;
		}
	}

	private void Warn(string message)
	{
		Action<string> warning = Warning;
		if (warning != null) warning(message);
	}

	private static string DirectionName(NetDirection direction)
	{
		return direction == NetDirection.Server ? "server" : "client";
	}

	// --- Envelope codec (internal for NetSelfTest; the receive path must stay permissive at
	// encode time so tests can craft hostile bytes, and strict at decode time) ---

	private static byte[] EncodeEnvelope(Envelope envelope)
	{
		return EncodeEnvelope(envelope.TargetHandle, envelope.Direction, envelope.Origin,
			envelope.SenderPeerId, envelope.Name, envelope.Args);
	}

	internal static byte[] EncodeEnvelope(int handle, NetDirection direction, NetOrigin origin,
		int senderPeerId, string name, byte[] argsPayload)
	{
		byte[] nameBytes = Encoding.UTF8.GetBytes(name ?? "");
		if (nameBytes.Length > MaxFunctionNameBytes)
			throw new NetSerializationException("net: function name too long (max " + MaxFunctionNameBytes + " bytes)");
		if (argsPayload == null) argsPayload = EmptyArgsPayload;

		MemoryStream stream = new MemoryStream(MaxEnvelopeOverhead + argsPayload.Length);
		BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8);
		writer.Write(handle);
		writer.Write((byte)direction);
		writer.Write((byte)origin);
		writer.Write(senderPeerId);
		writer.Write((byte)nameBytes.Length);
		writer.Write(nameBytes);
		writer.Write(argsPayload);
		writer.Flush();
		return stream.ToArray();
	}

	internal static bool TryDecodeEnvelope(byte[] payload, out Envelope envelope, out string error)
	{
		envelope = default;
		error = null;
		const int headerBytes = 4 + 1 + 1 + 4 + 1;
		if (payload.Length < headerBytes + 2) // header + shortest legal args payload (u16 count)
		{
			error = "too short (" + payload.Length + " bytes)";
			return false;
		}

		// Manual little-endian reads, same as NetSerializer's decoder: BitConverter would bake in
		// a host-endianness assumption the wire format doesn't have.
		int offset = 0;
		int handle = ReadInt32LE(payload, ref offset);
		byte directionByte = payload[offset++];
		byte originByte = payload[offset++];
		int sender = ReadInt32LE(payload, ref offset);
		int nameLength = payload[offset++];
		if (directionByte > (byte)NetDirection.Client)
		{
			error = "unknown direction " + directionByte;
			return false;
		}
		if (originByte > (byte)NetOrigin.System)
		{
			error = "unknown origin " + originByte;
			return false;
		}
		if (nameLength == 0)
		{
			error = "empty function name";
			return false;
		}
		if (offset + nameLength > payload.Length)
		{
			error = "truncated function name";
			return false;
		}
		string name = Encoding.UTF8.GetString(payload, offset, nameLength);
		offset += nameLength;

		byte[] args = new byte[payload.Length - offset];
		Array.Copy(payload, offset, args, 0, args.Length);

		envelope = new Envelope
		{
			TargetHandle = handle,
			Direction = (NetDirection)directionByte,
			Origin = (NetOrigin)originByte,
			SenderPeerId = sender,
			Name = name,
			Args = args
		};
		return true;
	}

	private static int ReadInt32LE(byte[] data, ref int offset)
	{
		int value = data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);
		offset += 4;
		return value;
	}
}
