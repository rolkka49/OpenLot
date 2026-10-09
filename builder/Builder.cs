using Godot;

/// <summary>
/// Top-level builder interaction mode. Build mode is the 3D creation environment (select, transform,
/// direct part dragging). UI mode turns the viewport into a 2D canvas editor: the left mouse button
/// belongs to the lot UI overlay, and the 3D gizmo and part dragging are suspended so nothing
/// competes for the click.
/// </summary>
public enum BuilderMode
{
	Build,
	UI
}

/// <summary>
/// Root orchestrator of the lot Builder (creation environment) scene. Ported from Unity's
/// LotBuilderUIController: draws the top menu bar, owns window toggling, the pause and
/// unsaved-changes modals, and routes the per-frame ImGui layout to every panel.
/// All builder-facing UI goes through the dear-imgui-godot addon (OpenLot's custom UI layer);
/// no Godot Control nodes are used here.
/// </summary>
public partial class Builder : Node
{
	public static Builder Instance { get; private set; }

	public SelectionManager Selection { get; private set; }
	public ScriptManager Scripts { get; private set; }
	public BuilderScene Scene { get; private set; }
	public ViewportWindow ViewportWindow { get; private set; }
	public GizmoController Gizmo { get; private set; }
	public UiGizmoController UiGizmo { get; private set; }
	public PartDragController Parts { get; private set; }
	/// <summary>The two-click weld/hinge tool (milestone 3.6). While armed it owns viewport clicks.</summary>
	public ConstraintPicker ConstraintPick { get; private set; }
	/// <summary>Editor-only hinge markers for the selected part (milestone 3.6) — what makes a
	/// hinge's orientation visible outside a session.</summary>
	public HingeVisualizer HingeMarkers { get; private set; }
	public SelectionOutline Outline { get; private set; }
	/// <summary>The single write path for lot properties (see <see cref="LotPropertyService"/>).</summary>
	public LotPropertyService Properties { get; private set; }
	/// <summary>The editor undo/redo stack (milestone 2.4). Every editor mutation goes through it.</summary>
	public EditorHistory History { get; private set; }
	public ToolboxPanel Toolbox { get { return _toolbox; } }

	/// <summary>Active builder interaction mode. UI mode gates the viewport's 3D input paths.</summary>
	public BuilderMode Mode { get; private set; } = BuilderMode.Build;

	public string LotName { get; private set; } = "Untitled Lot";
	public bool HasUnsavedChanges { get; private set; }

	private HierarchyPanel _hierarchy;
	private ToolboxPanel _toolbox;
	private InspectorPanel _inspector;
	private CodeEditorWindow _codeEditor;
	private OutputWindow _output;
	private BuilderCommandLine _commandLine;
	private EnvironmentControls _environment;
	private CollisionGroupControls _collisionGroups;

	// Modal state. _pauseOpen/_confirmOpen are sampled inside the layout pass (ImGui popup
	// state is only valid there); the request flags are fed from _UnhandledInput.
	private bool _pauseRequested = false;
	private bool _pauseOpen = false;
	private bool _closePause = false;
	private bool _confirmOpen = false;
	private bool _openConfirm = false;

	/// <summary>True when the previous session died and left a snapshot. Raised in the first layout pass
	/// so the creator answers before editing; cleared the moment the prompt is opened.</summary>
	private bool _offerRecovery = false;
	/// <summary>Sampled in the layout pass, like the other modals, so input can be gated on it.</summary>
	private bool _recoveryOpen = false;

	/// <summary>Set by the View menu's reveal entry; consumed by the layout pass, which opens the caution
	/// modal. Kept a request flag rather than opening the popup in the menu callback for the same reason
	/// the other modals are: popup state is only valid inside the layout pass.</summary>
	private bool _secretWarningRequested = false;
	/// <summary>Sampled in the layout pass so input can be gated on the caution modal.</summary>
	private bool _secretWarningOpen = false;

	/// <summary>Archive path this lot was last saved to or loaded from, or "" for a lot that has never
	/// been committed to a file (§4.2). Save Lot writes here; Save Lot As… and Open Lot… replace it.</summary>
	private string _lotPath = "";

	/// <summary>Creation stamp carried across a load so re-saving a loaded lot keeps its original creation
	/// time. 0 means "never saved", which the save then stamps.</summary>
	private long _lotCreatedUnix;

	/// <summary>A one-off outcome message (a save or load result) shown in its own modal. Held as a
	/// request flag for the same reason the other modals are: popup state is only valid in the layout pass.</summary>
	private string _lotMessage = "";
	private bool _lotMessageRequested = false;
	private bool _lotMessageOpen = false;

