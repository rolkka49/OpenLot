using System.Collections.Generic;
using Godot;

/// <summary>
/// The client's fonts (milestone 5.4's font clause): resolves the two slots through
/// <see cref="FontRegistry"/>, creates the ImGui font handles, and pushes/pops them around a
/// layout pass so the terminal, the builder panels, the dialogs and the Output window all wear the
/// same typography — one global pair, never per-surface drift.
///
/// Two rules this type exists to keep:
///  - <b>Fonts are created during setup, never inside the layout handler.</b> The wrapper documents
///    that, because adding a font rebuilds the ImGui atlas. So the two selected handles are created
///    in <see cref="LoadPersistedSettings"/> (from <c>_Ready</c>), and a change made *while the UI is
///    drawing* is only recorded — <see cref="Tick"/> applies it on the next frame, from <c>_Process</c>.
///  - <b>Zero means the built-in.</b> A handle of 0 pushes nothing, so "Default (ImGui)" is the
///    absence of an override rather than a special case, and an install that never picks a font
///    renders exactly as it did before this milestone.
/// </summary>
public static class FontService
{
	/// <summary>
	/// The pixel size every font is baked at. 13 matches ImGui's built-in default, so choosing a
	/// font changes the typeface and nothing else — no metric shift for an existing layout.
	/// </summary>
	public const float BaseSizePixels = 13f;

	public const string UiSlot = "ui";
	public const string ConsoleSlot = "console";

	public static FontRegistry Registry { get; private set; } = new FontRegistry();

	/// <summary>Settable seam: the settings file the selection is remembered in.</summary>
	public static string SettingsPath { get; set; } = FontRegistry.DefaultSettingsPath;

	/// <summary>True once <see cref="LoadPersistedSettings"/> has run (diagnostics / tests).</summary>
	public static bool Ready { get; private set; }

	private static readonly Dictionary<string, long> _handles = new Dictionary<string, long>
	{
		{ UiSlot, 0L },
		{ ConsoleSlot, 0L }
	};

	private static bool _applyRequested;

	/// <summary>
	/// Scans the font folders, restores the remembered selection and creates the handles. Idempotent,
	/// so the builder can call it after the terminal already did (the wallpaper does the same).
	/// </summary>
	public static void LoadPersistedSettings()
	{
		if (Ready) return;

		Registry.Scan(FontRegistry.BundledDir, FontRegistry.UserDir);
		Registry.Load(SettingsPath);
		ApplyHandles();
		Ready = true;
	}

	/// <summary>
	/// Applies a selection change requested during the last layout pass. Called from each scene's
	/// <c>_Process</c>, i.e. outside the ImGui layout, which is where a font may legally be created.
	/// </summary>
	public static void Tick()
	{
		if (!_applyRequested) return;
		_applyRequested = false;
		ApplyHandles();
	}

	private static void ApplyHandles()
	{
		_handles[UiSlot] = LoadHandle(Registry.UiSelection);
		_handles[ConsoleSlot] = LoadHandle(Registry.EffectiveConsole);
	}

	/// <summary>
	/// Creates one ImGui font handle. Returns 0 (the built-in) when there is nothing to load or the
	/// file cannot be read — a broken font must degrade to readable text, never to no text.
	/// </summary>
	private static long LoadHandle(string id)
	{
		if (string.IsNullOrEmpty(id) || id == FontRegistry.BuiltInId) return 0;
		if (!Registry.TryGet(id, out FontRegistry.FontEntry entry) || entry.Path.Length == 0) return 0;

		long handle = ImGui.AddFontFromFile(entry.Path, BaseSizePixels);
		if (handle == 0)
		{
			GD.PushWarning("[Fonts] could not load '" + id + "' from " + entry.Path + "; using the built-in font");
		}
		return handle;
	}

	/// <summary>
	/// Selects a font for a slot and remembers it. <paramref name="id"/> may be empty for the
	/// console slot, which means "follow the UI font". Returns false (with a readable reason) for an
	/// id this client does not know.
	///
	/// The change takes effect on the next frame — see the class note about creating fonts outside
	/// the layout pass.
	/// </summary>
	public static bool Select(string slot, string id, out string message)
	{
		bool isConsole = slot == ConsoleSlot;

		if (isConsole && string.IsNullOrEmpty(id))
		{
			Registry.SetConsole("");
			Registry.Save(SettingsPath);
			_applyRequested = true;
			message = "Console font now follows the UI font.";
			return true;
		}

		if (!Registry.IsKnown(id))
		{
			message = "Unknown font '" + id + "'.";
			return false;
		}

		if (isConsole) Registry.SetConsole(id); else Registry.SetUi(id);
		Registry.Save(SettingsPath);
		_applyRequested = true;
		message = (isConsole ? "Console font" : "UI font") + " set to " + FontCatalog.DisplayName(id) + ".";
		return true;
	}

	/// <summary>Display name of the font a slot currently uses (for the settings UI and diagnostics).</summary>
	public static string DisplayNameFor(string slot)
	{
		string id = slot == ConsoleSlot ? Registry.EffectiveConsole : Registry.UiSelection;
		return FontCatalog.DisplayName(id);
	}

	/// <summary>True while the console has its own font rather than following the UI one.</summary>
	public static bool ConsoleHasOwnFont { get { return Registry.ConsoleSelection.Length > 0; } }

	// --- push/pop (called from inside the layout pass; no allocation, no loading here) ---

	public static void Push()
	{
		long handle = _handles[UiSlot];
		if (handle != 0) ImGui.PushFont(handle);
	}

	public static void Pop()
	{
		if (_handles[UiSlot] != 0) ImGui.PopFont();
	}

	/// <summary>Pushes the console font — the terminal's scrollback and prompt use it.</summary>
	public static void PushConsole()
	{
		long handle = _handles[ConsoleSlot];
		if (handle != 0) ImGui.PushFont(handle);
	}

	public static void PopConsole()
	{
		if (_handles[ConsoleSlot] != 0) ImGui.PopFont();
	}
}
