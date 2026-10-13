using System.Collections.Generic;
using Godot;

/// <summary>
/// The <c>alias</c> command's table: a name -> command line shortcut, expanded once when the line
/// is submitted. Pure apart from its persistence, which takes an explicit path (the
/// <see cref="AccountStore"/> pattern) so a test drives a scratch file and can never touch the
/// real settings.
///
/// Rules, stated once here (and pinned by <c>CommandSelfTest</c>):
///  - Expansion is ONE level. An alias whose own head token is another alias is never re-expanded,
///    so <c>alias a=b</c> + <c>alias b=a</c> cannot loop.
///  - A real registered command always wins over an alias of the same name. The terminal checks
///    the registry first, and definitions refuse to shadow an existing command, so a shortcut can
///    never silently never fire.
///  - Whatever followed the alias name on the typed line is appended verbatim after the alias's
///    command line: <c>alias ll="openlot list"</c> + <c>ll my-lot</c> runs
///    <c>openlot list my-lot</c>.
/// </summary>
public class AliasTable
{
	/// <summary>
	/// The client settings file the alias block lives in — the same
	/// <c>user://openlot_settings.cfg</c> §2.3's wallpaper uses and §5.4's theme/font/layout blocks
	/// will join, rather than a second config format.
	/// </summary>
	public const string DefaultSettingsPath = "user://openlot_settings.cfg";

	private const string Section = "alias";

	// Order is kept explicitly (like CommandRegistry's registration order) so `alias` lists in the
	// order the creator defined them, which a dictionary's enumeration does not promise.
	private readonly List<string> _order = new List<string>();
	private readonly Dictionary<string, string> _aliases = new Dictionary<string, string>();

	public int Count { get { return _order.Count; } }

	/// <summary>Alias names in definition order.</summary>
	public IReadOnlyList<string> Names { get { return _order; } }

	public bool TryGet(string name, out string commandLine)
	{
		commandLine = "";
		if (string.IsNullOrEmpty(name)) return false;
		return _aliases.TryGetValue(name.ToLowerInvariant(), out commandLine);
	}

	/// <summary>Defines (or redefines) an alias. Redefinition keeps the original slot in the list.</summary>
	public void Define(string name, string commandLine)
	{
		if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(commandLine)) return;
		name = name.ToLowerInvariant();
		if (!_aliases.ContainsKey(name)) _order.Add(name);
		_aliases[name] = commandLine;
	}

	/// <summary>Removes an alias. False when it was not defined.</summary>
	public bool Remove(string name)
	{
		if (string.IsNullOrEmpty(name)) return false;
		name = name.ToLowerInvariant();
		if (!_aliases.Remove(name)) return false;
		_order.Remove(name);
		return true;
	}

	/// <summary>
	/// Parses <c>alias &lt;name&gt;=&lt;command&gt;</c> from the tokenized arguments. Both spellings
	/// work: <c>alias ll="openlot list"</c> arrives as one token (the tokenizer strips the quotes)
	/// and <c>alias ll=openlot list</c> arrives as several, whose tail is joined onto the value.
	/// </summary>
	public static bool TryParseDefinition(string[] args, out string name, out string commandLine)
	{
		name = "";
		commandLine = "";
		if (args == null || args.Length == 0) return false;

		string head = args[0];
		int equals = head.IndexOf('=');
		if (equals <= 0) return false;

		name = head.Substring(0, equals).Trim();
		string value = head.Substring(equals + 1).Trim();

		if (args.Length > 1)
		{
			string tail = string.Join(" ", args, 1, args.Length - 1);
			value = value.Length == 0 ? tail : value + " " + tail;
		}

		if (name.Length == 0 || value.Length == 0) return false;
		commandLine = value;
		return true;
	}

	/// <summary>
	/// The whole line an alias expands to: the alias's command line plus everything the creator
	/// typed after the alias name (spacing preserved).
	/// </summary>
	public static string CombineLine(string commandLine, string typedLine, string aliasToken)
	{
		if (typedLine == null || typedLine.Length <= aliasToken.Length) return commandLine;
		return commandLine + typedLine.Substring(aliasToken.Length);
	}

	/// <summary>Loads the <c>[alias]</c> block. A missing file or block is simply an empty table.</summary>
	public void Load(string path)
	{
		_order.Clear();
		_aliases.Clear();
		if (string.IsNullOrEmpty(path) || !Godot.FileAccess.FileExists(path)) return;

		ConfigFile config = new ConfigFile();
		if (config.Load(path) != Error.Ok) return;
		if (!config.HasSection(Section)) return;

		foreach (string key in config.GetSectionKeys(Section))
		{
			string value = config.GetValue(Section, key, "").AsString();
			if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value)) Define(key, value);
		}
	}

	/// <summary>
	/// Writes the <c>[alias]</c> block. Re-reads the file first so keys another writer owns (the
	/// wallpaper's, and later §5.4's theme/font/layout blocks) are never dropped by our save.
	/// </summary>
	public void Save(string path)
	{
		if (string.IsNullOrEmpty(path)) return;

		ConfigFile config = new ConfigFile();
		if (Godot.FileAccess.FileExists(path)) config.Load(path);
		if (config.HasSection(Section)) config.EraseSection(Section);

		for (int i = 0; i < _order.Count; i++)
		{
			config.SetValue(Section, _order[i], _aliases[_order[i]]);
		}

		Error result = config.Save(path);
		if (result != Error.Ok) GD.PushWarning("[Alias] could not save aliases (" + result + ")");
	}
}
