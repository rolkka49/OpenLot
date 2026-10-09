using System;
using System.Collections.Generic;
using Godot;

/// <summary>The easing curves the tween verbs accept (design doc 3.8 D9): a fixed pure-math set,
/// standard formulas, exact endpoints. No dependency; every curve is hand-verifiable in tests.</summary>
public enum LotEasingKind
{
	Linear,
	SineIn, SineOut, SineInOut,
	QuadIn, QuadOut, QuadInOut,
	CubicIn, CubicOut, CubicInOut
}

/// <summary>Pure easing math. Linear, sine, quadratic and cubic families in the in/out/in-out
/// variants; every curve maps 0 to exactly 0 and 1 to exactly 1.</summary>
public static class LotEasing
{
	private static readonly string[] Names =
	{
		"linear",
		"sineIn", "sineOut", "sineInOut",
		"quadIn", "quadOut", "quadInOut",
		"cubicIn", "cubicOut", "cubicInOut"
	};

	/// <summary>Parses an easing name (the vocabulary the sugar validates against).</summary>
	public static bool TryParse(string name, out LotEasingKind kind)
	{
		kind = LotEasingKind.Linear;
		if (string.IsNullOrEmpty(name)) return false;
		for (int i = 0; i < Names.Length; i++)
		{
			if (Names[i] == name)
			{
				kind = (LotEasingKind)i;
				return true;
			}
		}
		return false;
	}

	/// <summary>The curve's value at <paramref name="t"/> (0..1, clamped by the caller).</summary>
	public static float Apply(LotEasingKind kind, float t)
	{
		switch (kind)
		{
			case LotEasingKind.SineIn: return 1f - Mathf.Cos(t * Mathf.Pi * 0.5f);
			case LotEasingKind.SineOut: return Mathf.Sin(t * Mathf.Pi * 0.5f);
			case LotEasingKind.SineInOut: return -(Mathf.Cos(Mathf.Pi * t) - 1f) * 0.5f;
			case LotEasingKind.QuadIn: return t * t;
			case LotEasingKind.QuadOut: return 1f - (1f - t) * (1f - t);
			case LotEasingKind.QuadInOut:
				return t < 0.5f ? 2f * t * t : 1f - Mathf.Pow(-2f * t + 2f, 2f) * 0.5f;
			case LotEasingKind.CubicIn: return t * t * t;
			case LotEasingKind.CubicOut: return 1f - Mathf.Pow(1f - t, 3f);
			case LotEasingKind.CubicInOut:
				return t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) * 0.5f;
			default: return t;
		}
	}
}

/// <summary>What a tween interpolates.</summary>
public enum LotTweenKind
{
	Position,
	Rotation,
	Scale,
	Color,
	UIRect
}

/// <summary>
/// The lot's one scheduler (milestone 3.8, design doc D1/D2/D7): every running tween and timer on
/// a single accumulated-delta clock — simulation time, not wall time, so behaviour is
/// deterministic and pauses exactly with the session. Tweens are pure C# interpolation writing
/// live nodes (Lua never runs per frame); timer callbacks fire as one armed watchdog unit each
/// through the owner's <see cref="FireTimer"/> delegate. Godot types only, no scene ownership:
/// targets resolve through a delegate, so the whole thing is unit-testable headless.
///
/// Ownership (D10): a tween is owned by its TARGET handle (it dies with the part it animates); a
/// timer is owned by its SUBSCRIBER handle (destroying the entity cancels its timers). Both are
/// cleaned by <see cref="ForgetEntity"/>, the same hook the entity-destroy walk calls for events,
/// and by <see cref="ClearAll"/> on every script reload — nothing survives the world it belonged
/// to.
/// </summary>
public sealed class LotScheduler
{
	/// <summary>Live entries allowed per lot (design doc D7); past this the call is refused with
	/// one line and the sugar's token is a no-op.</summary>
	public const int MaxScheduled = 256;

