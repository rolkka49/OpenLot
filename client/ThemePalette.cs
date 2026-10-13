using Godot;

/// <summary>
/// The theme's value types (milestone 5.4): the eight role colours the terminal's log classifier and
/// the Output window's severity mapping read, and the window-shape set the client chrome is pushed
/// with. Pure data — no ImGui, no file access — so <see cref="ThemeSettings"/> owns the live state
/// and its persistence, and a suite can check presets and round-trips without an ImGui context.
///
/// One global theme, deliberately: the terminal, the builder panels and the dialogs all read the
/// same values, so the surfaces can never drift apart (§5.4). The roles are the terminal's existing
/// severity vocabulary (its <c>LogLine</c> classification), kept because <c>theme
/// foreground|background</c> is a pinned command contract; the window style is the part ImGui's
/// chrome is pushed with.
/// </summary>
public enum ThemeRole
{
	Foreground = 0,
	Background = 1,
	Error = 2,
	Warning = 3,
	Success = 4,
	System = 5,
	Echo = 6,
	Ghost = 7,
}

/// <summary>The role colours — one value per <see cref="ThemeRole"/>. A value type, so a copy is a
/// snapshot and a caller cannot mutate the theme's live palette by accident.</summary>
public struct ThemePalette
{
	public Color Foreground;
	public Color Background;
	public Color Error;
	public Color Warning;
	public Color Success;
	public Color System;
	public Color Echo;
	public Color Ghost;

	/// <summary>The 8 roles in settings-list order.</summary>
	public static readonly ThemeRole[] AllRoles =
	{
		ThemeRole.Foreground, ThemeRole.Background, ThemeRole.Error, ThemeRole.Warning,
		ThemeRole.Success, ThemeRole.System, ThemeRole.Echo, ThemeRole.Ghost,
	};

	/// <summary>The display name of a role (the settings rows and the design doc use these).</summary>
	public static string RoleName(ThemeRole role)
	{
		switch (role)
		{
			case ThemeRole.Foreground: return "Foreground";
			case ThemeRole.Background: return "Background";
			case ThemeRole.Error: return "Error";
			case ThemeRole.Warning: return "Warning";
			case ThemeRole.Success: return "Success";
			case ThemeRole.System: return "System";
			case ThemeRole.Echo: return "Echo";
			default: return "Ghost";
		}
	}

	public Color Role(ThemeRole role)
	{
		switch (role)
		{
			case ThemeRole.Foreground: return Foreground;
			case ThemeRole.Background: return Background;
			case ThemeRole.Error: return Error;
			case ThemeRole.Warning: return Warning;
			case ThemeRole.Success: return Success;
			case ThemeRole.System: return System;
			case ThemeRole.Echo: return Echo;
			default: return Ghost;
		}
	}

	public void SetRole(ThemeRole role, Color color)
	{
		switch (role)
		{
			case ThemeRole.Foreground: Foreground = color; break;
			case ThemeRole.Background: Background = color; break;
			case ThemeRole.Error: Error = color; break;
			case ThemeRole.Warning: Warning = color; break;
			case ThemeRole.Success: Success = color; break;
			case ThemeRole.System: System = color; break;
			case ThemeRole.Echo: Echo = color; break;
			default: Ghost = color; break;
		}
	}

	/// <summary>
	/// The startup palette — byte-for-byte the literals the terminal's palette fields were seeded
	/// with before this milestone, so <c>theme default</c> restores exactly them and an untouched
	/// install renders unchanged.
	/// </summary>
	public static ThemePalette Default
	{
		get
		{
			return new ThemePalette
			{
				Foreground = new Color(0.9f, 0.9f, 0.9f),
				Background = new Color(0.06f, 0.06f, 0.06f),
				Error = new Color(0.95f, 0.35f, 0.35f),
				Warning = new Color(0.95f, 0.75f, 0.3f),
				Success = new Color(0.4f, 0.9f, 0.45f),
				System = new Color(0.5f, 0.75f, 1.0f),
				Echo = new Color(0.55f, 0.55f, 0.55f),
				Ghost = new Color(0.45f, 0.45f, 0.45f),
			};
		}
	}

