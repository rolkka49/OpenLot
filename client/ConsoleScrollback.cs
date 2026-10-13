using Godot;

/// <summary>
/// The terminal console's scrollback: a bounded, drop-oldest ring of rendered lines — the same
/// pattern <see cref="LotLog"/> uses (§2.8), sized for a UI surface rather than a diagnostics
/// sink (post-3.11 review: the console buffer used to grow without limit). Extracted from
/// <see cref="Terminal"/> so the cap is unit-testable headless (the Terminal node cannot be
/// instantiated in the suites — it subscribes ImGui layout state in its _Ready), and because
/// §5.8's streaming command output will write into this same buffer.
///
/// Colors are baked at <see cref="Add"/> time; the draw loop only reads. Trimming has no UI
/// effect today — the console force-anchors its scroll to the bottom every frame — so this is a
/// memory bound first; if §5.1 restores free scrolling, revisit the cap.
/// </summary>
public sealed class ConsoleScrollback
{
	/// <summary>Maximum lines kept; the oldest is dropped once the ring is full.</summary>
	public const int Capacity = 2000;

	/// <summary>One rendered line: the text plus the color it was logged with.</summary>
	public struct Line
	{
		public string Text;
		public Color Color;
	}

	private readonly Line[] _lines = new Line[Capacity];
	private int _head;   // logical index 0 once the ring has wrapped
	private int _count;

	/// <summary>Lines currently held (≤ <see cref="Capacity"/>).</summary>
	public int Count { get { return _count; } }

	/// <summary>
	/// How many lines have ever been added: a monotonic id counter, so the newest held line's
	/// absolute id is <c>TotalAdded - 1</c>. A selection stores absolute ids rather than indices
	/// (<see cref="ConsoleSelection"/>) so a line dropping off the front of the ring cannot shift
	/// what is selected.
	/// </summary>
	public long TotalAdded { get; private set; }

	/// <summary>The absolute id of the oldest held line — the ring's shifting window origin.</summary>
	public long BaseId { get { return TotalAdded - _count; } }

	public void Add(string text, Color color)
	{
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
		_lines[index] = new Line { Text = text, Color = color };
		TotalAdded++;
	}

	/// <summary>The line at a logical index (0 = oldest). Behaviour is undefined out of range.</summary>
	public Line At(int index)
	{
		return _lines[(_head + index) % Capacity];
	}

	/// <summary>
	/// The line that was added at absolute id <paramref name="id"/> and is still held. False once it
	/// has been dropped off the front — which is how the selection survives (or deliberately ends)
	/// across the ring's own trimming.
	/// </summary>
	public bool TryGetAbsolute(long id, out Line line)
	{
		long index = id - BaseId;
		if (index < 0 || index >= _count)
		{
			line = default(Line);
			return false;
		}
		line = At((int)index);
		return true;
	}

	/// <summary>Drops every line.</summary>
	public void Clear()
	{
		_head = 0;
		_count = 0;
	}

	/// <summary>
	/// The whole buffer as one newline-joined string — the terminal's "copy all output" payload.
	/// Built on demand (never per frame): the console draws line by line and only a copy gesture
	/// needs the joined form, so this allocates only when the user asks to copy.
	/// </summary>
	public string ToText()
	{
		if (_count == 0) return "";
		System.Text.StringBuilder text = new System.Text.StringBuilder();
		for (int i = 0; i < _count; i++)
		{
			if (i > 0) text.Append('\n');
			text.Append(At(i).Text);
		}
		return text.ToString();
	}
}
