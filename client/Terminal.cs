using Godot;
using System;
using System.Collections.Generic;

public partial class Terminal : Node2D
{
	private enum UIState { MainMenu, Console, ServerList, Settings }
	private UIState currentState = UIState.MainMenu;

	// The console's rendered lines: a bounded, drop-oldest ring (post-3.11 review, see
	// ConsoleScrollback) — colors bake in at LogLine time so the draw loop stays allocation-free.
	private readonly ConsoleScrollback _scrollback = new ConsoleScrollback();
	private string inputBuffer = "";
	private int cursorIndex = 0;

	// --- Console text selection ---
	// ImGui draws the console as plain text (not selectable), the vendored addon is a prebuilt Rust
	// binary that exposes no text-measuring call, and ImGui has no selectable-colored-text widget —
	// so selection is built here. Line boxes are analytic (one text item per line), the highlight
	// uses ImGui's own selection color, and the character grid is recovered from per-character
	// advances measured once per font. The model is ConsoleSelection (pure, unit-tested).
	private readonly ConsoleSelection _selection = new ConsoleSelection();
	private readonly Dictionary<char, float> _charAdvance = new Dictionary<char, float>();
	private readonly List<float> _charOffsets = new List<float>(256);
	private string _advanceFontKey = "";
	private bool _advancesUnusable;
	private bool _selecting;

	/// <summary>The line shown when this device holds no usable account (first run, or after a reset).</summary>
	private const string AccountGuidance =
		"Type 'help' to begin, 'register <name>' to create your account, or 'login <key>' to import one from another device.";

	/// <summary>Invisible measurement window — see <see cref="MeasureMissingAdvances"/>.</summary>
	private const string MeasureWindowId = "##openlot_console_measure";

	// Title-bar-only movement for the terminal's windows. ImGui moves a window by dragging anywhere in
	// its body by default, which fights the console's drag-to-select (a drag meant for text moved the
	// window instead). The addon exposes no IO to switch that off, so each window is begun with
	// WindowNoMove and re-granted a title-bar drag — exactly what the hierarchy panel does.
	private readonly TitleBarWindowDrag _mainMenuDrag = new TitleBarWindowDrag();
	private readonly TitleBarWindowDrag _consoleDrag = new TitleBarWindowDrag();
	private readonly TitleBarWindowDrag _serverListDrag = new TitleBarWindowDrag();

	// --- Terminal palette: per-role colors used by LogLine classification ---
	// The palette lives in the shared theme service now (milestone 5.4): one global theme covers the
	// terminal, the builder panels and the dialogs, so these are read-only views of it. The values
	// are the seed palette the fields used to hold, so an untouched install looks identical.
	private Color foregroundColor { get { return ThemeService.Role(ThemeRole.Foreground); } }
	private Color backgroundColor { get { return ThemeService.Role(ThemeRole.Background); } }
	private Color errorColor { get { return ThemeService.Role(ThemeRole.Error); } }
	private Color warningColor { get { return ThemeService.Role(ThemeRole.Warning); } }
	private Color successColor { get { return ThemeService.Role(ThemeRole.Success); } }
	private Color systemColor { get { return ThemeService.Role(ThemeRole.System); } }
	private Color echoColor { get { return ThemeService.Role(ThemeRole.Echo); } }
	private Color ghostColor { get { return ThemeService.Role(ThemeRole.Ghost); } }

	// --- Ghost auto-complete state ---
	// Suggestion echoed after the typed text, e.g. typing "ope" shows "ope" + dimmed "nlot".
	// Holds only the untyped remainder, which is exactly what Tab commits.
	private string ghostSuffix = "";

	// --- Server list (the lot-list TUI) ---
	// Collected when the list is opened, never per frame: walking both lot folders every layout
	// pass would be filesystem work in the render loop.
	private List<LotListing.LotEntry> serverListEntries = new List<LotListing.LotEntry>();
	private int selectedServerIndex = 0;

	// --- Account identity (milestone 3.12a) ---
	// The terminal is a thin shell over the account session: the session owns the flows and their
	// guard rules; the terminal prints their messages and the one-time key reveal.

	/// <summary>
	/// Where this device's account lives. A settable seam (not a constant) so a test or a future
	/// settings surface can point at another store; the terminal reads it once, on first use.
	/// </summary>
	public string AccountFilePath { get; set; } = AccountStore.DefaultPath;

	/// <summary>
	/// Where the client's own settings live — today just the alias block, later the theme, window
	/// style and layout blocks §5.4/§5.6 add. A settable seam like <see cref="AccountFilePath"/> so
	/// a test or a future settings surface can point elsewhere.
	/// </summary>
	public string SettingsFilePath { get; set; } = AliasTable.DefaultSettingsPath;

	private AliasTable aliases = new AliasTable();

	private CommandRegistry registry;

	private AccountSession accountSession;

	/// <summary>
	/// Non-empty while the console is waiting for the display name of an imported account. `login
	/// &lt;key&gt;` with no name asks for one (a key carries the account, never a profile), and the next
	/// submitted line is the answer — `-` accepts a generated name, an empty line cancels. Only the
	/// terminal prompts; <see cref="AccountSession.Import"/> keeps its neutral fallback for
	/// programmatic callers.
	/// </summary>
	private string _pendingImportKey = "";

	/// <summary>True while this device's account is unlocked (the §5.1 auth gate's input).</summary>
	public bool IsLoggedIn { get { return accountSession != null && accountSession.IsUnlocked; } }

	/// <summary>Public account id of the unlocked account, or "" while locked.</summary>
	public string CurrentAccountId { get { return accountSession != null ? accountSession.AccountId : ""; } }

	private AccountSession GetAccountSession()
	{
		if (accountSession == null) accountSession = new AccountSession(AccountFilePath);
		return accountSession;
	}

