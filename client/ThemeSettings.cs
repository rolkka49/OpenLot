using System.Collections.Generic;
using Godot;

/// <summary>
/// The live theme and its persistence (milestone 5.4): the role palette, the window chrome colours,
/// the window shape, the preset it currently matches, and the <c>[theme]</c> / <c>[window_chrome]</c>
/// / <c>[window_style]</c> blocks of <c>user://openlot_settings.cfg</c>. A plain instance with an
/// explicit settings path — exactly the <see cref="FontRegistry"/> / <see cref="AliasTable"/> pattern
/// — so a suite drives a scratch file and can never touch the real settings;
/// <see cref="ThemeService"/> owns the one the client uses.
///
/// <see cref="Base"/> and the chrome base are what values are diffed against: a value equal to its
/// base is neither stored nor pushed, so the default theme leaves ImGui's own chrome untouched. Both
/// bases are adopted from the live ImGui style at startup (<see cref="ThemeService"/>).
/// </summary>
public class ThemeSettings
{
	/// <summary>Shared client settings file — the same one the wallpaper, alias and font blocks live in.</summary>
	public const string DefaultSettingsPath = "user://openlot_settings.cfg";

	private const string ThemeSection = "theme";
	private const string ChromeSection = "window_chrome";
	private const string WindowSection = "window_style";
	private const string CustomThemeSection = "custom_theme";
	private const string CustomChromeSection = "custom_chrome";
	private const string CustomStyleSection = "custom_style";
	private const string PresetKey = "preset";
	private const string CustomNameKey = "name";

	private ThemePalette _palette = ThemePalette.Default;
	private WindowStyle _style = WindowStyle.Default;
	private string _preset = TerminalThemeCatalog.DefaultPreset;
	private WindowStyle _base = WindowStyle.Default;
	private Color[] _chrome = ThemeChrome.Defaults();
	private Color[] _chromeBase = ThemeChrome.Defaults();

	// The creator's own saved preset: a snapshot of palette + chrome + shape under a name they chose.
	// Kept separate from the live theme (which a built-in preset overwrites), so applying another
	// preset never loses it.
	private string _customName = "";
	private ThemePalette _customPalette = ThemePalette.Default;
	private WindowStyle _customStyle = WindowStyle.Default;
	private Color[] _customChrome = ThemeChrome.Defaults();

	public ThemePalette Palette { get { return _palette; } }

	/// <summary>The stored shape, as authored.</summary>
	public WindowStyle Style { get { return _style; } }

	public string PresetName { get { return _preset; } }

	/// <summary>The shape a value must differ from to be stored or pushed. Never null.</summary>
	public WindowStyle Base { get { return _base; } }

	/// <summary>The chrome colour a slot must differ from to be stored or pushed.</summary>
	public Color ChromeBase(int index)
	{
		return _chromeBase[index];
	}

	/// <summary>The authored chrome colour of a slot.</summary>
	public Color Chrome(int index)
	{
		return _chrome[index];
	}

	// The slots the transparency controls write, resolved by name once (so a table reorder cannot
	// silently point them at the wrong row).
	private static readonly int WindowSlot = ThemeChrome.IndexOf("window_bg");
	private static readonly int WorkspaceSlot = ThemeChrome.IndexOf("docking_empty_bg");
	private static readonly int FrameSlot = ThemeChrome.IndexOf("frame");

	/// <summary>How see-through the client is: the window background's alpha (0 = invisible, 1 = opaque).</summary>
	public float WindowTransparency { get { return _chrome[WindowSlot].A; } }

	/// <summary>The input/slider frame's alpha (0 = invisible, 1 = opaque).</summary>
	public float FrameTransparency { get { return _chrome[FrameSlot].A; } }

	/// <summary>Sets a chrome slot's alpha, keeping its colour. Clamped, and the preset label is
	/// re-derived because transparency is part of the theme like any other value.</summary>
	public void SetChromeAlpha(int index, float alpha)
	{
		if (index < 0 || index >= _chrome.Length) return;
		Color color = _chrome[index];
		color.A = Mathf.Clamp(alpha, 0f, 1f);
		_chrome[index] = color;
		UpdatePresetFromValues();
	}

