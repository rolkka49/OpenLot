using System.Collections.Generic;
using Godot;

/// <summary>
/// One key binding (§5.5): a key plus the Ctrl/Shift/Alt held with it. A value type, so a binding is
/// a snapshot; <see cref="Format"/>/<see cref="TryParse"/> are the settings-file pair and
/// <see cref="Matches"/> is the one test the input handlers and the rebind capture use.
///
/// Ctrl covers Cmd (Meta) too — the convention the code editor's clipboard resolver already uses —
/// so "Ctrl+Z" is the same binding on macOS.
/// </summary>
public struct KeyChord
{
	public Key Key;
	public bool Ctrl;
	public bool Shift;
	public bool Alt;

	public KeyChord(Key key, bool ctrl = false, bool shift = false, bool alt = false)
	{
		Key = key;
		Ctrl = ctrl;
		Shift = shift;
		Alt = alt;
	}

	/// <summary>True for a plain key with no modifier — what a gameplay action requires.</summary>
	public bool IsModifierless { get { return !Ctrl && !Shift && !Alt; } }

	/// <summary>"Ctrl+Shift+Z" — the settings-file and menu form.</summary>
	public string Format()
	{
		string text = "";
		if (Ctrl) text += "Ctrl+";
		if (Shift) text += "Shift+";
		if (Alt) text += "Alt+";
		return text + OS.GetKeycodeString(Key);
	}

	/// <summary>Parses <see cref="Format"/>'s output: Ctrl/Shift/Alt prefixes, then a key name.</summary>
	public static bool TryParse(string text, out KeyChord chord)
	{
		chord = default;
		if (string.IsNullOrWhiteSpace(text)) return false;

		string[] parts = text.Trim().Split('+');
		string keyName = parts[parts.Length - 1].Trim();
		if (keyName.Length == 0) return false;

		bool ctrl = false, shift = false, alt = false;
		for (int i = 0; i < parts.Length - 1; i++)
		{
			switch (parts[i].Trim().ToUpperInvariant())
			{
				case "CTRL":
				case "CMD":
				case "META": ctrl = true; break;
				case "SHIFT": shift = true; break;
				case "ALT": alt = true; break;
				default: return false;
			}
		}

		Key key = OS.FindKeycodeFromString(keyName);
		if (key == Key.None) return false;
		chord = new KeyChord(key, ctrl, shift, alt);
		return true;
	}

	/// <summary>
	/// True when a key event presses exactly this chord. Modifier state is read from the event (the
	/// code-editor clipboard bridge reads it the same way), so the check does not depend on polling
	/// Input and never misses a modifier released in the same frame as the key.
	/// </summary>
	public bool Matches(InputEventKey e)
	{
		if (e == null || !e.Pressed || e.Echo) return false;
		if (e.Keycode != Key) return false;
		bool ctrl = e.CtrlPressed || e.MetaPressed;
		return ctrl == Ctrl && e.ShiftPressed == Shift && e.AltPressed == Alt;
	}

	/// <summary>True when the key is only a modifier — never a binding on its own.</summary>
	public static bool IsModifierKey(Key key)
	{
		return key == Key.Ctrl || key == Key.Shift || key == Key.Alt || key == Key.Meta;
	}
}

/// <summary>The binding ids. Gameplay rows are <c>action.&lt;script name&gt;</c> so the settings file
/// names the same vocabulary a script reads (§3.9).</summary>
public static class KeyBindingIds
{
	public const string ActionPrefix = "action.";
	public const string Save = "editor.save";
	public const string Undo = "editor.undo";
	public const string Redo = "editor.redo";
	public const string Delete = "editor.delete";
	public const string Terminal = "editor.terminal";
}

/// <summary>
/// One rebindable row: the keys currently bound to an action and the defaults it can be restored to.
/// A row holds a LIST because a creator expects several keys on one action — "save" on Ctrl+S and on
/// Enter, jump on Space and on a second key — so binding, matching, conflict checking and the UI all
/// work per key. An empty list is a legitimate state (the creator unbound the action).
/// </summary>
public sealed class KeyBinding
{
	public string Id;
	public string Label;

	/// <summary>Gameplay rows are pushed into Godot's InputMap (so a rebind moves the character, the
	/// freecam and every script together); editor rows are read by the builder and the terminal
	/// directly through <see cref="KeyChord.Matches"/>.</summary>
	public bool Gameplay;

	private readonly List<KeyChord> _defaults = new List<KeyChord>();
	private readonly List<KeyChord> _chords = new List<KeyChord>();

	/// <summary>The bound keys, in the order they were added (the first is the primary — the one the
	/// menus display).</summary>
	public IReadOnlyList<KeyChord> Chords { get { return _chords; } }

	public int ChordCount { get { return _chords.Count; } }

	/// <summary>The key at <paramref name="index"/>, or a default chord when out of range.</summary>
	public KeyChord ChordAt(int index)
	{
		return index >= 0 && index < _chords.Count ? _chords[index] : default;
	}

