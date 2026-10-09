
using System;
using System.Collections.Generic;

public class CommandRegistry
{
	public struct Command
	{
		public string Name;
		public string Description;
		public Action<string[]> Handler;
	}

	private Dictionary<string, Command> registry = new Dictionary<string, Command>();
	private Terminal outputConsole;

	public CommandRegistry(Terminal output)
	{
		outputConsole = output;
		RegisterDefaultCommands();
	}

	public IEnumerable<string> GetRegisteredCommandNames() => registry.Keys;

	public void RegisterCommand(string name, string description, Action<string[]> handler)
	{
		name = name.ToLower();
		if (!registry.ContainsKey(name))
		{
			registry.Add(name, new Command { Name = name, Description = description, Handler = handler });
		}
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
		else
		{
			outputConsole.LogLine($"Error: Command '{commandName}' not found. Type 'help' for a list of commands.");
		}
	}

	private void RegisterDefaultCommands()
	{
		RegisterCommand("help", "Lists all available commands.", (args) =>
		{
			outputConsole.LogLine("Available commands:");
			foreach (var cmd in registry.Values)
			{
				outputConsole.LogLine($"- {cmd.Name}: {cmd.Description}");
			}
		});

		RegisterCommand("openlot", "Core OpenLot system commands (join, host, create).", (args) =>
		{
			if (args.Length == 0)
			{
				outputConsole.LogLine("Usage: openlot <join|host|list|create> [id]");
				return;
			}

			string subCommand = args[0].ToLower();
			if (subCommand == "join" && args.Length > 1)
			{
				outputConsole.LogLine($"Attempting to connect to lot ID: {args[1]}...");
			}
			else if (subCommand == "host" && args.Length > 1)
			{
				outputConsole.LogLine($"Starting local host server for lot: {args[1]}...");
			}
			else if (subCommand == "create")
			{
				outputConsole.StartLotCreation();
			}
			else
			{
				outputConsole.LogLine("Invalid openlot arguments. Usage: openlot <join|host|list|create>");
			}
		});

		RegisterCommand("clear", "Clears the terminal output.", (args) => outputConsole.Clear());

		RegisterCommand("wallpaper", "Sets the desktop wallpaper behind the UI (no argument opens the file browser).", (args) =>
		{
			string sub = args.Length > 0 ? args[0].ToLower() : "set";

			if (sub == "clear")
			{
				outputConsole.ClearWallpaper();
				outputConsole.LogLine("Wallpaper cleared. Default background restored.");
				return;
			}

			if (sub == "status")
			{
				string assetId = WallpaperService.AssetId;
				outputConsole.LogLine(assetId.Length > 0
					? $"Wallpaper: {assetId}"
					: "Wallpaper: none (default background).");
				return;
			}

			if (sub == "set")
			{
				outputConsole.LogLine("Pick an image in the file browser that just opened.");
				outputConsole.StartWallpaperPicker();
				return;
			}

			outputConsole.LogLine("Usage: wallpaper [set|clear|status]  (no argument opens the file browser)");
		});

		RegisterCommand("neofetch", "Displays system and platform information.", (args) =>
		{
			ulong uptime = Godot.Time.GetTicksMsec() / 1000;
			outputConsole.LogLine(
				"      .xX######Xx.          OS: OpenLot Shell Alpha v0.0.1\n" +
				"  .xX*'   _.._   '*Xx.      Kernel: Godot 4.7 (.NET)\n" +
				" =======( [██] )=======     Uptime: " + uptime + "s\n" +
				"  '*Xx.   ~~~~   .xX*'      Shell: Pish\n" +
				"      '*Xx######xX*'       Client Platform: Linux/CachyOS\n" +
				" OpenLot Terminal"
			);
		});

		RegisterCommand("sysinfo", "Displays system information.", (args) => Execute("neofetch"));

		RegisterCommand("motd", "Prints the Message of the Day.", (args) =>
		{
			string[] quotes = new string[]
			{
				"\"The world is a lot. Build yours.\"",
				"\"Decentralized, self-hosted, and free. Always.\"",
				"\"No platform taxes here, creator.\"",
				"\"sudo rm -rf /corporate_greed\""
			};
			string randomQuote = quotes[Godot.GD.Randi() % quotes.Length];
			outputConsole.LogLine($"MOTD: {randomQuote}");
		});

		RegisterCommand("ping", "Pings the local network hub.", (args) =>
		{
			long fakePing = Godot.GD.Randi() % 77 + 12;
			outputConsole.LogLine($"Pong! Reply from local_node: time={fakePing}ms TTL=64");
		});

		RegisterCommand("matrix", "Wake up, Neo...", (args) =>
		{
			outputConsole.LogLine("01001111 01010000 01000101 01001110 01001100 01001111 01010100");
			outputConsole.LogLine("Analyzing host grid... Connection secure.");
			outputConsole.LogLine("You are in the lot now.");
		});

		RegisterCommand("register", "Registers a local system account.", (args) =>
		{
			if (outputConsole.IsLoggedIn)
			{
				outputConsole.LogLine("Error: You are already logged in.");
				return;
			}
			if (args.Length < 1) { outputConsole.LogLine("Usage: register <username>"); return; }
			outputConsole.InitiatePasswordPrompt(args[0], isRegister: true);
		});

		RegisterCommand("login", "Logs into your local profile.", (args) =>
		{
			if (outputConsole.IsLoggedIn)
			{
				outputConsole.LogLine("System: You are already logged in.");
				return;
			}
			if (args.Length < 1) { outputConsole.LogLine("Usage: login <username>"); return; }
			outputConsole.InitiatePasswordPrompt(args[0], isRegister: false);
		});

		RegisterCommand("logout", "Terminates the active session.", (args) => outputConsole.LogoutUser());
		RegisterCommand("disconnect", "Terminates the active physical lot connection.", (args) => outputConsole.DisconnectAndUnload());
	}

	// First-argument vocabulary per command; single source of truth for
	// sub-command suggestions and ghost auto-complete.
	private static readonly Dictionary<string, string[]> subCommandOptions = new Dictionary<string, string[]>
	{
		{ "openlot", new string[] { "join", "host", "list", "create" } },
		{ "theme", new string[] { "foreground", "background" } },
		{ "wallpaper", new string[] { "set", "clear", "status" } },
		{ "avatar", new string[] { "list", "equip" } },
	};

	public string GetSubCommandSuggestion(string baseCommand, string partialArg)
	{
		baseCommand = baseCommand.ToLower();
		partialArg = partialArg.ToLower();

		if (string.IsNullOrEmpty(partialArg)) return "";
		if (!subCommandOptions.TryGetValue(baseCommand, out string[] options)) return "";

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
		foreach (string name in registry.Keys)
		{
			if (name.StartsWith(partial) && name != partial)
			{
				return name.Substring(partial.Length);
			}
		}
		return "";
	}
}