	/// <summary>Set when the creator accepts the crash snapshot. Consumed at the top of the next layout
	/// pass rather than acted on where the modal is drawn, because the modal is drawn *after* the panels
	/// have already walked the scene this frame — replacing the lot there would free nodes a panel had just
	/// finished reading. Consuming it before any panel runs means they all see the new world.</summary>
	private bool _recoverRequested = false;

	/// <summary>True while the live lot came from the crash snapshot and has not been saved to a file of
	/// the creator's choosing yet. Its only job is to gate deleting that snapshot (see <see cref="WriteLot"/>).</summary>
	private bool _recoveredFromSnapshot = false;

	/// <summary>Whether the quit the creator is being asked about should close the client rather than
	/// return to the main menu (the OS window's close button vs. the menu's "Exit to Terminal").</summary>
	private bool _quitAppOnConfirm = false;

	/// <summary>What to do once a save requested from the unsaved-changes prompt succeeds. The save can need
	/// a destination first, so the intent has to outlive the frame the button was pressed in.</summary>
	private int _quitAfterSave = QuitAfterSaveNone;
	private const int QuitAfterSaveNone = 0;
	private const int QuitAfterSaveToTerminal = 1;
	private const int QuitAfterSaveToClose = 2;

	/// <summary>Dirty flag for changes that are not on the undo stack (see <see cref="MarkDirty"/>).</summary>
	private bool _dirtyOutsideHistory = false;

	/// <summary>
	/// The OS window's close button (and any other window-close request), routed through the same prompt
	/// as the menu's exit.
	///
	/// This matters more than it looks. Godot's default is to quit immediately, which discards unsaved
	/// work with no prompt — and because the scene tree is still torn down, <see cref="_ExitTree"/> runs
	/// and clears the crash marker, so the session would look "clean" and no recovery would be offered
	/// either. Handling the request is what stops a click on X from silently costing the creator their lot.
	/// </summary>
	public override void _Notification(int what)
	{
		if (what != (int)NotificationWMCloseRequest) return;

		// While the unsaved-changes prompt is up it owns the decision: a second click on X must not
		// silently discard the work the creator is being asked about.
		if (_openConfirm || _confirmOpen) return;

		RequestQuit(true);
	}

	/// <summary>
	/// The OS window's close button (and any other window-close request), routed through the same prompt
	/// as the menu's exit.
	/// </summary>
	public override void _Ready()
	{
		Instance = this;
		// The builder decides when the window's X may close the client (see _Notification); Godot's
		// default would quit past the unsaved-changes prompt. _ExitTree hands it back for the terminal.
		GetTree().AutoAcceptQuit = false;
		// Also normalize/restore here so running the builder scene on its own (dev/test) behaves like
		// entering it from the terminal. Both are idempotent, so they are no-ops when the terminal
		// already did them.
		DisplaySetup.NormalizeStretch();
		WallpaperService.LoadPersistedSettings();
		Selection = new SelectionManager();
		Scripts = new ScriptManager(this);
		// The history must exist before any panel can push to it (the gizmo, drag controllers and
		// Inspector all funnel their edits through it).
		History = new EditorHistory(this);
		History.Changed += OnHistoryChanged;
		Scene = GetNode<BuilderScene>("Scene");
		Properties = new LotPropertyService(this);
		ViewportWindow = new ViewportWindow(Scene);
		_hierarchy = new HierarchyPanel(this);
		_toolbox = new ToolboxPanel(this);
		Gizmo = new GizmoController(this);
		UiGizmo = new UiGizmoController(this);
		Parts = new PartDragController(this, Scene);
		ConstraintPick = new ConstraintPicker(this);
		HingeMarkers = new HingeVisualizer(this);
		Outline = new SelectionOutline(this);
		_inspector = new InspectorPanel(this);
		_codeEditor = new CodeEditorWindow(this);
		_output = new OutputWindow();
		_commandLine = new BuilderCommandLine(this);
		_environment = new EnvironmentControls();
		_collisionGroups = new CollisionGroupControls();

		// Crash recovery (milestone 4.1). The question is read BEFORE the marker is written, so the
		// marker this call creates cannot be mistaken for a crash; then it is re-armed for this session
		// and cleared on a clean exit, which is what makes an unclean exit detectable next boot.
		_offerRecovery = LotAutosave.HasRecoverableAutosave();
		LotAutosave.MarkSessionStart();

		ImGui.OnLayout(DrawLayout);
	}

	public override void _Process(double delta)
	{
		// Debounced script saving runs outside the ImGui layout (delta time from the engine).
		_codeEditor.Tick(delta);
		// Periodic snapshot (milestone 4.1): rides the same save pipeline a manual save uses, and is
		// skipped while a player session owns the world.
		LotAutosave.Tick(delta, Scene, LotName, HasUnsavedChanges, InTestMode);
	}