	/// <summary>True when every role equals <paramref name="other"/> — used to re-derive which preset
	/// a hand-edited theme now matches (so the settings label stays truthful).</summary>
	public bool Matches(ThemePalette other)
	{
		return Foreground == other.Foreground && Background == other.Background
			&& Error == other.Error && Warning == other.Warning
			&& Success == other.Success && System == other.System
			&& Echo == other.Echo && Ghost == other.Ghost;
	}
}

/// <summary>
/// The window-shape set pushed onto the ImGui chrome for a whole layout pass (§5.4): rounding,
/// border size, padding, title-bar alignment, frame and item spacing. Colours are NOT here — they
/// live in <see cref="ThemeChrome"/>, so the Theme tab owns every colour and this set owns the
/// numbers.
///
/// <see cref="Default"/> holds ImGui's documented stock values. The client pushes only the fields
/// that DIFFER from its base (<see cref="ThemeService"/>), so the default theme pushes nothing at
/// all and an install that never opens the settings renders exactly as it did before this milestone.
/// </summary>
public struct WindowStyle
{
	public float WindowRounding;
	public float WindowBorderSize;
	public Vector2 WindowPadding;
	public Vector2 TitleAlign;
	public Vector2 ItemSpacing;
	public Vector2 FramePadding;

	public static WindowStyle Default
	{
		get
		{
			return new WindowStyle
			{
				WindowRounding = 0f,
				WindowBorderSize = 1f,
				WindowPadding = new Vector2(8f, 8f),
				TitleAlign = new Vector2(0f, 0.5f),
				ItemSpacing = new Vector2(8f, 4f),
				FramePadding = new Vector2(4f, 3f),
			};
		}
	}

	/// <summary>True when every value equals <paramref name="other"/> — the client then pushes nothing.</summary>
	public bool Matches(WindowStyle other)
	{
		return Mathf.IsEqualApprox(WindowRounding, other.WindowRounding)
			&& Mathf.IsEqualApprox(WindowBorderSize, other.WindowBorderSize)
			&& WindowPadding == other.WindowPadding
			&& TitleAlign == other.TitleAlign
			&& ItemSpacing == other.ItemSpacing
			&& FramePadding == other.FramePadding;
	}
}

/// <summary>
/// The chrome colours a theme owns (§5.4): the ones a user actually sees as "the window" — the
/// window/popup/menu surfaces, the title bar, buttons, input and slider frames, tabs, selectable
/// rows, scrollbars and the border. Editing these is what makes a theme change the client's look;
/// the role colours in <see cref="ThemePalette"/> only colour log text and severity rows.
///
/// <see cref="Slots"/> carries ImGui's stock dark values as a fallback, but at startup the client
/// reads the LIVE values (<c>ImGui.GetStyleColor</c>, which the wrapper does expose) as the base — so
/// the default theme matches whatever the running style already is, pushes nothing, and a slot the
/// creator has not touched is never overridden.
/// </summary>
public static class ThemeChrome
{
	/// <summary>Where a slot's colour comes from when a preset builds a themed chrome.</summary>
	public enum Source
	{
		WindowBg,   // the window/panel surface
		Surface,    // secondary surfaces: popups, menu bar, title bar, scrollbar grab
		Control,    // idle buttons, tabs and selectable rows: the surface nudged toward the text colour
		Frame,      // input/slider fields: the surface nudged slightly less than a control
		Hover,      // the hovered state of a button/frame/tab/row
		Accent,     // the active/selected state (and the border, title bar active, check mark)
		Text,       // normal text and glyphs
		TextMuted,  // disabled text
	}

	public struct Slot
	{
		public string Key;
		public string Label;
		public int Index;      // the addon's Col* constant
		public Source Origin;
		public Color Default;  // ImGui's stock dark value (used only if the live read fails)
	}

