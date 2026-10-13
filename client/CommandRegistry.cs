
using System;
using System.Collections.Generic;

/// <summary>
/// The terminal's command surface: a name -> command table any component can register into —
/// the terminal's own set today (<see cref="TerminalCommands"/>), packages and plugins later
/// (§13.1/§18.2). The engine is deliberately generic (no Terminal, no ImGui, no Godot types),
/// so it is unit-testable headless: see <see cref="CommandSelfTest"/>.
/// </summary>
public class CommandRegistry
{
	public struct Command
	{
		public string Name;
		public string Description;
		public Action<string[]> Handler;
		/// <summary>First-argument vocabulary for ghost auto-complete; null when the command
		/// takes no sub-commands.</summary>
		public string[] Subcommands;
		/// <summary>Whether this command needs an unlocked account (§5.1's auth gate). Defaults to
		/// true — a command is locked unless it is explicitly declared open — so a command a package
		/// registers later cannot accidentally appear before login.</summary>
		public bool RequiresLogin;
	}

	private readonly Dictionary<string, Command> registry = new Dictionary<string, Command>();
	// Registration order: Help, completion and suggestion output iterate this instead of the
	// dictionary, so they stay deterministic even after an Unregister (a dictionary's
	// enumeration order is not a contract once entries are removed).
	private readonly List<string> registrationOrder = new List<string>();
	// Stub vocabulary for names that are not commands yet (the §5.1 parity stubs `theme` and
	// `avatar`): the ghost completion behaved for them before this refactor, so it still does.
	private readonly Dictionary<string, string[]> stubVocabulary = new Dictionary<string, string[]>();
	private readonly Action<string> logSink;

	public CommandRegistry(Action<string> log)
	{
		logSink = log;
	}

	public IEnumerable<string> GetRegisteredCommandNames() => registrationOrder;

	/// <summary>Every registered command, in registration order (the `help` handler and any
	/// future listing UI read this).</summary>
	public IEnumerable<Command> All
	{
		get
		{
			for (int i = 0; i < registrationOrder.Count; i++)
			{
				yield return registry[registrationOrder[i]];
			}
		}
	}

	/// <summary>Declares a command. A duplicate name is ignored (first wins), matching the
	/// pre-refactor behaviour. <paramref name="subcommands"/> is the first-argument vocabulary
	/// the ghost suggestion completes against (null when there is none).
	/// <paramref name="requiresLogin"/> is the §5.1 auth-gate flag; it defaults to true (locked),
	/// so only the pre-login set passes false explicitly.</summary>
	public void RegisterCommand(string name, string description, Action<string[]> handler,
		string[] subcommands = null, bool requiresLogin = true)
	{
		if (string.IsNullOrEmpty(name)) return;
		name = name.ToLower();
		if (registry.ContainsKey(name)) return;
		registry.Add(name, new Command
		{
			Name = name,
			Description = description,
			Handler = handler,
			Subcommands = subcommands,
			RequiresLogin = requiresLogin
		});
		registrationOrder.Add(name);
	}

	/// <summary>
	/// Looks a command up without running it. The auth gate needs the command's
	/// <see cref="Command.RequiresLogin"/> before dispatch, and the alias expander needs to know
	/// whether the head token is a real command (a real command always wins over an alias name).
	/// </summary>
	public bool TryGetCommand(string name, out Command command)
	{
		command = default;
		if (string.IsNullOrEmpty(name)) return false;
		return registry.TryGetValue(name.ToLower(), out command);
	}

	public bool HasCommand(string name)
	{
		return !string.IsNullOrEmpty(name) && registry.ContainsKey(name.ToLower());
	}

	/// <summary>
	/// The §5.1 auth gate's whole rule, kept a pure function so it is verifiable headless: every
	/// command needs an unlocked account except the ones registered with RequiresLogin = false
	/// (<c>help</c>, <c>clear</c>, <c>login</c>, <c>register</c>).
	///
	/// The gate runs in <c>Terminal.ExecuteSubmittedCommand</c>, not in <see cref="Execute"/> — an
	/// internal delegation (sysinfo -> neofetch) is already inside a gated call and must not be
	/// re-gated.
	/// </summary>
	public static bool IsAllowed(bool isLoggedIn, Command command)
	{
		return isLoggedIn || !command.RequiresLogin;
	}

	/// <summary>Removes a command by name — the counterpart packages need at uninstall
	/// (§18.2). False when it was not registered.</summary>
	public bool Unregister(string name)
	{
		if (string.IsNullOrEmpty(name)) return false;
		name = name.ToLower();
		if (!registry.Remove(name)) return false;
		registrationOrder.Remove(name);
		return true;
	}

	/// <summary>Registers ghost-completion vocabulary for a name that is NOT a command yet
	/// (§5.1's `theme`/`avatar` stubs); a registered command's own Subcommands take precedence.</summary>
	public void RegisterSubCommandVocabulary(string commandName, string[] options)
	{
		if (string.IsNullOrEmpty(commandName)) return;
		stubVocabulary[commandName.ToLower()] = options;
	}

	public void Execute(string rawInput)
	{
		if (string.IsNullOrWhiteSpace(rawInput)) return;

		List<string> tokens = CommandParser.Tokenize(rawInput);
		if (tokens.Count == 0) return;

		string commandName = tokens[0].ToLower();
		string[] args = new string[tokens.Count - 1];
		for (int i = 1; i < tokens.Count; i++)
		{
			args[i - 1] = tokens[i];
		}

		if (registry.TryGetValue(commandName, out Command cmd))
		{
			cmd.Handler.Invoke(args);
		}
		else if (logSink != null)
		{
			logSink($"Error: Command '{commandName}' not found. Type 'help' for a list of commands.");
		}
	}

	public string GetSubCommandSuggestion(string baseCommand, string partialArg)
	{
		baseCommand = baseCommand.ToLower();
		partialArg = partialArg.ToLower();

		if (string.IsNullOrEmpty(partialArg)) return "";
		string[] options = null;
		if (registry.TryGetValue(baseCommand, out Command command)) options = command.Subcommands;
		if (options == null) stubVocabulary.TryGetValue(baseCommand, out options);
		if (options == null) return "";

		foreach (string option in options)
		{
			if (option.StartsWith(partialArg) && option != partialArg)
			{
				return option.Substring(partialArg.Length);
			}
		}
		return "";
	}

	// Returns the untyped remainder of the first registered command whose name
	// starts with the given partial (case-insensitive), e.g. "ope" -> "nlot".
	// Empty when no command matches or the partial is already a full name.
	public string GetCommandNameCompletion(string partialName)
	{
		string partial = partialName.ToLower();
		foreach (string name in registrationOrder)
		{
			if (name.StartsWith(partial) && name != partial)
			{
				return name.Substring(partial.Length);
			}
		}
		return "";
	}
}