	public override void _PhysicsProcess(double delta)
	{
		// Picking and the collision-resolved part drag must run in the physics step: Godot only
		// allows space-state queries and kinematic moves there.
		Parts.Tick();
		// The constraint tool's pick runs here for the same reason (its click was recorded in the
		// layout pass).
		ConstraintPick.Tick();
		// Editor-only hinge markers follow the selected part and its axes (no-op unless a hinge
		// owner is selected).
		HingeMarkers.Tick();
	}

	public override void _ExitTree()
	{
		// Clean exit (milestone 4.1): dropping the crash marker here is what tells the next boot that
		// this session ended properly. A crash skips this, leaving the marker behind.
		LotAutosave.MarkSessionEnd();
		// Hand the window's close button back to Godot's default for whatever scene follows (the terminal
		// has no close handling of its own, and leaving this off would make its X do nothing).
		SceneTree tree = GetTree();
		if (tree != null) tree.AutoAcceptQuit = true;
		// The ImGui autoload outlives this scene; without disconnecting, the layout handler
		// would keep running (and draw over the next scene) after this node is freed.
		ImGui.DisconnectLayout(DrawLayout);
		// The gizmo and the outline hold Selection.OnSelectionChanged subscriptions; drop them
		// before the node dies.
		Outline.Shutdown();
		Gizmo.Shutdown();
		UiGizmo.Shutdown();
		// Commands hold detached nodes alive across undo, and a detached node does not die with the
		// scene, so the history is dropped explicitly (Clear frees whatever it was holding).
		if (History != null)
		{
			History.Changed -= OnHistoryChanged;
			History.Clear();
		}
		if (Instance == this) Instance = null;
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		InputEventKey keyEvent = @event as InputEventKey;
		if (keyEvent == null || !keyEvent.Pressed || keyEvent.Echo) return;

		// Undo/redo (milestone 2.4). While the code editor's text field has focus, Ctrl+Z belongs
		// to ImGui's own in-widget undo, so the lot stack deliberately stays out of the way.
		bool ctrl = Input.IsPhysicalKeyPressed(Key.Ctrl) || Input.IsPhysicalKeyPressed(Key.Meta);
		if (ctrl && keyEvent.Keycode == Key.S && !InTestMode)
		{
			// Ctrl+S follows the File menu's rule: it saves, but never from inside a player session.
			SaveLot(false);
			return;
		}
		if (ctrl && History != null && !_codeEditor.HasCodeFocus)
		{
			bool shift = Input.IsPhysicalKeyPressed(Key.Shift);
			if (keyEvent.Keycode == Key.Z && shift) { History.Redo(); return; }
			if (keyEvent.Keycode == Key.Z) { History.Undo(); return; }
			if (keyEvent.Keycode == Key.Y) { History.Redo(); return; }
		}

		// A player session owns Escape: it leaves the session instead of opening the pause modal.
		if (keyEvent.Keycode == Key.Escape && InTestMode)
		{
			ExitTestMode();
			return;
		}

		// An armed constraint tool claims Escape first: it ends the gesture rather than opening the
		// pause modal.
		if (keyEvent.Keycode == Key.Escape && ConstraintPick != null && ConstraintPick.IsArmed)
		{
			ConstraintPick.Cancel();
			return;
		}

		if (keyEvent.Keycode == Key.Escape && !_confirmOpen)
		{
			if (_pauseOpen) _closePause = true;
			else _pauseRequested = true;
		}
	}

	/// <summary>
	/// Marks the lot dirty for a change that is NOT on the undo stack (the lot name today).
	/// Editor mutations must go through <see cref="History"/> instead, so that undoing back to the
	/// save point can report the lot as clean again.
	/// </summary>
	public void MarkDirty()
	{
		_dirtyOutsideHistory = true;
		HasUnsavedChanges = true;
	}

	public void MarkSaved()
	{
		_dirtyOutsideHistory = false;
		if (History != null) History.MarkSavePoint();
		HasUnsavedChanges = false;
	}

	/// <summary>Recomputes the dirty flag after an undo/redo or save-point move.</summary>
	private void OnHistoryChanged()
	{
		HasUnsavedChanges = _dirtyOutsideHistory || (History != null && History.IsDirty);
	}

	/// <summary>True while the lot runs as a player session (Test or Game mode, §3.4).</summary>
	public bool InTestMode { get { return Scene != null && Scene.InTestMode; } }

	/// <summary>True while the session also hides the creation-environment UI (Game mode).</summary>
	public bool InGameMode { get { return Scene != null && Scene.InGameMode; } }

