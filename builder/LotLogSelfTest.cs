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
