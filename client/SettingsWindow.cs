using Godot;
using System.Collections.Generic;

/// <summary>
/// The client settings surface (milestone 5.4), grown from the terminal's theme-only window into a
/// tabbed one: Theme, Window Style, Camera, Graphics, Fonts and (5.5) Keys. ONE surface drawn by
/// both hosts — the terminal's main menu and the builder's View menu / pause menu — so the two can
/// never disagree, the same reasoning the `theme` command and the font pickers already follow.
///
/// Hosts pass a <see cref="Reporter"/> so messages land where each already logs (the terminal's
/// scrollback, or the builder's Output window).
///
/// Edits preview live and commit on release: a colour drag or a slider re-applies to the running UI
/// every frame and writes the settings file once, when the widget is deactivated — the
/// commit-on-release rule §2.4's undo coalescing uses.
/// </summary>
public static class SettingsWindow
{
	/// <summary>The window's ImGui id, pinned so a restarted client restores the same geometry.</summary>
	public const string WindowId = "Settings###openlot_settings";

	/// <summary>Host report sink: <c>isError</c> plus the message.</summary>
	public delegate void Reporter(bool isError, string message);

	private static string _fontFilter = "";

	// Per-frame allocations are avoided (the project's rule): the label strings and the option arrays
	// are built once instead of on every draw of a settings tab.
	private static readonly string[] ChromeLabels = BuildChromeLabels();
	private static readonly string[] RenderMethodLabels = BuildRenderMethodLabels();
	private static readonly string[] MsaaLabels = { "Off", "2x", "4x", "8x" };
	private static string[] _fontRowLabels;      // one label per font entry, built on first use

	private static string[] BuildChromeLabels()
	{
		string[] labels = new string[ThemeChrome.Count];
		for (int i = 0; i < labels.Length; i++)
		{
			labels[i] = ThemeChrome.Slots[i].Label + "##chrome" + i;
		}
		return labels;
	}

	private static string[] BuildRenderMethodLabels()
	{
		string[] labels = new string[GraphicsSettings.RenderMethods.Length];
		for (int i = 0; i < labels.Length; i++)
		{
			labels[i] = GraphicsSettings.RenderMethodLabel(GraphicsSettings.RenderMethods[i]);
		}
		return labels;
	}

	/// <summary>
	/// True while an edit in the current tab has changed a value but not yet been written. A slider or
	/// colour drag moves a value every frame and only reports "deactivated after edit" on release —
	/// the release frame itself usually reports no movement — so this flag is what lets the file be
	/// written exactly once per gesture, and never on a stray deactivation.
	/// </summary>
	private static bool _pendingEdit;

	/// <summary>Draws the window. Returns true while it is open; false when the host should close it
	/// (the Close button — the terminal returns to its menu, the builder hides the window).</summary>
	public static bool Draw(Reporter report)
	{
		ImGui.SetNextWindowSize(560, 620, ImGui.CondFirstUseEver);

		bool open = ImGui.Begin(WindowId, EditorChrome.PanelWindowFlags);
		if (!open)
		{
			// Collapsed is not a close request: still draw nothing, but report "open". A capture in
			// flight is dropped, since its "Press a key…" prompt is not visible any more.
			KeyBindingService.CancelCapture();
			ImGui.End();
			return true;
		}

		bool close = ImGui.Button("Close");
		// A capture must never outlive the window that started it, or it would swallow the next key
		// press anywhere in the client.
		if (close) KeyBindingService.CancelCapture();
		ImGui.SameLine();
		ImGui.TextDisabled("Theme: " + ThemeService.PresetDisplayName(ThemeService.PresetName));
		ImGui.Separator();

		if (ImGui.BeginTabBar("##openlot_settings_tabs"))
		{
			if (ImGui.BeginTabItem("Theme")) { DrawThemeTab(report); ImGui.EndTabItem(); }
			if (ImGui.BeginTabItem("Window Style")) { DrawWindowStyleTab(); ImGui.EndTabItem(); }
			if (ImGui.BeginTabItem("Camera")) { DrawCameraTab(); ImGui.EndTabItem(); }
			if (ImGui.BeginTabItem("Graphics")) { DrawGraphicsTab(report); ImGui.EndTabItem(); }
			if (ImGui.BeginTabItem("Fonts")) { DrawFontsTab(report); ImGui.EndTabItem(); }
			if (ImGui.BeginTabItem("Keys")) { DrawKeysTab(report); ImGui.EndTabItem(); }
			ImGui.EndTabBar();
		}

		ImGui.End();
		return !close;
	}