	/// <summary>
	/// Sets the window transparency. It writes the window background AND the workspace behind the
	/// panels, because either one alone shows no change at all: a transparent window sitting on an
	/// opaque workspace still looks opaque, and an opaque window on a transparent workspace still
	/// covers it. They therefore move together as one control (the individual rows below still allow
	/// setting them apart).
	/// </summary>
	public void SetWindowTransparency(float alpha)
	{
		SetChromeAlpha(WindowSlot, alpha);
		SetChromeAlpha(WorkspaceSlot, alpha);
	}

	/// <summary>Sets the input/slider frame's alpha (the "frame transparency" control).</summary>
	public void SetFrameTransparency(float alpha)
	{
		SetChromeAlpha(FrameSlot, alpha);
	}

	/// <summary>Restores the transparency of the window, the workspace and the frames to their base —
	/// without touching any other colour.</summary>
	public void ResetTransparency()
	{
		SetChromeAlpha(WindowSlot, _chromeBase[WindowSlot].A);
		SetChromeAlpha(WorkspaceSlot, _chromeBase[WorkspaceSlot].A);
		SetChromeAlpha(FrameSlot, _chromeBase[FrameSlot].A);
	}

	/// <summary>True when a custom preset has been saved (it always has a name then).</summary>
	public bool HasCustomPreset { get { return _customName.Length > 0; } }

	/// <summary>The name the creator gave the saved custom preset, or "".</summary>
	public string CustomPresetName { get { return _customName; } }

	public ThemeSettings()
	{
		ResetToDefaults();
	}

	/// <summary>Sets the diff bases (the client does this once, from the live ImGui style, at startup).</summary>
	public void SetBase(WindowStyle baseStyle, Color[] chromeBase)
	{
		_base = baseStyle;
		if (chromeBase != null && chromeBase.Length == ThemeChrome.Count) _chromeBase = chromeBase;
	}

	/// <summary>Restores the neutral palette, the base chrome and the stock shape — what
	/// `theme default` means.</summary>
	public void ResetToDefaults()
	{
		_preset = TerminalThemeCatalog.DefaultPreset;
		_palette = ThemePresets.PaletteFor(_preset);
		_style = ThemePresets.StyleFor(_preset);
		_chrome = ThemePresets.ChromeFor(_preset) ?? CopyOf(_chromeBase);
	}

	/// <summary>
	/// True for a name a preset can be applied by: the built-in list (read from
	/// <see cref="TerminalThemeCatalog.PresetNames"/>, so the table and the command cannot drift) or the
	/// creator's own <see cref="ThemePresets.CustomPreset"/>.
	/// </summary>
	public static bool IsKnownPreset(string preset)
	{
		if (string.IsNullOrEmpty(preset)) return false;
		if (preset == ThemePresets.CustomPreset) return true;

		IReadOnlyList<string> names = TerminalThemeCatalog.PresetNames;
		for (int i = 0; i < names.Count; i++)
		{
			if (names[i] == preset) return true;
		}
		return false;
	}

	/// <summary>Applies a canonical preset name (or <see cref="ThemePresets.CustomPreset"/>, which starts
	/// from the neutral palette, the base chrome and the stock shape). False for an unknown name.</summary>
	public bool ApplyPreset(string preset)
	{
		if (!IsKnownPreset(preset)) return false;
		_preset = preset;
		_palette = ThemePresets.PaletteFor(preset);
		_style = ThemePresets.StyleFor(preset);
		// A preset with no chrome of its own (the default) means "the base", i.e. push nothing.
		_chrome = ThemePresets.ChromeFor(preset) ?? CopyOf(_chromeBase);
		return true;
	}

	/// <summary>
	/// Sets one role colour by hand. The preset label is re-derived: the theme is <c>custom</c> unless
	/// the edit happens to reproduce a named preset exactly, which is what the settings header shows.
	/// </summary>
	public void SetRole(ThemeRole role, Color color)
	{
		_palette.SetRole(role, color);
		UpdatePresetFromValues();
	}

	/// <summary>Sets one chrome colour by hand (the window/button/frame/tab rows).</summary>
	public void SetChrome(int index, Color color)
	{
		if (index < 0 || index >= _chrome.Length) return;
		_chrome[index] = color;
		UpdatePresetFromValues();
	}

	/// <summary>Replaces the window shape by hand; the preset label is re-derived, like <see cref="SetRole"/>.</summary>
	public void SetStyle(WindowStyle style)
	{
		_style = style;
		UpdatePresetFromValues();
	}