	private struct Entry
	{
		public int Id;
		public int Owner;
		public bool IsTimer;
		public bool Repeating;
		public double Delay;       // timer: seconds until the next fire
		public double Interval;    // repeating timers
		public double Elapsed;     // tween: seconds since it started
		public double Duration;    // tweens
		public LotEasingKind Easing;
		public LotTweenKind Kind;
		public Node Target;
		public Vector4 From;
		public Vector4 To;
	}

	private readonly List<Entry> _entries = new List<Entry>();
	private readonly Action<string> _warn;
	private int _nextId = 1;

	public LotScheduler(Action<string> warn)
	{
		_warn = warn;
	}

	/// <summary>Owner hook: handle -> the node a tween targets (a part or a UI element), or null.</summary>
	public Func<int, Node> ResolveTarget { get; set; }

	/// <summary>Owner hook: fire one timer callback (owner handle, id, keep-registration) -> true
	/// when the handler ran (or is disabled); false when it no longer exists, so a repeating timer
	/// can be dropped.</summary>
	public Func<int, int, bool, bool> FireTimer { get; set; }

	/// <summary>Owner hook: write a UI rect through the same clamped path the Lua setter uses.</summary>
	public Action<LotUIElement, Vector2, Vector2> ApplyUiRect { get; set; }

	/// <summary>Live entries. Test/diagnostics observability.</summary>
	public int Count { get { return _entries.Count; } }

	/// <summary>True while an entry with that id is scheduled. Test observability.</summary>
	public bool Has(int id)
	{
		for (int i = 0; i < _entries.Count; i++)
		{
			if (_entries[i].Id == id) return true;
		}
		return false;
	}

	// --- Scheduling ----------------------------------------------------------------------------

	/// <summary>
	/// Schedules a tween (design doc D3/D4): reads the target's current value as the starting
	/// point, stores from/to, and returns an id the cancel token uses — or -1 (with a warning)
	/// for an unknown kind/easing, a bad duration, a missing target, a target whose type cannot
	/// hold the value, or a full schedule.
	/// </summary>
	public int ScheduleTween(int handle, string kindName, float a, float b, float c, float d,
		float duration, string easingName)
	{
		LotTweenKind kind;
		if (!TryParseKind(kindName, out kind))
		{
			Warn("[Tween] unknown property '" + (kindName ?? "") + "'; nothing scheduled");
			return -1;
		}
		LotEasingKind easing;
		if (!LotEasing.TryParse(easingName, out easing))
		{
			Warn("[Tween] unknown easing '" + (easingName ?? "") + "'; nothing scheduled");
			return -1;
		}
		if (duration < 0f)
		{
			Warn("[Tween] duration must not be negative; nothing scheduled");
			return -1;
		}
		if (_entries.Count >= MaxScheduled)
		{
			Warn("[Tween] schedule is full (" + MaxScheduled + " live entries); nothing scheduled");
			return -1;
		}
		Node target = ResolveTarget != null ? ResolveTarget(handle) : null;
		if (target == null || !GodotObject.IsInstanceValid(target))
		{
			Warn("[Tween] entity " + handle + " does not resolve; nothing scheduled");
			return -1;
		}

		Vector4 from;
		if (!TryReadCurrent(kind, target, out from))
		{
			Warn("[Tween] entity " + handle + " (" + target.GetType().Name + ") cannot " + kindName +
				"; nothing scheduled");
			return -1;
		}

		Entry entry = new Entry
		{
			Id = _nextId++,
			Owner = handle,
			Kind = kind,
			Target = target,
			From = from,
			To = new Vector4(a, b, c, d),
			Duration = duration,
			Easing = easing
		};
		_entries.Add(entry);
		return entry.Id;
	}