	// --- Theme ------------------------------------------------------------------------------------

	/// <summary>Name typed into the custom-preset field (kept while the window is open).</summary>
	private static string _customNameInput = "";

	private static void DrawThemeTab(Reporter report)
	{
		ImGui.Text("Preset");
		IReadOnlyList<string> presets = TerminalThemeCatalog.PresetNames;
		// Three per row: the set is too long for one line in this window.
		const int perRow = 3;
		for (int i = 0; i < presets.Count; i++)
		{
			if (i % perRow != 0) ImGui.SameLine(0f, 8f);
			bool active = ThemeService.PresetName == presets[i];
			if (active) ImGui.PushStyleColor(ImGui.ColButton, ImGui.GetStyleColor(ImGui.ColButtonActive));
			if (ImGui.Button(ThemeService.PresetDisplayName(presets[i])))
			{
				if (!ThemeService.ApplyPresetByName(presets[i], out string message)) report(true, message);
			}
			if (active) ImGui.PopStyleColor();
		}
		if (ImGui.Button("Restore defaults"))
		{
			ThemeService.ApplyPresetByName(TerminalThemeCatalog.DefaultPreset, out string _);
		}

		// --- the creator's own preset (a saved snapshot, so a built-in preset cannot lose it) ---
		ImGui.Spacing();
		ImGui.Text("Custom preset");
		if (ThemeService.HasCustomPreset)
		{
			if (ThemeService.IsCustomPresetActive) ImGui.PushStyleColor(ImGui.ColButton, ImGui.GetStyleColor(ImGui.ColButtonActive));
			if (ImGui.Button("Apply \"" + ThemeService.CustomPresetName + "\"", 260f, 0f))
			{
				if (!ThemeService.ApplyCustomPreset(out string message)) report(true, message);
			}
			if (ThemeService.IsCustomPresetActive) ImGui.PopStyleColor();
		}
		else
		{
			ImGui.TextDisabled("None saved yet.");
		}

		_customNameInput = ImGui.InputText("Name", _customNameInput, 48);
		ImGui.BeginDisabled(_customNameInput == null || _customNameInput.Trim().Length == 0);
		if (ImGui.Button("Save current theme as this preset", 260f, 0f))
		{
			if (ThemeService.SaveCustomPreset(_customNameInput, out string message)) report(false, message);
			else report(true, message);
		}
		ImGui.EndDisabled();
		if (ThemeService.HasCustomPreset)
		{
			ImGui.SameLine(0f, 8f);
			if (ImGui.Button("Delete"))
			{
				if (ThemeService.ClearCustomPreset(out string message)) report(false, message);
				else report(true, message);
			}
		}
		ImGui.TextDisabled("Save stores the current colours and shape under this name; the button re-applies them.");

		ImGui.Separator();
		ImGui.Text("Role colours");
		ThemePalette palette = ThemeService.Palette;
		for (int i = 0; i < ThemePalette.AllRoles.Length; i++)
		{
			ThemeRole role = ThemePalette.AllRoles[i];
			Color current = palette.Role(role);
			Color edited = ImGui.ColorEdit4(ThemePalette.RoleName(role), current,
				ImGui.ColorEditAlphaBar | ImGui.ColorEditAlphaPreview);
			if (edited != current) { ThemeService.SetRole(role, edited); _pendingEdit = true; }
			if (ImGui.IsItemDeactivatedAfterEdit() && _pendingEdit) { _pendingEdit = false; ThemeService.Save(); }
		}
		ImGui.TextDisabled("Role colours tint log lines and severity rows.");

		ImGui.Separator();
		ImGui.Text("Transparency");
		float windowPercent = Mathf.Round(ThemeService.Settings.WindowTransparency * 100f);
		float framePercent = Mathf.Round(ThemeService.Settings.FrameTransparency * 100f);

		float editedWindow = ImGui.SliderFloat("Windows & workspace", windowPercent, 0f, 100f, "%.0f%%", 0);
		bool released = ImGui.IsItemDeactivatedAfterEdit();
		if (!Mathf.IsEqualApprox(editedWindow, windowPercent))
		{
			ThemeService.SetWindowTransparency(editedWindow / 100f);
			_pendingEdit = true;
		}

		float editedFrame = ImGui.SliderFloat("Input fields (frames)", framePercent, 0f, 100f, "%.0f%%", 0);
		released |= ImGui.IsItemDeactivatedAfterEdit();
		if (!Mathf.IsEqualApprox(editedFrame, framePercent))
		{
			ThemeService.SetFrameTransparency(editedFrame / 100f);
			_pendingEdit = true;
		}
		if (released && _pendingEdit) { _pendingEdit = false; ThemeService.Save(); }

		ImGui.TextDisabled("Panel transparency shows what is behind the client — set a wallpaper to see it.");
		if (ImGui.Button("Reset transparency")) ThemeService.ResetTransparency();

		ImGui.Separator();
		ImGui.Text("Window colours");
		ImGui.TextDisabled("These are what make a theme change the client's look; each row's alpha is transparency.");
		ThemeSettings theme = ThemeService.Settings;
		for (int i = 0; i < ThemeChrome.Count; i++)
		{
			Color current = theme.Chrome(i);
			Color edited = ImGui.ColorEdit4(ChromeLabels[i], current,
				ImGui.ColorEditAlphaBar | ImGui.ColorEditAlphaPreview);
			if (edited != current) { ThemeService.SetChrome(i, edited); _pendingEdit = true; }
			if (ImGui.IsItemDeactivatedAfterEdit() && _pendingEdit) { _pendingEdit = false; ThemeService.Save(); }
		}
		if (ImGui.Button("Restore window colours")) ThemeService.ResetChrome();

		ImGui.Spacing();
		ImGui.TextDisabled(ThemeService.PresetName == ThemePresets.CustomPreset
			? "Edited by hand — the theme is now Custom."
			: "Editing a colour makes the theme Custom.");
	}