	/// <summary>Restores just the window shape to the stock values (colours untouched).</summary>
	public void ResetWindowStyle()
	{
		_style = WindowStyle.Default;
		UpdatePresetFromValues();
	}

	/// <summary>Restores every chrome colour to the base (the palette and shape are untouched).</summary>
	public void ResetChrome()
	{
		_chrome = CopyOf(_chromeBase);
		UpdatePresetFromValues();
	}

	/// <summary>
	/// Saves the CURRENT theme as the creator's custom preset under <paramref name="name"/>. The
	/// snapshot is independent of the live theme, so applying another preset afterwards does not lose
	/// it, and it is written to its own config blocks.
	/// </summary>
	public bool SaveCustomPreset(string name)
	{
		string trimmed = name == null ? "" : name.Trim();
		if (trimmed.Length == 0) return false;
		if (trimmed.Length > 48) trimmed = trimmed.Substring(0, 48);

		_customName = trimmed;
		_customPalette = _palette;
		_customStyle = _style;
		_customChrome = CopyOf(_chrome);
		return true;
	}

	/// <summary>Applies the saved custom preset. False when none has been saved.</summary>
	public bool ApplyCustomPreset()
	{
		if (!HasCustomPreset) return false;
		_palette = _customPalette;
		_style = _customStyle;
		_chrome = CopyOf(_customChrome);
		_preset = ThemePresets.CustomPreset;
		return true;
	}

	/// <summary>Forgets the saved custom preset (the live theme is untouched).</summary>
	public void ClearCustomPreset()
	{
		_customName = "";
		_customPalette = ThemePalette.Default;
		_customStyle = WindowStyle.Default;
		_customChrome = ThemeChrome.Defaults();
	}

	/// <summary>True when the live values are exactly the saved custom preset (so the settings window
	/// can mark that button as the active one).</summary>
	public bool MatchesCustomPreset()
	{
		if (!HasCustomPreset) return false;
		if (!_palette.Matches(_customPalette)) return false;
		if (!_style.Matches(_customStyle)) return false;
		return ChromeMatches(_customChrome);
	}

	/// <summary>Re-derives which preset the current values correspond to, so the label cannot lie.</summary>
	private void UpdatePresetFromValues()
	{
		string[] names =
		{
			TerminalThemeCatalog.DefaultPreset,
			TerminalThemeCatalog.GreenMatrixPreset,
			TerminalThemeCatalog.AmberRetroPreset,
		};
		for (int i = 0; i < names.Length; i++)
		{
			if (!_palette.Matches(ThemePresets.PaletteFor(names[i]))) continue;
			if (!_style.Matches(ThemePresets.StyleFor(names[i]))) continue;
			if (!ChromeMatches(ThemePresets.ChromeFor(names[i]) ?? _chromeBase)) continue;
			_preset = names[i];
			return;
		}
		_preset = ThemePresets.CustomPreset;
	}

	private bool ChromeMatches(Color[] other)
	{
		if (other == null || other.Length != _chrome.Length) return false;
		for (int i = 0; i < _chrome.Length; i++)
		{
			if (_chrome[i] != other[i]) return false;
		}
		return true;
	}

	private static Color[] CopyOf(Color[] source)
	{
		Color[] copy = new Color[source.Length];
		System.Array.Copy(source, copy, source.Length);
		return copy;
	}

	// --- persistence (explicit path, so a suite never touches the real settings) ---