	/// <summary>
	/// Enters the player session in place (§3.4). Any in-flight editor drag is dropped first and the
	/// selection is cleared, so no gizmo handle or drag survives the camera handoff; UI mode is left
	/// because it owns the left mouse button and a player session does not.
	/// </summary>
	public void EnterTestMode(bool gameMode)
	{
		if (Scene == null || Scene.InTestMode) return;
		SetMode(BuilderMode.Build);
		Selection.ClearSelection();
		// An armed tool (and its hinge-point marker) has no meaning inside a session; drop it.
		ConstraintPick.Cancel();
		Parts.QueueRelease();
		Scene.EnterTestMode(gameMode);
	}

	/// <summary>Leaves the player session and returns to the build-mode world.</summary>
	public void ExitTestMode()
	{
		if (Scene == null || !Scene.InTestMode) return;
		Scene.ExitTestMode();
	}

	/// <summary>
	/// Switches the builder's interaction mode. Any in-flight drag is dropped so a half-finished
	/// move/resize cannot survive the switch: the UI overlay drag is cleared here, and the 3D part
	/// drag is ended by queuing its release (consumed by the next physics step).
	/// </summary>
	public void SetMode(BuilderMode mode)
	{
		if (Mode == mode) return;
		Mode = mode;
		UiGizmo.CancelDrag();
		Parts.QueueRelease();
	}

	/// <summary>Leaves the builder for the main menu, asking first when the lot has unsaved changes.</summary>
	public void RequestQuit()
	{
		RequestQuit(false);
	}

	/// <summary>
	/// Leaves the builder. <paramref name="quitApplication"/> distinguishes the two ways out: the menu's
	/// "Exit to Terminal" returns to the main menu, while the OS window's close button means "close
	/// OpenLot" — so a confirmed close ends the process rather than dropping the creator at the terminal.
	/// </summary>
	public void RequestQuit(bool quitApplication)
	{
		if (HasUnsavedChanges)
		{
			_quitAppOnConfirm = quitApplication;
			_openConfirm = true;
			return;
		}

		if (quitApplication) QuitApplication();
		else ExitToTerminal();
	}

	public void ExitToTerminal()
	{
		HasUnsavedChanges = false;
		GetTree().ChangeSceneToFile("res://terminal.tscn");
	}

	/// <summary>
	/// Closes the client. This is a *deliberate* exit, so <see cref="_ExitTree"/> drops the crash marker
	/// and the next launch correctly offers no recovery — unlike a crash, which skips that teardown and
	/// leaves the marker behind for the prompt to find.
	/// </summary>
	private void QuitApplication()
	{
		HasUnsavedChanges = false;
		GetTree().Quit();
	}

	/// <summary>
	/// Acts on a quit the creator confirmed in the unsaved-changes prompt, once the save it asked for has
	/// landed. The save may need a destination first (the file dialog is asynchronous), so the intent is
	/// parked in <see cref="_quitAfterSave"/> rather than acted on where the button was pressed.
	/// </summary>
	private void FinishPendingQuit()
	{
		int pending = _quitAfterSave;
		_quitAfterSave = QuitAfterSaveNone;
		if (pending == QuitAfterSaveToTerminal) ExitToTerminal();
		else if (pending == QuitAfterSaveToClose) QuitApplication();
	}

	public void OpenCodeEditor(string path, string code)
	{
		_codeEditor.Open(path, code);
	}

	/// <summary>
	/// Writes a script buffer and refreshes the open editor if it is showing that path. This is the
	/// apply half of <see cref="ScriptEditCommand"/> — undo/redo of a code edit lands here, and a
	/// path whose file no longer exists is refused rather than recreated (see CodeEditorWindow).
	/// </summary>
	public void ApplyScriptCode(string path, string code)
	{
		_codeEditor.ApplyCode(path, code);
	}

	public void OnScriptRenamed(string oldPath, string newPath)
	{
		_codeEditor.OnPathRenamed(oldPath, newPath);
	}

