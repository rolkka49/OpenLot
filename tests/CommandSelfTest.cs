using System.Collections.Generic;
using Godot;

/// <summary>
/// Verification for the terminal command surface (CommandParser + CommandRegistry, post-3.11
/// friction review): tokenizing (quotes, spacing, the active partial), registration determinism,
/// dispatch, unregistration and the ghost-completion paths. Pure — no Terminal, no ImGui, no
/// scene — so it runs with the other root suites from <c>BuilderScene._Ready</c>.
///
/// Follows the project's self-test convention: no framework, prints pass/fail lines, returns the
/// failure count.
/// </summary>
public static class CommandSelfTest
{
	private static int _checks;
	private static int _failures;

	public static int Run()
	{
		_checks = 0;
		_failures = 0;

		TestTokenizer();
		TestRegistry();

		GD.Print("[CommandSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
		return _failures;
	}

	private static void Check(string name, bool condition)
	{
		_checks++;
		if (condition)
		{
			GD.Print("[CommandSelfTest] PASS  " + name);
		}
		else
		{
			_failures++;
			GD.PrintErr("[CommandSelfTest] FAIL  " + name);
		}
	}

	// --- tokenizer ------------------------------------------------------------------------------

	private static void TestTokenizer()
	{
		Check("tokenizer: empty input has no tokens", CommandParser.Tokenize("").Count == 0);
		Check("tokenizer: whitespace only has no tokens", CommandParser.Tokenize("   ").Count == 0);
		Check("tokenizer: a bare word is one token", CommandParser.Tokenize("help").Count == 1);

		List<string> quoted = CommandParser.Tokenize("wallpaper \"my file.png\"");
		Check("tokenizer: quotes keep a spaced argument whole",
			quoted.Count == 2 && quoted[1] == "my file.png");
		Check("tokenizer: runs of spaces collapse", CommandParser.Tokenize("a   b   c").Count == 3);
		Check("tokenizer: the active partial is the last token",
			CommandParser.GetActivePartialToken("ope") == "ope" &&
			CommandParser.GetActivePartialToken("openlot ") == "");
	}

	// --- registry -------------------------------------------------------------------------------

	private static void TestRegistry()
	{
		List<string> logs = new List<string>();
		CommandRegistry registry = new CommandRegistry(logs.Add);
		List<string> executed = new List<string>();

		registry.RegisterCommand("alpha", "first", (args) => executed.Add("alpha:" + args.Length),
			new string[] { "one", "two" });
		registry.RegisterCommand("beta", "second", (args) => executed.Add("beta:" + (args.Length > 0 ? args[0] : "")));

		Check("registry: names list in registration order",
			string.Join(",", registry.GetRegisteredCommandNames()) == "alpha,beta");

		registry.Execute("alpha 42");
		registry.Execute("beta seven");
		Check("registry: dispatch passes the args through",
			executed.Count == 2 && executed[0] == "alpha:1" && executed[1] == "beta:seven");

		logs.Clear();
		registry.Execute("gamma");
		Check("registry: an unknown command reports through the log sink",
			logs.Count == 1 && logs[0].Contains("'gamma'") && logs[0].Contains("not found"));

		registry.RegisterCommand("alpha", "duplicate", (args) => executed.Add("dup"));
		registry.Execute("alpha");
		Check("registry: a duplicate registration is ignored (first wins)",
			executed[executed.Count - 1] == "alpha:0");

		Check("registry: completion finds the untyped remainder",
			registry.GetCommandNameCompletion("alp") == "ha" &&
			registry.GetCommandNameCompletion("alpha") == "");
		Check("registry: sub-command suggestion completes from the registration",
			registry.GetSubCommandSuggestion("alpha", "o") == "ne" &&
			registry.GetSubCommandSuggestion("beta", "o") == "");

		registry.RegisterSubCommandVocabulary("gamma", new string[] { "list", "equip" });
		Check("registry: stub vocabulary suggests even without a command",
			registry.GetSubCommandSuggestion("gamma", "e") == "quip");

		Check("registry: unregister removes (and reports it)",
			registry.Unregister("beta") && !registry.Unregister("beta"));
		logs.Clear();
		registry.Execute("beta");
		Check("registry: an unregistered command is unknown again", logs.Count == 1);
		Check("registry: registration order survives an unregister",
			string.Join(",", registry.GetRegisteredCommandNames()) == "alpha");
	}
}