	// Ordered as the settings list draws them.
	public static readonly Slot[] Slots =
	{
		new Slot { Key = "window_bg", Label = "Window background", Index = ImGui.ColWindowBg, Origin = Source.WindowBg, Default = new Color(0.06f, 0.06f, 0.06f, 0.94f) },
		new Slot { Key = "docking_empty_bg", Label = "Workspace (behind panels)", Index = ImGui.ColDockingEmptyBg, Origin = Source.WindowBg, Default = new Color(0.20f, 0.20f, 0.20f, 1.0f) },
		new Slot { Key = "popup_bg", Label = "Popup background", Index = ImGui.ColPopupBg, Origin = Source.Surface, Default = new Color(0.08f, 0.08f, 0.08f, 0.94f) },
		new Slot { Key = "menu_bar_bg", Label = "Menu bar", Index = ImGui.ColMenuBarBg, Origin = Source.Surface, Default = new Color(0.07f, 0.07f, 0.07f, 0.94f) },
		new Slot { Key = "title_bg", Label = "Title bar", Index = ImGui.ColTitleBg, Origin = Source.Surface, Default = new Color(0.04f, 0.04f, 0.04f, 1f) },
		new Slot { Key = "title_bg_active", Label = "Title bar (active)", Index = ImGui.ColTitleBgActive, Origin = Source.Accent, Default = new Color(0.16f, 0.29f, 0.48f, 1f) },
		new Slot { Key = "button", Label = "Button", Index = ImGui.ColButton, Origin = Source.Control, Default = new Color(0.26f, 0.59f, 0.98f, 0.40f) },
		new Slot { Key = "button_hovered", Label = "Button (hovered)", Index = ImGui.ColButtonHovered, Origin = Source.Hover, Default = new Color(0.26f, 0.59f, 0.98f, 1f) },
		new Slot { Key = "button_active", Label = "Button (active)", Index = ImGui.ColButtonActive, Origin = Source.Accent, Default = new Color(0.06f, 0.53f, 0.98f, 1f) },
		new Slot { Key = "frame", Label = "Frame background", Index = ImGui.ColFrameBg, Origin = Source.Frame, Default = new Color(0.16f, 0.29f, 0.48f, 0.54f) },
		new Slot { Key = "frame_hovered", Label = "Frame (hovered)", Index = ImGui.ColFrameBgHovered, Origin = Source.Hover, Default = new Color(0.26f, 0.59f, 0.98f, 0.40f) },
		new Slot { Key = "frame_active", Label = "Frame (active)", Index = ImGui.ColFrameBgActive, Origin = Source.Accent, Default = new Color(0.26f, 0.59f, 0.98f, 0.67f) },
		new Slot { Key = "tab", Label = "Tab", Index = ImGui.ColTab, Origin = Source.Control, Default = new Color(0.11f, 0.15f, 0.20f, 0.86f) },
		new Slot { Key = "tab_hovered", Label = "Tab (hovered)", Index = ImGui.ColTabHovered, Origin = Source.Hover, Default = new Color(0.26f, 0.59f, 0.98f, 0.80f) },
		new Slot { Key = "tab_active", Label = "Tab (active)", Index = ImGui.ColTabActive, Origin = Source.Accent, Default = new Color(0.20f, 0.41f, 0.68f, 1f) },
		new Slot { Key = "row", Label = "Row (selectable)", Index = ImGui.ColHeader, Origin = Source.Control, Default = new Color(0.26f, 0.59f, 0.98f, 0.31f) },
		new Slot { Key = "row_hovered", Label = "Row (hovered)", Index = ImGui.ColHeaderHovered, Origin = Source.Hover, Default = new Color(0.26f, 0.59f, 0.98f, 0.80f) },
		new Slot { Key = "row_active", Label = "Row (selected)", Index = ImGui.ColHeaderActive, Origin = Source.Accent, Default = new Color(0.26f, 0.59f, 0.98f, 1f) },
		new Slot { Key = "scrollbar_bg", Label = "Scrollbar background", Index = ImGui.ColScrollbarBg, Origin = Source.WindowBg, Default = new Color(0.02f, 0.02f, 0.02f, 0.53f) },
		new Slot { Key = "scrollbar_grab", Label = "Scrollbar grab", Index = ImGui.ColScrollbarGrab, Origin = Source.Surface, Default = new Color(0.31f, 0.31f, 0.31f, 1f) },
		new Slot { Key = "check_mark", Label = "Check mark", Index = ImGui.ColCheckMark, Origin = Source.Accent, Default = new Color(0.26f, 0.59f, 0.98f, 1f) },
		new Slot { Key = "text", Label = "Text", Index = ImGui.ColText, Origin = Source.Text, Default = new Color(0.90f, 0.90f, 0.90f, 1f) },
		new Slot { Key = "text_disabled", Label = "Text (disabled)", Index = ImGui.ColTextDisabled, Origin = Source.TextMuted, Default = new Color(0.50f, 0.50f, 0.50f, 1f) },
		new Slot { Key = "border", Label = "Border", Index = ImGui.ColBorder, Origin = Source.Accent, Default = new Color(0.43f, 0.43f, 0.50f, 0.50f) },
	};

