using Godot;

/// <summary>
/// Verification for milestone 2.8's log sink: the bounded ring (cap, drop-oldest order), Clear,
/// the entry fields, and the script-output path — <c>print</c> and <c>log</c> driven through the
/// real VM and read back out of <see cref="LotLog"/>.
///
/// Runs from <c>BuilderScene._Ready</c> under <c>#if DEBUG</c> like every other suite, and clears
/// the sink on the way out so the Output window does not open full of self-test traffic.
/// </summary>
public static class LotLogSelfTest
{
	private static int _failures;
	private static int _checks;

	public static int Run()
	{
		_failures = 0;
		_checks = 0;

		TestRing();
		TestEntryFields();
		TestScriptOutput();
		TestConsoleScrollback();
		TestConsoleSelection();

		// Leave the sink empty: the suites around this one log real warnings, and a creator opening
		// the Output window should not see this suite's scratch entries.
		LotLog.Clear();

		GD.Print("[LotLogSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
		return _failures;
	}

	/// <summary>The ring: it holds exactly its capacity, drops the oldest first, and keeps order.</summary>
	private static void TestRing()
	{
		LotLog.Clear();
		Check("ring: a fresh sink is empty", LotLog.Count == 0);

		for (int i = 1; i <= LotLog.Capacity + 5; i++)
		{
			LotLog.Info("selftest", "e" + i);
		}
		Check("ring: the cap holds", LotLog.Count == LotLog.Capacity);
		Check("ring: the oldest entries were dropped, in order",
			LotLog.At(0).Text == "e6" && LotLog.At(1).Text == "e7");
		Check("ring: the newest entry is last", LotLog.At(LotLog.Count - 1).Text == "e" + (LotLog.Capacity + 5));

		LotLog.Clear();
		Check("ring: Clear empties it", LotLog.Count == 0);
	}

	/// <summary>Severity, source, text and the baked time label each entry carries.</summary>
	private static void TestEntryFields()
	{
		LotLog.Clear();
		LotLog.Info("a", "first");
		LotLog.Warn("b", "second");
		LotLog.Error("c", "third");

		Check("entries: count tracks adds", LotLog.Count == 3);
		Check("entries: severity is kept",
			LotLog.At(0).Severity == LotLogSeverity.Info
			&& LotLog.At(1).Severity == LotLogSeverity.Warn
			&& LotLog.At(2).Severity == LotLogSeverity.Error);
		Check("entries: source and text are kept",
			LotLog.At(1).Source == "b" && LotLog.At(1).Text == "second");
		Check("entries: the time is monotonic",
			LotLog.At(0).Seconds <= LotLog.At(2).Seconds && LotLog.At(0).Seconds >= 0.0);
		Check("entries: the time label is baked once",
			LotLog.At(0).TimeLabel.StartsWith("[") && LotLog.At(0).TimeLabel.EndsWith("]"));

		// Nulls are stored as empty text rather than crashing a producer.
		LotLog.Info(null, null);
		Check("entries: null source/text become empty", LotLog.At(3).Source == "" && LotLog.At(3).Text == "");

		LotLog.Clear();
	}

	/// <summary>The script-output path: <c>print</c> exists in both VM surfaces and both reach the
	/// sink through the bound <c>Lot.Log</c> method (§1.1 — Lua never touches the sink directly).</summary>
	private static void TestScriptOutput()
	{
		LuaManager lua = LuaManager.Instance;
		if (!lua.IsRuntimeAvailable)
		{
			Check("script output: print/log reach the sink (runtime unavailable, skipped)", true);
			return;
		}

		LotLog.Clear();

		// `print` is reinstated as a varargs alias of `log` (the sandbox strips Lua's native one),
		// so the entity sandbox must expose it.
		bool sandboxHasPrint = lua.RunString(
			"assert(type(__openlot_sandbox) == 'table' and type(__openlot_sandbox.print) == 'function', 'sandbox print missing')",
			"output_selftest_sandbox");
		Check("script output: the entity sandbox exposes print", sandboxHasPrint);

		// The scratch path (code editor, command strip) gets its own print; both forms must join
		// varargs with tabs, exactly like Lua's own print.
		bool ran = lua.RunString("print('smoke', 42, true)\nlog('via log')", "output_selftest");
		Check("script output: print and log run" + (ran ? "" : " (" + lua.LastError + ")"), ran);

		bool foundPrint = false;
		bool foundLog = false;
		for (int i = 0; i < LotLog.Count; i++)
		{
			LotLog.Entry entry = LotLog.At(i);
			if (entry.Source == "log" && entry.Text == "smoke\t42\ttrue") foundPrint = true;
			if (entry.Source == "log" && entry.Text == "via log") foundLog = true;
		}
		Check("script output: print lands in the sink with tab-joined arguments", foundPrint);
		Check("script output: log lands in the sink", foundLog);
	}

	/// <summary>The terminal console's scrollback (post-3.11 review): the same bounded,
	/// drop-oldest contract as the sink itself, sized for a UI surface.</summary>
	private static void TestConsoleScrollback()
	{
		ConsoleScrollback ring = new ConsoleScrollback();
		Check("scrollback: a fresh ring is empty", ring.Count == 0);

		Color color = new Color(0.25f, 0.9f, 0.35f);
		for (int i = 1; i <= ConsoleScrollback.Capacity + 5; i++)
		{
			ring.Add("c" + i, color);
		}
		Check("scrollback: the cap holds", ring.Count == ConsoleScrollback.Capacity);
		Check("scrollback: the oldest lines were dropped, in order",
			ring.At(0).Text == "c6" && ring.At(1).Text == "c7");
		Check("scrollback: the newest line is last",
			ring.At(ring.Count - 1).Text == "c" + (ConsoleScrollback.Capacity + 5));
		Check("scrollback: colors are baked per line", ring.At(0).Color == color);

		// The copy payload (the terminal's Ctrl+Shift+C / context-menu "copy all"): one
		// newline-joined string over the whole ring, oldest first.
		string[] joinedLines = ring.ToText().Split('\n');
		Check("scrollback: ToText joins every line with newlines",
			joinedLines.Length == ConsoleScrollback.Capacity &&
			joinedLines[0] == "c6" && joinedLines[1] == "c7" &&
			joinedLines[joinedLines.Length - 1] == "c" + (ConsoleScrollback.Capacity + 5));

		// Absolute ids (the selection addresses lines by these so the ring dropping its front
		// cannot shift what is selected). c1 is id 0, so the oldest held line (c6) is BaseId.
		Check("scrollback: TotalAdded counts every add", ring.TotalAdded == ConsoleScrollback.Capacity + 5);
		Check("scrollback: BaseId is the oldest held line's absolute id",
			ring.BaseId == ring.TotalAdded - ring.Count && ring.BaseId == 5);
		Check("scrollback: TryGetAbsolute resolves a held line",
			ring.TryGetAbsolute(ring.BaseId, out ConsoleScrollback.Line oldest) && oldest.Text == "c6");
		Check("scrollback: TryGetAbsolute refuses a dropped line", !ring.TryGetAbsolute(4, out ConsoleScrollback.Line _));
		Check("scrollback: TryGetAbsolute resolves the newest line",
			ring.TryGetAbsolute(ring.TotalAdded - 1, out ConsoleScrollback.Line newest) &&
			newest.Text == "c" + (ConsoleScrollback.Capacity + 5));

		ring.Clear();
		Check("scrollback: Clear empties it", ring.Count == 0);
		Check("scrollback: ToText of an empty ring is the empty string", ring.ToText() == "");
	}

	/// <summary>
	/// The selection model behind the terminal's drag-select: range ordering (either drag
	/// direction), the character snap rule, and the copy text. Pure, so it pins without an ImGui frame —
	/// the draw and input halves in Terminal are eye-checked, the project's usual split.
	/// </summary>
	private static void TestConsoleSelection()
	{
		ConsoleSelection selection = new ConsoleSelection();
		Check("selection: a fresh selection is empty", !selection.Active && !selection.HasRange);

		selection.Begin(10, 3);
		Check("selection: Begin anchors and focuses one point", selection.Active && !selection.HasRange);
		selection.Extend(12, 4);
		Check("selection: a drag creates a range", selection.HasRange);

		selection.Ordered(out long startLine, out int startChar, out long endLine, out int endChar);
		Check("selection: a forward drag orders front-to-back",
			startLine == 10 && startChar == 3 && endLine == 12 && endChar == 4);

		// A backward drag reports the same ordered range.
		selection.Begin(12, 4);
		selection.Extend(10, 3);
		selection.Ordered(out startLine, out startChar, out endLine, out endChar);
		Check("selection: a backward drag orders the same way",
			startLine == 10 && startChar == 3 && endLine == 12 && endChar == 4);

		selection.Clear();
		Check("selection: Clear empties it", !selection.Active && !selection.HasRange);

		// --- the row-from-offset rule ---
		// The console's origin is the absolute position its first line is drawn at, so it already
		// carries the window's scroll and no scroll term belongs in this mapping (getting that wrong
		// is what made a selection impossible to start on a scrolled console).
		Check("selection: the first row starts at the origin", ConsoleSelection.LineAt(0f, 10f, 5) == 0);
		Check("selection: part-way into a row is still that row", ConsoleSelection.LineAt(4f, 10f, 5) == 0);
		Check("selection: one step down is the second row", ConsoleSelection.LineAt(10f, 10f, 5) == 1);
		Check("selection: the row is clamped into the log at both ends",
			ConsoleSelection.LineAt(-500f, 10f, 5) == 0 && ConsoleSelection.LineAt(999f, 10f, 5) == 4);
		Check("selection: an empty log or a zero step has no row",
			ConsoleSelection.LineAt(5f, 10f, 0) == -1 && ConsoleSelection.LineAt(5f, 0f, 5) == -1);

		// --- the character snap rule (boundaries at 0, 5, 12 for a 2-char line) ---
		float[] offsets = { 0f, 5f, 12f };
		Check("selection: before the line start is char 0", ConsoleSelection.NearestCharacter(-3f, offsets) == 0);
		Check("selection: left of a character's midpoint snaps left", ConsoleSelection.NearestCharacter(2f, offsets) == 0);
		Check("selection: past a character's midpoint snaps right", ConsoleSelection.NearestCharacter(3f, offsets) == 1);
		Check("selection: past the line end is the last boundary", ConsoleSelection.NearestCharacter(99f, offsets) == 2);

		// --- the copy text ---
		string[] lines = { "alpha", "bravo", "charlie", "delta" };
		string mid = ConsoleSelection.BuildText(1, 2, 3, 3, id => id >= 0 && id < lines.Length ? lines[(int)id] : "");
		Check("selection: a multi-line copy slices the ends and keeps whole lines between (" +
			mid.Replace("\n", "\\n") + ")", mid == "avo\ncharlie\ndel");
		Check("selection: a single-line copy is a slice",
			ConsoleSelection.BuildText(2, 0, 2, 4, id => lines[(int)id]) == "char");
		Check("selection: a dropped line copies as empty",
			ConsoleSelection.BuildText(1, 0, 2, 3, id => id == 1 ? lines[1] : "") == "bravo\n");
		Check("selection: an inverted range copies nothing",
			ConsoleSelection.BuildText(5, 0, 1, 0, id => "x") == "");
	}

	private static void Check(string label, bool ok)
	{
		_checks++;
		if (!ok)
		{
			_failures++;
			GD.PrintErr("[LotLogSelfTest] FAIL  " + label);
		}
	}
}
