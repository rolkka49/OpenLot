using Godot;

/// <summary>
/// The running client's theme (milestone 5.4): one global palette plus one window-shape set shared by
/// the terminal, the builder panels and the dialogs, so the surfaces can never drift apart. Owns the
/// <see cref="ThemeSettings"/> instance, restores it at startup, exposes the role lookups the
/// terminal's log classifier and the Output window's severity mapping read, and pushes/pops the
/// chrome around a layout pass.
///
/// Two rules it exists to keep:
///  - <b>Only what differs from the base is pushed</b>, so the default theme pushes nothing at all
///    and an install that never opens the settings renders exactly as it did before this milestone.
///  - <b>Saved on every change</b>, exactly like font selection: an edit is applied to the running UI
///    and written straight away, so there is no separate Apply step and nothing to forget.
/// </summary>
public static class ThemeService
{
	/// <summary>Settable seam: the settings file the theme is remembered in.</summary>
	public static string SettingsPath { get; set; } = ThemeSettings.DefaultSettingsPath;

	/// <summary>The live theme. Replaced never; mutated through the methods below.</summary>
	public static ThemeSettings Settings { get; private set; } = new ThemeSettings();

	/// <summary>True once <see cref="LoadPersistedSettings"/> has run (diagnostics / tests).</summary>
	public static bool Ready { get; private set; }

	public static ThemePalette Palette { get { return Settings.Palette; } }
	public static string PresetName { get { return Settings.PresetName; } }

	/// <summary>One role colour — the terminal's log classifier and the Output window read these.</summary>
	public static Color Role(ThemeRole role)
	{
		return Settings.Palette.Role(role);
	}

	/// <summary>
	/// Adopts the live ImGui style as the neutral base (every chrome colour plus the stock shape) and
	/// restores the remembered theme. Idempotent, so the terminal and the builder can both call it
	/// (the wallpaper and fonts do the same) and whichever scene boots first owns the work.
	/// </summary>
	public static void LoadPersistedSettings()
	{
		if (Ready) return;
		Ready = true;

		// The wrapper exposes style COLOURS (though not style vars), so every chrome slot's base is
		// the live value: that is what makes the default theme's diff empty — nothing is pushed, so
		// the chrome is exactly what it was before this milestone, and a slot nobody edited keeps
		// following whatever the running style uses.
		Color[] chromeBase = ThemeChrome.Defaults();
		for (int i = 0; i < ThemeChrome.Count; i++)
		{
			chromeBase[i] = ImGui.GetStyleColor(ThemeChrome.Slots[i].Index);
		}
		Settings.SetBase(WindowStyle.Default, chromeBase);
		Settings.Load(SettingsPath);
	}

	/// <summary>Applies a preset by any name `theme` accepts, remembers it and reports it.</summary>
	public static bool ApplyPreset(string name, out string message)
	{
		if (!TerminalThemeCatalog.TryParsePreset(name, out string preset))
		{
			message = "Unknown theme '" + name + "'.";
			return false;
		}
		return ApplyPresetByName(preset, out message);
	}

	/// <summary>Applies a canonical preset (<see cref="TerminalThemeCatalog"/>'s name), saves it and
	/// reports it. A preset is a discrete action, so it persists immediately. "custom" applies the
	/// creator's own saved preset rather than a built-in.</summary>
	public static bool ApplyPresetByName(string preset, out string message)
	{
		if (preset == ThemePresets.CustomPreset)
		{
			if (!Settings.HasCustomPreset)
			{
				message = "No custom preset has been saved yet.";
				return false;
			}
			Settings.ApplyCustomPreset();
			Save();
			message = "Theme set to Custom (" + Settings.CustomPresetName + ").";
			return true;
		}

		if (!Settings.ApplyPreset(preset))
		{
			message = "Unknown theme '" + preset + "'.";
			return false;
		}
		Save();
		message = "Theme set to " + PresetDisplayName(preset) + ".";
		return true;
	}

	/// <summary>The name of the creator's saved custom preset, or "" when none is saved.</summary>
	public static string CustomPresetName { get { return Settings.CustomPresetName; } }

	/// <summary>True when a custom preset has been saved.</summary>
	public static bool HasCustomPreset { get { return Settings.HasCustomPreset; } }

	/// <summary>True when the live theme IS the saved custom preset (the settings window marks it).</summary>
	public static bool IsCustomPresetActive { get { return Settings.MatchesCustomPreset(); } }

	/// <summary>Saves the current theme as the creator's custom preset and remembers it.</summary>
	public static bool SaveCustomPreset(string name, out string message)
	{
		if (!Settings.SaveCustomPreset(name))
		{
			message = "Give the preset a name first.";
			return false;
		}
		Save();
		message = "Saved the current theme as \"" + Settings.CustomPresetName + "\".";
		return true;
	}

	/// <summary>Applies the saved custom preset and remembers that it is the active one.</summary>
	public static bool ApplyCustomPreset(out string message)
	{
		return ApplyPresetByName(ThemePresets.CustomPreset, out message);
	}

	/// <summary>Forgets the saved custom preset (the live theme keeps whatever it is showing).</summary>
	public static bool ClearCustomPreset(out string message)
	{
		if (!Settings.HasCustomPreset)
		{
			message = "No custom preset to delete.";
			return false;
		}
		Settings.ClearCustomPreset();
		Save();
		message = "Deleted the custom preset.";
		return true;
	}