	// --- Window Style -----------------------------------------------------------------------------

	private static void DrawWindowStyleTab()
	{
		WindowStyle style = ThemeService.Settings.Style;
		WindowStyle edited = style;

		edited.WindowRounding = ImGui.SliderFloat("Window rounding", style.WindowRounding, 0f, 12f, "%.1f", 0);
		bool commit = ImGui.IsItemDeactivatedAfterEdit();

		edited.WindowBorderSize = ImGui.SliderFloat("Border size", style.WindowBorderSize, 0f, 6f, "%.1f", 0);
		commit |= ImGui.IsItemDeactivatedAfterEdit();

		edited.WindowPadding = ImGui.SliderFloat2("Window padding", style.WindowPadding, 0f, 24f);
		commit |= ImGui.IsItemDeactivatedAfterEdit();

		edited.TitleAlign = ImGui.SliderFloat2("Title alignment", style.TitleAlign, 0f, 1f);
		commit |= ImGui.IsItemDeactivatedAfterEdit();

		edited.ItemSpacing = ImGui.SliderFloat2("Item spacing", style.ItemSpacing, 0f, 24f);
		commit |= ImGui.IsItemDeactivatedAfterEdit();

		edited.FramePadding = ImGui.SliderFloat2("Frame padding", style.FramePadding, 0f, 16f);
		commit |= ImGui.IsItemDeactivatedAfterEdit();

		if (!edited.Matches(style)) { ThemeService.SetStyle(edited); _pendingEdit = true; }
		if (commit && _pendingEdit) { _pendingEdit = false; ThemeService.Save(); }

		ImGui.Separator();
		if (ImGui.Button("Restore window style")) ThemeService.ResetWindowStyle();
		ImGui.TextDisabled("The shape applies to the terminal, every builder panel and every dialog.");
		ImGui.TextDisabled("Colours live in the Theme tab.");
	}

	// --- Camera -----------------------------------------------------------------------------------