	/// <summary>Schedules a timer (design doc D5): a one-shot fires once after <paramref name="delay"/>,
	/// a repeating one every <paramref name="interval"/> seconds. Returns the id the cancel token
	/// uses, or -1 when the schedule is full.</summary>
	public int ScheduleTimer(int handle, double delay, bool repeating, double interval)
	{
		if (_entries.Count >= MaxScheduled)
		{
			Warn("[Tween] schedule is full (" + MaxScheduled + " live entries); nothing scheduled");
			return -1;
		}
		Entry entry = new Entry
		{
			Id = _nextId++,
			Owner = handle,
			IsTimer = true,
			Repeating = repeating,
			Delay = Math.Max(0.0, delay),
			Interval = repeating ? Math.Max(0.001, interval) : 0.0
		};
		_entries.Add(entry);
		return entry.Id;
	}

	/// <summary>Cancels one entry by id. False when it is not scheduled (already done/cancelled) —
	/// the token's harmless no-op path.</summary>
	public bool Cancel(int id)
	{
		for (int i = 0; i < _entries.Count; i++)
		{
			if (_entries[i].Id != id) continue;
			_entries.RemoveAt(i);
			return true;
		}
		return false;
	}

	private static bool TryParseKind(string name, out LotTweenKind kind)
	{
		switch (name)
		{
			case "position": kind = LotTweenKind.Position; return true;
			case "rotation": kind = LotTweenKind.Rotation; return true;
			case "scale": kind = LotTweenKind.Scale; return true;
			case "color": kind = LotTweenKind.Color; return true;
			case "rect": kind = LotTweenKind.UIRect; return true;
			default: kind = LotTweenKind.Position; return false;
		}
	}

	/// <summary>Reads the target's current value for the tween's kind; false when the node cannot
	/// hold it (a UI element has no Position, a plain Node3D has no Color).</summary>
	private static bool TryReadCurrent(LotTweenKind kind, Node target, out Vector4 value)
	{
		value = Vector4.Zero;
		switch (kind)
		{
			case LotTweenKind.Position:
			{
				Node3D node = target as Node3D;
				if (node == null) return false;
				value = new Vector4(node.Position.X, node.Position.Y, node.Position.Z, 0f);
				return true;
			}
			case LotTweenKind.Rotation:
			{
				Node3D node = target as Node3D;
				if (node == null) return false;
				value = new Vector4(node.RotationDegrees.X, node.RotationDegrees.Y, node.RotationDegrees.Z, 0f);
				return true;
			}
			case LotTweenKind.Scale:
			{
				Node3D node = target as Node3D;
				if (node == null) return false;
				value = new Vector4(node.Scale.X, node.Scale.Y, node.Scale.Z, 0f);
				return true;
			}
			case LotTweenKind.Color:
			{
				LotObject part = target as LotObject;
				if (part != null)
				{
					Color color = part.Color;
					value = new Vector4(color.R, color.G, color.B, color.A);
					return true;
				}
				LotUIElement element = target as LotUIElement;
				if (element != null)
				{
					value = new Vector4(element.Color.R, element.Color.G, element.Color.B, element.Color.A);
					return true;
				}
				return false;
			}
			default:
			{
				LotUIElement element = target as LotUIElement;
				if (element == null) return false;
				value = new Vector4(element.Position.X, element.Position.Y, element.Size.X, element.Size.Y);
				return true;
			}
		}
	}

	// --- Advancing -----------------------------------------------------------------------------

