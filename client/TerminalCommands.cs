using System;

/// <summary>
/// The terminal's built-in command set, kept apart from <see cref="CommandRegistry"/> so the
/// registry stays a generic engine any component can register into (the post-3.11 friction
/// review): packages and plugins use the same surface later (§13.1/§18.2). Each registration
/// carries its description, its handler, and — when it has one — its first-argument vocabulary
/// for ghost auto-complete.
///
/// The handlers close over the <see cref="Terminal"/> instance they were registered for, exactly
/// as they did inside the registry before; nothing about their behaviour changed in the move.
/// </summary>
public static class TerminalCommands
{
	public static void RegisterCore(CommandRegistry registry, Terminal terminal)
	{
		registry.RegisterCommand("help", "Lists all available commands.", (args) =>
		{
			terminal.LogLine("Available commands:");
			foreach (CommandRegistry.Command cmd in registry.All)
			{
				terminal.LogLine($"- {cmd.Name}: {cmd.Description}");
			}
		});

		registry.RegisterCommand("openlot", "Core OpenLot system commands (join, host, create).", (args) =>
		{
			if (args.Length == 0)
			{
				terminal.LogLine("Usage: openlot <join|host|list|create> [id]");
				return;
			}

			string subCommand = args[0].ToLower();
			if (subCommand == "join" && args.Length > 1)
			{
				terminal.LogLine($"Attempting to connect to lot ID: {args[1]}...");
			}
			else if (subCommand == "host" && args.Length > 1)
			{
				terminal.LogLine($"Starting local host server for lot: {args[1]}...");
			}
			else if (subCommand == "create")
			{
				terminal.StartLotCreation();
			}
			else
			{
				terminal.LogLine("Invalid openlot arguments. Usage: openlot <join|host|list|create>");
			}
		}, new string[] { "join", "host", "list", "create" });

		registry.RegisterCommand("clear", "Clears the terminal output.", (args) => terminal.Clear());

		registry.RegisterCommand("wallpaper", "Sets the desktop wallpaper behind the UI (no argument opens the file browser).", (args) =>
		{
			string sub = args.Length > 0 ? args[0].ToLower() : "set";

			if (sub == "clear")
			{
				terminal.ClearWallpaper();
				terminal.LogLine("Wallpaper cleared. Default background restored.");
				return;
			}

			if (sub == "status")
			{
				string assetId = WallpaperService.AssetId;
				terminal.LogLine(assetId.Length > 0
					? $"Wallpaper: {assetId}"
					: "Wallpaper: none (default background).");
				return;
			}

			if (sub == "set")
			{
				terminal.LogLine("Pick an image in the file browser that just opened.");
				terminal.StartWallpaperPicker();
				return;
			}

			terminal.LogLine("Usage: wallpaper [set|clear|status]  (no argument opens the file browser)");
		}, new string[] { "set", "clear", "status" });

		registry.RegisterCommand("neofetch", "Displays system and platform information.", (args) =>
		{
			ulong uptime = Godot.Time.GetTicksMsec() / 1000;
			terminal.LogLine(
				"      .xX######Xx.          OS: OpenLot Shell Alpha v0.0.1\n" +
				"  .xX*'   _.._   '*Xx.      Kernel: Godot 4.7 (.NET)\n" +
				" =======( [██] )=======     Uptime: " + uptime + "s\n" +
				"  '*Xx.   ~~~~   .xX*'      Shell: Pish\n" +
				"      '*Xx######xX*'       Client Platform: Linux/CachyOS\n" +
				" OpenLot Terminal"
			);
		});

		registry.RegisterCommand("sysinfo", "Displays system information.", (args) => registry.Execute("neofetch"));

		registry.RegisterCommand("motd", "Prints the Message of the Day.", (args) =>
		{
			string[] quotes = new string[]
			{
				"\"The world is a lot. Build yours.\"",
				"\"Decentralized, self-hosted, and free. Always.\"",
				"\"No platform taxes here, creator.\"",
				"\"sudo rm -rf /corporate_greed\""
			};
			string randomQuote = quotes[Godot.GD.Randi() % quotes.Length];
			terminal.LogLine($"MOTD: {randomQuote}");
		});

		registry.RegisterCommand("ping", "Pings the local network hub.", (args) =>
		{
			long fakePing = Godot.GD.Randi() % 77 + 12;
			terminal.LogLine($"Pong! Reply from local_node: time={fakePing}ms TTL=64");
		});

		registry.RegisterCommand("matrix", "Wake up, Neo...", (args) =>
		{
			terminal.LogLine("01001111 01010000 01000101 01001110 01001100 01001111 01010100");
			terminal.LogLine("Analyzing host grid... Connection secure.");
			terminal.LogLine("You are in the lot now.");
		});

		registry.RegisterCommand("register", "Creates this device's OpenLot account (generates its key).", (args) =>
		{
			if (args.Length == 0) { terminal.LogLine("Usage: register <display name>"); return; }
			terminal.RegisterNewAccount(string.Join(" ", args));
		});

		registry.RegisterCommand("login", "Unlocks this device's account, or imports one from its 50-character key.", (args) =>
		{
			if (args.Length == 0) { terminal.LoginStoredAccount(); return; }
			terminal.ImportAccount(args[0], args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1) : "");
		});

		registry.RegisterCommand("logout", "Terminates the active session.", (args) => terminal.LogoutUser());
		registry.RegisterCommand("disconnect", "Terminates the active physical lot connection.", (args) => terminal.DisconnectAndUnload());

		// Vocabulary for the §5.1 parity stubs — `theme` and `avatar` complete in the ghost today
		// but are not commands yet. Registered as vocabulary only so nothing claims to be a
		// command; when those commands land they move to RegisterCommand's subcommands parameter.
		registry.RegisterSubCommandVocabulary("theme", new string[] { "foreground", "background" });
		registry.RegisterSubCommandVocabulary("avatar", new string[] { "list", "equip" });
	}
}