	private static void DrawCameraTab()
	{
		CameraSettings camera = CameraSettingsService.Current;

		float fovBefore = camera.Fov;
		float sensitivityBefore = camera.LookSensitivity;
		float zoomStepBefore = camera.ZoomStep;
		float zoomBlendBefore = camera.ZoomBlendStart;
		bool invertBefore = camera.InvertY;

		camera.Fov = ImGui.SliderFloat("Field of view", camera.Fov,
			CameraSettings.MinFov, CameraSettings.MaxFov, "%.0f", 0);
		bool released = ImGui.IsItemDeactivatedAfterEdit();

		camera.LookSensitivity = ImGui.SliderFloat("Look sensitivity", camera.LookSensitivity,
			CameraSettings.MinSensitivity, CameraSettings.MaxSensitivity, "%.3f", 0);
		released |= ImGui.IsItemDeactivatedAfterEdit();

		camera.InvertY = ImGui.Checkbox("Invert Y (look)", camera.InvertY);
		if (camera.InvertY != invertBefore) released = true;   // a checkbox is a discrete action

		ImGui.TextDisabled("One value drives the editor camera and the player camera alike.");

		ImGui.Separator();
		ImGui.Text("Player zoom");
		camera.ZoomStep = ImGui.SliderFloat("Zoom speed", camera.ZoomStep,
			CameraSettings.MinZoomStep, CameraSettings.MaxZoomStep, "%.2f", 0);
		released |= ImGui.IsItemDeactivatedAfterEdit();

		camera.ZoomBlendStart = ImGui.SliderFloat("Zoom blend range", camera.ZoomBlendStart,
			CameraSettings.MinZoomBlendStart, CameraSettings.MaxZoomBlendStart, "%.2f", 0);
		released |= ImGui.IsItemDeactivatedAfterEdit();

		// Live preview only when something actually moved; the file is written once, on release. The
		// pending flag is what makes the release frame (which reports no movement of its own) commit.
		bool changed = !Mathf.IsEqualApprox(camera.Fov, fovBefore)
			|| !Mathf.IsEqualApprox(camera.LookSensitivity, sensitivityBefore)
			|| camera.InvertY != invertBefore
			|| !Mathf.IsEqualApprox(camera.ZoomStep, zoomStepBefore)
			|| !Mathf.IsEqualApprox(camera.ZoomBlendStart, zoomBlendBefore);
		if (changed) { CameraSettingsService.NotifyChanged(); _pendingEdit = true; }
		if (released && _pendingEdit)
		{
			_pendingEdit = false;
			CameraSettingsService.Current.ClampToLimits();
			CameraSettingsService.Save();
		}

		ImGui.Separator();
		if (ImGui.Button("Restore camera defaults"))
		{
			CameraSettingsService.Current.ResetToDefaults();
			CameraSettingsService.ApplyAndSave();
		}
	}


	// --- Graphics ---------------------------------------------------------------------------------

	private static void DrawGraphicsTab(Reporter report)
	{
		GraphicsSettings graphics = GraphicsSettingsService.Current;

		float scaleBefore = graphics.RenderScale;
		bool shadowsBefore = graphics.Shadows;
		int msaaBefore = graphics.Msaa;

		graphics.RenderScale = ImGui.SliderFloat("Resolution scale", graphics.RenderScale,
			GraphicsSettings.MinRenderScale, GraphicsSettings.MaxRenderScale, "%.2f", 0);
		bool released = ImGui.IsItemDeactivatedAfterEdit();

		graphics.Shadows = ImGui.Checkbox("Shadows", graphics.Shadows);
		if (graphics.Shadows != shadowsBefore) released = true;

		graphics.Msaa = Mathf.Clamp(ImGui.Combo("MSAA", graphics.Msaa, MsaaLabels),
			GraphicsSettings.MinMsaa, GraphicsSettings.MaxMsaa);
		if (graphics.Msaa != msaaBefore) released = true;

		// The render scale previews live (it is cheap); MSAA and the shadow toggle reconfigure the
		// render target, so they are applied on release rather than on every frame of a drag.
		bool changed = !Mathf.IsEqualApprox(graphics.RenderScale, scaleBefore)
			|| graphics.Shadows != shadowsBefore || graphics.Msaa != msaaBefore;
		if (!Mathf.IsEqualApprox(graphics.RenderScale, scaleBefore)) GraphicsSettingsService.NotifyChanged();
		if (changed) _pendingEdit = true;
		if (released && _pendingEdit)
		{
			_pendingEdit = false;
			GraphicsSettingsService.Current.ClampToLimits();
			GraphicsSettingsService.Save();
			GraphicsSettingsService.NotifyChanged();
		}

		ImGui.Separator();
		ImGui.Text("Render method");
		int current = System.Array.IndexOf(GraphicsSettings.RenderMethods, graphics.RenderMethod);
		if (current < 0) current = 0;
		int picked = ImGui.Combo("Method", current, RenderMethodLabels);
		// The wrapper documents no return range for Combo, so the index is checked before it is used
		// to index the table (the same defensive rule the Inspector's dropdown rows follow).
		if (picked >= 0 && picked < GraphicsSettings.RenderMethods.Length && picked != current)
		{
			graphics.RenderMethod = GraphicsSettings.RenderMethods[picked];
			GraphicsSettingsService.Save();
		}

		ImGui.TextDisabled("Running: " + GraphicsSettings.RenderMethodLabel(GraphicsSettings.RunningRenderMethod()));
		ImGui.TextDisabled("Godot fixes the renderer at startup — a change needs a restart.");
		if (ImGui.Button("Restart now to apply"))
		{
			if (GraphicsSettingsService.RestartWithRenderMethod(out string error))
			{
				report(false, "Restarting with " + GraphicsSettings.RenderMethodLabel(graphics.RenderMethod) + "…");
				SceneTree tree = Engine.GetMainLoop() as SceneTree;
				if (tree != null) tree.Quit();
			}
			else
			{
				report(true, error);
			}
		}

		ImGui.Separator();
		if (ImGui.Button("Restore graphics defaults"))
		{
			GraphicsSettingsService.Current.ResetToDefaults();
			GraphicsSettingsService.ApplyAndSave();
		}
	}