	/// <summary>
	/// Sets one role colour by hand (the `theme foreground|background` command and the settings
	/// colour rows). In-memory only: the running UI previews immediately, and the caller commits with
	/// <see cref="Save"/> when the edit gesture ends — so dragging a colour picker does not write the
	/// settings file every frame.
	/// </summary>
	public static void SetRole(ThemeRole role, Color color)
	{
		Settings.SetRole(role, color);
	}

	/// <summary>Replaces the window shape (the settings sliders call this). In-memory only — see
	/// <see cref="SetRole"/> for the commit rule.</summary>
	public static void SetStyle(WindowStyle style)
	{
		Settings.SetStyle(style);
	}

	/// <summary>Sets one chrome colour (the window/button/frame/tab rows). In-memory only — see
	/// <see cref="SetRole"/> for the commit rule.</summary>
	public static void SetChrome(int index, Color color)
	{
		Settings.SetChrome(index, color);
	}

	/// <summary>Sets one chrome slot's alpha (its colour is kept). In-memory only — see
	/// <see cref="SetRole"/> for the commit rule.</summary>
	public static void SetChromeAlpha(int index, float alpha)
	{
		Settings.SetChromeAlpha(index, alpha);
	}

	/// <summary>Sets the window transparency (the window background and the workspace together — see
	/// <see cref="ThemeSettings.SetWindowTransparency"/>).</summary>
	public static void SetWindowTransparency(float alpha)
	{
		Settings.SetWindowTransparency(alpha);
	}

	/// <summary>Sets the input/slider frame transparency.</summary>
	public static void SetFrameTransparency(float alpha)
	{
		Settings.SetFrameTransparency(alpha);
	}

	/// <summary>Restores the transparency of the window, the workspace and the frames (colours stay).</summary>
	public static void ResetTransparency()
	{
		Settings.ResetTransparency();
		Save();
	}

	/// <summary>Writes the theme to <see cref="SettingsPath"/> (one write per edit gesture).</summary>
	public static void Save()
	{
		Settings.Save(SettingsPath);
	}

	/// <summary>Restores just the window shape to the stock values, keeping the palette and colours.</summary>
	public static void ResetWindowStyle()
	{
		Settings.ResetWindowStyle();
		Save();
	}

	/// <summary>Restores every chrome colour to the base, keeping the palette and shape.</summary>
	public static void ResetChrome()
	{
		Settings.ResetChrome();
		Save();
	}

	/// <summary>Display label for a preset ("green-matrix" -> "Green Matrix", "custom" -> "Custom").</summary>
	public static string PresetDisplayName(string preset)
	{
		string[] words = preset.Split('-');
		for (int i = 0; i < words.Length; i++)
		{
			if (words[i].Length == 0) continue;
			words[i] = char.ToUpperInvariant(words[i][0]) + words[i].Substring(1);
		}
		return string.Join(" ", words);
	}

	// --- chrome (called inside a layout pass, around every draw) ---------------------------------

	// The number of values pushed for the current pass, so the pop is exact and always balanced.
	private static int _pushedVars;
	private static int _pushedColors;

	/// <summary>
	/// Pushes the window shape AND every chrome colour for the whole layout pass. A value that equals
	/// its base is skipped, so the default theme pushes nothing and an untouched install keeps ImGui's
	/// own chrome exactly.
	/// </summary>
	public static void PushChrome()
	{
		_pushedVars = 0;
		_pushedColors = 0;

		WindowStyle style = Settings.Style;
		WindowStyle baseStyle = Settings.Base;

		if (!Mathf.IsEqualApprox(style.WindowRounding, baseStyle.WindowRounding))
		{
			ImGui.PushStyleVar(ImGui.StyleVarWindowRounding, style.WindowRounding);
			_pushedVars++;
		}
		if (!Mathf.IsEqualApprox(style.WindowBorderSize, baseStyle.WindowBorderSize))
		{
			ImGui.PushStyleVar(ImGui.StyleVarWindowBorderSize, style.WindowBorderSize);
			_pushedVars++;
		}
		if (style.WindowPadding != baseStyle.WindowPadding)
		{
			ImGui.PushStyleVar(ImGui.StyleVarWindowPadding, style.WindowPadding);
			_pushedVars++;
		}
		if (style.TitleAlign != baseStyle.TitleAlign)
		{
			ImGui.PushStyleVar(ImGui.StyleVarWindowTitleAlign, style.TitleAlign);
			_pushedVars++;
		}
		if (style.ItemSpacing != baseStyle.ItemSpacing)
		{
			ImGui.PushStyleVar(ImGui.StyleVarItemSpacing, style.ItemSpacing);
			_pushedVars++;
		}
		if (style.FramePadding != baseStyle.FramePadding)
		{
			ImGui.PushStyleVar(ImGui.StyleVarFramePadding, style.FramePadding);
			_pushedVars++;
		}

		// The chrome colours: window/popup/menu surfaces, title bar, buttons, frames, tabs, rows,
		// scrollbars and the border. This is what a theme actually changes about the client's look.
		for (int i = 0; i < ThemeChrome.Count; i++)
		{
			Color color = Settings.Chrome(i);
			if (color == Settings.ChromeBase(i)) continue;
			ImGui.PushStyleColor(ThemeChrome.Slots[i].Index, color);
			_pushedColors++;
		}
	}

	/// <summary>Pops exactly what <see cref="PushChrome"/> pushed (colours first, then vars, as ImGui
	/// requires the two stacks to unwind independently).</summary>
	public static void PopChrome()
	{
		if (_pushedColors > 0) ImGui.PopStyleColor(_pushedColors);
		if (_pushedVars > 0) ImGui.PopStyleVar(_pushedVars);
		_pushedColors = 0;
		_pushedVars = 0;
	}
}
