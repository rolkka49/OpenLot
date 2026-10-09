using Godot;
using System;
using System.Collections.Generic;

public partial class Terminal : Node2D
{
	private enum UIState { MainMenu, Console, ServerList, Settings }
	private UIState currentState = UIState.MainMenu;

	// One entry per console row: the text plus the ImGui color it renders with.
	// Color is baked into the row at LogLine time so the draw loop stays allocation-free.
	private struct ConsoleLine
	{
		public string Text;
		public Color Color;
	}

	private List<ConsoleLine> consoleLines = new List<ConsoleLine>();
	private string inputBuffer = "";
	private int cursorIndex = 0;

	// --- Terminal palette: per-role colors used by LogLine classification ---
	private Color foregroundColor = new Color(0.9f, 0.9f, 0.9f);
	private Color backgroundColor = new Color(0.06f, 0.06f, 0.06f);
	private Color errorColor = new Color(0.95f, 0.35f, 0.35f);
	private Color warningColor = new Color(0.95f, 0.75f, 0.3f);
	private Color successColor = new Color(0.4f, 0.9f, 0.45f);
	private Color systemColor = new Color(0.5f, 0.75f, 1.0f);
	private Color echoColor = new Color(0.55f, 0.55f, 0.55f);
	private Color ghostColor = new Color(0.45f, 0.45f, 0.45f);

	// --- Ghost auto-complete state ---
	// Suggestion echoed after the typed text, e.g. typing "ope" shows "ope" + dimmed "nlot".
	// Holds only the untyped remainder, which is exactly what Tab commits.
	private string ghostSuffix = "";

	private string[] mockServers = new string[]
	{
		"LocalLot - Bare Platform Node",
		"Lot #302 - Alpine Heights",
		"Lot #105 - Neon Sandbox",
		"Lot #440 - Cyber Lounge"
	};

	private CommandRegistry registry;

	public bool IsLoggedIn { get; private set; } = false;
	private string currentUser = "";

