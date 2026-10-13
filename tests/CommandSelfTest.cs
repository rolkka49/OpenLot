using System.Collections.Generic;
using Godot;

/// <summary>
/// Verification for the terminal command surface (CommandParser + CommandRegistry + the §5.1
/// command set): tokenizing (quotes, spacing, the active partial), registration determinism,
/// dispatch, unregistration and the ghost-completion paths, plus the auth gate's rule and the
/// pre-login whitelist, the alias table (parse, expand, persistence), the theme vocabulary and
/// the lot listing behind `ls` / `openlot list`. Pure — no Terminal, no ImGui, no scene — so it
/// runs with the other root suites from <c>BuilderScene._Ready</c>.
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
		TestAuthGate();
		TestAliasTable();
		TestThemeCatalog();
		TestLotListing();

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

	// --- auth gate (§5.1) -----------------------------------------------------------------------

	private static void TestAuthGate()
	{
		CommandRegistry registry = new CommandRegistry(null);
		registry.RegisterCommand("locked", "needs login", (args) => { }, null, true);
		registry.RegisterCommand("open", "pre-login", (args) => { }, null, false);
		registry.RegisterCommand("implicit", "no flag passed", (args) => { });

		registry.TryGetCommand("locked", out CommandRegistry.Command locked);
		registry.TryGetCommand("open", out CommandRegistry.Command open);
		registry.TryGetCommand("implicit", out CommandRegistry.Command implicitCommand);

		Check("gate: RequiresLogin is honoured as declared", locked.RequiresLogin && !open.RequiresLogin);
		Check("gate: requiresLogin defaults to true (fail-closed)", implicitCommand.RequiresLogin);
		Check("gate: a locked command is refused while signed out", !CommandRegistry.IsAllowed(false, locked));
		Check("gate: an open command runs while signed out", CommandRegistry.IsAllowed(false, open));
		Check("gate: signing in allows everything",
			CommandRegistry.IsAllowed(true, locked) && CommandRegistry.IsAllowed(true, open));

		Check("gate: lookup is case-insensitive",
			registry.TryGetCommand("LOCKED", out _) && registry.HasCommand("OpEn") && !registry.HasCommand("missing"));

		// The whitelist drives every core registration's flag (TerminalCommands.RequiresLogin), so
		// this list is the gate's whole pre-login surface.
		Check("gate: the pre-login whitelist is exactly help/clear/login/register/unregister",
			string.Join(",", TerminalCommands.PreLoginCommands) == "help,clear,login,register,unregister");
	}

	// --- alias table (§5.1) ---------------------------------------------------------------------

	private static void TestAliasTable()
	{
		AliasTable aliases = new AliasTable();
		Check("alias: a fresh table is empty", aliases.Count == 0 && aliases.Names.Count == 0);

		Check("alias: a quoted definition parses into name + command",
			AliasTable.TryParseDefinition(new string[] { "ll=openlot list" }, out string n1, out string v1) &&
			n1 == "ll" && v1 == "openlot list");
		Check("alias: an unquoted tail joins the value",
			AliasTable.TryParseDefinition(new string[] { "gg=openlot", "join", "abc" }, out string n2, out string v2) &&
			n2 == "gg" && v2 == "openlot join abc");
		Check("alias: a definition without '=' is refused",
			!AliasTable.TryParseDefinition(new string[] { "ll" }, out _, out _));
		Check("alias: a definition with no value is refused",
			!AliasTable.TryParseDefinition(new string[] { "ll=" }, out _, out _));

		aliases.Define("ll", "openlot list");
		aliases.Define("gg", "openlot join abc");
		Check("alias: define and case-insensitive lookup",
			aliases.Count == 2 && aliases.TryGet("LL", out string llValue) && llValue == "openlot list");
		Check("alias: names keep definition order",
			string.Join(",", aliases.Names) == "ll,gg");

		aliases.Define("ll", "ls");
		Check("alias: redefinition replaces in place",
			aliases.Count == 2 && string.Join(",", aliases.Names) == "ll,gg" &&
			aliases.TryGet("ll", out string llAgain) && llAgain == "ls");
		Check("alias: removal reports and drops",
			aliases.Remove("gg") && !aliases.Remove("gg") && aliases.Count == 1);

		Check("alias: expansion appends the rest of the typed line",
			AliasTable.CombineLine("openlot list", "ll foo bar", "ll") == "openlot list foo bar");
		Check("alias: a bare alias expands to its own line",
			AliasTable.CombineLine("ls", "ls", "ls") == "ls");

		// Persistence round-trip on a scratch file (the AccountStore pattern: explicit path).
		string path = "user://alias_selftest.cfg";
		aliases.Save(path);
		AliasTable reloaded = new AliasTable();
		reloaded.Load(path);
		Check("alias: persistence round-trips",
			reloaded.Count == 1 && reloaded.TryGet("ll", out string reloadedValue) && reloadedValue == "ls");

		// Our save must not drop blocks another writer owns (the wallpaper's, §5.4's later ones).
		ConfigFile config = new ConfigFile();
		config.Load(path);
		config.SetValue("wallpaper", "source_path", "user://keep.png");
		config.Save(path);
		reloaded.Save(path);
		ConfigFile after = new ConfigFile();
		after.Load(path);
		Check("alias: saving preserves other settings blocks",
			after.GetValue("wallpaper", "source_path", "").AsString() == "user://keep.png");
		Check("alias: a missing file loads as an empty table",
			EmptyAliasTableLoads());
		DirAccess.RemoveAbsolute(path);
	}

	// --- theme vocabulary (§5.1) ----------------------------------------------------------------

	private static void TestThemeCatalog()
	{
		Check("theme: preset shorthands resolve",
			TerminalThemeCatalog.TryParsePreset("Green Matrix", out string green) && green == TerminalThemeCatalog.GreenMatrixPreset &&
			TerminalThemeCatalog.TryParsePreset("amber", out string amber) && amber == TerminalThemeCatalog.AmberRetroPreset &&
			TerminalThemeCatalog.TryParsePreset("reset", out string reset) && reset == TerminalThemeCatalog.DefaultPreset);
		Check("theme: an unknown preset is refused",
			!TerminalThemeCatalog.TryParsePreset("solarized", out _) &&
			!TerminalThemeCatalog.TryParsePreset("", out _));

		// The list and the parser must agree: every name `theme` lists has to parse back to itself
		// (so a new preset cannot be added to one and forgotten in the other).
		bool everyListedNameParses = TerminalThemeCatalog.PresetNames.Count > 3;
		for (int i = 0; i < TerminalThemeCatalog.PresetNames.Count; i++)
		{
			string name = TerminalThemeCatalog.PresetNames[i];
			if (!TerminalThemeCatalog.TryParsePreset(name, out string parsed) || parsed != name)
			{
				everyListedNameParses = false;
				break;
			}
		}
		Check("theme: the listed presets are the ones the command accepts, and each parses back to itself",
			everyListedNameParses);
		Check("theme: the extra presets resolve, with their shorthands",
			TerminalThemeCatalog.TryParsePreset("cyberpunk", out string cyber) && cyber == TerminalThemeCatalog.CyberpunkPreset &&
			TerminalThemeCatalog.TryParsePreset("bladerunner", out string blade) && blade == TerminalThemeCatalog.CyberpunkPreset &&
			TerminalThemeCatalog.TryParsePreset("synthwave", out string synth) && synth == TerminalThemeCatalog.SynthwavePreset &&
			TerminalThemeCatalog.TryParsePreset("kawaii", out string kawaii) && kawaii == TerminalThemeCatalog.KawaiiPreset &&
			TerminalThemeCatalog.TryParsePreset("sakura", out string sakura) && sakura == TerminalThemeCatalog.KawaiiPreset &&
			TerminalThemeCatalog.TryParsePreset("navy", out string navy) && navy == TerminalThemeCatalog.NavyPreset &&
			TerminalThemeCatalog.TryParsePreset("forest", out string forest) && forest == TerminalThemeCatalog.ForestPreset &&
			TerminalThemeCatalog.TryParsePreset("mono", out string mono) && mono == TerminalThemeCatalog.MonoPreset);
		Check("theme: 'custom' resolves to the user preset slot",
			TerminalThemeCatalog.TryParsePreset("custom", out string custom) && custom == TerminalThemeCatalog.CustomPreset);

		Check("theme: hex #rrggbb parses to the right components",
			TerminalThemeCatalog.TryParseColor("#33ff88", out Color hex) &&
			Mathf.Abs(hex.R - 0x33 / 255f) < 0.002f &&
			Mathf.Abs(hex.G - 0xff / 255f) < 0.002f &&
			Mathf.Abs(hex.B - 0x88 / 255f) < 0.002f &&
			Mathf.Abs(hex.A - 1f) < 0.002f);
		Check("theme: short hex and named colors parse",
			TerminalThemeCatalog.TryParseColor("#f00", out Color shortHex) &&
			Mathf.Abs(shortHex.R - 1f) < 0.002f && Mathf.Abs(shortHex.G) < 0.002f &&
			TerminalThemeCatalog.TryParseColor("Green", out Color named) &&
			Mathf.Abs(named.G - 0.9f) < 0.002f);
		Check("theme: malformed colors are refused",
			!TerminalThemeCatalog.TryParseColor("#12345", out _) &&
			!TerminalThemeCatalog.TryParseColor("#gggggg", out _) &&
			!TerminalThemeCatalog.TryParseColor("nope", out _) &&
			!TerminalThemeCatalog.TryParseColor("", out _));
	}

	// --- lot listing (`ls` / `openlot list`) -----------------------------------------------------

	private static void TestLotListing()
	{
		string root = "user://lot_listing_selftest";
		string userLots = root + "/lots";
		string samples = root + "/samples";
		DirAccess.MakeDirRecursiveAbsolute(userLots);
		DirAccess.MakeDirRecursiveAbsolute(samples);
		WriteScratch(userLots + "/b.lot", "x");
		WriteScratch(userLots + "/a.lot", "yy");
		WriteScratch(userLots + "/notes.txt", "nope");
		WriteScratch(samples + "/sample.lot", "zzz");

		List<LotListing.LotEntry> entries = LotListing.Collect(userLots, samples);
		Check("ls: only .lot files are listed", entries.Count == 3);
		Check("ls: entries are name-ascending across both folders",
			entries.Count == 3 && entries[0].Name == "a" && entries[1].Name == "b" && entries[2].Name == "sample");
		Check("ls: source is recorded per folder",
			entries[0].Source == "local" && entries[2].Source == "sample");
		Check("ls: the recorded size is the file's", entries[0].SizeBytes == 2 && entries[2].SizeBytes == 3);
		Check("ls: the loader path points at the file",
			entries[0].Path == userLots + "/a.lot");
		Check("ls: a folder that does not exist is simply empty",
			LotListing.Collect(root + "/absent", samples).Count == 1 &&
			LotListing.Collect(root + "/absent", root + "/absent-too").Count == 0);

		DirAccess.RemoveAbsolute(userLots + "/a.lot");
		DirAccess.RemoveAbsolute(userLots + "/b.lot");
		DirAccess.RemoveAbsolute(userLots + "/notes.txt");
		DirAccess.RemoveAbsolute(samples + "/sample.lot");
		DirAccess.RemoveAbsolute(userLots);
		DirAccess.RemoveAbsolute(samples);
		DirAccess.RemoveAbsolute(root);
	}

	private static void WriteScratch(string path, string contents)
	{
		using FileAccess file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
		if (file != null) file.StoreString(contents);
	}

	private static bool EmptyAliasTableLoads()
	{
		AliasTable missing = new AliasTable();
		missing.Load("user://alias_selftest_absent.cfg");
		return missing.Count == 0;
	}
}