	public static int Count { get { return Slots.Length; } }

	/// <summary>The slot index of a key ("window_bg", "frame", …), or -1. Lets callers name a slot
	/// instead of hard-coding its position in the table.</summary>
	public static int IndexOf(string key)
	{
		for (int i = 0; i < Slots.Length; i++)
		{
			if (Slots[i].Key == key) return i;
		}
		return -1;
	}

	/// <summary>The stock table — the base when the live values cannot be read, and what tests use.</summary>
	public static Color[] Defaults()
	{
		Color[] colors = new Color[Slots.Length];
		for (int i = 0; i < Slots.Length; i++) colors[i] = Slots[i].Default;
		return colors;
	}

	/// <summary>
	/// Builds a full chrome table from four anchors — the window background, the secondary surface,
	/// the accent used for active/hovered states and the TEXT colour — so a preset's chrome is
	/// uniform rather than twenty hand-picked literals, while every slot still resolves to something
	/// deliberate. The text anchor is what makes a light preset readable: controls are derived by
	/// nudging the surface toward the text colour, so buttons and fields read as raised on a white
	/// window and on a black one alike.
	/// </summary>
	public static Color[] Build(Color windowBg, Color surface, Color accent, Color text)
	{
		Color control = surface.Lerp(text, 0.14f);
		Color frame = surface.Lerp(text, 0.08f);
		Color hover = surface.Lerp(accent, 0.40f);
		Color muted = text.Lerp(windowBg, 0.45f);

		Color[] colors = new Color[Slots.Length];
		for (int i = 0; i < Slots.Length; i++)
		{
			switch (Slots[i].Origin)
			{
				case Source.WindowBg: colors[i] = windowBg; break;
				case Source.Surface: colors[i] = surface; break;
				case Source.Control: colors[i] = control; break;
				case Source.Frame: colors[i] = frame; break;
				case Source.Hover: colors[i] = hover; break;
				case Source.Accent: colors[i] = accent; break;
				case Source.Text: colors[i] = text; break;
				default: colors[i] = muted; break;
			}
		}
		return colors;
	}
}

