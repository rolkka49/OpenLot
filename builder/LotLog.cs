using Godot;

/// <summary>Severity of an Output entry; the window maps these to colours at draw time.</summary>
public enum LotLogSeverity
{
	Info,
	Warn,
	Error
}

/// <summary>
/// The creation environment's diagnostic sink (milestone 2.8): one place every producer reports to,
/// so build-mode messages accumulate and stay readable instead of flashing for four seconds or
/// landing only in Godot's console. The Output window draws it; nothing else reads it.
///
/// The buffer is a <b>bounded ring</b> — a fixed-capacity array, drop-oldest — because a runaway
/// script or a per-frame warning path must never grow it without limit. Entries are baked at log
/// time (including a preformatted time label), so the window's per-frame draw loop allocates
/// nothing while replaying rows.
///
/// Static on purpose: producers live in static and headless contexts, and the log is editor state
/// that intentionally survives a Test-mode round trip — the same lifetime reasoning as
/// <see cref="LotTextureCache"/>. Script output reaches it through the bound <c>Lot.Log</c> method
/// only (§1.1: Lua never touches this type directly).
/// </summary>
public static class LotLog
{
	/// <summary>Maximum entries kept; the oldest is dropped once the ring is full.</summary>
	public const int Capacity = 500;

	/// <summary>One baked entry. Fields are readonly-by-convention; the window reads copies, which
	/// is a value copy (no heap allocation).</summary>
	public struct Entry
	{
		public LotLogSeverity Severity;
		public string Source;
		public string Text;
		public double Seconds;   // since the session's first log call; for ordering and tests
		public string TimeLabel; // preformatted "[mm:ss.mmm]", baked once so drawing never formats
	}

	private static readonly Entry[] _entries = new Entry[Capacity];
	private static int _head;   // logical index 0 once the ring has wrapped
	private static int _count;
	private static double _originTicksMs = -1.0;

	/// <summary>Entries currently held (≤ <see cref="Capacity"/>).</summary>
	public static int Count { get { return _count; } }

	/// <summary>Reports an informational line (script output, lot outcomes, editor notices).</summary>
	public static void Info(string source, string text) { Add(LotLogSeverity.Info, source, text); }

	/// <summary>Reports a warning: something was refused or degraded, but the session continues.</summary>
	public static void Warn(string source, string text) { Add(LotLogSeverity.Warn, source, text); }

	/// <summary>Reports an error: an operation failed or scripting was suspended.</summary>
	public static void Error(string source, string text) { Add(LotLogSeverity.Error, source, text); }

	/// <summary>Drops every entry. Editor action (the window's Clear button).</summary>
	public static void Clear()
	{
		_head = 0;
		_count = 0;
	}

	/// <summary>The entry at a logical index (0 = oldest). Behaviour is undefined out of range.</summary>
	public static Entry At(int index)
	{
		return _entries[(_head + index) % Capacity];
	}

	private static void Add(LotLogSeverity severity, string source, string text)
	{
		double ticksMs = Time.GetTicksMsec();
		if (_originTicksMs < 0.0) _originTicksMs = ticksMs;
		double seconds = (ticksMs - _originTicksMs) / 1000.0;

		int index;
		if (_count < Capacity)
		{
			index = (_head + _count) % Capacity;
			_count++;
		}
		else
		{
			// Full: overwrite the oldest and advance the head.
			index = _head;
			_head = (_head + 1) % Capacity;
		}

		_entries[index] = new Entry
		{
			Severity = severity,
			Source = source ?? "",
			Text = text ?? "",
			Seconds = seconds,
			TimeLabel = FormatTime(seconds)
		};
	}

	/// <summary>Formats seconds-since-session-start as "[mm:ss.mmm]". Called once per entry, never
	/// per draw.</summary>
	private static string FormatTime(double seconds)
	{
		if (seconds < 0.0) seconds = 0.0;
		int totalMs = (int)(seconds * 1000.0);
		int minutes = (totalMs / 60000) % 100;
		int secs = (totalMs / 1000) % 60;
		int millis = totalMs % 1000;
		return "[" + minutes.ToString("00") + ":" + secs.ToString("00") + "." + millis.ToString("000") + "]";
	}
}