	public override void _Ready()
	{
		// The registry is a generic engine now; the terminal's own commands register through the
		// same surface a package will use later (§13.1/§18.2). The sink keeps "command not found"
		// flowing through LogLine's color classification exactly as before.
		registry = new CommandRegistry((string message) => LogLine(message));
		TerminalCommands.RegisterCore(registry, this);
		// Remembered shortcuts (§5.1's `alias`). Loaded once at startup; saved whenever the set
		// changes, into the same settings file the wallpaper block lives in.
		aliases.Load(SettingsFilePath);

		// Pin the content-scale mode before the first layout, so the whole ImGui overlay renders
		// 1:1 at true window pixels and never scales/blurs with the window (see DisplaySetup).
		DisplaySetup.NormalizeStretch();

		// Restore the remembered wallpaper before the first layout draws. Idempotent, so entering
		// the creation environment later will not re-run it.
		WallpaperService.LoadPersistedSettings();

		// Restore the remembered fonts (milestone 5.4). Idempotent, like the wallpaper. The font
		// handles are created here rather than on the first layout pass: adding a font rebuilds the
		// ImGui atlas, which the wrapper only allows during setup.
		FontService.LoadPersistedSettings();

		// Restore the remembered theme (milestone 5.4): the role palette, the window shape and the
		// preset. Idempotent and shared with the builder, so whichever scene boots first owns it.
		ThemeService.LoadPersistedSettings();

		// The camera and graphics settings (§5.4) are read here too, so the settings window shows the
		// stored values whether it was opened from the main menu or from the builder.
		CameraSettingsService.LoadPersistedSettings();
		GraphicsSettingsService.LoadPersistedSettings();
		// Remembered key bindings (§5.5) — loading applies them to Godot's InputMap.
		KeyBindingService.LoadPersistedSettings();

		// Subscribe to Shatadev's ImGui layout event. The overlay renders 1:1 over the window
		// because the content-scale stretch is disabled (see DisplaySetup); layout coordinates are
		// therefore true window pixels and ImGui never scales with the window.
		ImGui.OnLayout(OnLayout);

		LogLine("Welcome to OpenLot v0.0.1");

		// Sign in up front when this device already holds an account. The key is stored plaintext in
		// v1 (design doc D6), so there is no secret left to ask for and retyping 'login' every launch
		// protects nothing; 'logout' still locks for the session, and a passphrase is what would make
		// this stop being automatic (that rule lives in AccountSession.UnlocksAtStartup).
		if (AccountSession.UnlocksAtStartup(AccountFilePath))
		{
			AccountSession session = GetAccountSession();
			if (session.Unlock(out string welcome))
			{
				LogSuccess(welcome);
			}
			else
			{
				// The file is present but unusable (a corrupt key, an unreadable file). A launch that
				// cannot sign in must say why AND how to get out of it — otherwise the "signed in" path
				// silently degrades into a locked client with no explanation and no visible way forward.
				LogWarning(welcome);
				LogLine(AccountGuidance);
			}
		}
		else
		{
			LogLine(AccountGuidance);
		}
	}

	public override void _Process(double delta)
	{
		// A font chosen from the settings window (or by `theme font`) is applied here, outside the
		// ImGui layout pass — see FontService on why it cannot be applied where it is chosen.
		FontService.Tick();
	}

