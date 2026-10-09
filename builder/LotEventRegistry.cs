using System;
using System.Collections.Generic;

/// <summary>Outcome of asking the owner to attach a subject's event sensor (design doc 3.7 §4).</summary>
public enum SensorAttachResult
{
	/// <summary>The sensor exists now (created, or already there).</summary>
	Ok,
	/// <summary>The subject's node is not resolvable yet (spawned later); retry next drain.</summary>
	Retry,
	/// <summary>The node cannot host a sensor (a decal, a mesh-less group); give up and report.</summary>
	Refused
}

/// <summary>
/// C#-side bookkeeping and delivery spine for milestone 3.7's event system: which (subject, event)
/// pairs have subscribers, the per-frame delivery queue (coalescing + a per-frame cap), and the
/// sensor attach/detach protocol. The Lua-side registry (LuaBootstrap) owns the callbacks and
/// their declaration order; this table owns presence, delivery and cleanup, so a destroyed entity
/// leaves nothing behind in either (design doc D13).
///
/// Godot-free like NetRouter and NetSerializer: sensors report through the owner's callbacks,
/// dispatch goes through the injected <see cref="Dispatch"/> delegate, and the clock is injected so
/// the throttle is testable. No allocation per delivery beyond the queue's retained capacity —
/// entries are structs and the coalesce set is cleared, never rebuilt (design doc D5/D9).
/// </summary>
public sealed class LotEventRegistry
{
	/// <summary>
	/// The event vocabulary this build accepts. Only the sensor-driven pair fires today; the rest
	/// are reserved names (design doc D17) so the API surface is settled once — a subscription to
	/// a reserved name is accepted and simply never fires.
	/// </summary>
	public static readonly string[] KnownEvents =
	{
		"touched", "touchEnded", "clicked", "entered", "exited", "destroyed", "playerJoined", "playerLeft"
	};

	/// <summary>Max queue entries dispatched per frame; the overflow is dropped with one throttled
	/// line (design doc D9).</summary>
	public const int MaxDeliveriesPerFrame = 128;

	/// <summary>Minimum wall-clock seconds between two drop-warning lines.</summary>
	public const double WarnThrottleSeconds = 1.0;

	private struct Delivery
	{
		public int Subject;
		public string Event;
		public int Other;
		public int PlayerId;
		public bool FromPlayer;
		public long Sequence;
	}

	// subject -> event -> subscriber handles, in first-subscription order (delivery order).
	private readonly Dictionary<int, Dictionary<string, List<int>>> _bySubject = new Dictionary<int, Dictionary<string, List<int>>>();
	// subscriber -> the (subject, event) pairs it watches, for subscriber-side cleanup.
	private readonly Dictionary<int, List<(int Subject, string Event)>> _bySubscriber = new Dictionary<int, List<(int, string)>>();
	private readonly List<Delivery> _queue = new List<Delivery>();
	private readonly HashSet<(int Subject, int Other, string Event)> _queued = new HashSet<(int, int, string)>();
	private readonly List<int> _pendingSensors = new List<int>();
	private readonly HashSet<int> _attachedSensors = new HashSet<int>();
	private readonly List<(int Subject, string Event)> _subscriberScratch = new List<(int, string)>();

	private long _sequence;
	private double _lastDropWarnSeconds = double.NegativeInfinity;
	private readonly Func<double> _nowSeconds;
	private readonly Action<string> _warn;

	/// <summary>Owner hook: create (or find) the subject's event sensor, or say why not.</summary>
	public Func<int, SensorAttachResult> AttachSensor { get; set; }

	/// <summary>Owner hook: remove the subject's event sensor (session end / last unsubscribe).</summary>
	public Action<int> DetachSensor { get; set; }

	/// <summary>Owner hook: true while a player session is active (design doc D1).</summary>
	public Func<bool> SessionActive { get; set; }

	/// <summary>
	/// Owner hook: deliver one event to one subscriber under a watchdog unit. Assigned by the
	/// scene to <see cref="LuaNetBridge.TryDispatchEvent"/>; tests assign a recorder.
	/// </summary>
	public Func<int, string, int, int, int, bool, bool> Dispatch { get; set; }

	// Test/observability counters (the VmRebuildCount pattern).
	public int DeliveredEvents { get; private set; }
	public int CoalescedEvents { get; private set; }
	public int DroppedEvents { get; private set; }
	public int QueuedCount { get { return _queue.Count; } }

	public LotEventRegistry(Action<string> warn, Func<double> nowSeconds = null)
	{
		_warn = warn;
		_nowSeconds = nowSeconds ?? DefaultClock;
	}

	private static double DefaultClock()
	{
		return System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
	}