	public override void _Ready()
	{
		registry = new CommandRegistry(this);

		// Pin the content-scale mode before the first layout, so the whole ImGui overlay renders
		// 1:1 at true window pixels and never scales/blurs with the window (see DisplaySetup).
		DisplaySetup.NormalizeStretch();

		// Restore the remembered wallpaper before the first layout draws. Idempotent, so entering
		// the creation environment later will not re-run it.
		WallpaperService.LoadPersistedSettings();

		// Subscribe to Shatadev's ImGui layout event. The overlay renders 1:1 over the window
		// because the content-scale stretch is disabled (see DisplaySetup); layout coordinates are
		// therefore true window pixels and ImGui never scales with the window.
		ImGui.OnLayout(OnLayout);

		LogLine("Welcome to OpenLot v0.0.1");
		LogLine("Type 'help' to begin or 'login <user>' to unlock.");
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventKey keyEvent && keyEvent.Pressed)
		{
			// Toggle terminal overlay on ~ (Grave / Quoteleft)
			if (keyEvent.Keycode == Key.Quoteleft)
			{
				currentState = currentState == UIState.Console ? UIState.MainMenu : UIState.Console;
				return;
			}

			// Capture keyboard typing directly when inside the console view
			if (currentState == UIState.Console)
			{
				// Enter / Numpad Enter -> Submit command
				if (keyEvent.Keycode == Key.Enter || keyEvent.Keycode == Key.KpEnter)
				{
					ExecuteSubmittedCommand();
					return;
				}

				// Backspace -> Erase character behind cursor
				if (keyEvent.Keycode == Key.Backspace)
				{
					if (inputBuffer.Length > 0 && cursorIndex > 0)
					{
						inputBuffer = inputBuffer.Remove(cursorIndex - 1, 1);
						cursorIndex--;
						RefreshGhostSuggestion();
					}
					return;
				}

				// Left Arrow -> Move cursor left
				if (keyEvent.Keycode == Key.Left)
				{
					cursorIndex = Mathf.Clamp(cursorIndex - 1, 0, inputBuffer.Length);
					return;
				}

				// Right Arrow -> Move cursor right
				if (keyEvent.Keycode == Key.Right)
				{
					cursorIndex = Mathf.Clamp(cursorIndex + 1, 0, inputBuffer.Length);
					return;
				}

				// Tab -> Accept the pending ghost suggestion (no-op when none is
				// pending). Tab's unicode (9) never passes the printable-character
				// check below, so echo repeats are inert by themselves.
				if (keyEvent.Keycode == Key.Tab)
				{
					AcceptGhostSuggestion();
					return;
				}

				// Append printable unicode characters to input buffer
				if (keyEvent.Unicode > 31 && keyEvent.Unicode < 127)
				{
					char c = (char)keyEvent.Unicode;
					inputBuffer = inputBuffer.Insert(cursorIndex, c.ToString());
					cursorIndex++;
					RefreshGhostSuggestion();
				}
			}
		}
	}

	public override void _ExitTree()
	{
		// The ImGui autoload outlives this scene; without disconnecting, its layout signal keeps
		// invoking into this freed node every frame after a scene switch.
		ImGui.DisconnectLayout(OnLayout);
	}

	private void OnLayout()
	{
		// Draws the wallpaper (when one is set) and then the dockspace. With no wallpaper this is
		// exactly the previous DockspaceOverMainViewport() call, so the default look is unchanged.
		WallpaperService.DrawBackgroundAndDockspace();

		switch (currentState)
		{
			case UIState.MainMenu:
				DrawMainMenu();
				break;
			case UIState.Console:
				DrawConsoleWindow();
				break;
			case UIState.ServerList:
				DrawServerListWindow();
				break;
			case UIState.Settings:
				DrawSettingsWindow();
				break;
		}
	}

	private void DrawMainMenu()
	{
		ImGui.SetNextWindowPos(400, 200, ImGui.CondFirstUseEver);
		ImGui.SetNextWindowSize(300, 250, ImGui.CondFirstUseEver);

		// ImGui requires a matching End() for every Begin(), even when Begin()
		// returns false (collapsed or docked-away window). Skipping it corrupts
		// the native window stack and trips an ImGui assert -> hard crash.
		bool open = ImGui.Begin("OpenLot", EditorChrome.PanelWindowFlags);
		if (!open)
		{
			ImGui.End();
			return;
		}

		ImGui.Spacing();
		ImGui.Text("      OpenLot Terminal      ");
		ImGui.Separator();
		ImGui.Spacing();

		if (ImGui.Button("OpenLot List", 280, 35))
		{
			currentState = UIState.ServerList;
		}

		ImGui.Spacing();
		if (ImGui.Button("Console", 280, 35))
		{
			currentState = UIState.Console;
		}

		ImGui.Spacing();
		if (ImGui.Button("Settings", 280, 35))
		{
			currentState = UIState.Settings;
		}

		ImGui.End();
	}

	private void DrawConsoleWindow()
	{
		ImGui.SetNextWindowSize(700, 450, ImGui.CondFirstUseEver);

		bool open = ImGui.Begin("Terminal Console", EditorChrome.PanelWindowFlags);
		if (!open)
		{
			ImGui.End();
			return;
		}

		if (ImGui.Button("<- Return to Main Menu"))
		{
			currentState = UIState.MainMenu;
		}

		ImGui.Separator();

		// Main terminal viewport taking up full window space. BeginChild() returning
		// false means "skip the body", and ImGui allows skipping EndChild() there
		// (unlike Begin()/End(), which must always be paired).
		if (ImGui.BeginChild("ScrollingRegion", 0, 0))
		{
			// Themed console surface: child background from the palette.
			ImGui.PushStyleColor(ImGui.ColChildBg, backgroundColor);

			// Render past console output, one color push/pop per line.
			for (int i = 0; i < consoleLines.Count; i++)
			{
				ImGui.PushStyleColor(ImGui.ColText, consoleLines[i].Color);
				ImGui.Text(consoleLines[i].Text);
				ImGui.PopStyleColor();
			}

			DrawPromptLine();

			// Keep scroll anchored to the bottom as you type or receive logs
			ImGui.SetScrollHereY(1.0f);

			// This pop must happen *inside* the child, before EndChild(). The addon
			// calls ImGui's ErrorCheckEndWindowRecover() in end_child(), which silently
			// pops any style color still open down to the size recorded when the child
			// began. Leaving the push open across EndChild() means the recovery pops it,
			// and this pop then underflows the native color stack -> abort with no error.
			ImGui.PopStyleColor();

			ImGui.EndChild();
		}

		ImGui.End();
	}

	// Renders the active prompt line. Without a pending suggestion this is the
	// plain echo line with the '_' caret marker; with one, the typed text is
	// followed on the same line by the dimmed ghost continuation.
	private void DrawPromptLine()
	{
		string typedText = inputBuffer.Substring(0, cursorIndex);

		if (ghostSuffix.Length == 0)
		{
			ImGui.PushStyleColor(ImGui.ColText, echoColor);
			ImGui.Text($"> {typedText}_{inputBuffer.Substring(cursorIndex)}");
			ImGui.PopStyleColor();
			return;
		}

		ImGui.PushStyleColor(ImGui.ColText, echoColor);
		ImGui.Text($"> {typedText}");
		ImGui.PopStyleColor();

		ImGui.SameLine();
		ImGui.PushStyleColor(ImGui.ColText, ghostColor);
		ImGui.Text(ghostSuffix);
		ImGui.PopStyleColor();
	}

	private void DrawServerListWindow()
	{
		ImGui.SetNextWindowSize(500, 300, ImGui.CondFirstUseEver);

		bool open = ImGui.Begin("Active OpenLot Server List", EditorChrome.PanelWindowFlags);
		if (!open)
		{
			ImGui.End();
			return;
		}

		if (ImGui.Button("<- Back")) currentState = UIState.MainMenu;
		ImGui.Separator();

		for (int i = 0; i < mockServers.Length; i++)
		{
			if (ImGui.Button($"Join: {mockServers[i]}", 460, 30))
			{
				LogLine($"Connecting to node {mockServers[i]}...");
				currentState = UIState.Console;
			}
		}

		ImGui.End();
	}

	private void DrawSettingsWindow()
	{
		ImGui.SetNextWindowSize(400, 200, ImGui.CondFirstUseEver);

		bool open = ImGui.Begin("Settings", EditorChrome.PanelWindowFlags);
		if (!open)
		{
			ImGui.End();
			return;
		}

		if (ImGui.Button("<- Back")) currentState = UIState.MainMenu;
		ImGui.Separator();

		ImGui.Text("Theme Configurations");
		if (ImGui.Button("Green Matrix"))
		{
			ApplyGreenMatrixTheme();
			LogLine("Theme set to Green Matrix.", successColor);
		}
		ImGui.SameLine();
		if (ImGui.Button("Amber Retro"))
		{
			ApplyAmberRetroTheme();
			LogLine("Theme set to Amber Retro.", successColor);
		}

		ImGui.End();
	}

	private void ExecuteSubmittedCommand()
	{
		if (string.IsNullOrWhiteSpace(inputBuffer)) return;

		LogLine($"> {inputBuffer}", echoColor);
		registry.Execute(inputBuffer);
		inputBuffer = "";
		cursorIndex = 0;
		RefreshGhostSuggestion();
	}

	// --- GHOST AUTO-COMPLETE ---

	// Recomputes the ghost suggestion from the current input buffer. Called on
	// every edit (insert/backspace/accept/submit), not per frame, so the
	// suggestion always matches the buffer being drawn.
	private void RefreshGhostSuggestion()
	{
		ghostSuffix = "";

		// Ghosts only continue the end of the line; with the caret parked
		// mid-buffer there is nothing sensible to append after it.
		if (cursorIndex != inputBuffer.Length) return;

		string partialToken = CommandParser.GetActivePartialToken(inputBuffer);
		if (partialToken.Length == 0) return;

		// First token -> complete the command name; later tokens -> complete the
		// argument of the command that owns the line.
		bool completingCommandName = !inputBuffer.Contains(' ');
		string completion = completingCommandName
			? registry.GetCommandNameCompletion(partialToken)
			: registry.GetSubCommandSuggestion(CommandParser.Tokenize(inputBuffer)[0], partialToken);

		ghostSuffix = completion;
	}

	// Commits the pending ghost completion at the cursor, then refreshes so a
	// follow-up suggestion (e.g. an argument for the completed command) can appear.
	private void AcceptGhostSuggestion()
	{
		if (ghostSuffix.Length == 0) return;

		// The typed partial token already sits in the buffer up to the cursor, so
		// only the untyped remainder is inserted. Inserting the whole completion
		// would duplicate the partial (typing "ope" then Tab gave "opeopenlot").
		inputBuffer = inputBuffer.Insert(cursorIndex, ghostSuffix);
		cursorIndex += ghostSuffix.Length;
		RefreshGhostSuggestion();
	}

	// --- HELPER STUBS FOR COMMAND REGISTRY ---

	public void StartLotCreation()
	{
		LogLine("System: Loading Builder assets and initializing creation environment...");
		// Enter the build-mode scene (same flow as the Unity build: terminal scene → builder scene).
		GetTree().ChangeSceneToFile("res://builder/builder.tscn");
	}

	// --- Wallpaper (the 2D chrome behind the UI, not the lot's 3D sky) ---

	/// <summary>Opens the wallpaper file browser. This node is the dialog's tree parent.</summary>
	public void StartWallpaperPicker() => WallpaperService.RequestFileDialog(this);

	/// <summary>Drops the wallpaper and returns to the default background.</summary>
	public void ClearWallpaper() => WallpaperService.Clear();

	public void InitiatePasswordPrompt(string username, bool isRegister)
	{
		if (isRegister)
		{
			IsLoggedIn = true;
			currentUser = username;
			LogSuccess($"Account '{username}' registered and logged in!");
		}
		else
		{
			IsLoggedIn = true;
			currentUser = username;
			LogSuccess($"Welcome back, {username}. Session unlocked.");
		}
	}

	public void LogoutUser()
	{
		IsLoggedIn = false;
		currentUser = "";
		LogSuccess("Logged out successfully. System locked.");
	}

	public void DisconnectAndUnload()
	{
		LogLine("Disconnecting... Returning to main menu.");
		currentState = UIState.MainMenu;
	}

	public void SetForegroundColor(Color color)
	{
		// Applies to future lines; already-logged lines keep their baked color.
		foregroundColor = color;
		LogLine($"Foreground color updated to {color}.");
	}

	public void SetBackgroundColor(Color color)
	{
		backgroundColor = color;
		LogLine($"Background color updated to {color}.");
	}

	public void SetAlias(string shortcut, string fullCommand) => LogLine($"Alias created: '{shortcut}' -> '{fullCommand}'.");

	// Logs a line in the default foreground color, classifying common message
	// prefixes (Error:/Warning:/System:) into their palette roles so plain
	// callers get sane colors without knowing the palette.
	public void LogLine(string message)
	{
		LogLine(message, ClassifyLineColor(message));
	}

	// Logs a line with an explicit color, for call sites that already know the
	// semantic role of the message.
	public void LogLine(string message, Color color)
	{
		consoleLines.Add(new ConsoleLine { Text = message, Color = color });
	}

	public void LogError(string message) => LogLine(message, errorColor);
	public void LogWarning(string message) => LogLine(message, warningColor);
	public void LogSuccess(string message) => LogLine(message, successColor);

	private Color ClassifyLineColor(string message)
	{
		if (message.StartsWith("Error:", StringComparison.Ordinal)) return errorColor;
		if (message.StartsWith("Warning:", StringComparison.Ordinal)) return warningColor;
		if (message.StartsWith("System:", StringComparison.Ordinal)) return systemColor;
		return foregroundColor;
	}

	// --- THEME PALETTES ---

	private void ApplyGreenMatrixTheme()
	{
		foregroundColor = new Color(0.25f, 0.9f, 0.35f);
		backgroundColor = new Color(0.02f, 0.05f, 0.02f);
		errorColor = new Color(1.0f, 0.3f, 0.3f);
		warningColor = new Color(0.6f, 0.9f, 0.3f);
		successColor = new Color(0.4f, 1.0f, 0.5f);
		systemColor = new Color(0.5f, 1.0f, 0.7f);
		echoColor = new Color(0.2f, 0.6f, 0.3f);
		ghostColor = new Color(0.15f, 0.5f, 0.25f);
	}

	private void ApplyAmberRetroTheme()
	{
		foregroundColor = new Color(1.0f, 0.72f, 0.25f);
		backgroundColor = new Color(0.07f, 0.045f, 0.01f);
		errorColor = new Color(1.0f, 0.35f, 0.15f);
		warningColor = new Color(1.0f, 0.85f, 0.3f);
		successColor = new Color(0.95f, 0.9f, 0.4f);
		systemColor = new Color(1.0f, 0.8f, 0.55f);
		echoColor = new Color(0.65f, 0.45f, 0.15f);
		ghostColor = new Color(0.55f, 0.4f, 0.15f);
	}

	public void Clear()
	{
		consoleLines.Clear();
	}
}