	public override void _Input(InputEvent @event)
	{
		// The key-rebind capture (§5.5) is fed here, before ImGui can consume the press — the same
		// seam the builder uses. Escape cancels (it is deliberately not a rebindable chord).
		if (!KeyBindingService.Capturing || !(@event is InputEventKey captureKey) || !captureKey.Pressed) return;

		if (captureKey.Keycode == Key.Escape)
		{
			KeyBindingService.CancelCapture();
		}
		else if (KeyBindingService.TryCapture(captureKey, out bool captured, out string message))
		{
			if (message.Length > 0)
			{
				if (captured) LogSuccess(message);
				else LogError("Error: " + message);
			}
		}

		Viewport viewport = GetViewport();
		if (viewport != null) viewport.SetInputAsHandled();
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventKey keyEvent && keyEvent.Pressed)
		{
			// Toggle the terminal overlay on its bound key (~ by default, rebindable since §5.5).
			if (KeyBindingService.ResolveEditorCommand(keyEvent) == KeyBindingIds.Terminal)
			{
				currentState = currentState == UIState.Console ? UIState.MainMenu : UIState.Console;
				return;
			}

			// The lot list is navigable by keyboard (arrow keys move the highlight, Enter opens the
			// selected lot), so the TUI is usable without reaching for the mouse.
			if (currentState == UIState.ServerList)
			{
				if (keyEvent.Keycode == Key.Up) { MoveServerSelection(-1); return; }
				if (keyEvent.Keycode == Key.Down) { MoveServerSelection(1); return; }
				if (keyEvent.Keycode == Key.Enter || keyEvent.Keycode == Key.KpEnter) { OpenSelectedLot(); return; }
			}

			// Capture keyboard typing directly when inside the console view
			if (currentState == UIState.Console)
			{
				// Clipboard. The console is hand-rolled rather than an ImGui InputText, so the
				// standard editing shortcuts are wired here explicitly: Ctrl+V pastes the OS
				// clipboard at the cursor (which is what makes copying a 50-character key into
				// `login <key>` practical) and Ctrl+Shift+C copies the whole scrollback.
				if (keyEvent.CtrlPressed && keyEvent.Keycode == Key.V)
				{
					PasteFromClipboard();
					return;
				}
				if (keyEvent.CtrlPressed && keyEvent.ShiftPressed && keyEvent.Keycode == Key.C)
				{
					CopyAllOutputToClipboard();
					return;
				}
				// Plain Ctrl+C copies the drag-selection (nothing when there is none — Ctrl+Shift+C
				// above is the "everything" shortcut).
				if (keyEvent.CtrlPressed && !keyEvent.ShiftPressed && keyEvent.Keycode == Key.C)
				{
					CopySelectionToClipboard();
					return;
				}

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
		// A key-rebind capture (§5.5) must not survive this scene either.
		KeyBindingService.CancelCapture();
	}

	private void OnLayout()
	{
		// One shared font for every surface this scene draws (§5.4): pushed for the whole pass and
		// popped at the end. Every draw path below returns normally, so the pair always matches.
		FontService.Push();
		// The shared window shape rides the same pass (pushed inside the font push, popped before it,
		// so the two stacks unwind independently). The default theme pushes nothing at all.
		ThemeService.PushChrome();

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

		FontService.Pop();
		ThemeService.PopChrome();
	}

	private void DrawMainMenu()
	{
		ImGui.SetNextWindowSize(300, 250, ImGui.CondFirstUseEver);
		_mainMenuDrag.BeforeBegin(new Vector2(400f, 200f));

		// ImGui requires a matching End() for every Begin(), even when Begin()
		// returns false (collapsed or docked-away window). Skipping it corrupts
		// the native window stack and trips an ImGui assert -> hard crash.
		bool open = ImGui.Begin("OpenLot", EditorChrome.PanelWindowFlags | ImGui.WindowNoMove);
		_mainMenuDrag.AfterBegin(open);
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
			ShowLotList();
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
		// NoMove + a title-bar-only drag: the console's body is for selecting text (see the field docs).
		_consoleDrag.BeforeBegin();

		bool open = ImGui.Begin("Terminal Console", EditorChrome.PanelWindowFlags | ImGui.WindowNoMove);
		_consoleDrag.AfterBegin(open);
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

		// Main terminal viewport taking up full window space. BeginChild() returning false means
		// "skip the body", but EndChild() is still required — ImGui pairs the two
		// unconditionally, exactly like Begin()/End() (a clipped child is still on the stack;
		// skipping the end trips the addon's guard). Only the body below is conditional.
		// WindowHorizontalScrollbar: long lines (a key reveal, a stack trace, the neofetch block)
		// overflow the width, and without the flag ImGui clips them with no way to reach the rest.
		if (ImGui.BeginChild("ScrollingRegion", 0, 0, false, ImGui.WindowHorizontalScrollbar))
		{
			// Themed console surface: child background from the palette.
			ImGui.PushStyleColor(ImGui.ColChildBg, backgroundColor);
			// The console's own font: it may differ from the UI font (§5.4's console slot), and both
			// pushes stay inside the child — the addon's child-end recovery pops the style stack, so
			// anything pushed here must be popped here too.
			FontService.PushConsole();

			// Selection geometry, taken before anything is drawn. The console is one text item per
			// line, so every line's box is analytic: top = origin.Y + i * step.
			// GetCursorScreenPos() is the ABSOLUTE position the next item is drawn at, so it already
			// carries the window's scroll — the scroll must not be subtracted again here (doing that
			// put every line's box off by the scroll amount on a scrolled console, which is why a
			// selection could never be started). Reading the geometry rather than querying each item
			// also keeps this free on the common path: a frame with no selection makes no extra
			// ImGui call at all.
			Vector2 origin = ImGui.GetCursorScreenPos();
			float lineHeight = ImGui.GetTextLineHeight();
			float step = ImGui.GetTextLineHeightWithSpacing();
			Vector2 panelPos = ImGui.GetWindowPos();
			Vector2 panelSize = ImGui.GetWindowSize();

			// The highlight is laid down first so the text draws over it, exactly like ImGui's own
			// selection background (ColTextSelectedBg) — new behaviour, no new visual language.
			DrawSelectionHighlight(origin, step, lineHeight, panelPos, panelSize);

			// Render past console output, one color push/pop per line. The buffer is a bounded
			// ring (post-3.11 review), so this loop is capped however long the session runs.
			for (int i = 0; i < _scrollback.Count; i++)
			{
				ConsoleScrollback.Line line = _scrollback.At(i);
				ImGui.PushStyleColor(ImGui.ColText, line.Color);
				ImGui.Text(line.Text);
				ImGui.PopStyleColor();
			}

			DrawPromptLine();

			// Drag-select, using the same geometry the highlight was drawn with.
			UpdateSelectionInput(origin, step, panelPos, panelSize);

			// Keep scroll anchored to the bottom as you type or receive logs
			ImGui.SetScrollHereY(1.0f);

			FontService.PopConsole();

			// Right-click the console for the copy actions. Drawn after the console font push is
			// popped so the popup uses the UI font, and ImGui manages the popup's own style stack;
			// the child's style pushes remain balanced around it.
			DrawConsoleContextMenu();

			// This pop must happen *inside* the child, before EndChild(). The addon
			// calls ImGui's ErrorCheckEndWindowRecover() in end_child(), which silently
			// pops any style color still open down to the size recorded when the child
			// began. Leaving the push open across EndChild() means the recovery pops it,
			// and this pop then underflows the native color stack -> abort with no error.
			ImGui.PopStyleColor();
		}

		// EndChild() is unconditional (above): a clipped child is still on the stack, so the
		// pop stays inside the body but the end does not.
		ImGui.EndChild();

		ImGui.End();
	}

	// Renders the active prompt line. Without a pending suggestion this is the
	// plain echo line with the '_' caret marker; with one, the typed text is
	// followed on the same line by the dimmed ghost continuation.
	private void DrawPromptLine()
	{
		// While an import is asking for a display name the prompt says so, so the typed line is not
		// mistaken for a command.
		string prompt = _pendingImportKey.Length > 0 ? "name> " : "> ";
		string typedText = inputBuffer.Substring(0, cursorIndex);

		if (ghostSuffix.Length == 0)
		{
			ImGui.PushStyleColor(ImGui.ColText, echoColor);
			ImGui.Text(prompt + typedText + "_" + inputBuffer.Substring(cursorIndex));
			ImGui.PopStyleColor();
			return;
		}

		ImGui.PushStyleColor(ImGui.ColText, echoColor);
		ImGui.Text(prompt + typedText);
		ImGui.PopStyleColor();

		ImGui.SameLine();
		ImGui.PushStyleColor(ImGui.ColText, ghostColor);
		ImGui.Text(ghostSuffix);
		ImGui.PopStyleColor();
	}

	/// <summary>
	/// The console's right-click menu. ImGui draws the console as plain text, which is not
	/// selectable, so copying is an explicit action here: <b>Copy</b> takes the drag-selection (and is
	/// disabled while there is none), <b>Paste</b> inserts the OS clipboard at the cursor exactly as
	/// Ctrl+V does, and the two "all output"/"last line" items are shortcuts for the common cases.
	/// </summary>
	private void DrawConsoleContextMenu()
	{
		if (!ImGui.BeginPopupContextWindow("ConsoleContext")) return;

		if (ImGui.MenuItem("Copy", "Ctrl+C", false, _selection.HasRange)) CopySelectionToClipboard();
		if (ImGui.MenuItem("Paste", "Ctrl+V")) PasteFromClipboard();
		ImGui.Separator();
		if (ImGui.MenuItem("Copy all output", "Ctrl+Shift+C")) CopyAllOutputToClipboard();
		if (ImGui.MenuItem("Copy last line", "")) CopyLastLineToClipboard();
		ImGui.Separator();
		if (ImGui.MenuItem("Clear", "")) Clear();

		ImGui.EndPopup();
	}

	/// <summary>
	/// Inserts the OS clipboard at the cursor (Ctrl+V). Newlines are flattened to spaces so a
	/// multi-line paste cannot submit a command early or split one in two; the buffer stays a single
	/// prompt line.
	/// </summary>
	private void PasteFromClipboard()
	{
		string clip = DisplayServer.ClipboardGet();
		if (string.IsNullOrEmpty(clip)) return;
		clip = clip.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
		inputBuffer = inputBuffer.Insert(cursorIndex, clip);
		cursorIndex += clip.Length;
		RefreshGhostSuggestion();
	}

	/// <summary>Copies the whole scrollback to the OS clipboard (Ctrl+Shift+C, or the context menu).</summary>
	private void CopyAllOutputToClipboard()
	{
		DisplayServer.ClipboardSet(_scrollback.ToText());
	}

	/// <summary>Copies only the most recent line to the OS clipboard (the context menu's last-line action).</summary>
	private void CopyLastLineToClipboard()
	{
		if (_scrollback.Count == 0) return;
		DisplayServer.ClipboardSet(_scrollback.At(_scrollback.Count - 1).Text);
	}

	// --- Console text selection -------------------------------------------------------------------
	// The draw, hit-test and measurement halves. The range model itself is ConsoleSelection (pure).

	/// <summary>
	/// Paints the selection behind the console text using ImGui's own selection colour, so a
	/// highlight reads exactly like a selection anywhere else in the UI. Drawn before the lines: the
	/// window draw list is ordered, so anything drawn here lands under the glyphs.
	/// </summary>
	private void DrawSelectionHighlight(Vector2 origin, float step, float lineHeight, Vector2 panelPos, Vector2 panelSize)
	{
		if (!_selection.HasRange) return;

		_selection.Ordered(out long firstLine, out int firstChar, out long lastLine, out int lastChar);
		Color color = ImGui.GetStyleColor(ImGui.ColTextSelectedBg);
		if (color.A <= 0f) color = new Color(0.26f, 0.59f, 0.98f, 0.35f);

		long baseId = _scrollback.BaseId;
		float panelBottom = panelPos.Y + panelSize.Y;
		for (int i = 0; i < _scrollback.Count; i++)
		{
			long id = baseId + i;
			if (id < firstLine || id > lastLine) continue;

			float top = origin.Y + i * step;
			float bottom = top + lineHeight;
			if (bottom < panelPos.Y || top > panelBottom) continue;

			ConsoleScrollback.Line line = _scrollback.At(i);
			float left = origin.X;
			float x0 = id == firstLine ? left + PrefixWidth(line.Text, firstChar) : left;
			float x1 = id == lastLine ? left + PrefixWidth(line.Text, lastChar) : left + PrefixWidth(line.Text, line.Text.Length);
			if (x1 < x0) x1 = x0;
			ImGui.DrawRectFilled(new Vector2(x0, top), new Vector2(x1, bottom), color);
		}
	}

	/// <summary>
	/// Turns mouse state into a selection: a press inside the console body anchors it, a held drag
	/// moves the other end (clamped to the log, so a drag past an edge keeps extending), and a
	/// release keeps it — a press with no drag clears it. Raw mouse reads, so it cannot fight ImGui's
	/// own item focus.
	/// </summary>
	private void UpdateSelectionInput(Vector2 origin, float step, Vector2 panelPos, Vector2 panelSize)
	{
		if (_scrollback.Count == 0)
		{
			_selection.Clear();
			_selecting = false;
			return;
		}

		// Exclude the scrollbar strip on the right so dragging it does not also start a selection.
		// (16 px is ImGui's float default; the wrapper exposes no style-var read for the real width.)
		float usableRight = panelPos.X + panelSize.X - 16f;
		Vector2 mouse = ImGui.GetMousePos();
		bool insidePanel = mouse.X >= panelPos.X && mouse.X <= usableRight &&
			mouse.Y >= panelPos.Y && mouse.Y <= panelPos.Y + panelSize.Y;

		if (!_selecting && ImGui.IsMouseClicked(ImGui.MouseButtonLeft) && insidePanel)
		{
			if (PickCharacter(mouse, origin, step, out long lineId, out int character))
			{
				_selection.Begin(lineId, character);
				_selecting = true;
			}
		}
		else if (_selecting)
		{
			if (ImGui.IsMouseDown(ImGui.MouseButtonLeft))
			{
				ExtendSelection(mouse, origin, step);
			}
			else
			{
				_selecting = false;
				// A press with no drag is a caret, not a selection — drop it.
				if (!_selection.HasRange) _selection.Clear();
			}
		}
	}

	/// <summary>
	/// Maps a mouse position to a line + character boundary. The line is clamped into the log, so a
	/// press in the empty space below the output (or part-way onto a clipped line) still anchors to
	/// the nearest line instead of silently doing nothing.
	/// </summary>
	private bool PickCharacter(Vector2 mouse, Vector2 origin, float step, out long lineId, out int character)
	{
		lineId = 0;
		character = 0;
		int index = ConsoleSelection.LineAt(mouse.Y - origin.Y, step, _scrollback.Count);
		if (index < 0) return false;

		ConsoleScrollback.Line line = _scrollback.At(index);
		lineId = _scrollback.BaseId + index;
		character = CharacterAt(line.Text, mouse.X - origin.X);
		return true;
	}

	private void ExtendSelection(Vector2 mouse, Vector2 origin, float step)
	{
		int index = ConsoleSelection.LineAt(mouse.Y - origin.Y, step, _scrollback.Count);
		if (index < 0) return;

		ConsoleScrollback.Line line = _scrollback.At(index);
		float localX = mouse.X - origin.X;
		if (localX < 0f) localX = -1f;   // dragged left past the edge → the line's start
		_selection.Extend(_scrollback.BaseId + index, CharacterAt(line.Text, localX));
	}

	/// <summary>The character boundary nearest a horizontal offset within a line.</summary>
	private int CharacterAt(string text, float localX)
	{
		if (string.IsNullOrEmpty(text)) return 0;
		EnsureAdvances(text);

		_charOffsets.Clear();
		_charOffsets.Add(0f);
		float x = 0f;
		for (int i = 0; i < text.Length; i++)
		{
			x += AdvanceFor(text[i]);
			_charOffsets.Add(x);
		}
		return ConsoleSelection.NearestCharacter(localX, _charOffsets);
	}

	/// <summary>The pixel width of the first <paramref name="count"/> characters of a line.</summary>
	private float PrefixWidth(string text, int count)
	{
		if (string.IsNullOrEmpty(text) || count <= 0) return 0f;
		int limit = count < text.Length ? count : text.Length;
		EnsureAdvances(text);
		float width = 0f;
		for (int i = 0; i < limit; i++) width += AdvanceFor(text[i]);
		return width;
	}

	/// <summary>
	/// One character's advance width. A cache miss falls back to an estimate (never zero) so a failed
	/// measurement still yields a usable, if slightly approximate, selection instead of a degenerate one.
	/// </summary>
	private float AdvanceFor(char c)
	{
		if (_charAdvance.TryGetValue(c, out float advance)) return advance;
		return ImGui.GetFontSize() * 0.55f;
	}

	/// <summary>
	/// Ensures every character of <paramref name="text"/> has a measured advance, measuring what is
	/// missing once. The cache is per font — changing the console font (or a scene restart)
	/// invalidates it — and <see cref="_advancesUnusable"/> latches a bad measurement off so it can
	/// never be retried every frame.
	/// </summary>
	private void EnsureAdvances(string text)
	{
		if (string.IsNullOrEmpty(text) || _advancesUnusable) return;

		string fontKey = FontService.Registry.EffectiveConsole;
		if (fontKey != _advanceFontKey)
		{
			_charAdvance.Clear();
			_advanceFontKey = fontKey;
			_advancesUnusable = false;
		}

		for (int i = 0; i < text.Length; i++)
		{
			if (!_charAdvance.ContainsKey(text[i]))
			{
				MeasureMissingAdvances(text);
				return;
			}
		}
	}

	/// <summary>
	/// Fills the advances the cache is missing by laying the characters out once in an
	/// <b>invisible</b> window and reading their widths back. It is invisible by construction — no
	/// background, no decoration, no inputs, a zero border, and its text pushed to alpha 0 — so it
	/// changes nothing the user sees; it exists only because the vendored addon exposes no
	/// text-measuring call.
	///
	/// Widths are per-character and additive: Dear ImGui lays text out by summing glyph advances with
	/// no kerning, so one measurement per character is exact for every line at that font. Each
	/// character is measured as width("|" + c) - width("|") so a whitespace or otherwise edge-case
	/// character cannot report a trimmed or wrong width. A result that is not a plausible glyph
	/// advance latches measurement off entirely (the estimate in <see cref="AdvanceFor"/> takes over)
	/// rather than being accepted and re-tried forever.
	/// </summary>
	private void MeasureMissingAdvances(string text)
	{
		ImGui.PushStyleVar(ImGui.StyleVarWindowBorderSize, 0f);
		ImGui.PushStyleColor(ImGui.ColBorder, new Color(0f, 0f, 0f, 0f));
		ImGui.SetNextWindowPos(0f, 0f, ImGui.CondAlways);
		ImGui.SetNextWindowBgAlpha(0f);
		int flags = ImGui.WindowNoDecoration | ImGui.WindowNoBackground | ImGui.WindowNoInputs
			| ImGui.WindowNoSavedSettings | ImGui.WindowNoFocusOnAppearing | ImGui.WindowNoBringToFrontOnFocus;

		bool open = ImGui.Begin(MeasureWindowId, flags);
		if (open)
		{
			ImGui.PushStyleColor(ImGui.ColText, new Color(0f, 0f, 0f, 0f));
			FontService.PushConsole();
			float fontSize = ImGui.GetFontSize();
			float measured = 0f;
			for (int i = 0; i < text.Length; i++)
			{
				char c = text[i];
				if (_charAdvance.ContainsKey(c)) continue;
				float reference = MeasureStringWidth("|");
				float advance = MeasureStringWidth("|" + c) - reference;
				// A glyph advance is positive and under a few ems; anything else means the measuring
				// call did not behave as expected, so keep the estimate and stop trying.
				if (advance <= 0f || advance >= fontSize * 4f)
				{
					_advancesUnusable = true;
					break;
				}
				_charAdvance[c] = advance;
				measured += advance;
			}
			if (measured <= 0f) _advancesUnusable = true;
			FontService.PopConsole();
			ImGui.PopStyleColor();
		}
		// End() is always paired with Begin(), even when Begin() returned false.
		ImGui.End();
		ImGui.PopStyleColor();
		ImGui.PopStyleVar();
	}

	private static float MeasureStringWidth(string text)
	{
		ImGui.Text(text);
		return ImGui.GetItemRectSize().X;
	}

	/// <summary>Copies the current selection to the OS clipboard (Ctrl+C, or the context menu).</summary>
	private void CopySelectionToClipboard()
	{
		if (!_selection.HasRange) return;
		_selection.Ordered(out long startLine, out int startChar, out long endLine, out int endChar);
		DisplayServer.ClipboardSet(ConsoleSelection.BuildText(startLine, startChar, endLine, endChar, LineTextFor));
	}

	private string LineTextFor(long id)
	{
		return _scrollback.TryGetAbsolute(id, out ConsoleScrollback.Line line) ? line.Text : "";
	}

	private void DrawServerListWindow()
	{
		ImGui.SetNextWindowSize(500, 300, ImGui.CondFirstUseEver);
		_serverListDrag.BeforeBegin();

		bool open = ImGui.Begin("Active OpenLot Server List", EditorChrome.PanelWindowFlags | ImGui.WindowNoMove);
		_serverListDrag.AfterBegin(open);
		if (!open)
		{
			ImGui.End();
			return;
		}

		if (ImGui.Button("<- Back")) currentState = UIState.MainMenu;
		ImGui.SameLine();
		if (ImGui.Button("Refresh")) RefreshLotList();
		ImGui.Separator();

		if (serverListEntries.Count == 0)
		{
			ImGui.Text("No lots found.");
			ImGui.TextWrapped("Create one with 'openlot create', or drop .lot files into " + LotListing.UserLotsDir + ".");
			ImGui.End();
			return;
		}

		// Keyboard: Up/Down move the highlight, Enter opens (see _UnhandledInput). The rows are
		// the same list `ls` prints, so both surfaces can never disagree about what exists.
		for (int i = 0; i < serverListEntries.Count; i++)
		{
			LotListing.LotEntry entry = serverListEntries[i];
			if (ImGui.Selectable($"{entry.Name}  [{entry.Source}]", i == selectedServerIndex, 0, new Vector2(460f, 0f)))
			{
				selectedServerIndex = i;
			}
		}

		ImGui.Separator();
		if (ImGui.Button("Open Selected", 460, 30)) OpenSelectedLot();

		ImGui.End();
	}

	/// <summary>
	/// The terminal's settings window (milestone 5.4): a thin host around the shared
	/// <see cref="SettingsWindow"/>, so the terminal's main menu and the builder's pause menu draw the
	/// SAME surface (tabs: Theme, Window Style, Camera, Graphics, Fonts). The `theme` and
	/// `theme font` commands are pinned contracts and keep working independently.
	/// </summary>
	private void DrawSettingsWindow()
	{
		if (!SettingsWindow.Draw(ReportSetting)) currentState = UIState.MainMenu;
	}

	/// <summary>Sends a settings message to the console, using the log classifier's own prefixes.</summary>
	private void ReportSetting(bool isError, string message)
	{
		if (isError) LogError("Error: " + message);
		else LogSuccess(message);
	}

	/// <summary>Applies a font choice (or the empty "follow the UI" for the console) and reports it.</summary>
	private void SelectFont(string slot, string id)
	{
		if (FontService.Select(slot, id, out string message)) LogSuccess(message);
		else LogError("Error: " + message);
	}

	private void ExecuteSubmittedCommand()
	{
		// A pending "what should this account be called?" prompt owns the line — including an empty
		// one, which cancels — so it is answered here, before the whitespace guard and any dispatch.
		if (_pendingImportKey.Length > 0)
		{
			string answer = inputBuffer.Trim();
			inputBuffer = "";
			cursorIndex = 0;
			CompletePendingImport(answer);
			RefreshGhostSuggestion();
			return;
		}

		if (string.IsNullOrWhiteSpace(inputBuffer)) return;

		string typed = inputBuffer;
		LogLine("> " + typed, echoColor);
		inputBuffer = "";
		cursorIndex = 0;

		ExecuteGated(ExpandAlias(typed));
		RefreshGhostSuggestion();
	}

	/// <summary>
	/// Applies the alias table to a typed line: ONE expansion, and only when the head token is not a
	/// real command (a real command always wins — see <see cref="AliasTable"/>). The expanded form is
	/// echoed in the ghost color so the creator sees what actually ran.
	/// </summary>
	private string ExpandAlias(string typed)
	{
		if (aliases.Count == 0) return typed;

		List<string> tokens = CommandParser.Tokenize(typed);
		if (tokens.Count == 0) return typed;

		string head = tokens[0];
		if (registry.HasCommand(head)) return typed;
		if (!aliases.TryGet(head, out string commandLine)) return typed;

		string expanded = AliasTable.CombineLine(commandLine, typed, head);
		LogLine("> " + expanded, ghostColor);
		return expanded;
	}

	/// <summary>
	/// Runs a command line through the registry, applying §5.1's auth gate first: the command is
	/// looked up (aliases already expanded) and refused with a readable line when it needs an
	/// unlocked account. An unknown command is passed straight to the registry, so its own
	/// "not found" message keeps flowing through the log sink.
	/// </summary>
	private void ExecuteGated(string commandLine)
	{
		List<string> tokens = CommandParser.Tokenize(commandLine);
		if (tokens.Count == 0) return;

		if (registry.TryGetCommand(tokens[0], out CommandRegistry.Command command) &&
			!CommandRegistry.IsAllowed(IsLoggedIn, command))
		{
			LogError("Error: '" + command.Name + "' requires a logged-in account. Use 'login' or 'register <name>' first.");
			return;
		}

		registry.Execute(commandLine);
	}

	// --- GHOST AUTO-COMPLETE ---

	// Recomputes the ghost suggestion from the current input buffer. Called on
	// every edit (insert/backspace/accept/submit), not per frame, so the
	// suggestion always matches the buffer being drawn.
	private void RefreshGhostSuggestion()
	{
		ghostSuffix = "";

		// No command completion while the console is waiting for an imported account's name: the
		// line is an answer, not a command line.
		if (_pendingImportKey.Length > 0) return;

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

	public void RegisterNewAccount(string displayName)
	{
		AccountSession session = GetAccountSession();
		if (!session.Register(displayName, out string message, out string revealKey))
		{
			LogLine("Error: " + message);
			return;
		}
		LogSuccess(message);
		RevealKeyOnce(revealKey);
	}

	public void LoginStoredAccount()
	{
		AccountSession session = GetAccountSession();
		if (!session.Unlock(out string message))
		{
			LogLine("Error: " + message);
			return;
		}
		LogSuccess(message);
	}

	/// <summary>
	/// Imports an account from its key. The key carries the account only — never a profile — so when
	/// no display name is given the terminal <b>asks</b> for one instead of silently inventing it.
	/// The key and the preconditions are checked first, so a typo (or an import that would be
	/// refused anyway) never starts a prompt.
	/// </summary>
	public void ImportAccount(string keyString, string displayName)
	{
		if (!AccountKey.TryFromKeyString(keyString, out AccountKey key, out string keyError))
		{
			LogLine("Error: " + keyError);
			return;
		}

		AccountSession session = GetAccountSession();

		// Pasting THIS device's own key is a login, not an import: the key is the account, so nothing
		// would be destroyed and no name is needed. Unlock instead of tripping the no-clobber refusal
		// (and never ask for a name for an identity we already hold).
		if (AccountStore.TryLoad(AccountFilePath, out AccountRecord stored, out string _) &&
			stored.Key.AccountId == key.AccountId)
		{
			LogLine("That key is this device's account - unlocking it.");
			LoginStoredAccount();
			return;
		}

		if (!session.CanStoreNewAccount(out string reason))
		{
			LogLine("Error: " + reason);
			return;
		}

		if ((displayName ?? "").Trim().Length == 0)
		{
			_pendingImportKey = keyString;
			LogLine("That key carries no display name - a key is the account, never the profile.");
			LogLine("Type the name this account should use in lots ('-' accepts a generated one, an empty line cancels):");
			return;
		}
		FinishImport(keyString, displayName);
	}

	/// <summary>The second half of the prompt started by <see cref="ImportAccount"/>. Empty cancels.</summary>
	private void CompletePendingImport(string answer)
	{
		string keyString = _pendingImportKey;
		_pendingImportKey = "";

		if (answer.Length == 0)
		{
			LogLine("Import cancelled - the key was not stored.");
			return;
		}
		LogLine("> " + answer, echoColor);
		FinishImport(keyString, answer == "-" ? "" : answer);
	}

	/// <summary>Stores an imported account (or reports the refusal) and unlocks it.</summary>
	private void FinishImport(string keyString, string displayName)
	{
		AccountSession session = GetAccountSession();
		if (!session.Import(keyString, displayName, out string message))
		{
			LogLine("Error: " + message);
			return;
		}
		LogSuccess(message);
	}

	/// <summary>
	/// Deletes this device's account — the "I lost my key" reset. Two-step on purpose: the bare
	/// command explains what will be destroyed and which account it is, and only `unregister confirm`
	/// acts, because the key cannot be recovered afterwards.
	/// </summary>
	public void UnregisterAccount(string[] args)
	{
		bool confirmed = args != null && args.Length > 0 &&
			args[0].Equals("confirm", StringComparison.OrdinalIgnoreCase);

		if (!confirmed)
		{
			if (!AccountStore.TryLoad(AccountFilePath, out AccountRecord record, out string loadError))
			{
				LogLine(loadError.Length > 0
					? "Error: " + loadError
					: "No account exists on this device - nothing to remove.");
				return;
			}
			LogWarning("This deletes this device's account (" + record.Key.AccountId + ", named \"" + record.DisplayName + "\").");
			LogWarning("The key cannot be recovered, and lot data keyed to this account becomes unreachable.");
			LogLine("If you are sure, run: unregister confirm");
			return;
		}

		AccountSession session = GetAccountSession();
		if (!session.Remove(out string message))
		{
			LogLine("Error: " + message);
			return;
		}
		LogSuccess(message);
	}

	/// <summary>
	/// Prints the one-time key reveal. A lost key cannot be recovered and a shared key hands over
	/// the account, so the warning is part of the flow rather than a footnote; re-revealing later
	/// is the §5.4 settings surface's job (the key is stored on this device meanwhile).
	/// </summary>
	private void RevealKeyOnce(string keyString)
	{
		LogLine("Save this key: it IS your account, and it cannot be recovered if lost.", warningColor);
		LogLine("  " + keyString, warningColor);
		LogLine("Run 'login <key>' on another device to use the account there. Anyone who has it can use the account - nothing will ever need to ask you for it.", warningColor);
	}

	public void LogoutUser()
	{
		AccountSession session = GetAccountSession();
		if (!session.Lock(out string message))
		{
			LogLine(message);
			return;
		}
		LogSuccess(message);
	}

	public void DisconnectAndUnload()
	{
		LogLine("Disconnecting... Returning to main menu.");
		currentState = UIState.MainMenu;
	}

	public void SetForegroundColor(Color color)
	{
		// Applies to future lines; already-logged lines keep their baked color. Committed here
		// because a `theme foreground <color>` command is one discrete action (§5.4's theme service).
		ThemeService.SetRole(ThemeRole.Foreground, color);
		ThemeService.Save();
		LogLine($"Foreground color updated to {color}.");
	}

	public void SetBackgroundColor(Color color)
	{
		ThemeService.SetRole(ThemeRole.Background, color);
		ThemeService.Save();
		LogLine($"Background color updated to {color}.");
	}

	// --- alias (the `alias` command) ---

	/// <summary>
	/// The `alias` command: list, define (<c>alias name=command</c>) or remove (<c>alias -r name</c>).
	/// Definitions are validated against the registry — a real command always wins over an alias of
	/// the same name, so shadowing one is refused up front instead of leaving a shortcut that
	/// silently never fires.
	/// </summary>
	public void HandleAliasCommand(string[] args)
	{
		if (args == null || args.Length == 0)
		{
			ListAliases();
			return;
		}

		if (args[0] == "-r" || args[0] == "--remove")
		{
			if (args.Length < 2) { LogLine("Usage: alias -r <name>"); return; }
			if (!aliases.Remove(args[1])) { LogError("Error: no alias named '" + args[1] + "'."); return; }
			aliases.Save(SettingsFilePath);
			LogSuccess("Alias removed: '" + args[1] + "'.");
			return;
		}

		if (!AliasTable.TryParseDefinition(args, out string name, out string commandLine))
		{
			LogLine("Usage: alias <name>=<command>   e.g. alias ll=\"openlot list\"   (remove with: alias -r <name>)");
			return;
		}

		if (registry.HasCommand(name))
		{
			LogError("Error: '" + name + "' is already a command; pick another alias name.");
			return;
		}

		aliases.Define(name, commandLine);
		aliases.Save(SettingsFilePath);
		LogSuccess("Alias created: '" + name + "' -> '" + commandLine + "'.");
	}

	private void ListAliases()
	{
		if (aliases.Count == 0)
		{
			LogLine("No aliases defined. (alias <name>=<command>)");
			return;
		}

		LogLine("Aliases:");
		for (int i = 0; i < aliases.Names.Count; i++)
		{
			string name = aliases.Names[i];
			aliases.TryGet(name, out string commandLine);
			LogLine("- " + name + " -> " + commandLine);
		}
	}

	// --- lot listing (`ls` and `openlot list`) ---

	/// <summary>
	/// The `ls` command: the lots this device can open, from <see cref="LotListing"/> — the same
	/// rows the server-list TUI shows, so the two surfaces cannot disagree about what exists.
	/// </summary>
	public void ListLots()
	{
		List<LotListing.LotEntry> lots = LotListing.Collect(LotListing.UserLotsDir, LotListing.SamplesDir);
		if (lots.Count == 0)
		{
			LogLine("No lots found. Create one with 'openlot create', or add .lot files to " + LotListing.UserLotsDir + ".");
			return;
		}

		LogLine("Lots (" + lots.Count + "):");
		for (int i = 0; i < lots.Count; i++)
		{
			LogLine("- " + lots[i].Name + "  [" + lots[i].Source + ", " + lots[i].SizeBytes + " bytes]");
		}
	}

	/// <summary>
	/// `openlot list` and the main menu's list button: refresh the rows and show the navigable TUI.
	/// The scan happens here, on open, not per frame — walking both lot folders every layout pass
	/// would be filesystem work in the render loop.
	/// </summary>
	public void ShowLotList()
	{
		RefreshLotList();
		currentState = UIState.ServerList;
	}

	private void RefreshLotList()
	{
		serverListEntries = LotListing.Collect(LotListing.UserLotsDir, LotListing.SamplesDir);
		selectedServerIndex = Mathf.Clamp(selectedServerIndex, 0, Mathf.Max(0, serverListEntries.Count - 1));
	}

	/// <summary>Moves the list highlight by <paramref name="delta" />, clamped to the rows present.</summary>
	private void MoveServerSelection(int delta)
	{
		if (serverListEntries.Count == 0) return;
		selectedServerIndex = Mathf.Clamp(selectedServerIndex + delta, 0, serverListEntries.Count - 1);
	}

	/// <summary>
	/// Opens the highlighted lot. There is no independent player runtime yet (§6.1) — the creation
	/// environment is the only host — so "load a selected lot" means the hand-off `openlot create`
	/// already uses, with the chosen path waiting on <see cref="Builder.PendingLotPath" />. The real
	/// play/host/join flow into the gameplay scene lands with §6.1/§10.1.
	/// </summary>
	private void OpenSelectedLot()
	{
		if (serverListEntries.Count == 0)
		{
			LogLine("Nothing to open.");
			return;
		}

		LotListing.LotEntry entry = serverListEntries[selectedServerIndex];
		LogLine("System: opening '" + entry.Name + "' in the creation environment (the player runtime arrives with milestone 6.1).");
		Builder.PendingLotPath = entry.Path;
		GetTree().ChangeSceneToFile("res://builder/builder.tscn");
	}

	// --- theme (the `theme` command) ---

	/// <summary>
	/// The `theme` command: list presets, apply one, or set one role color. The command contract
	/// §5.4's shared theme service will keep; this is the terminal-local implementation (§5.1).
	/// </summary>
	public void HandleThemeCommand(string[] args)
	{
		if (args == null || args.Length == 0)
		{
			ListThemes();
			return;
		}

		string sub = args[0].ToLowerInvariant();
		if (sub == "foreground" || sub == "background")
		{
			if (args.Length < 2)
			{
				LogLine("Usage: theme " + sub + " <color>   (hex like #33ff88, or a name like green)");
				return;
			}

			string colorText = string.Join(" ", args, 1, args.Length - 1);
			if (!TerminalThemeCatalog.TryParseColor(colorText, out Color color))
			{
				LogError("Error: unrecognized color '" + colorText + "'.");
				return;
			}

			if (sub == "foreground") SetForegroundColor(color);
			else SetBackgroundColor(color);
			return;
		}

		if (sub == "font")
		{
			HandleThemeFontCommand(args);
			return;
		}

		// `theme save <name>` — snapshot the current theme as the creator's own preset; the plain
		// `theme custom` below applies it again later (and `theme custom clear` forgets it).
		if (sub == "save")
		{
			if (args.Length < 2)
			{
				LogLine("Usage: theme save <name>");
				return;
			}
			string name = string.Join(" ", args, 1, args.Length - 1);
			if (ThemeService.SaveCustomPreset(name, out string message)) LogSuccess(message);
			else LogError("Error: " + message);
			return;
		}
		if (sub == "custom" && args.Length > 1 && args[1].ToLowerInvariant() == "clear")
		{
			if (ThemeService.ClearCustomPreset(out string message)) LogSuccess(message);
			else LogError("Error: " + message);
			return;
		}

		if (TerminalThemeCatalog.TryParsePreset(sub, out string preset))
		{
			ApplyPreset(preset);
			return;
		}

		LogError("Error: unknown theme '" + args[0] + "'. Try 'theme' for the list.");
	}

	private void ListThemes()
	{
		LogLine("Themes: " + string.Join(", ", TerminalThemeCatalog.PresetNames) + ", custom (your saved preset)");
		LogLine("Usage: theme <preset> | theme custom | theme save <name> | theme custom clear"
			+ " | theme foreground|background <color> | theme font [console] <name>");
	}

	/// <summary>
	/// `theme font` — the command surface for the same two slots the Settings window offers, so the
	/// two can never disagree: list (<c>theme font</c>), set the UI font (<c>theme font &lt;name&gt;</c>),
	/// set the console font (<c>theme font console &lt;name&gt;</c>), or put the console back on the UI font
	/// (<c>theme font console follow</c>). Names may be the id or the display name the settings list
	/// shows, and may contain spaces.
	/// </summary>
	private void HandleThemeFontCommand(string[] args)
	{
		FontRegistry registry = FontService.Registry;

		if (args.Length == 1)
		{
			LogLine("UI font: " + FontService.DisplayNameFor(FontService.UiSlot) +
				"   Console font: " + FontService.DisplayNameFor(FontService.ConsoleSlot) +
				(FontService.ConsoleHasOwnFont ? "" : " (follows the UI font)"));
			LogLine("Available fonts (theme font [console] <name>):");
			for (int i = 0; i < registry.Entries.Count; i++)
			{
				FontRegistry.FontEntry entry = registry.Entries[i];
				string tag = entry.BuiltIn ? "built-in" : FontCatalog.IsBundled(entry.Id) ? "bundled" : "custom";
				LogLine("- " + entry.DisplayName + "  [" + entry.Category + ", " + tag + "]");
			}
			return;
		}

		if (args[1].ToLowerInvariant() == "console")
		{
			if (args.Length < 3)
			{
				LogLine("Usage: theme font console <name|follow>");
				return;
			}

			string consoleValue = string.Join(" ", args, 2, args.Length - 2);
			string lowered = consoleValue.ToLowerInvariant();
			if (lowered == "follow" || lowered == "ui" || lowered == "reset")
			{
				SelectFont(FontService.ConsoleSlot, "");
				return;
			}

			if (!registry.TryResolve(consoleValue, out string consoleId))
			{
				LogError("Error: unknown font '" + consoleValue + "'.");
				return;
			}
			SelectFont(FontService.ConsoleSlot, consoleId);
			return;
		}

		string uiValue = string.Join(" ", args, 1, args.Length - 1);
		if (!registry.TryResolve(uiValue, out string uiId))
		{
			LogError("Error: unknown font '" + uiValue + "'.");
			return;
		}
		SelectFont(FontService.UiSlot, uiId);
	}

	/// <summary>Applies a canonical preset (the names <see cref="TerminalThemeCatalog" /> returns)
	/// through the shared theme service and reports it.</summary>
	private void ApplyPreset(string preset)
	{
		if (ThemeService.ApplyPresetByName(preset, out string message)) LogSuccess(message);
		else LogError("Error: " + message);
	}

	// --- avatar (placeholder) ---

	/// <summary>
	/// The `avatar` command's placeholder surface. There is no avatar system yet — rigged characters
	/// are §7.1 and the avatar/element APIs are §9 — so every path answers honestly rather than
	/// pretending. When rigs land this hands over to the real avatar service without changing the
	/// command contract.
	/// </summary>
	public void HandleAvatarCommand(string[] args)
	{
		string sub = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "";
		switch (sub)
		{
			case "list":
				LogLine("No avatars yet — the avatar system arrives with rigged characters (milestone 7.1).");
				break;
			case "equip":
				LogLine("Nothing to equip yet — the avatar system arrives with rigged characters (milestone 7.1).");
				break;
			default:
				LogLine("Usage: avatar <list|equip>   (placeholder — the avatar system lands with milestone 7.1)");
				break;
		}
	}

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
		_scrollback.Add(message, color);
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

	public void Clear()
	{
		_scrollback.Clear();
		// The selected lines are gone; drop the selection with them (its absolute ids would
		// otherwise resolve to nothing and copy as empty).
		_selection.Clear();
		_selecting = false;
	}
}
