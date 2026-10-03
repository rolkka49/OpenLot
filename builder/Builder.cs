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
	public SelectionOutline Outline { get; private set; }
	public ToolboxPanel Toolbox { get { return _toolbox; } }

	/// <summary>Active builder interaction mode. UI mode gates the viewport's 3D input paths.</summary>
	public BuilderMode Mode { get; private set; } = BuilderMode.Build;

	public string LotName { get; private set; } = "Untitled Lot";
	public bool HasUnsavedChanges { get; private set; }

	private HierarchyPanel _hierarchy;
	private ToolboxPanel _toolbox;
	private InspectorPanel _inspector;
	private CodeEditorWindow _codeEditor;
	private BuilderCommandLine _commandLine;

	// Modal state. _pauseOpen/_confirmOpen are sampled inside the layout pass (ImGui popup
	// state is only valid there); the request flags are fed from _UnhandledInput.
	private bool _pauseRequested = false;
	private bool _pauseOpen = false;
	private bool _closePause = false;
	private bool _confirmOpen = false;
	private bool _openConfirm = false;

	public override void _Ready()
	{
		Instance = this;
		Selection = new SelectionManager();
		Scripts = new ScriptManager(this);
		Scene = GetNode<BuilderScene>("Scene");
		ViewportWindow = new ViewportWindow(Scene);
		_hierarchy = new HierarchyPanel(this);
		_toolbox = new ToolboxPanel(this);
		Gizmo = new GizmoController(this);
		UiGizmo = new UiGizmoController(this);
		Parts = new PartDragController(this, Scene);
		Outline = new SelectionOutline(this);
		_inspector = new InspectorPanel(this);
		_codeEditor = new CodeEditorWindow(this);
		_commandLine = new BuilderCommandLine(this);

		ImGui.OnLayout(DrawLayout);
	}

	public override void _Process(double delta)
	{
		// Debounced script saving runs outside the ImGui layout (delta time from the engine).
		_codeEditor.Tick(delta);
	}

	public override void _PhysicsProcess(double delta)
	{
		// Picking and the collision-resolved part drag must run in the physics step: Godot only
		// allows space-state queries and kinematic moves there.
		Parts.Tick();
	}

	public override void _ExitTree()
	{
		// The ImGui autoload outlives this scene; without disconnecting, the layout handler
		// would keep running (and draw over the next scene) after this node is freed.
		ImGui.DisconnectLayout(DrawLayout);
		// The gizmo and the outline hold Selection.OnSelectionChanged subscriptions; drop them
		// before the node dies.
		Outline.Shutdown();
		Gizmo.Shutdown();
		UiGizmo.Shutdown();
		if (Instance == this) Instance = null;
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		InputEventKey keyEvent = @event as InputEventKey;
		if (keyEvent == null || !keyEvent.Pressed || keyEvent.Echo) return;

		// A player session owns Escape: it leaves the session instead of opening the pause modal.
		if (keyEvent.Keycode == Key.Escape && InTestMode)
		{
			ExitTestMode();
			return;
		}

		if (keyEvent.Keycode == Key.Escape && !_confirmOpen)
		{
			if (_pauseOpen) _closePause = true;
			else _pauseRequested = true;
		}
	}

	public void MarkDirty() => HasUnsavedChanges = true;
	public void MarkSaved() => HasUnsavedChanges = false;

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

	public void RequestQuit()
	{
		if (HasUnsavedChanges) _openConfirm = true;
		else ExitToTerminal();
	}

	public void ExitToTerminal()
	{
		HasUnsavedChanges = false;
		GetTree().ChangeSceneToFile("res://terminal.tscn");
	}

	public void OpenCodeEditor(string path, string code)
	{
		_codeEditor.Open(path, code);
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

		ImGui.DockspaceOverMainViewport();
		DrawMainMenuBar();

		// Camera input dies the moment a modal is up, on top of the viewport focus gate.
		ViewportWindow.InputEnabled = !(_pauseOpen || _confirmOpen);

		ViewportWindow.Draw(this);
		_hierarchy.Draw(this);
		_toolbox.Draw(this);
		_inspector.Draw(this);
		_commandLine.Draw(this);
		if (_codeEditor.IsOpen) _codeEditor.Draw(this);

		if (_openConfirm)
		{
			ImGui.OpenPopup("Unsaved Changes");
			_openConfirm = false;
		}
		DrawPauseMenu();
	}

	private void DrawMainMenuBar()
	{
		if (!ImGui.BeginMainMenuBar()) return;

		if (ImGui.BeginMenu("File"))
		{
			if (ImGui.MenuItem("Save Lot"))
			{
				MarkSaved();
				GD.Print("[TopMenuBar] Save Lot requested (lot file persistence lands with milestone 2.3).");
			}
			ImGui.Separator();
			if (ImGui.MenuItem("Exit to Terminal"))
				RequestQuit();
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
			{
				MarkSaved();
				GD.Print("[Builder] Save Lot requested (lot file persistence lands with milestone 2.3).");
			}
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
				MarkSaved();
				ExitToTerminal();
			}
			ImGui.SameLine();
			if (ImGui.Button("Discard && Quit", 170f, 0f))
				ExitToTerminal();
			ImGui.SameLine();
			if (ImGui.Button("Cancel", 90f, 0f))
				ImGui.CloseCurrentPopup();
			ImGui.EndPopup();
		}
	}
}