	/// <summary>True when <paramref name="eventName"/> is in the accepted vocabulary.</summary>
	public static bool IsKnownEvent(string eventName)
	{
		if (string.IsNullOrEmpty(eventName)) return false;
		for (int i = 0; i < KnownEvents.Length; i++)
		{
			if (KnownEvents[i] == eventName) return true;
		}
		return false;
	}

	/// <summary>The events delivered from a subject's overlap sensor. The others wait on their own
	/// milestones (design doc D17): subscription sugar for §5.2/§5.3, §8.1, §3.13.</summary>
	public static bool IsSensorDriven(string eventName)
	{
		return eventName == "touched" || eventName == "touchEnded";
	}

	/// <summary>
	/// Records that <paramref name="subscriber"/> watches <paramref name="eventName"/> on
	/// <paramref name="subject"/>. Idempotent (add-if-missing), so the Lua sugar's replace path and
	/// two scripts on one entity both mirror cleanly. False (with a warning) for a bad handle or an
	/// unknown event name, so a mistake is reported instead of silently doing nothing.
	/// </summary>
	public bool Subscribe(int subscriber, int subject, string eventName)
	{
		if (subscriber < 0 || subject < 0 || !IsKnownEvent(eventName))
		{
			Warn("[Events] Subscribe refused (subscriber " + subscriber + ", subject " + subject +
				", event '" + (eventName ?? "") + "'): bad handle or unknown event");
			return false;
		}

		Dictionary<string, List<int>> events;
		if (!_bySubject.TryGetValue(subject, out events))
		{
			events = new Dictionary<string, List<int>>();
			_bySubject[subject] = events;
		}
		List<int> subscribers;
		if (!events.TryGetValue(eventName, out subscribers))
		{
			subscribers = new List<int>();
			events[eventName] = subscribers;
		}
		if (!subscribers.Contains(subscriber)) subscribers.Add(subscriber);

		List<(int Subject, string Event)> watched;
		if (!_bySubscriber.TryGetValue(subscriber, out watched))
		{
			watched = new List<(int, string)>();
			_bySubscriber[subscriber] = watched;
		}
		bool known = false;
		for (int i = 0; i < watched.Count; i++)
		{
			if (watched[i].Subject == subject && watched[i].Event == eventName)
			{
				known = true;
				break;
			}
		}
		if (!known) watched.Add((subject, eventName));

		MarkPendingIfSensorDriven(subject);
		return true;
	}

	/// <summary>Removes one (subscriber, subject, event) subscription. Safe when absent. The last
	/// subscriber of a subject detaches its sensor.</summary>
	public void Unsubscribe(int subscriber, int subject, string eventName)
	{
		RemoveEventSubscription(subscriber, subject, eventName);
	}

	/// <summary>Subscribers watching (subject, event) in declaration order, or null. Internal to
	/// the drain loop; callers must not mutate the list.</summary>
	private List<int> FindSubscribers(int subject, string eventName)
	{
		Dictionary<string, List<int>> events;
		if (!_bySubject.TryGetValue(subject, out events)) return null;
		List<int> subscribers;
		return events.TryGetValue(eventName, out subscribers) ? subscribers : null;
	}

	/// <summary>True while anything watches (subject, event). Test observability.</summary>
	public bool HasSubscription(int subject, string eventName)
	{
		List<int> subscribers = FindSubscribers(subject, eventName);
		return subscribers != null && subscribers.Count > 0;
	}

	/// <summary>How many subscribers watch (subject, event). Test observability.</summary>
	public int SubscriberCount(int subject, string eventName)
	{
		List<int> subscribers = FindSubscribers(subject, eventName);
		return subscribers != null ? subscribers.Count : 0;
	}

	/// <summary>True while the subject's sensor is attached. Test observability.</summary>
	public bool HasAttachedSensor(int subject)
	{
		return _attachedSensors.Contains(subject);
	}

	/// <summary>True while the subject's sensor is waiting for its node to resolve. Test observability.</summary>
	public bool HasPendingSensor(int subject)
	{
		return _pendingSensors.Contains(subject);
	}

	/// <summary>
	/// The shared removal core: drop the subscriber from (subject, event), prune the now-empty
	/// levels, detach the sensor when the subject has no subscribers left (design doc D13), and
	/// forget the pair on the subscriber's own list.
	/// </summary>
	private void RemoveEventSubscription(int subscriber, int subject, string eventName)
	{
		Dictionary<string, List<int>> events;
		if (_bySubject.TryGetValue(subject, out events))
		{
			List<int> subscribers;
			if (events.TryGetValue(eventName, out subscribers))
			{
				subscribers.Remove(subscriber);
				if (subscribers.Count == 0)
				{
					events.Remove(eventName);
					if (events.Count == 0)
					{
						_bySubject.Remove(subject);
						RemoveSubjectBookkeeping(subject);
					}
				}
			}
		}
		RemoveRegistration(subscriber, subject, eventName);
	}