	private void DrawLayout()
	{
		if (InGameMode)
		{
			// Game mode (§3.4): the player session with no creation-environment tooling — the 3D view
			// fills the display and the editor's dockspace, menu bar and panels are not drawn at all.
			ViewportWindow.InputEnabled = true;
			ViewportWindow.Draw(this);
			return;
		}

		// Accepted crash recovery is applied here, before anything walks the scene: the modal that asks for
		// it is drawn at the *end* of the pass, and replacing the lot there would free nodes the panels had
		// already read this frame. Running it first means every panel below sees the recovered world.
		if (_recoverRequested)
		{
			_recoverRequested = false;
			RecoverFromSnapshot();
		}

		// Draws the wallpaper (when one is set) and then the dockspace. With no wallpaper this is
		// exactly the previous DockspaceOverMainViewport() call, so the default look is unchanged.
		WallpaperService.DrawBackgroundAndDockspace();
		DrawMainMenuBar();

		// Camera input dies the moment a modal is up, on top of the viewport focus gate.
		ViewportWindow.InputEnabled = !(_pauseOpen || _confirmOpen || _recoveryOpen || _secretWarningOpen || _lotMessageOpen);

		ViewportWindow.Draw(this);
		_hierarchy.Draw(this);
		_toolbox.Draw(this);
		_inspector.Draw(this);
		_commandLine.Draw(this);
		if (_codeEditor.IsOpen) _codeEditor.Draw(this);
		if (_output.IsOpen) _output.Draw(this);

		if (_openConfirm)
		{
			ImGui.OpenPopup("Unsaved Changes");
			_openConfirm = false;
		}
		if (_offerRecovery)
		{
			ImGui.OpenPopup("Recover Autosave");
			_offerRecovery = false;
		}
		if (_secretWarningRequested)
		{
			ImGui.OpenPopup("Secret Content Warning");
			_secretWarningRequested = false;
		}
		if (_lotMessageRequested)
		{
			ImGui.OpenPopup("Lot File");
			_lotMessageRequested = false;
		}
		DrawPauseMenu();
		DrawSecretContentWarning();
		DrawLotMessage();
	}

	private void DrawMainMenuBar()
	{
		if (!ImGui.BeginMainMenuBar()) return;

		if (ImGui.BeginMenu("File"))
		{
			// Saving or loading the whole lot is not meaningful while a player session owns the world:
			// scripts are driving it and it is mid-run (§3.4). The same rule autosave follows.
			bool lotFileEnabled = !InTestMode && Scene != null;
			if (ImGui.MenuItem("Save Lot", "Ctrl+S", false, lotFileEnabled))
				SaveLot(false);
			if (ImGui.MenuItem("Save Lot As...", "", false, lotFileEnabled))
				SaveLot(true);
			if (ImGui.MenuItem("Open Lot...", "", false, lotFileEnabled))
				RequestOpenLot();
			ImGui.Separator();
			if (ImGui.MenuItem("Exit to Terminal"))
				RequestQuit();
			ImGui.EndMenu();
		}
		if (ImGui.BeginMenu("Edit"))
		{
			// Undo/redo (milestone 2.4). Labels name the command so the menu says what will happen.
			string undoLabel = History.CanUndo ? "Undo " + History.UndoLabel : "Undo";
			if (ImGui.MenuItem(undoLabel, "Ctrl+Z", false, History.CanUndo)) History.Undo();
			string redoLabel = History.CanRedo ? "Redo " + History.RedoLabel : "Redo";
			if (ImGui.MenuItem(redoLabel, "Ctrl+Shift+Z", false, History.CanRedo)) History.Redo();
			ImGui.Separator();
			if (ImGui.MenuItem("Clear History", "", false, History.Count > 0)) History.Clear();
			ImGui.EndMenu();
		}
		if (ImGui.BeginMenu("Test"))
		{
			// Test mode (§3.4): play the lot in place — scripts reload, the freecam yields to the player
			// camera, and every editor tool that owns the mouse is suspended. Escape drops back out.
			if (ImGui.MenuItem(InTestMode ? "Stop Test Mode" : "Play in Test Mode"))
			{
				if (InTestMode) ExitTestMode();
				else EnterTestMode(false);
			}
			ImGui.EndMenu();
		}
		if (ImGui.BeginMenu("Add Window"))
		{
			if (ImGui.MenuItem("Code Editor"))
				_codeEditor.Show();
			// The Output panel (milestone 2.8): build-mode diagnostics that stay readable instead
			// of flashing for four seconds or reaching only Godot's console.
			if (ImGui.MenuItem("Output"))
				_output.Show();
			ImGui.EndMenu();
		}
		if (ImGui.BeginMenu("View"))
		{
			// Creation-environment lighting (milestone 2.5): sun/time of day, sky and fog. It lives
			// here rather than in its own panel because it is a view setting, next to the wallpaper.
			if (ImGui.BeginMenu("Environment"))
			{
				_environment.Draw(this);
				ImGui.EndMenu();
			}
			// Named collision groups (milestone 3.5): the lot-wide interaction matrix. Lot-wide like
			// the environment, so it sits here too; a single part's group is an Inspector property.
			if (ImGui.BeginMenu("Collision Groups"))
			{
				_collisionGroups.Draw(this);
				ImGui.EndMenu();
			}
			ImGui.Separator();
			// Wallpaper is UI chrome shared with the terminal (2D background, not the lot's sky).
			if (ImGui.MenuItem("Set Wallpaper..."))
				WallpaperService.RequestFileDialog(this);
			if (ImGui.MenuItem("Clear Wallpaper"))
				WallpaperService.Clear();
			ImGui.Separator();
			// Secret content (milestone 4.1): a lot's base content is hidden by default. Revealing it is
			// a deliberate, warned action because those files are shared and not meant to be edited
			// casually; hiding it again is free. The label doubles as the state, like Test/Game mode.
			if (ImGui.MenuItem(SecretContent.Revealed ? "Hide Secret Content" : "Show Secret Content..."))
			{
				if (SecretContent.Revealed) SecretContent.Hide();
				else _secretWarningRequested = true;
			}
			ImGui.EndMenu();
		}
		if (ImGui.BeginMenu("Game"))
		{
			// Game mode (§3.4): the same player session with the creation-environment UI hidden — what
			// a player sees. Entered from the builder until lot persistence (§8) lands.
			if (ImGui.MenuItem(InGameMode ? "Stop Game Mode" : "Play in Game Mode"))
			{
				if (InTestMode) ExitTestMode();
				else EnterTestMode(true);
			}
			ImGui.EndMenu();
		}
		if (ImGui.MenuItem("Code"))
		{
			if (_codeEditor.IsOpen) _codeEditor.Close();
			else _codeEditor.Show();
		}
		if (ImGui.MenuItem("UI Mode", "", Mode == BuilderMode.UI))
			SetMode(Mode == BuilderMode.UI ? BuilderMode.Build : BuilderMode.UI);
		if (ImGui.BeginMenu("Animate"))
		{
			ImGui.BeginDisabled(true);
			ImGui.MenuItem("Animation editor pending (roadmap 6.x)");
			ImGui.EndDisabled();
			ImGui.EndMenu();
		}

		// Lot title, right side of the bar (stand-in for Unity's TitlePanel).
		ImGui.SameLine();
		ImGui.SetCursorPosX(ImGui.GetWindowWidth() - 160f);
		ImGui.Text(LotName);

		ImGui.EndMainMenuBar();
	}