/// <summary>
/// The three named presets (§5.1's `theme` vocabulary, kept in <see cref="TerminalThemeCatalog"/> so
/// the command and the settings window cannot drift). A preset carries a palette, a chrome colour
/// set AND a window shape together, which is what "one theme" means: choosing Green Matrix restyles
/// the text roles, the window chrome and the shape in one step. The preset shapes and chrome are a
/// starting point a creator can refine in the settings tabs, not a fixed look.
/// </summary>
/// <summary>
/// The built-in presets (§5.1's `theme` vocabulary, kept in <see cref="TerminalThemeCatalog"/> so the
/// command and the settings window cannot drift). A preset carries a palette, a chrome colour set AND
/// a window shape together, which is what "one theme" means: choosing Cyberpunk restyles the text
/// roles, the window chrome and the shape in one step.
///
/// The set is data — one <see cref="Definition"/> per preset in the table below — so adding a theme is
/// one entry, not a new branch in three accessors. Dark and light themes both work because the chrome
/// is built from anchors: the window background, a secondary surface, an accent and the TEXT colour
/// (controls are nudged from the surface toward the text, so buttons and fields read as raised on
/// white and on black alike).
///
/// A creator's own preset is separate: it has no fixed palette, so it is stored as a snapshot
/// (<see cref="ThemeSettings"/>) and offered as "Custom".
/// </summary>
public static class ThemePresets
{
	/// <summary>The preset name a theme wears once a role colour, a chrome colour or a shape value is
	/// edited by hand, and the name the saved user preset is applied as.</summary>
	public const string CustomPreset = "custom";

	/// <summary>
	/// One built-in preset. The four colour fields are the chrome anchors; the default preset has none
	/// (<see cref="HasChrome"/> false), so it pushes no colour and keeps the live ImGui style.
	/// </summary>
	private sealed class Definition
	{
		public string Name;
		public ThemePalette Palette;
		public WindowStyle Style;
		public bool HasChrome;
		public Color WindowBg;
		public Color Surface;
		public Color Accent;
		public Color Text;
	}