	/// <summary>Drops one (subject, event) pair from a subscriber's own list; removes the list when
	/// empty.</summary>
	private void RemoveRegistration(int subscriber, int subject, string eventName)
	{
		List<(int Subject, string Event)> watched;
		if (!_bySubscriber.TryGetValue(subscriber, out watched)) return;
		for (int i = watched.Count - 1; i >= 0; i--)
		{
			if (watched[i].Subject == subject && watched[i].Event == eventName) watched.RemoveAt(i);
		}
		if (watched.Count == 0) _bySubscriber.Remove(subscriber);
	}

	// --- Lifecycle (design doc D13) -------------------------------------------------------------

	/// <summary>
	/// Forgets an entity on BOTH ends: what others registered on it as a subject (their entries go
	/// too — a destroyed subject can never fire again) and what it registered on others as a
	/// subscriber. Called from the single entity-destroy walk, so a freed node leaves nothing in
	/// either registry. Safe when the handle is unknown.
	/// </summary>
	public void ForgetEntity(int handle)
	{
		// It as the subject: drop every subscriber's registration and the subject wholesale.
		Dictionary<string, List<int>> watchedByOthers;
		if (_bySubject.TryGetValue(handle, out watchedByOthers))
		{
			foreach (KeyValuePair<string, List<int>> pair in watchedByOthers)
			{
				for (int i = 0; i < pair.Value.Count; i++)
				{
					RemoveRegistration(pair.Value[i], handle, pair.Key);
				}
			}
			_bySubject.Remove(handle);
			RemoveSubjectBookkeeping(handle);
		}

		// It as the subscriber: remove every pair it watched (scratch copy: the removal mutates
		// the very list we would be iterating).
		List<(int Subject, string Event)> watched;
		if (_bySubscriber.TryGetValue(handle, out watched))
		{
			_subscriberScratch.Clear();
			_subscriberScratch.AddRange(watched);
			for (int i = 0; i < _subscriberScratch.Count; i++)
			{
				RemoveEventSubscription(handle, _subscriberScratch[i].Subject, _subscriberScratch[i].Event);
			}
		}
	}

	/// <summary>
	/// Wipes everything and detaches every sensor. Used on reload (the re-run scripts re-subscribe)
	/// and on lot teardown, so no subscription or sensor survives the world it belonged to.
	/// </summary>
	public void ClearAll()
	{
		_bySubject.Clear();
		_bySubscriber.Clear();
		_queue.Clear();
		_queued.Clear();
		_pendingSensors.Clear();
		DetachAllSensors();
	}

	/// <summary>Session entry (design doc D1): every subject with a sensor-driven subscription is
	/// marked pending, and the first drain attaches the sensors.</summary>
	public void OnSessionStarted()
	{
		// Copy the keys first: no mutation here, but a subject subscribed DURING the sweep would
		// otherwise invalidate the enumerator.
		_subjectScratch.Clear();
		foreach (int subject in _bySubject.Keys) _subjectScratch.Add(subject);
		for (int i = 0; i < _subjectScratch.Count; i++) MarkPendingIfSensorDriven(_subjectScratch[i]);
	}

	/// <summary>Session exit: sensors come down (they are session artifacts) and the pending list
	/// and queue are cleared, including anything queued during the last frame.</summary>
	public void OnSessionEnded()
	{
		DetachAllSensors();
		_pendingSensors.Clear();
		_queue.Clear();
		_queued.Clear();
	}

	private readonly List<int> _subjectScratch = new List<int>();

	/// <summary>Marks the subject pending when it has a sensor-driven subscription and a session is
	/// running; the drain then creates the sensor as soon as the node resolves.</summary>
	private void MarkPendingIfSensorDriven(int subject)
	{
		if (SessionActive == null || !SessionActive()) return;
		if (_attachedSensors.Contains(subject) || _pendingSensors.Contains(subject)) return;
		if (!HasSensorDrivenSubscription(subject)) return;
		_pendingSensors.Add(subject);
	}

	private bool HasSensorDrivenSubscription(int subject)
	{
		Dictionary<string, List<int>> events;
		if (!_bySubject.TryGetValue(subject, out events)) return false;
		foreach (KeyValuePair<string, List<int>> pair in events)
		{
			if (pair.Value.Count > 0 && IsSensorDriven(pair.Key)) return true;
		}
		return false;
	}

	/// <summary>Detaches a subject's sensor if attached and drops it from the pending list. Shared
	/// by the last-unsubscribe path, destroy and session end.</summary>
	private void RemoveSubjectBookkeeping(int subject)
	{
		_pendingSensors.Remove(subject);
		if (!_attachedSensors.Remove(subject)) return;
		Action<int> detach = DetachSensor;
		if (detach != null) detach(subject);
	}

