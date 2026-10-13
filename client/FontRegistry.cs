using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// Discovers the fonts this client can use and remembers which two are selected (milestone 5.4's
/// font clause). Pure apart from reading directories and its own config path, which is a parameter
/// — so a suite points it at scratch folders and a scratch file (the <see cref="AliasTable"/> /
/// <see cref="AccountStore"/> pattern).
///
/// Two slots, because one font for everything is a worse default than a clean UI face plus a
/// terminal face:
///  - <b>UI</b> — menus, panels, dialogs, the creation environment.
///  - <b>Console</b> — the terminal's scrollback and prompt. Left unset it follows the UI slot.
///
/// Sources, in priority order (a bundled font with the same id as a drop-in wins):
///  1. <c>res://assets/fonts</c> — the curated CC0 set the repo ships (see
///     <c>assets/fonts/LICENSE.md</c> and <see cref="FontCatalog"/>).
///  2. <c>user://fonts</c> — anything the user drops in; appears as a <see cref="FontCatalog.CustomCategory"/>
///     entry with no code change. A future §18 font pack installs here, so it needs no new path.
///
/// The built-in is always present as id <see cref="BuiltInId"/> ("Default (ImGui)"), so a chosen
/// font can always be undone and an untouched install looks exactly as it did before.
/// </summary>
public class FontRegistry
{
	public const string BuiltInId = "default";
	public const string BuiltInDisplayName = "Default (ImGui)";

	/// <summary>Shared client settings file — the same one the wallpaper and alias blocks live in.</summary>
	public const string DefaultSettingsPath = "user://openlot_settings.cfg";

	public const string BundledDir = "res://assets/fonts";
	public const string UserDir = "user://fonts";

	private const string Section = "font";
	private const string UiKey = "ui";
	private const string ConsoleKey = "console";

	private static readonly string[] Extensions = { ".ttf", ".otf" };

	public struct FontEntry
	{
		public string Id;
		public string DisplayName;
		public string Category;
		public string Path;      // "" for the built-in (nothing to load)
		public bool BuiltIn;
	}

	private readonly List<FontEntry> _entries = new List<FontEntry>();
	private readonly Dictionary<string, FontEntry> _byId = new Dictionary<string, FontEntry>();

	private string _ui = BuiltInId;
	private string _console = "";    // "" = follow the UI slot

	/// <summary>Every selectable font: the built-in first, then category order, then name.</summary>
	public IReadOnlyList<FontEntry> Entries { get { return _entries; } }

	/// <summary>Entries in one category, in list order (the settings sections walk this).</summary>
	public List<FontEntry> EntriesInCategory(string category)
	{
		List<FontEntry> result = new List<FontEntry>();
		for (int i = 0; i < _entries.Count; i++)
		{
			if (_entries[i].BuiltIn) continue;
			if (string.Equals(_entries[i].Category, category, StringComparison.Ordinal)) result.Add(_entries[i]);
		}
		return result;
	}

	public string UiSelection { get { return _ui; } }

	/// <summary>The console slot's own value, which is "" while it follows the UI slot.</summary>
	public string ConsoleSelection { get { return _console; } }

	/// <summary>The font the console actually uses (its own choice, or the UI slot's).</summary>
	public string EffectiveConsole { get { return _console.Length > 0 ? _console : _ui; } }

	public int BundledCount
	{
		get
		{
			int count = 0;
			for (int i = 0; i < _entries.Count; i++) if (!_entries[i].BuiltIn && FontCatalog.IsBundled(_entries[i].Id)) count++;
			return count;
		}
	}

	/// <summary>
	/// Rescans both folders and rebuilds the list. Deterministic: built-in first, then category
	/// order (<see cref="FontCatalog.CategoryOrder"/>), then display name.
	/// </summary>
	public void Scan(string bundledDir, string userDir)
	{
		_entries.Clear();
		_byId.Clear();

		AddBuiltIn();
		CollectFrom(bundledDir);
		CollectFrom(userDir);

		_entries.Sort((a, b) =>
		{
			if (a.BuiltIn != b.BuiltIn) return a.BuiltIn ? -1 : 1;
			int byCategory = FontCatalog.CategoryRank(a.Category).CompareTo(FontCatalog.CategoryRank(b.Category));
			if (byCategory != 0) return byCategory;
			int byName = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
			return byName != 0 ? byName : string.CompareOrdinal(a.Id, b.Id);
		});

		for (int i = 0; i < _entries.Count; i++) _byId[_entries[i].Id] = _entries[i];
		ValidateSelections();
	}

	// A selection whose file has gone (or a hand-edited config) falls back rather than leaving the
	// UI with a font it cannot load. A no-op before the first Scan, when nothing is known yet.
	private void ValidateSelections()
	{
		if (_byId.Count == 0) return;
		if (!IsKnown(_ui)) _ui = BuiltInId;
		if (_console.Length > 0 && !IsKnown(_console)) _console = "";
	}

	private void AddBuiltIn()
	{
		_entries.Add(new FontEntry
		{
			Id = BuiltInId,
			DisplayName = BuiltInDisplayName,
			Category = "Built-in",
			Path = "",
			BuiltIn = true
		});
	}