	private static readonly Definition[] Definitions =
	{
		new Definition
		{
			Name = TerminalThemeCatalog.DefaultPreset,
			Palette = ThemePalette.Default,
			Style = WindowStyle.Default,
			HasChrome = false,
		},
		new Definition
		{
			Name = TerminalThemeCatalog.GreenMatrixPreset,
			Style = WindowStyle.Default,
			HasChrome = true,
			WindowBg = new Color(0.02f, 0.05f, 0.02f), Surface = new Color(0.05f, 0.13f, 0.06f),
			Accent = new Color(0.12f, 0.34f, 0.15f), Text = new Color(0.80f, 1.0f, 0.82f),
			Palette = new ThemePalette
			{
				Foreground = new Color(0.25f, 0.9f, 0.35f),
				Background = new Color(0.02f, 0.05f, 0.02f),
				Error = new Color(1.0f, 0.3f, 0.3f),
				Warning = new Color(0.6f, 0.9f, 0.3f),
				Success = new Color(0.4f, 1.0f, 0.5f),
				System = new Color(0.5f, 1.0f, 0.7f),
				Echo = new Color(0.2f, 0.6f, 0.3f),
				Ghost = new Color(0.15f, 0.5f, 0.25f),
			},
		},
		new Definition
		{
			Name = TerminalThemeCatalog.AmberRetroPreset,
			Style = Rounded(3f, 2f),
			HasChrome = true,
			WindowBg = new Color(0.07f, 0.045f, 0.01f), Surface = new Color(0.15f, 0.10f, 0.03f),
			Accent = new Color(0.45f, 0.30f, 0.09f), Text = new Color(1.0f, 0.86f, 0.55f),
			Palette = new ThemePalette
			{
				Foreground = new Color(1.0f, 0.72f, 0.25f),
				Background = new Color(0.07f, 0.045f, 0.01f),
				Error = new Color(1.0f, 0.35f, 0.15f),
				Warning = new Color(1.0f, 0.85f, 0.3f),
				Success = new Color(0.95f, 0.9f, 0.4f),
				System = new Color(1.0f, 0.8f, 0.55f),
				Echo = new Color(0.65f, 0.45f, 0.15f),
				Ghost = new Color(0.55f, 0.4f, 0.15f),
			},
		},
		new Definition
		{
			// Neon cyan and magenta over near-black — the Blade Runner look.
			Name = TerminalThemeCatalog.CyberpunkPreset,
			Style = Rounded(2f, 2f),
			HasChrome = true,
			WindowBg = new Color(0.02f, 0.01f, 0.05f), Surface = new Color(0.06f, 0.03f, 0.12f),
			Accent = new Color(0.55f, 0.05f, 0.55f), Text = new Color(0.80f, 0.96f, 1.0f),
			Palette = new ThemePalette
			{
				Foreground = new Color(0.30f, 0.95f, 1.0f),
				Background = new Color(0.02f, 0.01f, 0.05f),
				Error = new Color(1.0f, 0.15f, 0.45f),
				Warning = new Color(1.0f, 0.80f, 0.15f),
				Success = new Color(0.35f, 1.0f, 0.55f),
				System = new Color(1.0f, 0.35f, 0.85f),
				Echo = new Color(0.45f, 0.30f, 0.65f),
				Ghost = new Color(0.35f, 0.22f, 0.50f),
			},
		},

		new Definition
		{
			// Vaporwave: purple and pink, a little softer than Cyberpunk.
			Name = TerminalThemeCatalog.SynthwavePreset,
			Style = Rounded(4f, 1f),
			HasChrome = true,
			WindowBg = new Color(0.09f, 0.04f, 0.16f), Surface = new Color(0.15f, 0.07f, 0.26f),
			Accent = new Color(0.75f, 0.15f, 0.55f), Text = new Color(0.97f, 0.88f, 1.0f),
			Palette = new ThemePalette
			{
				Foreground = new Color(0.95f, 0.75f, 0.98f),
				Background = new Color(0.09f, 0.04f, 0.16f),
				Error = new Color(1.0f, 0.30f, 0.45f),
				Warning = new Color(1.0f, 0.78f, 0.35f),
				Success = new Color(0.45f, 1.0f, 0.75f),
				System = new Color(0.65f, 0.55f, 1.0f),
				Echo = new Color(0.70f, 0.45f, 0.85f),
				Ghost = new Color(0.55f, 0.35f, 0.70f),
			},
		},
		new Definition
		{
			// Kawaii: pink and white. A LIGHT theme — dark text, pale surfaces.
			Name = TerminalThemeCatalog.KawaiiPreset,
			Style = Rounded(6f, 1f),
			HasChrome = true,
			WindowBg = new Color(0.99f, 0.95f, 0.97f), Surface = new Color(1.0f, 0.99f, 1.0f),
			Accent = new Color(0.93f, 0.42f, 0.62f), Text = new Color(0.24f, 0.12f, 0.19f),
			Palette = new ThemePalette
			{
				Foreground = new Color(0.24f, 0.12f, 0.19f),
				Background = new Color(0.99f, 0.95f, 0.97f),
				Error = new Color(0.85f, 0.15f, 0.35f),
				Warning = new Color(0.90f, 0.55f, 0.10f),
				Success = new Color(0.20f, 0.65f, 0.42f),
				System = new Color(0.45f, 0.35f, 0.85f),
				Echo = new Color(0.62f, 0.42f, 0.55f),
				Ghost = new Color(0.72f, 0.55f, 0.66f),
			},
		},
		new Definition
		{
			// Navy: blue and white. A LIGHT theme with a high-contrast ink colour.
			Name = TerminalThemeCatalog.NavyPreset,
			Style = Rounded(2f, 1f),
			HasChrome = true,
			WindowBg = new Color(0.96f, 0.97f, 1.0f), Surface = new Color(1.0f, 1.0f, 1.0f),
			Accent = new Color(0.14f, 0.28f, 0.55f), Text = new Color(0.10f, 0.15f, 0.28f),
			Palette = new ThemePalette
			{
				Foreground = new Color(0.10f, 0.15f, 0.28f),
				Background = new Color(0.96f, 0.97f, 1.0f),
				Error = new Color(0.80f, 0.15f, 0.20f),
				Warning = new Color(0.85f, 0.55f, 0.05f),
				Success = new Color(0.13f, 0.55f, 0.35f),
				System = new Color(0.12f, 0.32f, 0.72f),
				Echo = new Color(0.42f, 0.48f, 0.60f),
				Ghost = new Color(0.58f, 0.63f, 0.72f),
			},
		},
		new Definition
		{
			// Forest: deep greens and bark.
			Name = TerminalThemeCatalog.ForestPreset,
			Style = Rounded(3f, 1f),
			HasChrome = true,
			WindowBg = new Color(0.05f, 0.10f, 0.06f), Surface = new Color(0.10f, 0.18f, 0.11f),
			Accent = new Color(0.22f, 0.45f, 0.25f), Text = new Color(0.88f, 0.95f, 0.86f),
			Palette = new ThemePalette
			{
				Foreground = new Color(0.78f, 0.92f, 0.76f),
				Background = new Color(0.05f, 0.10f, 0.06f),
				Error = new Color(0.90f, 0.35f, 0.30f),
				Warning = new Color(0.92f, 0.78f, 0.35f),
				Success = new Color(0.45f, 0.90f, 0.50f),
				System = new Color(0.55f, 0.80f, 0.65f),
				Echo = new Color(0.40f, 0.58f, 0.42f),
				Ghost = new Color(0.32f, 0.46f, 0.34f),
			},
		},
		new Definition
		{
			// Mono: greyscale chrome, so only the status roles carry colour.
			Name = TerminalThemeCatalog.MonoPreset,
			Style = WindowStyle.Default,
			HasChrome = true,
			WindowBg = new Color(0.10f, 0.10f, 0.10f), Surface = new Color(0.16f, 0.16f, 0.16f),
			Accent = new Color(0.55f, 0.55f, 0.58f), Text = new Color(0.90f, 0.90f, 0.90f),
			Palette = new ThemePalette
			{
				Foreground = new Color(0.88f, 0.88f, 0.88f),
				Background = new Color(0.10f, 0.10f, 0.10f),
				Error = new Color(0.95f, 0.45f, 0.45f),
				Warning = new Color(0.95f, 0.85f, 0.45f),
				Success = new Color(0.75f, 0.95f, 0.75f),
				System = new Color(0.70f, 0.80f, 0.95f),
				Echo = new Color(0.60f, 0.60f, 0.60f),
				Ghost = new Color(0.45f, 0.45f, 0.45f),
			},
		},
	};