	private void DrawPauseMenu()
	{
		_pauseOpen = ImGui.IsPopupOpen("Pause Menu");
		_confirmOpen = ImGui.IsPopupOpen("Unsaved Changes");
		_recoveryOpen = ImGui.IsPopupOpen("Recover Autosave");

		if (_pauseRequested && !_pauseOpen)
		{
			ImGui.OpenPopup("Pause Menu");
			_pauseRequested = false;
		}

		if (ImGui.BeginPopupModal("Pause Menu"))
		{
			if (_closePause)
			{
				ImGui.CloseCurrentPopup();
				_closePause = false;
			}
			ImGui.Text(LotName);
			ImGui.Separator();

			string renamed = ImGui.InputText("Lot Name", LotName, 64);
			if (renamed != LotName && renamed.Trim().Length > 0)
			{
				LotName = renamed.Trim();
				MarkDirty();
			}

			if (ImGui.Button("Save Lot", 220f, 0f))
				SaveLot(false);
			if (ImGui.Button("Quit to Terminal", 220f, 0f))
				RequestQuit();
			if (ImGui.Button("Resume", 220f, 0f))
				ImGui.CloseCurrentPopup();

			ImGui.EndPopup();
		}
		else
		{
			// Clear a stale close request if ImGui already dismissed the modal for us.
			_closePause = false;
		}

		if (ImGui.BeginPopupModal("Unsaved Changes"))
		{
			ImGui.Text("The lot has unsaved changes. Quit anyway?");
			ImGui.Spacing();
			if (ImGui.Button("Save && Quit", 170f, 0f))
			{
				ImGui.CloseCurrentPopup();
				// Record what to do once the save lands, rather than quitting here: the save may still
				// need a destination from the file dialog, which completes asynchronously.
				_quitAfterSave = _quitAppOnConfirm ? QuitAfterSaveToClose : QuitAfterSaveToTerminal;
				if (_lotPath.Length > 0) WriteLot(_lotPath);
				else LotFilePicker.OpenForSave(this, "Save Lot", LotName + ".lot", WriteLot);
			}
			ImGui.SameLine();
			if (ImGui.Button("Discard && Quit", 170f, 0f))
			{
				ImGui.CloseCurrentPopup();
				// A deliberate discard: no save, so nothing to wait for.
				if (_quitAppOnConfirm) QuitApplication();
				else ExitToTerminal();
			}
			ImGui.SameLine();
			if (ImGui.Button("Cancel", 90f, 0f))
			{
				// Nothing was decided, so any queued quit intent must not survive.
				_quitAfterSave = QuitAfterSaveNone;
				ImGui.CloseCurrentPopup();
			}
			ImGui.EndPopup();
		}
		DrawRecoveryPrompt();
	}
	/// <summary>
	/// File → Save Lot / Save Lot As… (milestone 4.2). With a known path Save Lot writes straight over it;
	/// otherwise (and for Save As) the path is chosen first. The lot is never written from inside a player
	/// session — the menu entry is disabled there.
	/// </summary>
	private void SaveLot(bool forceSaveAs)
	{
		if (Scene == null) return;
		if (!forceSaveAs && _lotPath.Length > 0)
		{
			WriteLot(_lotPath);
			return;
		}
		LotFilePicker.OpenForSave(this, "Save Lot", LotName + ".lot", WriteLot);
	}