	// --- Fonts (moved here from the terminal's settings window, §5.4) -----------------------------

	/// <summary>Which font slot the list below is showing (one list at a time — see the note in the tab).</summary>
	private static string _fontSlot = FontService.UiSlot;

	/// <summary>
	/// The font slots. ONE list is drawn at a time, chosen by the radio buttons: this addon's child
	/// window API HANGS when used inside a tab bar's window (verified headlessly — an empty
	/// <c>BeginChild</c>/<c>EndChild</c> pair reproduces it, and it hung for the terminal's old
	/// settings window too, which is how the Fonts tab crash was reported). The bounded scroll region
	/// that old window used therefore cannot live here; the scrolling region is this window instead.
	/// </summary>
	private static void DrawFontsTab(Reporter report)
	{
		bool ui = _fontSlot == FontService.UiSlot;
		if (ImGui.RadioButton("UI font", ui)) _fontSlot = FontService.UiSlot;
		ImGui.SameLine(0f, 16f);
		if (ImGui.RadioButton("Console font", !ui)) _fontSlot = FontService.ConsoleSlot;

		_fontFilter = ImGui.InputText("Search", _fontFilter, 64);
		DrawFontSlot(_fontSlot, report);
	}

	/// <summary>
	/// One font slot: the current choice and a searchable list of everything this client can use —
	/// the built-in first, then the bundled CC0 set grouped by category, then whatever the user
	/// dropped into <c>user://fonts/</c>. Selecting applies on the next frame through
	/// <see cref="FontService"/> (a font may not be created while the UI is drawing).
	/// </summary>
	private static void DrawFontSlot(string slot, Reporter report)
	{
		bool isConsole = slot == FontService.ConsoleSlot;
		ImGui.Spacing();
		ImGui.Text("Current " + (isConsole ? "console" : "UI") + " font: " + FontService.DisplayNameFor(slot));

		// The console defaults to following the UI font; this is the one control that says so.
		if (isConsole)
		{
			bool follows = !FontService.ConsoleHasOwnFont;
			if (ImGui.Checkbox("Follow the UI font", follows) != follows) SelectFont(slot, "", report);
		}

		string filter = _fontFilter == null ? "" : _fontFilter.Trim().ToLowerInvariant();
		string ownValue = isConsole ? FontService.Registry.ConsoleSelection : FontService.Registry.UiSelection;
		string lastCategory = "";
		IReadOnlyList<FontRegistry.FontEntry> entries = FontService.Registry.Entries;

		// The row labels are built once (they only change when the font set does), not per frame.
		if (_fontRowLabels == null || _fontRowLabels.Length != entries.Count)
		{
			_fontRowLabels = new string[entries.Count];
			for (int i = 0; i < entries.Count; i++)
			{
				FontRegistry.FontEntry item = entries[i];
				string itemTag = item.BuiltIn ? "" : FontCatalog.IsBundled(item.Id) ? "  (bundled)" : "  (custom)";
				_fontRowLabels[i] = item.DisplayName + itemTag;
			}
		}

		for (int i = 0; i < entries.Count; i++)
		{
			FontRegistry.FontEntry entry = entries[i];
			if (!MatchesFontFilter(entry, filter)) continue;

			// The list is sorted by category, so one header per category is enough.
			if (!entry.BuiltIn && entry.Category != lastCategory)
			{
				ImGui.Spacing();
				ImGui.TextDisabled(entry.Category);
				lastCategory = entry.Category;
			}

			bool selected = isConsole ? ownValue == entry.Id : FontService.Registry.UiSelection == entry.Id;
			if (ImGui.Selectable(_fontRowLabels[i], selected, 0, new Vector2(400f, 0f)))
			{
				SelectFont(slot, entry.Id, report);
			}
		}
	}

