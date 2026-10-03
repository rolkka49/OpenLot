using System;

/// <summary>
/// The seam between the net.* router and whatever moves bytes (design doc v2 §2). Godot-free on
/// purpose: the §7 ENet/P2P transport will be Godot-layer and plug in here without the router
/// changing. Host identity is ALWAYS resolved through the active transport (IsHost/LocalPeerId),
/// never a hardcoded peer id — the §7.2 P2P rule.
///
/// Receives are push-style: the transport raises MessageReceived(fromPeerId, payload); the
/// router treats every received payload as untrusted bytes.
/// </summary>
public interface INetTransport
{
	bool IsHost { get; }
	int LocalPeerId { get; }

	event Action<int, byte[]> MessageReceived;

	void SendToHost(byte[] payload);
	void BroadcastToClients(byte[] payload);
	void SendToClient(int peerId, byte[] payload);
}

/// <summary>
/// Offline/single-player/Test-mode transport (design doc v2 §2, §6.1): IsHost is always true and
/// the local peer id is 1 (Godot's host convention, sourced here rather than hardcoded in the
/// router). This is the §3.4 degradation story: net.* behaves identically because the ROUTER
/// still queues, routes and dispatches exactly as it does online — the transport simply has no
/// remote peers. SendToHost echoes locally so the transport stays honest when exercised
/// standalone; BroadcastToClients/SendToClient are no-ops because no remote clients exist (the
/// router delivers host-local net.client dispatches itself, so echoing here would double-deliver).
/// </summary>
public sealed class LoopbackTransport : INetTransport
{
	public bool IsHost => true;
	public int LocalPeerId => 1;

	public event Action<int, byte[]> MessageReceived;

	public void SendToHost(byte[] payload)
	{
		MessageReceived?.Invoke(LocalPeerId, payload);
	}

	public void BroadcastToClients(byte[] payload)
	{
		// No remote clients offline; the router enqueues the host-local dispatch itself.
	}

	public void SendToClient(int peerId, byte[] payload)
	{
		// No remote clients offline.
	}
}