	/// <summary>
	/// Reads the three blocks. A missing file or block leaves the defaults in place. The stored roles
	/// are authoritative (a theme is stored with all eight), so a preset whose definition later
	/// changes still loads exactly as the creator last saw it.
	/// </summary>
	public void Load(string path)
	{
		ResetToDefaults();
		if (string.IsNullOrEmpty(path) || !Godot.FileAccess.FileExists(path)) return;

		ConfigFile config = new ConfigFile();
		if (config.Load(path) != Error.Ok) return;

		string preset = config.GetValue(ThemeSection, PresetKey, TerminalThemeCatalog.DefaultPreset).AsString();
		ApplyPreset(IsKnownPreset(preset) ? preset : TerminalThemeCatalog.DefaultPreset);

		if (config.HasSection(ThemeSection))
		{
			for (int i = 0; i < ThemePalette.AllRoles.Length; i++)
			{
				ThemeRole role = ThemePalette.AllRoles[i];
				string key = RoleKey(role);
				if (!config.HasSectionKey(ThemeSection, key)) continue;
				_palette.SetRole(role, config.GetValue(ThemeSection, key).AsColor());
			}
		}

		// A stored chrome key means "differs from the base"; a missing one means "the base colour".
		if (config.HasSection(ChromeSection))
		{
			for (int i = 0; i < ThemeChrome.Count; i++)
			{
				string key = ThemeChrome.Slots[i].Key;
				if (!config.HasSectionKey(ChromeSection, key)) continue;
				_chrome[i] = config.GetValue(ChromeSection, key).AsColor();
			}
		}

		// A stored shape key means "differs from the base"; a missing one means "the base value".
		if (config.HasSection(WindowSection))
		{
			if (config.HasSectionKey(WindowSection, "rounding")) _style.WindowRounding = config.GetValue(WindowSection, "rounding").AsSingle();
			if (config.HasSectionKey(WindowSection, "border_size")) _style.WindowBorderSize = config.GetValue(WindowSection, "border_size").AsSingle();
			if (config.HasSectionKey(WindowSection, "padding")) _style.WindowPadding = config.GetValue(WindowSection, "padding").AsVector2();
			if (config.HasSectionKey(WindowSection, "title_align")) _style.TitleAlign = config.GetValue(WindowSection, "title_align").AsVector2();
			if (config.HasSectionKey(WindowSection, "item_spacing")) _style.ItemSpacing = config.GetValue(WindowSection, "item_spacing").AsVector2();
			if (config.HasSectionKey(WindowSection, "frame_padding")) _style.FramePadding = config.GetValue(WindowSection, "frame_padding").AsVector2();
		}

		LoadCustomPreset(config);
	}

	/// <summary>Writes the saved custom preset's own blocks (a no-op when none is saved).</summary>
	private void WriteCustomPreset(ConfigFile config)
	{
		if (_customName.Length == 0) return;

		config.SetValue(CustomThemeSection, CustomNameKey, _customName);
		for (int i = 0; i < ThemePalette.AllRoles.Length; i++)
		{
			ThemeRole role = ThemePalette.AllRoles[i];
			config.SetValue(CustomThemeSection, RoleKey(role), _customPalette.Role(role));
		}

		for (int i = 0; i < ThemeChrome.Count; i++)
		{
			if (_customChrome[i] == _chromeBase[i]) continue;
			config.SetValue(CustomChromeSection, ThemeChrome.Slots[i].Key, _customChrome[i]);
		}

		if (!Mathf.IsEqualApprox(_customStyle.WindowRounding, _base.WindowRounding)) config.SetValue(CustomStyleSection, "rounding", _customStyle.WindowRounding);
		if (!Mathf.IsEqualApprox(_customStyle.WindowBorderSize, _base.WindowBorderSize)) config.SetValue(CustomStyleSection, "border_size", _customStyle.WindowBorderSize);
		if (_customStyle.WindowPadding != _base.WindowPadding) config.SetValue(CustomStyleSection, "padding", _customStyle.WindowPadding);
		if (_customStyle.TitleAlign != _base.TitleAlign) config.SetValue(CustomStyleSection, "title_align", _customStyle.TitleAlign);
		if (_customStyle.ItemSpacing != _base.ItemSpacing) config.SetValue(CustomStyleSection, "item_spacing", _customStyle.ItemSpacing);
		if (_customStyle.FramePadding != _base.FramePadding) config.SetValue(CustomStyleSection, "frame_padding", _customStyle.FramePadding);
	}