	private static bool MatchesFontFilter(FontRegistry.FontEntry entry, string filter)
	{
		if (filter.Length == 0) return true;
		return entry.DisplayName.ToLowerInvariant().Contains(filter) ||
		       entry.Category.ToLowerInvariant().Contains(filter);
	}

	/// <summary>Applies a font choice (or the empty "follow the UI" for the console) and reports it.</summary>
	private static void SelectFont(string slot, string id, Reporter report)
	{
		if (FontService.Select(slot, id, out string message)) report(false, message);
		else report(true, message);
	}

	// --- Keys (milestone 5.5) ---------------------------------------------------------------------

	/// <summary>
	/// The rebindable rows: one line each, with a button per bound key (click it to change that key),
	/// an "x" to remove it and a "+" to add another — an action may hold as many keys as the creator
	/// wants. The capture is fed from the host's own input hook (Builder._Input / Terminal._Input),
	/// not from ImGui, because the addon consumes key events while a text field has focus — the same
	/// seam the code-editor clipboard bridge uses.
	/// </summary>
	private static void DrawKeysTab(Reporter report)
	{
		ImGui.Text("Click a key to change it, x to remove it, + to add another key.");
		ImGui.Separator();

		IReadOnlyList<KeyBinding> rows = KeyBindingService.Current.Rows;
		for (int i = 0; i < rows.Count; i++)
		{
			KeyBinding row = rows[i];
			bool rowCapturing = KeyBindingService.IsCapturingRow(row.Id);

			ImGui.PushID(row.Id);
			ImGui.Text(row.Label);
			ImGui.SameLine(220f, 8f);

			if (row.ChordCount == 0 && !rowCapturing)
			{
				ImGui.TextDisabled("(unbound)");
				ImGui.SameLine(0f, 8f);
			}

			// One control per bound key: the key itself (click = replace) plus its remove button.
			for (int c = 0; c < row.ChordCount; c++)
			{
				ImGui.PushID(c);
				DrawChordSlot(row, c, report);
				ImGui.PopID();
			}

			// The "+" (and the cancel for the key it would add).
			bool appending = KeyBindingService.IsCapturing(row.Id, row.ChordCount);
			ImGui.PushID(row.ChordCount);
			if (appending)
			{
				ImGui.TextDisabled("Press a key…");
				ImGui.SameLine(0f, 4f);
				if (ImGui.Button("Cancel")) KeyBindingService.CancelCapture();
			}
			else if (ImGui.SmallButton("+"))
			{
				KeyBindingService.BeginCapture(row.Id, row.ChordCount);
			}
			ImGui.PopID();

			ImGui.PopID();
		}

		ImGui.Separator();
		if (ImGui.Button("Restore default keys"))
		{
			KeyBindingService.RestoreDefaults();
			report(false, "Key bindings restored to their defaults.");
		}
		ImGui.TextDisabled("Gameplay keys (movement, jump) take plain keys; the editor chords may use Ctrl/Shift/Alt.");
	}

	/// <summary>One bound key of a row: click to replace it, "x" to remove it.</summary>
	private static void DrawChordSlot(KeyBinding row, int index, Reporter report)
	{
		if (KeyBindingService.IsCapturing(row.Id, index))
		{
			ImGui.TextDisabled("Press a key…");
			ImGui.SameLine(0f, 4f);
			if (ImGui.Button("Cancel")) KeyBindingService.CancelCapture();
			ImGui.SameLine(0f, 8f);
			return;
		}

		KeyChord chord = row.ChordAt(index);
		if (ImGui.Button(chord.Format())) KeyBindingService.BeginCapture(row.Id, index);
		ImGui.SameLine(0f, 2f);
		if (ImGui.SmallButton("x"))
		{
			KeyBindingService.RemoveChord(row.Id, index, out string removed);
			report(false, removed);
		}
		ImGui.SameLine(0f, 8f);
	}

}