	/// <summary>
	/// The one manual-save path. Flushing the code editor first is not a nicety: the archive collects
	/// script text from disk, so a pending edit that had not been written yet would ship the script as it
	/// was before the creator's last keystrokes.
	/// </summary>
	private void WriteLot(string path)
	{
		_codeEditor.FlushPendingSave();

		System.Collections.Generic.List<string> missing = new System.Collections.Generic.List<string>();
		// The manifest carries the creation stamp forward, so re-saving a loaded lot does not claim it was
		// created today.
		LotArchive.LotManifest manifest = LotSave.BuildManifest(Scene.LotRoot, LotName, _lotCreatedUnix);

		if (!LotSave.WriteScene(Scene, path, manifest, missing, out string error))
		{
			// A save the creator asked for as part of quitting failed: never let that be the thing that
			// ends the session, or the failure would take the unsaved work with it.
			_quitAfterSave = QuitAfterSaveNone;
			ShowLotError("Could not save the lot.\n\n" + error);
			return;
		}

		_lotPath = path;
		_lotCreatedUnix = manifest.CreatedUnix;
		MarkSaved();

		// The snapshot has served its purpose: the work it held now exists in a file the creator chose.
		// Deleting it only here — after a save that actually succeeded — is what keeps a failed or
		// cancelled save from destroying the only copy of recovered work.
		if (_recoveredFromSnapshot)
		{
			_recoveredFromSnapshot = false;
			LotAutosave.DiscardSnapshot();
		}

		ShowLotMessage(DescribeSave(path, missing));
		FinishPendingQuit();
	}

	/// <summary>File → Open Lot…: pick a file, then load it (the load itself validates before mutating).</summary>
	private void RequestOpenLot()
	{
		if (Scene == null) return;
		LotFilePicker.OpenForLoad(this, "Open Lot", OpenLot);
	}

	/// <summary>
	/// Replaces the live lot with an archive's contents. <see cref="LotLoad.Load"/> validates the whole
	/// file before it touches anything, so a bad lot leaves the current one untouched and simply reports why.
	/// </summary>
	private void OpenLot(string path)
	{
		LoadLotIntoBuilder(path, false);
	}

	/// <summary>
	/// Loads the crash snapshot (milestone 4.1's recovery prompt, wired to §4.2's loader).
	///
	/// Deliberately does NOT adopt the snapshot's path as the lot's file: a recovered lot has no
	/// destination the creator chose, so the first save asks where to put it rather than overwriting the
	/// snapshot. The lot also counts as unsaved work, which is what makes quitting warn — and the snapshot
	/// itself is deleted only once a manual save has actually succeeded (see <see cref="WriteLot"/>).
	/// </summary>
	private void RecoverFromSnapshot()
	{
		if (!LoadLotIntoBuilder(LotAutosave.AutosavePath, true)) return;
		_recoveredFromSnapshot = true;
	}

	/// <summary>
	/// The shared load path for both Open Lot… and crash recovery. <paramref name="recovery"/> is the only
	/// difference between them: whether the loaded file becomes the lot's own path and whether the lot is
	/// treated as saved.
	/// </summary>
	private bool LoadLotIntoBuilder(string path, bool recovery)
	{
		if (Scene == null) return false;

		// An in-flight editor drag holds a node this load is about to free, and the code editor may be
		// sitting on a script file the loaded lot does not contain.
		Parts.QueueRelease();
		_codeEditor.FlushPendingSave();
		_codeEditor.Close();

		if (!LotLoad.Load(Scene, path, out LotArchive.LotManifest manifest, out LotSceneLoad.LoadReport report, out string error))
		{
			ShowLotError("Could not open the lot.\n\n" + error);
			return false;
		}

		_lotCreatedUnix = manifest.CreatedUnix;
		if (!string.IsNullOrWhiteSpace(manifest.Name)) LotName = manifest.Name;

		if (recovery)
		{
			// Unsaved work: no destination has been chosen for it, so the quit prompt must still fire and
			// Ctrl+S must ask for a file. MarkDirty is exactly that state (dirty outside the undo stack),
			// so undoing edits cannot silently clear it.
			MarkDirty();
		}
		else
		{
			_lotPath = path;
			MarkSaved();
		}

		ShowLotMessage(DescribeLoad(path, report));
		return true;
	}