	/// <summary>
	/// One frame of the schedule: tweens interpolate (pure C#, writing the live node) and due
	/// timers fire exactly once through <see cref="FireTimer"/> — a repeating timer under a large
	/// delta reschedules one interval from now instead of bursting (design doc D7). A tween whose
	/// target no longer resolves is dropped silently. Called from `ScriptRuntime.Tick` while a
	/// session is active (D8).
	/// </summary>
	public void Advance(double delta)
	{
		if (_entries.Count == 0) return;
		if (delta < 0.0) delta = 0.0;

		for (int i = _entries.Count - 1; i >= 0; i--)
		{
			Entry entry = _entries[i];

			if (entry.IsTimer)
			{
				entry.Delay -= delta;
				if (entry.Delay > 0.0)
				{
					_entries[i] = entry;
					continue;
				}
				bool keep = entry.Repeating;
				Func<int, int, bool, bool> fire = FireTimer;
				bool handled = fire == null || fire(entry.Owner, entry.Id, keep);
				if (!entry.Repeating || !handled)
				{
					_entries.RemoveAt(i);
				}
				else
				{
					// At most one fire per frame: skip missed cycles, resume one interval from now.
					entry.Delay = entry.Interval;
					_entries[i] = entry;
				}
				continue;
			}

			if (entry.Target == null || !GodotObject.IsInstanceValid(entry.Target))
			{
				_entries.RemoveAt(i); // the part it animated is gone: nothing left to write
				continue;
			}

			entry.Elapsed += delta;
			float t = entry.Duration <= 0.0 ? 1f : (float)Math.Min(entry.Elapsed / entry.Duration, 1.0);
			float eased = LotEasing.Apply(entry.Easing, t);
			Vector4 value = new Vector4(
				entry.From.X + (entry.To.X - entry.From.X) * eased,
				entry.From.Y + (entry.To.Y - entry.From.Y) * eased,
				entry.From.Z + (entry.To.Z - entry.From.Z) * eased,
				entry.From.W + (entry.To.W - entry.From.W) * eased);
			ApplyTween(entry.Kind, entry.Target, value);

			if (t >= 1f) _entries.RemoveAt(i);
			else _entries[i] = entry;
		}
	}

	/// <summary>Writes one interpolated value into the live node — the same writes the Lua setters
	/// perform (the UI rect goes through the owner's clamped path).</summary>
	private void ApplyTween(LotTweenKind kind, Node target, Vector4 value)
	{
		switch (kind)
		{
			case LotTweenKind.Position:
			{
				Node3D node = target as Node3D;
				if (node != null) node.Position = new Vector3(value.X, value.Y, value.Z);
				break;
			}
			case LotTweenKind.Rotation:
			{
				Node3D node = target as Node3D;
				if (node != null) node.RotationDegrees = new Vector3(value.X, value.Y, value.Z);
				break;
			}
			case LotTweenKind.Scale:
			{
				Node3D node = target as Node3D;
				if (node != null) node.Scale = new Vector3(value.X, value.Y, value.Z);
				break;
			}
			case LotTweenKind.Color:
			{
				LotObject part = target as LotObject;
				if (part != null)
				{
					part.Color = new Color(value.X, value.Y, value.Z, value.W);
					break;
				}
				LotUIElement element = target as LotUIElement;
				if (element != null) element.Color = new Color(value.X, value.Y, value.Z, value.W);
				break;
			}
			default:
			{
				LotUIElement element = target as LotUIElement;
				if (element == null) break;
				Action<LotUIElement, Vector2, Vector2> apply = ApplyUiRect;
				if (apply != null) apply(element, new Vector2(value.X, value.Y), new Vector2(value.Z, value.W));
				else
				{
					element.Position = new Vector2(value.X, value.Y);
					element.Size = new Vector2(value.Z, value.W);
				}
				break;
			}
		}
	}

	// --- Lifecycle -----------------------------------------------------------------------------

	/// <summary>Drops every entry owned by (or targeting) <paramref name="handle"/> — the same
	/// entity-destroy hook the event registry uses (design doc D10).</summary>
	public void ForgetEntity(int handle)
	{
		for (int i = _entries.Count - 1; i >= 0; i--)
		{
			if (_entries[i].Owner == handle) _entries.RemoveAt(i);
		}
	}

	/// <summary>Wipes the whole schedule — every script reload and lot teardown, so nothing
	/// survives the world it belonged to.</summary>
	public void ClearAll()
	{
		_entries.Clear();
	}

	private void Warn(string message)
	{
		Action<string> warn = _warn;
		if (warn != null) warn(message);
	}
}

