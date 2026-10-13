using System.Collections.Generic;
using Godot;

/// <summary>
/// Verification for the client's font system (milestone 5.4's font clause): the bundled CC0
/// library, discovery of both font folders, the two slots, their persistence, and the two asset
/// guards that stop a font shipping broken or unrecorded.
///
/// Deliberately exercises <see cref="FontRegistry"/> and <see cref="FontCatalog"/> only. The ImGui
/// half (<see cref="FontService"/> creating handles) needs a live ImGui context, so it is
/// compile-verified and checked by eye in the running client — the project's usual split, the same
/// one <c>WallpaperSelfTest</c> and <c>AccountSelfTest</c> take for the renderer and the Terminal
/// node.
///
/// Follows the project's self-test convention: no framework, prints pass/fail lines, returns the
/// failure count.
/// </summary>
public static class FontServiceSelfTest
{
	private static int _checks;
	private static int _failures;

	// A few ids the curated set must always contain, by category — a smoke test that the shipped
	// files are actually there and mapped in the catalog.
	private static readonly string[] ExpectedIds =
	{
		"jupiteroid-regular", "typelightsans", "3x3-mono", "braillecc0",
		"publicpixel", "oserif-regular", "matrixtype-regular"
	};

	public static int Run()
	{
		_checks = 0;
		_failures = 0;

		TestCatalog();
		TestBundledScan();
		TestScratchScan();
		TestSlots();
		TestPersistence();
		TestAssetGuards();

		GD.Print("[FontServiceSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
		return _failures;
	}

	private static void Check(string name, bool condition)
	{
		_checks++;
		if (condition)
		{
			GD.Print("[FontServiceSelfTest] PASS  " + name);
		}
		else
		{
			_failures++;
			GD.PrintErr("[FontServiceSelfTest] FAIL  " + name);
		}
	}

	// --- catalog (pure data) --------------------------------------------------------------------

	private static void TestCatalog()
	{
		Check("catalog: a bundled id has its curated name and category",
			FontCatalog.DisplayName("jupiteroid-regular") == "Jupiteroid" &&
			FontCatalog.Category("jupiteroid-regular") == "Clean" &&
			FontCatalog.Category("publicpixel") == "Pixel" &&
			FontCatalog.Category("oserif-regular") == "Serif" &&
			FontCatalog.Category("3x3-mono") == "Mono");

		Check("catalog: an unknown id is a custom font with a tidied name",
			FontCatalog.Category("my_font-x") == FontCatalog.CustomCategory &&
			FontCatalog.DisplayName("my_font-x") == "My Font X" &&
			!FontCatalog.IsBundled("my_font-x"));

		Check("catalog: categories sort in the documented order",
			FontCatalog.CategoryRank("Clean") == 0 && FontCatalog.CategoryRank("Mono") == 1 &&
			FontCatalog.CategoryRank("Pixel") == 2 && FontCatalog.CategoryRank("Serif") == 3 &&
			FontCatalog.CategoryRank("Display") == 4 &&
			FontCatalog.CategoryRank(FontCatalog.CustomCategory) == 5 &&
			FontCatalog.CategoryRank("nonsense") == FontCatalog.CategoryOrder.Length);

		Check("catalog: the table is non-empty", FontCatalog.BundledCount >= 20);
	}

	// --- scanning the real bundled folder -------------------------------------------------------

	private static void TestBundledScan()
	{
		FontRegistry registry = new FontRegistry();
		registry.Scan(FontRegistry.BundledDir, "user://font_selftest_absent");

		Check("scan: the built-in is the first entry",
			registry.Entries.Count > 0 && registry.Entries[0].BuiltIn &&
			registry.Entries[0].Id == FontRegistry.BuiltInId &&
			registry.Entries[0].Path.Length == 0);

		int missing = 0;
		for (int i = 0; i < ExpectedIds.Length; i++)
		{
			if (!registry.IsKnown(ExpectedIds[i])) missing++;
		}
		Check("scan: every expected bundled font is found (" + ExpectedIds.Length + " ids)", missing == 0);

		Check("scan: entries are ordered by category, then name",
			IsOrdered(registry));

		Check("scan: every discovered id is mapped in the catalog (no bundled font falls back to Custom)",
			NoBundledCustomCategory(registry));

		Check("scan: every entry carries the path a loader will use",
			EveryNonBuiltInHasPath(registry));

		FontRegistry.FontEntry jupiteroid;
		Check("scan: an entry's display name and category come from the catalog",
			registry.TryGet("Jupiteroid-Regular", out jupiteroid) &&
			jupiteroid.DisplayName == "Jupiteroid" && jupiteroid.Category == "Clean");

		Check("scan: an unknown id does not resolve",
			!registry.TryGet("definitely-not-a-font", out _));

		Check("scan: a missing folder is tolerated", registry.Entries.Count > 1);
	}

	// --- scanning scratch folders (drop-ins) ---------------------------------------------------

	private static void TestScratchScan()
	{
		string root = "user://font_scan_selftest";
		string bundledDir = root + "/bundled";
		string userDir = root + "/user";
		DirAccess.MakeDirRecursiveAbsolute(bundledDir);
		DirAccess.MakeDirRecursiveAbsolute(userDir);

		WriteScratch(bundledDir + "/Jupiteroid-Regular.ttf", "not really a font");
		WriteScratch(bundledDir + "/notes.txt", "ignore me");
		WriteScratch(userDir + "/My_Font-x.otf", "not really a font either");
		// Same id as the bundled one: the bundled copy must win.
		WriteScratch(userDir + "/Jupiteroid-Regular.ttf", "shadow attempt");

		FontRegistry registry = new FontRegistry();
		registry.Scan(bundledDir, userDir);

		Check("scan: non-font files are ignored",
			registry.Entries.Count == 3 && !registry.IsKnown("notes"));
		Check("scan: a drop-in is registered with a tidied name and the Custom category",
			registry.TryGet("my_font-x", out FontRegistry.FontEntry dropIn) &&
			dropIn.DisplayName == "My Font X" &&
			dropIn.Category == FontCatalog.CustomCategory &&
			dropIn.Path == userDir + "/My_Font-x.otf");
		Check("scan: a drop-in cannot shadow a bundled font with the same id",
			registry.TryGet("jupiteroid-regular", out FontRegistry.FontEntry shadowed) &&
			shadowed.Path == bundledDir + "/Jupiteroid-Regular.ttf");

		Check("resolve: an id resolves, a display name resolves, nonsense does not",
			registry.TryResolve("jupiteroid-regular", out string byId) && byId == "jupiteroid-regular" &&
			registry.TryResolve("Jupiteroid", out string byName) && byName == "jupiteroid-regular" &&
			!registry.TryResolve("nope", out _));

		CleanupScratch(root, new[] { bundledDir + "/Jupiteroid-Regular.ttf", bundledDir + "/notes.txt",
			userDir + "/My_Font-x.otf", userDir + "/Jupiteroid-Regular.ttf" },
			new[] { bundledDir, userDir });
	}

	// --- the two slots -------------------------------------------------------------------------

	private static void TestSlots()
	{
		FontRegistry registry = new FontRegistry();
		registry.Scan(FontRegistry.BundledDir, "user://font_selftest_absent");

		Check("slots: a fresh registry starts on the built-in with the console following the UI",
			registry.UiSelection == FontRegistry.BuiltInId &&
			registry.ConsoleSelection.Length == 0 &&
			registry.EffectiveConsole == FontRegistry.BuiltInId);

		Check("slots: an unknown id is refused by both setters",
			!registry.SetUi("nope") && !registry.SetConsole("nope") &&
			registry.UiSelection == FontRegistry.BuiltInId);

		Check("slots: the UI slot takes the id case-insensitively",
			registry.SetUi("Jupiteroid-Regular") && registry.UiSelection == "jupiteroid-regular");

		Check("slots: the console follows the UI slot until it is given its own",
			registry.EffectiveConsole == "jupiteroid-regular");

		Check("slots: setting a console font makes it independent",
			registry.SetConsole("publicpixel") && registry.EffectiveConsole == "publicpixel" &&
			registry.ConsoleSelection == "publicpixel");

		Check("slots: clearing the console font makes it follow the UI slot again",
			registry.SetConsole("") && registry.EffectiveConsole == "jupiteroid-regular" &&
			registry.ConsoleSelection.Length == 0);

		Check("slots: the built-in can always be selected back",
			registry.SetUi(FontRegistry.BuiltInId) && registry.UiSelection == FontRegistry.BuiltInId);

		registry.ResetSelections();
		Check("slots: reset returns both slots to the built-in",
			registry.UiSelection == FontRegistry.BuiltInId && registry.ConsoleSelection.Length == 0);
	}

	// --- persistence ---------------------------------------------------------------------------

	private static void TestPersistence()
	{
		string path = "user://font_selftest.cfg";
		if (Godot.FileAccess.FileExists(path)) DirAccess.RemoveAbsolute(path);

		// Another block in the same file must survive our save (the wallpaper's, and §5.4's later
		// theme/window-style blocks).
		ConfigFile config = new ConfigFile();
		config.SetValue("wallpaper", "source_path", "user://keep.png");
		config.Save(path);

		FontRegistry registry = new FontRegistry();
		registry.Scan(FontRegistry.BundledDir, "user://font_selftest_absent");
		registry.SetUi("publicpixel");
		registry.SetConsole("3x3-mono");
		registry.Save(path);

		FontRegistry reloaded = new FontRegistry();
		reloaded.Scan(FontRegistry.BundledDir, "user://font_selftest_absent");
		reloaded.Load(path);
		Check("persist: both slots round-trip through the settings file",
			reloaded.UiSelection == "publicpixel" && reloaded.ConsoleSelection == "3x3-mono");

		ConfigFile after = new ConfigFile();
		after.Load(path);
		Check("persist: saving the font block preserves other settings blocks",
			after.GetValue("wallpaper", "source_path", "").AsString() == "user://keep.png");

		// A console slot that went back to following the UI stops being stored.
		reloaded.SetConsole("");
		reloaded.Save(path);
		ConfigFile following = new ConfigFile();
		following.Load(path);
		Check("persist: \"follow the UI\" is stored as the absence of a console value",
			!following.HasSectionKey("font", "console"));

		// A stored id whose font is gone must not survive a scan (a hand-edited config, or a font
		// deleted from the folder).
		ConfigFile broken = new ConfigFile();
		broken.Load(path);
		broken.SetValue("font", "ui", "vanished-font");
		broken.Save(path);
		FontRegistry recovered = new FontRegistry();
		recovered.Scan(FontRegistry.BundledDir, "user://font_selftest_absent");
		recovered.Load(path);
		Check("persist: an unknown stored id falls back to the built-in",
			recovered.UiSelection == FontRegistry.BuiltInId);

		// An absent file is simply the defaults.
		FontRegistry empty = new FontRegistry();
		empty.Load("user://font_selftest_absent.cfg");
		Check("persist: a missing settings file loads as the defaults",
			empty.UiSelection == FontRegistry.BuiltInId && empty.ConsoleSelection.Length == 0);

		DirAccess.RemoveAbsolute(path);
	}

	// --- the two asset guards ------------------------------------------------------------------

	private static void TestAssetGuards()
	{
		string licencePath = FontRegistry.BundledDir + "/LICENSE.md";
		string licence = Godot.FileAccess.FileExists(licencePath)
			? Godot.FileAccess.GetFileAsString(licencePath)
			: "";

		Check("assets: the bundled folder carries its licence/provenance file", licence.Length > 0);
		Check("assets: the licence names CC0", licence.Contains("CC0"));

		int unprotected = 0;
		int unrecorded = 0;
		int counted = 0;

		using DirAccess access = DirAccess.Open(FontRegistry.BundledDir);
		if (access != null)
		{
			access.ListDirBegin();
			while (true)
			{
				string name = access.GetNext();
				if (string.IsNullOrEmpty(name)) break;
				if (access.CurrentIsDir()) continue;
				if (!name.EndsWith(".ttf") && !name.EndsWith(".otf")) continue;

				counted++;

				// Export guard: the ImGui addon reads the font's bytes by path through Godot's
				// FileAccess, so an imported (font_data_dynamic) .ttf would be missing from an
				// exported build. "keep" leaves the original binary as the shipped artefact.
				string importPath = FontRegistry.BundledDir + "/" + name + ".import";
				string importText = Godot.FileAccess.FileExists(importPath)
					? Godot.FileAccess.GetFileAsString(importPath)
					: "";
				if (!importText.Contains("importer=\"keep\"")) unprotected++;

				// Provenance guard: every shipped font is named in the licence file.
				if (!licence.Contains(name)) unrecorded++;
			}
			access.ListDirEnd();
		}

		Check("assets: the bundled folder has fonts in it", counted >= 20);
		Check("assets: every bundled font is kept as a raw file for export (" + counted + " checked)",
			unprotected == 0);
		Check("assets: every bundled font is recorded in LICENSE.md", unrecorded == 0);
	}

	// --- helpers --------------------------------------------------------------------------------

	private static bool IsOrdered(FontRegistry registry)
	{
		int lastRank = -1;
		string lastName = "";
		for (int i = 0; i < registry.Entries.Count; i++)
		{
			FontRegistry.FontEntry entry = registry.Entries[i];
			if (entry.BuiltIn) continue;

			int rank = FontCatalog.CategoryRank(entry.Category);
			if (rank < lastRank) return false;
			if (rank == lastRank && string.Compare(entry.DisplayName, lastName, System.StringComparison.OrdinalIgnoreCase) < 0)
			{
				return false;
			}
			lastRank = rank;
			lastName = entry.DisplayName;
		}
		return true;
	}

	private static bool NoBundledCustomCategory(FontRegistry registry)
	{
		for (int i = 0; i < registry.Entries.Count; i++)
		{
			FontRegistry.FontEntry entry = registry.Entries[i];
			if (entry.BuiltIn) continue;
			// The bundled folder is what the repo ships, so everything in it must be in the
			// catalog; a Custom entry here means a font file whose table entry was forgotten.
			if (entry.Path.StartsWith(FontRegistry.BundledDir) && entry.Category == FontCatalog.CustomCategory)
			{
				return false;
			}
		}
		return true;
	}

	private static bool EveryNonBuiltInHasPath(FontRegistry registry)
	{
		for (int i = 0; i < registry.Entries.Count; i++)
		{
			FontRegistry.FontEntry entry = registry.Entries[i];
			if (!entry.BuiltIn && entry.Path.Length == 0) return false;
		}
		return true;
	}

	private static void WriteScratch(string path, string contents)
	{
		using FileAccess file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
		if (file != null) file.StoreString(contents);
	}

	private static void CleanupScratch(string root, string[] files, string[] directories)
	{
		for (int i = 0; i < files.Length; i++) DirAccess.RemoveAbsolute(files[i]);
		for (int i = 0; i < directories.Length; i++) DirAccess.RemoveAbsolute(directories[i]);
		DirAccess.RemoveAbsolute(root);
	}
}
