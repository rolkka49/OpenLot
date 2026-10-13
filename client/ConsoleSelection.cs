using System;
using System.Collections.Generic;

/// <summary>
/// The terminal console's text selection. A drag-selection model over the scrollback, addressed by
/// <b>absolute line id</b> (not index) so a line dropping off the front of the bounded ring cannot
/// silently shift what is selected.
///
/// Pure: no ImGui, no Godot. The range math, the hit-test rule and the copy text are therefore
/// unit-testable headless — the <see cref="ConsoleScrollback"/> precedent. The draw and input halves
/// live in <see cref="Terminal"/>.
/// </summary>
public sealed class ConsoleSelection
{
	/// <summary>False while nothing is selected (before the first drag, after <see cref="Clear"/>).</summary>
	public bool Active { get; private set; }

	/// <summary>The end the drag started at; with <see cref="FocusLine"/> it defines the range.</summary>
	public long AnchorLine { get; private set; }
	public int AnchorChar { get; private set; }

	/// <summary>The end the cursor is at (the moving end of the drag).</summary>
	public long FocusLine { get; private set; }
	public int FocusChar { get; private set; }

	/// <summary>True when the two ends differ — i.e. at least one character is selected.</summary>
	public bool HasRange
	{
		get { return Active && (AnchorLine != FocusLine || AnchorChar != FocusChar); }
	}

	/// <summary>Starts a new selection at (line, char). Coordinates are absolute line ids.</summary>
	public void Begin(long line, int character)
	{
		Active = true;
		AnchorLine = line;
		AnchorChar = character < 0 ? 0 : character;
		FocusLine = AnchorLine;
		FocusChar = AnchorChar;
	}

	/// <summary>Moves the moving end of the drag to (line, char). No effect before <see cref="Begin"/>.</summary>
	public void Extend(long line, int character)
	{
		if (!Active) return;
		FocusLine = line;
		FocusChar = character < 0 ? 0 : character;
	}

	public void Clear()
	{
		Active = false;
		AnchorLine = FocusLine = 0;
		AnchorChar = FocusChar = 0;
	}

	/// <summary>
	/// Orders the two ends so the range can be walked front-to-back: the start line/char are the
	/// smaller of the pair (line first, then char). Deterministic and independent of drag direction.
	/// </summary>
	public void Ordered(out long startLine, out int startChar, out long endLine, out int endChar)
	{
		if (AnchorLine < FocusLine || (AnchorLine == FocusLine && AnchorChar <= FocusChar))
		{
			startLine = AnchorLine; startChar = AnchorChar;
			endLine = FocusLine; endChar = FocusChar;
		}
		else
		{
			startLine = FocusLine; startChar = FocusChar;
			endLine = AnchorLine; endChar = AnchorChar;
		}
	}

	/// <summary>
	/// The line index nearest a vertical offset into the console's text block.
	///
	/// <paramref name="localY"/> is measured from the console's <b>own origin</b> — the absolute
	/// screen position its first line is drawn at, which already carries the window's scroll. So no
	/// scroll term belongs in this mapping; adding one (subtracting the scroll again) puts every row
	/// off by the scroll amount and silently breaks hit-testing on a scrolled console, which is
	/// exactly what happened once.
	///
	/// The result is clamped into the log, so a press past either end anchors to the nearest line
	/// rather than doing nothing. Returns -1 when there is no row to hit (an empty log, or a zero
	/// line step). Pure, so the rule is pinned headless.
	/// </summary>
	public static int LineAt(float localY, float step, int lineCount)
	{
		if (lineCount <= 0 || step <= 0f) return -1;
		int index = (int)Math.Floor(localY / step);
		if (index < 0) return 0;
		if (index >= lineCount) return lineCount - 1;
		return index;
	}

	/// <summary>
	/// The boundary index (0..length) nearest to a horizontal offset within a line, given that
	/// line's cumulative character offsets (<c>offsets[k]</c> is the left edge of character
	/// <c>k</c>; <c>offsets[length]</c> is the line width). Pure so the snap rule is pinned without
	/// an ImGui frame: past the midpoint of a character, the caret snaps to its right edge.
	/// </summary>
	public static int NearestCharacter(float x, IReadOnlyList<float> offsets)
	{
		int last = offsets.Count - 1;
		if (last <= 0) return 0;
		if (x <= offsets[0]) return 0;
		if (x >= offsets[last]) return last;

		int index = 0;
		while (index < last && offsets[index + 1] <= x) index++;
		float left = offsets[index];
		float right = offsets[index + 1];
		return (x - left) > (right - left) * 0.5f ? index + 1 : index;
	}

	/// <summary>
	/// The selected text. <paramref name="lineText"/> supplies a line's text by absolute id (an empty
	/// string for a line that has since been dropped); the first line is sliced from
	/// <paramref name="startChar"/>, the last up to <paramref name="endChar"/>, and the lines between
	/// are whole. Lines are joined with a newline, exactly as they were logged.
	/// </summary>
	public static string BuildText(long startLine, int startChar, long endLine, int endChar, Func<long, string> lineText)
	{
		if (lineText == null || endLine < startLine) return "";

		System.Text.StringBuilder text = new System.Text.StringBuilder();
		bool first = true;
		for (long id = startLine; id <= endLine; id++)
		{
			string line = lineText(id) ?? "";
			int from = id == startLine ? Clamp(startChar, line.Length) : 0;
			int to = id == endLine ? Clamp(endChar, line.Length) : line.Length;
			if (to < from) to = from;
			if (!first) text.Append('\n');
			text.Append(line, from, to - from);
			first = false;
		}
		return text.ToString();
	}

	private static int Clamp(int value, int length)
	{
		if (value < 0) return 0;
		if (value > length) return length;
		return value;
	}
}