	/// <summary>True when the row holds exactly its default keys, in the default order.</summary>
	public bool IsDefault
	{
		get
		{
			if (_chords.Count != _defaults.Count) return false;
			for (int i = 0; i < _chords.Count; i++)
			{
				if (_chords[i].Key != _defaults[i].Key || _chords[i].Ctrl != _defaults[i].Ctrl
					|| _chords[i].Shift != _defaults[i].Shift || _chords[i].Alt != _defaults[i].Alt)
				{
					return false;
				}
			}
			return true;
		}
	}

	/// <summary>The script-facing action name of a gameplay row ("moveForward"), or "".</summary>
	public string ActionName
	{
		get { return Gameplay ? Id.Substring(KeyBindingIds.ActionPrefix.Length) : ""; }
	}

	internal void AddDefault(KeyChord chord) { _defaults.Add(chord); }

	internal void ResetToDefault()
	{
		_chords.Clear();
		_chords.AddRange(_defaults);
	}

	internal void Clear() { _chords.Clear(); }

	/// <summary>Replaces the key at <paramref name="index"/>, or appends it when the index is at (or
	/// past) the end — which is how the "+" control adds one.</summary>
	internal void Set(int index, KeyChord chord)
	{
		if (index < 0 || index >= _chords.Count)
		{
			_chords.Add(chord);
			return;
		}
		_chords[index] = chord;
	}

	internal void RemoveAt(int index)
	{
		if (index >= 0 && index < _chords.Count) _chords.RemoveAt(index);
	}
}

/// <summary>
/// The rebindable set (§5.5): the seven gameplay actions plus the editor commands, their defaults
/// (exactly the keys that were hardcoded before this milestone, so an untouched install behaves
/// identically), conflict detection, and the <c>[keybindings]</c> block of the shared settings file.
/// A plain instance with an explicit path (the <see cref="FontRegistry"/> pattern), so a suite drives
/// a scratch file; <see cref="KeyBindingService"/> owns the one the client uses.
///
/// Several keys per row: the stored value is a comma-separated list of chords, an empty value means
/// the creator unbound the action (distinguished from a missing key, which means "the default"), and
/// a value whose entries cannot be parsed is treated as unbound rather than silently replaced by the
/// defaults.
/// </summary>
public class KeyBindings
{
	public const string DefaultSettingsPath = "user://openlot_settings.cfg";
	private const string Section = "keybindings";

	private readonly List<KeyBinding> _rows = new List<KeyBinding>();

	public KeyBindings()
	{
		BuildDefaults();
	}

	/// <summary>Every row, gameplay first, in the order the settings tab lists them.</summary>
	public IReadOnlyList<KeyBinding> Rows { get { return _rows; } }

	public KeyBinding Find(string id)
	{
		for (int i = 0; i < _rows.Count; i++)
		{
			if (_rows[i].Id == id) return _rows[i];
		}
		return null;
	}

	private void BuildDefaults()
	{
		_rows.Clear();
		// Gameplay (the exact keys LotInputActions registered before §5.5).
		Add(KeyBindingIds.ActionPrefix + "moveForward", "Move forward", true, new KeyChord(Key.W));
		Add(KeyBindingIds.ActionPrefix + "moveBackward", "Move backward", true, new KeyChord(Key.S));
		Add(KeyBindingIds.ActionPrefix + "moveLeft", "Move left", true, new KeyChord(Key.A));
		Add(KeyBindingIds.ActionPrefix + "moveRight", "Move right", true, new KeyChord(Key.D));
		Add(KeyBindingIds.ActionPrefix + "jump", "Jump", true, new KeyChord(Key.Space));
		Add(KeyBindingIds.ActionPrefix + "sprint", "Sprint", true, new KeyChord(Key.Shift));
		Add(KeyBindingIds.ActionPrefix + "interact", "Interact", true, new KeyChord(Key.E));
		// Editor (the chords the builder and the terminal hardcoded before §5.5). Redo carries BOTH
		// chords it has always accepted — one row, two keys, which is what the old "Redo (alternate)"
		// row was a workaround for.
		Add(KeyBindingIds.Save, "Save lot", false, new KeyChord(Key.S, ctrl: true));
		Add(KeyBindingIds.Undo, "Undo", false, new KeyChord(Key.Z, ctrl: true));
		Add(KeyBindingIds.Redo, "Redo", false,
			new KeyChord(Key.Z, ctrl: true, shift: true), new KeyChord(Key.Y, ctrl: true));
		Add(KeyBindingIds.Delete, "Delete selection", false, new KeyChord(Key.Delete));
		Add(KeyBindingIds.Terminal, "Toggle terminal", false, new KeyChord(Key.Quoteleft));
	}

	private void Add(string id, string label, bool gameplay, params KeyChord[] defaultChords)
	{
		KeyBinding row = new KeyBinding
		{
			Id = id,
			Label = label,
			Gameplay = gameplay,
		};
		for (int i = 0; i < defaultChords.Length; i++) row.AddDefault(defaultChords[i]);
		row.ResetToDefault();
		_rows.Add(row);
	}

