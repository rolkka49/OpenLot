using System;

/// <summary>
/// The terminal's built-in command set, kept apart from <see cref="CommandRegistry"/> so the
/// registry stays a generic engine any component can register into (the post-3.11 friction
/// review): packages and plugins use the same surface later (§13.1/§18.2). Each registration
/// carries its description, its handler, its first-argument vocabulary for ghost auto-complete
/// (when it has one), and its auth-gate flag.
///
/// The handlers close over the <see cref="Terminal"/> instance they were registered for, exactly
/// as they did inside the registry before; nothing about their behaviour changed in the move.
/// </summary>
public static class TerminalCommands
{
	/// <summary>
	/// Commands that work before this device's account is unlocked (§5.1's auth gate). Everything
	/// else is locked until <c>login</c>/<c>register</c> has run. <c>unregister</c> is here on
	/// purpose: the case it exists for — a lost key — leaves the device locked out, so it must be
	/// reachable without signing in. Kept as data so the gate's flags cannot drift apart from the
	/// list, and so the list itself is verifiable headless (the Terminal node cannot be instantiated
	/// in a suite).
	/// </summary>
	public static readonly string[] PreLoginCommands = { "help", "clear", "login", "register", "unregister" };

	private static bool RequiresLogin(string name) => Array.IndexOf(PreLoginCommands, name) < 0;

	public static void RegisterCore(CommandRegistry registry, Terminal terminal)
	{
		// Every core registration goes through this helper so the pre-login whitelist above is the
		// single source of truth for the gate: a command added here is locked by default.
		void Register(string name, string description, Action<string[]> handler, string[] subcommands = null)
		{
			registry.RegisterCommand(name, description, handler, subcommands, RequiresLogin(name));
		}

		Register("help", "Lists all available commands.", (args) =>
		{
			terminal.LogLine("Available commands:");
			foreach (CommandRegistry.Command cmd in registry.All)
			{
				terminal.LogLine($"- {cmd.Name}: {cmd.Description}");
			}
		});

		Register("openlot", "Core OpenLot system commands (join, host, list, create).", (args) =>
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
			else if (subCommand == "list")
			{
				// The same rows `ls` prints, in the navigable TUI window.
				terminal.ShowLotList();
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

		Register("ls", "Lists the lots available on this device.", (args) => terminal.ListLots());

		Register("clear", "Clears the terminal output.", (args) => terminal.Clear());

		Register("alias", "Defines a command shortcut (alias <name>=<command>; alias -r <name> removes).",
			(args) => terminal.HandleAliasCommand(args));

		Register("wallpaper", "Sets the desktop wallpaper behind the UI (no argument opens the file browser).", (args) =>
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

		Register("theme", "Lists or applies UI themes, colours and fonts (theme <preset> | theme foreground|background <color> | theme font [console] <name>).",
			(args) => terminal.HandleThemeCommand(args), new string[] { "foreground", "background", "font" });

		Register("avatar", "Avatar controls. Placeholder until rigged characters land (milestone 7.1).",
			(args) => terminal.HandleAvatarCommand(args), new string[] { "list", "equip" });

		Register("neofetch", "Displays system and platform information.", (args) =>
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

		// Internal delegation, deliberately through the registry: neofetch is itself a registered
		// command, and this call is already inside sysinfo's own gated dispatch (§5.1).
		Register("sysinfo", "Displays system information.", (args) => registry.Execute("neofetch"));

		Register("motd", "Prints the Message of the Day.", (args) =>
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

		Register("ping", "Pings the local network hub.", (args) =>
		{
			long fakePing = Godot.GD.Randi() % 77 + 12;
			terminal.LogLine($"Pong! Reply from local_node: time={fakePing}ms TTL=64");
		});

		Register("matrix", "Wake up, Neo...", (args) =>
		{
			terminal.LogLine("01001111 01010000 01000101 01001110 01001100 01001111 01010100");
			terminal.LogLine("Analyzing host grid... Connection secure.");
			terminal.LogLine("You are in the lot now.");
		});

		Register("register", "Creates this device's OpenLot account (generates its key).", (args) =>
		{
			if (args.Length == 0) { terminal.LogLine("Usage: register <display name>"); return; }
			terminal.RegisterNewAccount(string.Join(" ", args));
		});

		Register("login", "Unlocks this device's account, or imports one from its 50-character key.", (args) =>
		{
			if (args.Length == 0) { terminal.LoginStoredAccount(); return; }
			terminal.ImportAccount(args[0], args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1) : "");
		});

		Register("unregister", "Deletes this device's account (the key cannot be recovered). Requires 'unregister confirm'.", (args) =>
			terminal.UnregisterAccount(args), new string[] { "confirm" });

		Register("logout", "Terminates the active session.", (args) => terminal.LogoutUser());
		Register("disconnect", "Terminates the active physical lot connection.", (args) => terminal.DisconnectAndUnload());

		// `theme` and `avatar` were ghost-completion-only stubs until this pass; they are real
		// commands now (above), so their standalone vocabulary registrations are gone with them.
	}
}