	/// <summary>The saved custom preset's own blocks; a missing name means nothing was saved.</summary>
	private void LoadCustomPreset(ConfigFile config)
	{
		string name = config.GetValue(CustomThemeSection, CustomNameKey, "").AsString();
		if (name.Length == 0) return;

		_customName = name;
		_customPalette = ThemePalette.Default;
		if (config.HasSection(CustomThemeSection))
		{
			for (int i = 0; i < ThemePalette.AllRoles.Length; i++)
			{
				ThemeRole role = ThemePalette.AllRoles[i];
				string key = RoleKey(role);
				if (!config.HasSectionKey(CustomThemeSection, key)) continue;
				_customPalette.SetRole(role, config.GetValue(CustomThemeSection, key).AsColor());
			}
		}

		_customChrome = CopyOf(_chromeBase);
		if (config.HasSection(CustomChromeSection))
		{
			for (int i = 0; i < ThemeChrome.Count; i++)
			{
				string key = ThemeChrome.Slots[i].Key;
				if (!config.HasSectionKey(CustomChromeSection, key)) continue;
				_customChrome[i] = config.GetValue(CustomChromeSection, key).AsColor();
			}
		}

		_customStyle = WindowStyle.Default;
		if (config.HasSection(CustomStyleSection))
		{
			if (config.HasSectionKey(CustomStyleSection, "rounding")) _customStyle.WindowRounding = config.GetValue(CustomStyleSection, "rounding").AsSingle();
			if (config.HasSectionKey(CustomStyleSection, "border_size")) _customStyle.WindowBorderSize = config.GetValue(CustomStyleSection, "border_size").AsSingle();
			if (config.HasSectionKey(CustomStyleSection, "padding")) _customStyle.WindowPadding = config.GetValue(CustomStyleSection, "padding").AsVector2();
			if (config.HasSectionKey(CustomStyleSection, "title_align")) _customStyle.TitleAlign = config.GetValue(CustomStyleSection, "title_align").AsVector2();
			if (config.HasSectionKey(CustomStyleSection, "item_spacing")) _customStyle.ItemSpacing = config.GetValue(CustomStyleSection, "item_spacing").AsVector2();
			if (config.HasSectionKey(CustomStyleSection, "frame_padding")) _customStyle.FramePadding = config.GetValue(CustomStyleSection, "frame_padding").AsVector2();
		}
	}

	public void Save(string path)
	{
		if (string.IsNullOrEmpty(path)) return;

		ConfigFile config = new ConfigFile();
		// Re-read first: the wallpaper, alias and font blocks share this file, and a save must not
		// drop a block another writer owns.
		if (Godot.FileAccess.FileExists(path)) config.Load(path);

		if (config.HasSection(ThemeSection)) config.EraseSection(ThemeSection);
		if (config.HasSection(ChromeSection)) config.EraseSection(ChromeSection);
		if (config.HasSection(WindowSection)) config.EraseSection(WindowSection);
		if (config.HasSection(CustomThemeSection)) config.EraseSection(CustomThemeSection);
		if (config.HasSection(CustomChromeSection)) config.EraseSection(CustomChromeSection);
		if (config.HasSection(CustomStyleSection)) config.EraseSection(CustomStyleSection);

		config.SetValue(ThemeSection, PresetKey, _preset);
		for (int i = 0; i < ThemePalette.AllRoles.Length; i++)
		{
			ThemeRole role = ThemePalette.AllRoles[i];
			config.SetValue(ThemeSection, RoleKey(role), _palette.Role(role));
		}

		// Only chrome colours that differ from the base are stored, so an untouched slot keeps
		// following whatever ImGui itself uses.
		for (int i = 0; i < ThemeChrome.Count; i++)
		{
			if (_chrome[i] == _chromeBase[i]) continue;
			config.SetValue(ChromeSection, ThemeChrome.Slots[i].Key, _chrome[i]);
		}

		// Only shape values that differ from the base are stored, which is what keeps the default
		// theme's file free of keys and makes the chrome push a no-op for it.
		if (!Mathf.IsEqualApprox(_style.WindowRounding, _base.WindowRounding)) config.SetValue(WindowSection, "rounding", _style.WindowRounding);
		if (!Mathf.IsEqualApprox(_style.WindowBorderSize, _base.WindowBorderSize)) config.SetValue(WindowSection, "border_size", _style.WindowBorderSize);
		if (_style.WindowPadding != _base.WindowPadding) config.SetValue(WindowSection, "padding", _style.WindowPadding);
		if (_style.TitleAlign != _base.TitleAlign) config.SetValue(WindowSection, "title_align", _style.TitleAlign);
		if (_style.ItemSpacing != _base.ItemSpacing) config.SetValue(WindowSection, "item_spacing", _style.ItemSpacing);
		if (_style.FramePadding != _base.FramePadding) config.SetValue(WindowSection, "frame_padding", _style.FramePadding);

		WriteCustomPreset(config);

		Error result = config.Save(path);
		if (result != Error.Ok) GD.PushWarning("[Theme] could not save the theme (" + result + ")");
	}

	private static string RoleKey(ThemeRole role)
	{
		return ThemePalette.RoleName(role).ToLowerInvariant();
	}
}