	/// <summary>Restores every row to its default keys (one or more).</summary>
	public void RestoreDefaults()
	{
		for (int i = 0; i < _rows.Count; i++) _rows[i].ResetToDefault();
	}

	/// <summary>
	/// The label of the row already using <paramref name="chord"/>, ignoring the slot being assigned —
	/// the conflict rule, refused with a message rather than silently stealing the binding.
	/// <paramref name="sameRow"/> distinguishes "this action already has that key" from a clash with
	/// another action, so the message can say which.
	/// </summary>
	public string FindConflict(string id, int ignoredIndex, KeyChord chord, out bool sameRow)
	{
		sameRow = false;
		for (int r = 0; r < _rows.Count; r++)
		{
			KeyBinding row = _rows[r];
			for (int c = 0; c < row.ChordCount; c++)
			{
				if (row.Id == id && c == ignoredIndex) continue;
				KeyChord other = row.ChordAt(c);
				if (other.Key != chord.Key || other.Ctrl != chord.Ctrl
					|| other.Shift != chord.Shift || other.Alt != chord.Alt)
				{
					continue;
				}
				sameRow = row.Id == id;
				return row.Label;
			}
		}
		return "";
	}

	/// <summary>The display label of a row id (for refusal messages), or the id itself.</summary>
	public string LabelFor(string id)
	{
		KeyBinding row = Find(id);
		return row != null ? row.Label : id;
	}

	/// <summary>
	/// Assigns a key to a row: <paramref name="index"/> replaces that key, or appends one when it is
	/// at (or past) the end — the "+" case. Refuses modifiers on a gameplay action and refuses a key
	/// another slot already uses (naming it) rather than silently stealing it.
	/// </summary>
	public bool TryAssign(string id, int index, KeyChord chord, out string message)
	{
		message = "";
		KeyBinding row = Find(id);
		if (row == null)
		{
			message = "Unknown binding '" + id + "'.";
			return false;
		}

		if (row.Gameplay && !chord.IsModifierless)
		{
			message = row.Label + " cannot use Ctrl/Shift/Alt — movement keys are plain.";
			return false;
		}

		string owner = FindConflict(id, index, chord, out bool sameRow);
		if (owner.Length > 0)
		{
			message = sameRow
				? chord.Format() + " is already on " + owner + "."
				: chord.Format() + " is already used by " + owner + ".";
			return false;
		}

		row.Set(index, chord);
		return true;
	}

	/// <summary>Removes one key from a row. Removing the last one leaves the action unbound.</summary>
	public bool TryRemoveChord(string id, int index)
	{
		KeyBinding row = Find(id);
		if (row == null || index < 0 || index >= row.ChordCount) return false;
		row.RemoveAt(index);
		return true;
	}

	// --- persistence (explicit path, so a suite never touches the real settings) ---

	public void Load(string path)
	{
		RestoreDefaults();
		if (string.IsNullOrEmpty(path) || !Godot.FileAccess.FileExists(path)) return;

		ConfigFile config = new ConfigFile();
		if (config.Load(path) != Error.Ok) return;
		if (!config.HasSection(Section)) return;

		for (int i = 0; i < _rows.Count; i++)
		{
			// A missing key means "the default"; a present (even empty) value is authoritative, so an
			// unbound action stays unbound instead of being silently restored.
			if (!config.HasSectionKey(Section, _rows[i].Id)) continue;
			string text = config.GetValue(Section, _rows[i].Id, "").AsString();
			_rows[i].Clear();
			string[] parts = text.Split(',');
			for (int p = 0; p < parts.Length; p++)
			{
				string part = parts[p].Trim();
				if (part.Length == 0) continue;
				if (KeyChord.TryParse(part, out KeyChord chord)) _rows[i].Set(_rows[i].ChordCount, chord);
			}
		}
	}

	public void Save(string path)
	{
		if (string.IsNullOrEmpty(path)) return;

		ConfigFile config = new ConfigFile();
		// Re-read first: the wallpaper, alias, font, theme, camera and graphics blocks share this file.
		if (Godot.FileAccess.FileExists(path)) config.Load(path);
		if (config.HasSection(Section)) config.EraseSection(Section);

		for (int i = 0; i < _rows.Count; i++)
		{
			// One value per row: the keys as a comma-separated list ("" = unbound). Chords never
			// contain a comma, so the list is unambiguous, and a single-key value written by an older
			// build still parses as a one-entry list.
			KeyBinding row = _rows[i];
			string[] chords = new string[row.ChordCount];
			for (int c = 0; c < row.ChordCount; c++) chords[c] = row.ChordAt(c).Format();
			config.SetValue(Section, row.Id, string.Join(", ", chords));
		}

		Error result = config.Save(path);
		if (result != Error.Ok) GD.PushWarning("[Keys] could not save the key bindings (" + result + ")");
	}
}

