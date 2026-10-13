using System.Collections.Generic;
using Godot;

/// <summary>
/// The client's live key bindings (§5.5). A static holder around one <see cref="KeyBindings"/> —
/// the same shape as <see cref="ThemeService"/> — plus the two things the rest of the client needs
/// from it: the pure editor-command resolver the builder and the terminal route their key handling
/// through, and the "press a key" capture the settings window drives.
///
/// Gameplay rows are pushed into Godot's InputMap through <see cref="LotInputActions.Rebind"/>, which
/// is §3.9's single input route: one rebind moves the character, the freecam and every script
/// together. Editor rows are matched directly, so the Ctrl+S/Ctrl+Z rules cannot drift between the
/// two hosts.
/// </summary>
public static class KeyBindingService
{
	/// <summary>Settable seam: the settings file the bindings are remembered in.</summary>
	public static string SettingsPath { get; set; } = KeyBindings.DefaultSettingsPath;

	public static KeyBindings Current { get; private set; } = new KeyBindings();

	/// <summary>True once <see cref="LoadPersistedSettings"/> has run (diagnostics / tests).</summary>
	public static bool Ready { get; private set; }

	private static string _capturingId = "";
	private static int _capturingIndex = -1;

	/// <summary>True while any key slot is waiting for the creator to press a key.</summary>
	public static bool Capturing { get { return _capturingId.Length > 0; } }

	/// <summary>True while this row has a slot capturing (so the row can show a Cancel).</summary>
	public static bool IsCapturingRow(string id) { return _capturingId.Length > 0 && _capturingId == id; }

	/// <summary>True while this exact key slot is capturing. <paramref name="index"/> may be the row's
	/// current count, which is the "+" case (capture a new key to add).</summary>
	public static bool IsCapturing(string id, int index)
	{
		return _capturingId.Length > 0 && _capturingId == id && _capturingIndex == index;
	}

	/// <summary>Starts capturing for one key slot: an existing index replaces that key, the row's
	/// current count appends a new one.</summary>
	public static void BeginCapture(string id, int index)
	{
		_capturingId = id;
		_capturingIndex = index;
	}

	public static void CancelCapture()
	{
		_capturingId = "";
		_capturingIndex = -1;
	}

	/// <summary>Idempotent, so both scenes can call it (the wallpaper/theme/settings do the same).</summary>
	public static void LoadPersistedSettings()
	{
		if (Ready) return;
		Ready = true;
		Current.Load(SettingsPath);
		ApplyToInputMap();
	}

	/// <summary>Pushes every gameplay row's keys into Godot's InputMap (keyboard events only, one
	/// event per key; gamepad bindings stay as registered — pad rebinding is §16.1's). An unbound row
	/// clears the action's keyboard events.</summary>
	public static void ApplyToInputMap()
	{
		IReadOnlyList<KeyBinding> rows = Current.Rows;
		for (int i = 0; i < rows.Count; i++)
		{
			if (!rows[i].Gameplay) continue;
			Key[] keys = new Key[rows[i].ChordCount];
			for (int c = 0; c < keys.Length; c++) keys[c] = rows[i].ChordAt(c).Key;
			LotInputActions.RebindKeys(rows[i].ActionName, keys);
		}
	}

	public static void Save()
	{
		Current.Save(SettingsPath);
	}

	public static void RestoreDefaults()
	{
		Current.RestoreDefaults();
		ApplyToInputMap();
		Save();
	}

	/// <summary>
	/// The editor command a key event triggers (a <see cref="KeyBindingIds"/> value), or "". Pure, so
	/// the mapping is unit-testable without a live scene. Every key of every editor row is checked
	/// (row order first, then the row's key order), so adding a second key to a command is all it
	/// takes for that command to answer to both.
	/// </summary>
	public static string ResolveEditorCommand(InputEventKey e)
	{
		IReadOnlyList<KeyBinding> rows = Current.Rows;
		for (int i = 0; i < rows.Count; i++)
		{
			KeyBinding row = rows[i];
			if (row.Gameplay) continue;
			for (int c = 0; c < row.ChordCount; c++)
			{
				if (row.ChordAt(c).Matches(e)) return row.Id;
			}
		}
		return "";
	}

	/// <summary>The display form of a row's PRIMARY key (its first) — the menus use it, so a menu
	/// accelerator can never disagree with the binding that actually fires. "" when unbound.</summary>
	public static string ChordLabel(string id)
	{
		KeyBinding row = Current.Find(id);
		return row != null && row.ChordCount > 0 ? row.ChordAt(0).Format() : "";
	}

	/// <summary>
	/// Feeds a key event to the capture in progress. Returns true when the event belongs to the
	/// capture (the host must then mark it handled so the key does not also reach the editor); a bare
	/// modifier keeps waiting, and a refused chord still ends the capture with a readable reason.
	/// </summary>
	public static bool TryCapture(InputEventKey e, out bool ok, out string message)
	{
		ok = false;
		message = "";
		if (_capturingId.Length == 0 || e == null || !e.Pressed || e.Echo) return false;

		// A bare modifier is never a binding on its own: keep capturing, consume nothing.
		if (KeyChord.IsModifierKey(e.Keycode)) return true;

		KeyChord chord = new KeyChord(e.Keycode, e.CtrlPressed || e.MetaPressed, e.ShiftPressed, e.AltPressed);
		string id = _capturingId;
		int index = _capturingIndex;
		CancelCapture();
		ok = TryAssign(id, index, chord, out message);
		return true;
	}

	/// <summary>
	/// Assigns a key to one slot of a row (<paramref name="index"/> replaces, the row's count appends
	/// — the "+" case), re-applies the InputMap and writes the settings file. The rules themselves
	/// live in <see cref="KeyBindings.TryAssign"/>.
	/// </summary>
	public static bool TryAssign(string id, int index, KeyChord chord, out string message)
	{
		KeyBinding row = Current.Find(id);
		int existing = row != null ? row.ChordCount : 0;
		if (!Current.TryAssign(id, index, chord, out message)) return false;

		ApplyToInputMap();
		Save();
		message = row.Label + (index < existing ? " now uses " : " now also uses ") + chord.Format() + ".";
		return true;
	}

	/// <summary>Removes one key from a row (the row's "x"), re-applies the InputMap and saves. Any
	/// capture in flight is cancelled, because removing a key can shift the slot it was filling.</summary>
	public static bool RemoveChord(string id, int index, out string message)
	{
		message = "";
		KeyBinding row = Current.Find(id);
		if (row == null || index < 0 || index >= row.ChordCount) return false;

		string removed = row.ChordAt(index).Format();
		CancelCapture();
		Current.TryRemoveChord(id, index);
		ApplyToInputMap();
		Save();
		message = row.ChordCount == 0
			? "Removed " + removed + " — " + row.Label + " is now unbound."
			: "Removed " + removed + " from " + row.Label + ".";
		return true;
	}
}