	/// <summary>A stock shape with the two values a preset usually touches.</summary>
	private static WindowStyle Rounded(float rounding, float border)
	{
		WindowStyle style = WindowStyle.Default;
		style.WindowRounding = rounding;
		style.WindowBorderSize = border;
		return style;
	}

	private static Definition Find(string preset)
	{
		for (int i = 0; i < Definitions.Length; i++)
		{
			if (Definitions[i].Name == preset) return Definitions[i];
		}
		return null;
	}

	/// <summary>The palette for a preset (the neutral seed palette for the default preset).</summary>
	public static ThemePalette PaletteFor(string preset)
	{
		Definition definition = Find(preset);
		return definition != null ? definition.Palette : ThemePalette.Default;
	}

	/// <summary>The shape for a preset. The default preset is stock.</summary>
	public static WindowStyle StyleFor(string preset)
	{
		Definition definition = Find(preset);
		return definition != null ? definition.Style : WindowStyle.Default;
	}

	/// <summary>
	/// The chrome colours for a preset, or null when it has none — null means "keep the base", so the
	/// default theme pushes no colour at all and keeps the live ImGui style.
	/// </summary>
	public static Color[] ChromeFor(string preset)
	{
		Definition definition = Find(preset);
		if (definition == null || !definition.HasChrome) return null;
		return ThemeChrome.Build(definition.WindowBg, definition.Surface, definition.Accent, definition.Text);
	}
}