	private void CollectFrom(string directory)
	{
		if (string.IsNullOrEmpty(directory)) return;

		using DirAccess access = DirAccess.Open(directory);
		if (access == null) return; // a folder that does not exist yet is simply empty

		access.ListDirBegin();
		while (true)
		{
			string name = access.GetNext();
			if (string.IsNullOrEmpty(name)) break;
			if (access.CurrentIsDir()) continue;

			string extension = System.IO.Path.GetExtension(name).ToLowerInvariant();
			if (Array.IndexOf(Extensions, extension) < 0) continue;

			string id = System.IO.Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
			if (id.Length == 0 || id == BuiltInId) continue;
			// First id wins, and the bundled folder is scanned first — so a drop-in sharing a
			// bundled font's id never shadows it. Within one folder the first file wins too, since
			// directory order is not a contract and the list must stay stable either way.
			if (ContainsId(_entries, id)) continue;

			_entries.Add(new FontEntry
			{
				Id = id,
				DisplayName = FontCatalog.DisplayName(id),
				Category = FontCatalog.Category(id),
				Path = directory.TrimEnd('/') + "/" + name,
				BuiltIn = false
			});
		}
		access.ListDirEnd();
	}

	private static bool ContainsId(List<FontEntry> entries, string id)
	{
		for (int i = 0; i < entries.Count; i++) if (entries[i].Id == id) return true;
		return false;
	}

	public bool IsKnown(string id)
	{
		return !string.IsNullOrEmpty(id) && _byId.ContainsKey(id.ToLowerInvariant());
	}

	public bool TryGet(string id, out FontEntry entry)
	{
		entry = default;
		if (string.IsNullOrEmpty(id)) return false;
		return _byId.TryGetValue(id.ToLowerInvariant(), out entry);
	}

	/// <summary>
	/// Resolves a typed font name to an id: the id itself (<c>jupiteroid-regular</c>) or the display
	/// name from the settings list ("Jupiteroid"), case-insensitively either way. `theme font` uses
	/// this so a creator can type what they see rather than a file stem.
	/// </summary>
	public bool TryResolve(string token, out string id)
	{
		id = "";
		if (string.IsNullOrWhiteSpace(token)) return false;

		string trimmed = token.Trim();
		string lowered = trimmed.ToLowerInvariant();
		if (_byId.ContainsKey(lowered))
		{
			id = lowered;
			return true;
		}

		for (int i = 0; i < _entries.Count; i++)
		{
			if (string.Equals(_entries[i].DisplayName, trimmed, StringComparison.OrdinalIgnoreCase))
			{
				id = _entries[i].Id;
				return true;
			}
		}
		return false;
	}

	/// <summary>Points the UI slot at a font. False for an id this client does not know.</summary>
	public bool SetUi(string id)
	{
		if (!IsKnown(id)) return false;
		_ui = id.ToLowerInvariant();
		return true;
	}

	/// <summary>Points the console slot at a font, or at nothing ("") to follow the UI slot.</summary>
	public bool SetConsole(string id)
	{
		if (id == null || id.Length == 0) { _console = ""; return true; }
		if (!IsKnown(id)) return false;
		_console = id.ToLowerInvariant();
		return true;
	}

	// --- persistence (explicit path, so a suite never touches the real settings) ---

	public void Load(string path)
	{
		_ui = BuiltInId;
		_console = "";
		if (string.IsNullOrEmpty(path) || !Godot.FileAccess.FileExists(path)) return;

		ConfigFile config = new ConfigFile();
		if (config.Load(path) != Error.Ok) return;
		if (!config.HasSection(Section)) return;

		string ui = config.GetValue(Section, UiKey, BuiltInId).AsString();
		string console = config.GetValue(Section, ConsoleKey, "").AsString();
		_ui = string.IsNullOrEmpty(ui) ? BuiltInId : ui.ToLowerInvariant();
		_console = console.Length == 0 ? "" : console.ToLowerInvariant();
		ValidateSelections();
	}

	/// <summary>
	/// Restores the two selections to the built-in. The caller still scans; kept explicit so the
	/// restore action has something honest to call.
	/// </summary>
	public void ResetSelections()
	{
		_ui = BuiltInId;
		_console = "";
	}

	public void Save(string path)
	{
		if (string.IsNullOrEmpty(path)) return;

		ConfigFile config = new ConfigFile();
		// Re-read first: the wallpaper, alias and (later) theme/window-style blocks share this file.
		if (Godot.FileAccess.FileExists(path)) config.Load(path);

		// Rewrite the block from scratch, so a console slot that went back to "follow the UI" stops
		// being stored rather than lingering as a stale id. (Erasing the last key drops the section,
		// which the SetValue below then recreates.)
		if (config.HasSection(Section)) config.EraseSection(Section);
		config.SetValue(Section, UiKey, _ui);
		if (_console.Length > 0) config.SetValue(Section, ConsoleKey, _console);

		Error result = config.Save(path);
		if (result != Error.Ok) GD.PushWarning("[Fonts] could not save the font selection (" + result + ")");
	}
}