	private void ShowLotMessage(string message)
	{
		// The modal is the acknowledgement; the Output window keeps the history (milestone 2.8).
		LotLog.Info("lot", message);
		_lotMessage = message;
		_lotMessageRequested = true;
	}

	/// <summary>The failure half of <see cref="ShowLotMessage"/>: same modal, but the Output entry
	/// is an error so it survives an "errors only" filter.</summary>
	private void ShowLotError(string message)
	{
		LotLog.Error("lot", message);
		_lotMessage = message;
		_lotMessageRequested = true;
	}

	private static string DescribeSave(string path, System.Collections.Generic.List<string> missing)
	{
		string text = "Saved " + path;
		if (missing != null && missing.Count > 0)
		{
			// The save still succeeded; these are the pieces that could not be carried along.
			text += "\n\n" + missing.Count + " item(s) had no source file left to embed and were left out:\n"
				+ string.Join("\n", missing);
		}
		return text;
	}

	private static string DescribeLoad(string path, LotSceneLoad.LoadReport report)
	{
		string text = "Opened " + path;
		if (report != null && report.Warnings.Count > 0)
		{
			text += "\n\n" + report.Warnings.Count + " item(s) could not be restored:\n"
				+ string.Join("\n", report.Warnings);
		}
		return text;
	}

	/// <summary>The save/load outcome modal — one message and an OK.</summary>
	private void DrawLotMessage()
	{
		_lotMessageOpen = ImGui.IsPopupOpen("Lot File");
		if (!ImGui.BeginPopupModal("Lot File")) return;

		ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 460f);
		ImGui.TextWrapped(_lotMessage);
		ImGui.PopTextWrapPos();
		ImGui.Spacing();
		if (ImGui.Button("OK", 100f, 0f)) ImGui.CloseCurrentPopup();
		ImGui.EndPopup();
	}


	/// <summary>
	/// The crash-recovery prompt (milestone 4.1). Offered when the previous session left a snapshot — which
	/// means it did not close cleanly, since a deliberate exit drops the crash marker.
	///
	/// Recover hands the snapshot to the same loader Open Lot… uses (see <see cref="RecoverFromSnapshot"/>),
	/// and the load is deferred to the top of the next layout pass rather than run here, because this modal
	/// is drawn after the panels have already walked the scene.
	/// </summary>

	private void DrawRecoveryPrompt()
	{
		_recoveryOpen = ImGui.IsPopupOpen("Recover Autosave");
		if (!ImGui.BeginPopupModal("Recover Autosave")) return;

		ImGui.Text("OpenLot found a snapshot from a session that did not close cleanly.");
		ImGui.TextWrapped("It is the last automatic snapshot, not necessarily the last manual save.");
		ImGui.Spacing();
		ImGui.TextWrapped("Recovering loads it without picking a file: the first save will ask where to put it, and "
			+ "the snapshot is deleted once that save succeeds.");
		ImGui.Spacing();

		if (ImGui.Button("Recover", 180f, 0f))
		{
			// Deferred: the load runs at the top of the next layout pass, not here (see _recoverRequested).
			_recoverRequested = true;
			ImGui.CloseCurrentPopup();
		}
		ImGui.SameLine();
		if (ImGui.Button("Discard", 120f, 0f))
		{
			LotAutosave.DiscardSnapshot();
			LotLog.Info("lot", "recovery snapshot discarded");
			GD.Print("[Builder] discarded the recovery snapshot.");
			ImGui.CloseCurrentPopup();
		}
		ImGui.EndPopup();
	}
	/// <summary>
	/// The secret-content caution (milestone 4.1). Revealing a lot's base content is a deliberate action
	/// behind this prompt, so a creator cannot wander into editing shared files by accident; cancelling
	/// leaves the editor exactly as it was.
	/// </summary>
	private void DrawSecretContentWarning()
	{
		_secretWarningOpen = ImGui.IsPopupOpen("Secret Content Warning");
		if (!ImGui.BeginPopupModal("Secret Content Warning")) return;

		ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 460f);
		ImGui.TextWrapped(SecretContent.RevealWarning);
		ImGui.PopTextWrapPos();
		ImGui.Spacing();

		if (ImGui.Button("I understand, show it", 200f, 0f))
		{
			SecretContent.Reveal();
			LotLog.Info("editor", "secret content revealed (View menu)");
			GD.Print("[Builder] secret content revealed (View menu).");
			ImGui.CloseCurrentPopup();
		}
		ImGui.SameLine();
		if (ImGui.Button("Cancel", 100f, 0f))
			ImGui.CloseCurrentPopup();
		ImGui.EndPopup();
	}
}