	private void DetachAllSensors()
	{
		Action<int> detach = DetachSensor;
		if (detach != null)
		{
			foreach (int subject in _attachedSensors) detach(subject);
		}
		_attachedSensors.Clear();
	}

	/// <summary>
	/// Tries to attach every pending subject's sensor: Ok attaches, Retry leaves it pending for the
	/// next drain (the part may be spawned a frame later), Refused reports once and gives up — a
	/// decal or a mesh-less group cannot host a sensor, and saying so beats failing silently.
	/// </summary>
	private void ProcessPendingSensors()
	{
		if (_pendingSensors.Count == 0) return;
		Func<int, SensorAttachResult> attach = AttachSensor;
		if (attach == null) return;
		for (int i = 0; i < _pendingSensors.Count; i++)
		{
			int subject = _pendingSensors[i];
			SensorAttachResult result = attach(subject);
			if (result == SensorAttachResult.Retry) continue;
			_pendingSensors.RemoveAt(i--);
			if (result == SensorAttachResult.Ok) _attachedSensors.Add(subject);
			else
			{
				Warn("[Events] entity " + subject + " cannot host an event sensor (no collision " +
					"shape, or it is not a part); its subscriptions will not fire");
			}
		}
	}

	private void Warn(string message)
	{
		Action<string> warn = _warn;
		if (warn != null) warn(message);
	}

	private void WarnThrottled(string message)
	{
		double now = _nowSeconds();
		if (now - _lastDropWarnSeconds < WarnThrottleSeconds) return;
		_lastDropWarnSeconds = now;
		Warn(message);
	}

	// --- Delivery (design doc D5/D6/D8/D9) ------------------------------------------------------

	/// <summary>
	/// Queues one delivery. Called by the scene's sensor callbacks during the physics step; Lua
	/// never runs here (design doc D6). Duplicates of (subject, event, other) inside one frame
	/// coalesce (design doc D9).
	/// </summary>
	public void Enqueue(int subject, string eventName, int other, bool fromPlayer, int playerId)
	{
		if (!HasSubscription(subject, eventName)) return;
		if (!_queued.Add((subject, other, eventName)))
		{
			CoalescedEvents++;
			return;
		}
		_queue.Add(new Delivery
		{
			Subject = subject,
			Event = eventName,
			Other = other,
			PlayerId = playerId,
			FromPlayer = fromPlayer,
			Sequence = _sequence++
		});
	}

	/// <summary>
	/// Drains the frame's deliveries: pending sensors first (a subject spawned after its
	/// subscription resolves here), then the queue in deterministic order — subject handle
	/// ascending, then enqueue sequence (design doc D8) — with each subscriber call going through
	/// the owner's <see cref="Dispatch"/>. Past <see cref="MaxDeliveriesPerFrame"/> the overflow is
	/// dropped with ONE throttled line (design doc D9). Called from ScriptRuntime.Tick while a
	/// session is active.
	/// </summary>
	public void Drain()
	{
		ProcessPendingSensors();

		int count = _queue.Count;
		if (count == 0) return;

		_queue.Sort(DeliveryOrder);
		int delivered = 0;
		int dropped = 0;
		for (int i = 0; i < count; i++)
		{
			if (delivered >= MaxDeliveriesPerFrame)
			{
				dropped = count - i;
				break;
			}
			delivered++;
			Delivery delivery = _queue[i];
			List<int> subscribers = FindSubscribers(delivery.Subject, delivery.Event);
			if (subscribers == null) continue; // everybody unsubscribed between enqueue and drain
			Func<int, string, int, int, int, bool, bool> dispatch = Dispatch;
			if (dispatch == null) continue;
			for (int s = 0; s < subscribers.Count; s++)
			{
				dispatch(delivery.Subject, delivery.Event, subscribers[s], delivery.Other,
					delivery.PlayerId, delivery.FromPlayer);
			}
		}
		if (dropped > 0)
		{
			DroppedEvents += dropped;
			WarnThrottled("[Events] " + dropped + " event deliveries dropped this frame (cap " +
				MaxDeliveriesPerFrame + "); coalesced " + CoalescedEvents + " so far");
		}
		DeliveredEvents += delivered;
		_queue.Clear();
		_queued.Clear();
	}

	private static readonly Comparison<Delivery> DeliveryOrder = CompareDeliveries;

	private static int CompareDeliveries(Delivery a, Delivery b)
	{
		if (a.Subject != b.Subject) return a.Subject < b.Subject ? -1 : 1;
		if (a.Sequence != b.Sequence) return a.Sequence < b.Sequence ? -1 : 1;
		return 0;
	}
}
