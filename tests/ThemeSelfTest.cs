using Godot;

/// <summary>
/// Verification for milestone 5.4's theme service: the seed palette the default preset restores
/// exactly, preset coherence (a preset carries a palette AND a window shape), and the config
/// round-trip (parse -> apply -> persist -> reload) — all on a scratch file, never the real
/// settings. Follows the project's self-test convention: no framework, prints PASS/FAIL lines,
/// returns the failure count. Runs from <c>BuilderScene._Ready</c> under <c>#if DEBUG</c>.
///
/// Scope note: only the deterministic parts are covered. What the chrome actually looks like on
/// screen (rounding, borders, padding) is a rendering result checked by eye in the running client.
/// </summary>
public static class ThemeSelfTest
{
	private const string ScratchPath = "user://theme_selftest.cfg";
	private const float Tolerance = 1e-4f;

	private static int _failures;
	private static int _checks;

	public static int Run()
	{
		_failures = 0;
		_checks = 0;

		TestSeedPalette();
		TestPresetTable();
		TestPresetCoherence();
		TestCustomPreset();
		TestTransparency();
		TestBaseDiff();
		TestRoundTrip();
		TestDefaultStoresNoShape();
		TestOtherBlocksSurvive();
		TestRefusals();

		GD.Print("[ThemeSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
		return _failures;
	}

	private static void Check(string name, bool condition)
	{
		_checks++;
		if (condition) GD.Print("[ThemeSelfTest] PASS  " + name);
		else { _failures++; GD.PrintErr("[ThemeSelfTest] FAIL  " + name); }
	}

	private static bool Near(Color a, Color b)
	{
		return Mathf.Abs(a.R - b.R) <= Tolerance && Mathf.Abs(a.G - b.G) <= Tolerance
			&& Mathf.Abs(a.B - b.B) <= Tolerance && Mathf.Abs(a.A - b.A) <= Tolerance;
	}

	private static bool Near(float a, float b)
	{
		return Mathf.Abs(a - b) <= Tolerance;
	}

	private static void TestSeedPalette()
	{
		ThemePalette palette = ThemePalette.Default;
		Check("seed palette: foreground is the pre-5.4 literal",
			Near(palette.Foreground, new Color(0.9f, 0.9f, 0.9f)));
		Check("seed palette: background is the pre-5.4 literal",
			Near(palette.Background, new Color(0.06f, 0.06f, 0.06f)));
		Check("seed palette: every role has a distinct lookup",
			!Near(palette.Role(ThemeRole.Error), palette.Role(ThemeRole.Warning)) &&
			!Near(palette.Role(ThemeRole.Success), palette.Role(ThemeRole.System)));
		Check("seed palette: SetRole round-trips through one role",
			RoleOf(ThemeRole.Echo) && Near(WithRole(ThemeRole.Echo, new Color(0.1f, 0.2f, 0.3f)).Role(ThemeRole.Echo), new Color(0.1f, 0.2f, 0.3f)));
		Check("roles: eight roles are listed for the settings rows", ThemePalette.AllRoles.Length == 8);
	}

	private static bool RoleOf(ThemeRole role)
	{
		return ThemePalette.RoleName(role) == "Echo";
	}

	private static ThemePalette WithRole(ThemeRole role, Color color)
	{
		ThemePalette palette = ThemePalette.Default;
		palette.SetRole(role, color);
		return palette;
	}

	private static void TestPresetCoherence()
	{
		ThemeSettings settings = new ThemeSettings();
		settings.SetBase(WindowStyle.Default, ThemeChrome.Defaults());

		bool green = settings.ApplyPreset(TerminalThemeCatalog.GreenMatrixPreset);
		Check("preset: green-matrix applies", green);
		Check("preset: green-matrix carries its palette",
			Near(settings.Palette.Foreground, new Color(0.25f, 0.9f, 0.35f)));
		Check("preset: green-matrix carries a window shape",
			Mathf.IsEqualApprox(settings.Style.WindowBorderSize, 1f)
			&& Mathf.IsEqualApprox(settings.Style.WindowRounding, 0f));
		Check("preset: green-matrix tints the chrome (the border is green-dominant)",
			settings.Chrome(BorderIndex()).G > settings.Chrome(BorderIndex()).R);

		settings.ApplyPreset(TerminalThemeCatalog.AmberRetroPreset);
		Check("preset: amber-retro carries its palette",
			Near(settings.Palette.Foreground, new Color(1.0f, 0.72f, 0.25f)));
		Check("preset: amber-retro carries a rounded, thicker border",
			Mathf.IsEqualApprox(settings.Style.WindowRounding, 3f) && Mathf.IsEqualApprox(settings.Style.WindowBorderSize, 2f));
		Check("preset: amber-retro tints the chrome (the accent is warm)",
			settings.Chrome(ButtonIndex()).R > settings.Chrome(ButtonIndex()).B);

		settings.ResetToDefaults();
		Check("preset: reset restores the neutral palette, the stock shape and the base chrome",
			Near(settings.Palette.Foreground, ThemePalette.Default.Foreground)
			&& Mathf.IsEqualApprox(settings.Style.WindowRounding, 0f)
			&& ChromeIsBase(settings));
	}

	/// <summary>The chrome slot index of a named slot (the test names them, so a reorder is caught).</summary>
	private static int IndexOf(string key)
	{
		return ThemeChrome.IndexOf(key);
	}

	private static int BorderIndex() { return IndexOf("border"); }
	private static int ButtonIndex() { return IndexOf("button_active"); }

	private static bool ChromeIsBase(ThemeSettings settings)
	{
		for (int i = 0; i < ThemeChrome.Count; i++)
		{
			if (settings.Chrome(i) != settings.ChromeBase(i)) return false;
		}
		return true;
	}

	/// <summary>
	/// The preset table must be complete and readable: every listed preset carries a palette, a chrome
	/// set and a shape (the default one deliberately carries no chrome), each preset's text contrasts
	/// with its own window background — which is what makes the light themes usable — and no two
	/// presets are the same theme under two names.
	/// </summary>
	private static void TestPresetTable()
	{
		int chromeCount = ThemeChrome.Count;
		int textIndex = IndexOf("text");
		int windowIndex = IndexOf("window_bg");

		bool allComplete = true, contrastOk = true, distinct = true;
		bool sawNewPresets = false;
		string previousKey = null;

		for (int i = 0; i < TerminalThemeCatalog.PresetNames.Count; i++)
		{
			string name = TerminalThemeCatalog.PresetNames[i];
			Color[] chrome = ThemePresets.ChromeFor(name);
			ThemePalette palette = ThemePresets.PaletteFor(name);

			if (name == TerminalThemeCatalog.CyberpunkPreset || name == TerminalThemeCatalog.KawaiiPreset
				|| name == TerminalThemeCatalog.NavyPreset)
			{
				sawNewPresets = true;
			}

			if (name == TerminalThemeCatalog.DefaultPreset)
			{
				// The default preset keeps the live ImGui style, so it must NOT carry a chrome table.
				if (chrome != null) allComplete = false;
				continue;
			}

			if (chrome == null || chrome.Length != chromeCount) { allComplete = false; continue; }

			float textLum = Luminance(chrome[textIndex]);
			float bgLum = Luminance(chrome[windowIndex]);
			if (Mathf.Abs(textLum - bgLum) < 0.35f) contrastOk = false;

			string key = Key(palette.Foreground) + "|" + Key(chrome[windowIndex]);
			if (previousKey != null && key == previousKey) distinct = false;
			previousKey = key;
		}

		Check("presets: every named preset carries a palette, a chrome set and a shape", allComplete);
		Check("presets: the new themes are in the table (cyberpunk, kawaii, navy, forest, mono, synthwave)",
			sawNewPresets && TerminalThemeCatalog.PresetNames.Count >= 9);
		Check("presets: each preset's text contrasts with its window background", contrastOk);
		Check("presets: no two presets are the same palette and window background", distinct);
	}

	private static float Luminance(Color color)
	{
		return color.R * 0.299f + color.G * 0.587f + color.B * 0.114f;
	}

	private static string Key(Color color)
	{
		return ((int)(color.R * 255f)) + "," + ((int)(color.G * 255f)) + "," + ((int)(color.B * 255f));
	}

	/// <summary>
	/// The creator's own preset: it saves the current theme under a name, survives applying another
	/// preset, re-applies exactly what was saved (palette, chrome AND shape), round-trips through the
	/// real config encoder, and is forgotten — not resurrected — when deleted.
	/// </summary>
	private static void TestCustomPreset()
	{
		Color[] chromeBase = ThemeChrome.Defaults();
		ThemeSettings settings = new ThemeSettings();
		settings.SetBase(WindowStyle.Default, chromeBase);

		Check("custom: nothing is saved to begin with", !settings.HasCustomPreset);
		Check("custom: applying with nothing saved is refused", !settings.ApplyCustomPreset());

		Color role = new Color(0.12f, 0.34f, 0.56f);
		Color chrome = new Color(0.5f, 0.1f, 0.1f);
		settings.SetRole(ThemeRole.System, role);
		settings.SetChrome(ButtonIndex(), chrome);
		WindowStyle shape = WindowStyle.Default;
		shape.WindowRounding = 7f;
		settings.SetStyle(shape);

		Check("custom: saving under a name succeeds", settings.SaveCustomPreset("Mine"));
		Check("custom: the saved name is reported", settings.CustomPresetName == "Mine");
		Check("custom: a blank name is refused and does not clobber the saved one",
			!settings.SaveCustomPreset("   ") && settings.CustomPresetName == "Mine");
		Check("custom: the live theme matches the preset it just saved", settings.MatchesCustomPreset());

		settings.ApplyPreset(TerminalThemeCatalog.CyberpunkPreset);
		Check("custom: a built-in preset replaced the live values",
			!Near(settings.Palette.Role(ThemeRole.System), role));
		Check("custom: the saved preset survived applying a built-in", settings.HasCustomPreset);

		Check("custom: applying the saved preset succeeds", settings.ApplyCustomPreset());
		Check("custom: the role colour came back", Near(settings.Palette.Role(ThemeRole.System), role));
		Check("custom: the chrome colour came back", Near(settings.Chrome(ButtonIndex()), chrome));
		Check("custom: the shape came back", Mathf.IsEqualApprox(settings.Style.WindowRounding, 7f));
		Check("custom: the preset label reports custom", settings.PresetName == ThemePresets.CustomPreset);

		// Round-trip the saved preset through the real config encoder.
		settings.Save(ScratchPath);
		ThemeSettings read = new ThemeSettings();
		read.SetBase(WindowStyle.Default, chromeBase);
		read.Load(ScratchPath);
		Check("custom: the saved preset round-trips (name)", read.CustomPresetName == "Mine");
		Check("custom: the saved preset round-trips (values)",
			read.ApplyCustomPreset()
			&& Near(read.Palette.Role(ThemeRole.System), role)
			&& Near(read.Chrome(ButtonIndex()), chrome)
			&& Mathf.IsEqualApprox(read.Style.WindowRounding, 7f));

		read.ClearCustomPreset();
		Check("custom: clearing forgets it", !read.HasCustomPreset && !read.ApplyCustomPreset());
		read.Save(ScratchPath);
		ThemeSettings reread = new ThemeSettings();
		reread.SetBase(WindowStyle.Default, chromeBase);
		reread.Load(ScratchPath);
		Check("custom: a deleted preset is not resurrected by the next load", !reread.HasCustomPreset);
	}

	/// <summary>
	/// The transparency controls: the window control must move the window background AND the workspace
	/// behind the panels (either alone shows no change), the frame control only the frames, values are
	/// clamped, RGB is never touched, the reset restores just the alphas, and the result survives a
	/// save/reload.
	/// </summary>
	private static void TestTransparency()
	{
		Color[] chromeBase = ThemeChrome.Defaults();
		chromeBase[IndexOf("window_bg")] = new Color(0.06f, 0.06f, 0.06f, 0.94f);
		chromeBase[IndexOf("docking_empty_bg")] = new Color(0.20f, 0.20f, 0.20f, 1f);
		chromeBase[IndexOf("frame")] = new Color(0.16f, 0.29f, 0.48f, 0.54f);

		ThemeSettings settings = new ThemeSettings();
		settings.SetBase(WindowStyle.Default, chromeBase);

		Check("transparency: the slots the controls use exist",
			IndexOf("window_bg") >= 0 && IndexOf("docking_empty_bg") >= 0 && IndexOf("frame") >= 0);
		Check("transparency: it starts at the base", Near(settings.WindowTransparency, 0.94f)
			&& Near(settings.FrameTransparency, 0.54f));

		settings.SetWindowTransparency(0.5f);
		Check("transparency: the window control sets the window background",
			Near(settings.Chrome(IndexOf("window_bg")).A, 0.5f));
		Check("transparency: the window control sets the workspace behind the panels too",
			Near(settings.Chrome(IndexOf("docking_empty_bg")).A, 0.5f));
		Check("transparency: the window background's colour is untouched",
			Near(settings.Chrome(IndexOf("window_bg")).R, 0.06f));

		settings.SetFrameTransparency(0.2f);
		Check("transparency: the frame control sets the frames",
			Near(settings.FrameTransparency, 0.2f));
		Check("transparency: the frame control leaves the window alone",
			Near(settings.Chrome(IndexOf("window_bg")).A, 0.5f));

		settings.SetWindowTransparency(4f);
		Check("transparency: a value above 1 clamps to opaque", Near(settings.WindowTransparency, 1f));
		settings.SetWindowTransparency(-3f);
		Check("transparency: a value below 0 clamps to invisible", Near(settings.WindowTransparency, 0f));

		settings.ResetTransparency();
		Check("transparency: the reset restores every alpha to the base",
			Near(settings.WindowTransparency, 0.94f) && Near(settings.FrameTransparency, 0.54f)
			&& Near(settings.Chrome(IndexOf("docking_empty_bg")).A, 1f));
		Check("transparency: the reset is exactly the base chrome again", ChromeIsBase(settings));

		settings.SetWindowTransparency(0.35f);
		settings.Save(ScratchPath);
		ThemeSettings read = new ThemeSettings();
		read.SetBase(WindowStyle.Default, chromeBase);
		read.Load(ScratchPath);
		Check("transparency: the value survives a save and reload", Near(read.WindowTransparency, 0.35f));
	}

	/// <summary>
	/// The core contract: the base is what a value diffs against, so the default theme matches it
	/// exactly (nothing pushed) while an authored value differs from it.
	/// </summary>
	private static void TestBaseDiff()
	{
		Color[] chromeBase = ThemeChrome.Defaults();
		chromeBase[BorderIndex()] = new Color(0.43f, 0.43f, 0.5f, 0.5f);   // a stand-in for the live border

		ThemeSettings settings = new ThemeSettings();
		settings.SetBase(WindowStyle.Default, chromeBase);

		Check("base: the default shape matches the base", settings.Style.Matches(settings.Base));
		Check("base: the default chrome matches the base", ChromeIsBase(settings));

		WindowStyle rounded = WindowStyle.Default;
		rounded.WindowRounding = 4f;
		settings.SetStyle(rounded);
		Check("base: an authored shape differs from the base", !settings.Style.Matches(settings.Base));
		Check("base: the authored rounding is kept", Mathf.IsEqualApprox(settings.Style.WindowRounding, 4f));

		settings.SetChrome(BorderIndex(), new Color(0.9f, 0.1f, 0.1f));
		Check("base: an authored chrome colour differs from the base",
			settings.Chrome(BorderIndex()) != settings.ChromeBase(BorderIndex()));
		settings.ResetChrome();
		Check("base: ResetChrome returns every slot to the base", ChromeIsBase(settings));
	}

	/// <summary>parse -> apply -> persist -> reload on a scratch file, the same shape as WallpaperSelfTest.</summary>
	private static void TestRoundTrip()
	{
		Color[] chromeBase = ThemeChrome.Defaults();

		ThemeSettings written = new ThemeSettings();
		written.SetBase(WindowStyle.Default, chromeBase);
		written.SetRole(ThemeRole.Error, new Color(0.11f, 0.22f, 0.33f));
		WindowStyle shape = WindowStyle.Default;
		shape.WindowRounding = 5f;
		shape.WindowBorderSize = 3f;
		written.SetStyle(shape);
		written.SetChrome(ButtonIndex(), new Color(0.2f, 0.4f, 0.6f, 0.9f));
		written.Save(ScratchPath);

		ThemeSettings read = new ThemeSettings();
		read.SetBase(WindowStyle.Default, chromeBase);
		read.Load(ScratchPath);

		Check("round-trip: a hand-edited theme loads as custom",
			read.PresetName == ThemePresets.CustomPreset);
		Check("round-trip: the edited role colour survives",
			Near(read.Palette.Role(ThemeRole.Error), new Color(0.11f, 0.22f, 0.33f)));
		Check("round-trip: the edited rounding survives",
			Mathf.IsEqualApprox(read.Style.WindowRounding, 5f));
		Check("round-trip: the edited border size survives",
			Mathf.IsEqualApprox(read.Style.WindowBorderSize, 3f));
		Check("round-trip: the edited chrome colour survives (alpha included)",
			Near(read.Chrome(ButtonIndex()), new Color(0.2f, 0.4f, 0.6f, 0.9f)));
		Check("round-trip: an untouched chrome slot stays on the base",
			read.Chrome(BorderIndex()) == read.ChromeBase(BorderIndex()));
		Check("round-trip: an untouched role keeps the neutral value",
			Near(read.Palette.Role(ThemeRole.Success), ThemePalette.Default.Success));

		// A preset round-trips as itself (its stored roles and chrome are authoritative).
		ThemeSettings preset = new ThemeSettings();
		preset.SetBase(WindowStyle.Default, chromeBase);
		preset.ApplyPreset(TerminalThemeCatalog.GreenMatrixPreset);
		preset.Save(ScratchPath);
		ThemeSettings reloaded = new ThemeSettings();
		reloaded.SetBase(WindowStyle.Default, chromeBase);
		reloaded.Load(ScratchPath);
		Check("round-trip: the preset name survives", reloaded.PresetName == TerminalThemeCatalog.GreenMatrixPreset);
		Check("round-trip: the preset palette survives",
			Near(reloaded.Palette.Foreground, new Color(0.25f, 0.9f, 0.35f)));
		Check("round-trip: the preset chrome survives",
			reloaded.Chrome(BorderIndex()) == preset.Chrome(BorderIndex()));
	}

	/// <summary>The default theme's file carries no shape or chrome keys, so nothing is pushed — the
	/// "unchanged install" contract.</summary>
	private static void TestDefaultStoresNoShape()
	{
		Color[] chromeBase = ThemeChrome.Defaults();
		chromeBase[BorderIndex()] = new Color(0.1f, 0.1f, 0.1f, 0.5f);

		ThemeSettings settings = new ThemeSettings();
		settings.SetBase(WindowStyle.Default, chromeBase);
		settings.ResetToDefaults();
		settings.Save(ScratchPath);

		ConfigFile config = new ConfigFile();
		if (config.Load(ScratchPath) != Error.Ok)
		{
			Check("default file: the scratch settings could be read", false);
			return;
		}
		Check("default file: no window_style block is stored",
			!config.HasSection("window_style"));
		Check("default file: no window_chrome block is stored",
			!config.HasSection("window_chrome"));
		Check("default file: the preset is recorded",
			config.GetValue("theme", "preset", "").AsString() == TerminalThemeCatalog.DefaultPreset);
		Check("default file: all eight roles are recorded",
			config.GetSectionKeys("theme").Length == 9);   // preset + 8 roles
	}

	/// <summary>A save must not drop a block another writer owns (the wallpaper's, the alias's, the fonts').</summary>
	private static void TestOtherBlocksSurvive()
	{
		ConfigFile seed = new ConfigFile();
		seed.SetValue("wallpaper", "source_path", "user://keep.png");
		seed.SetValue("alias", "ll", "openlot list");
		seed.Save(ScratchPath);

		ThemeSettings settings = new ThemeSettings();
		settings.SetBase(WindowStyle.Default, ThemeChrome.Defaults());
		settings.ApplyPreset(TerminalThemeCatalog.AmberRetroPreset);
		settings.Save(ScratchPath);

		ConfigFile config = new ConfigFile();
		if (config.Load(ScratchPath) != Error.Ok)
		{
			Check("other blocks: the settings file could be re-read", false);
			return;
		}
		Check("other blocks: the wallpaper block survives a theme save",
			config.GetValue("wallpaper", "source_path", "").AsString() == "user://keep.png");
		Check("other blocks: the alias block survives a theme save",
			config.GetValue("alias", "ll", "").AsString() == "openlot list");
	}

	private static void TestRefusals()
	{
		ThemeSettings settings = new ThemeSettings();
		Check("refusals: an unknown preset name is refused",
			!settings.ApplyPreset("not-a-preset"));
		Check("refusals: custom is a known preset", ThemeSettings.IsKnownPreset(ThemePresets.CustomPreset));
		Check("refusals: a missing file loads the defaults silently",
			LoadMissingKeepsDefaults());
	}

	private static bool LoadMissingKeepsDefaults()
	{
		ThemeSettings settings = new ThemeSettings();
		settings.ApplyPreset(TerminalThemeCatalog.AmberRetroPreset);
		settings.Load("user://theme_selftest_does_not_exist.cfg");
		return settings.PresetName == TerminalThemeCatalog.DefaultPreset
			&& Near(settings.Palette.Foreground, ThemePalette.Default.Foreground);
	}

}
