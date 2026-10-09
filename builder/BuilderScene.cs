using System.Collections.Generic;
using Godot;

/// <summary>
/// Builds and owns the isolated 3D lot world rendered into the builder viewport: a SubViewport
/// with its own World3D (procedural sky, shadowed sun, freecam camera) plus the LotRoot /
/// LotUIRoot containers every object and script in a lot lives under. Also hosts the integer
/// handle registry that the Lua API surface addresses objects through.
/// </summary>
public partial class BuilderScene : Node3D
{
	public SubViewport LotViewport { get; private set; }
	public Node3D LotRoot { get; private set; }
	public Node LotUIRoot { get; private set; }
	public FreecamController Freecam { get; private set; }
	public LotLuaApi Api { get; private set; }

	/// <summary>Per-entity script loading and the per-frame net drain (milestone 3.2 step 4).</summary>
	public ScriptRuntime LuaScripts { get; private set; }

	/// <summary>
	/// Live creation-environment lighting (milestone 2.5): time of day, sun angle and colour, sky
	/// colours and fog. Session-only — see <see cref="LotEnvironmentSettings"/> for why it is not
	/// serialized into a lot and not on the undo stack.
	/// <see cref="ApplyEnvironment"/> is the only writer of these values.
	/// </summary>
	public LotEnvironmentSettings Environment { get; private set; } = LotEnvironmentSettings.Default();

	// The Godot objects ApplyEnvironment writes to. They are fields (rather than the old locals)
	// because milestone 2.5's whole point is that the sky and sun are now live, editable state.
	private Godot.Environment _environment;
	private ProceduralSkyMaterial _skyMaterial;
	private DirectionalLight3D _sun;

	/// <summary>True while the lot runs as a player session (Test/Game mode, §3.4): editor tooling
	/// is suspended, the freecam has yielded to the player camera, and scripts were reloaded.</summary>
	public bool InTestMode { get; private set; }

	/// <summary>True while the session also hides the creation-environment UI (Game mode).</summary>
	public bool InGameMode { get; private set; }

	/// <summary>The player session's orbital camera (created on first entry, then reused).</summary>
	public OrbitalCamera PlayerCamera { get; private set; }

	/// <summary>The kinematic player character controller (created lazily, attached per session).</summary>
	public CapsuleController Player { get; private set; }

	/// <summary>The shared camera input source (the Viewport window owns the ImGui layout state).</summary>
	private ICameraInputSource _cameraInput;

	/// <summary>Smallest canvas the UI editor accepts, so a collapsed/undocked Viewport window can
	/// never make element clamping collapse every element to nothing.</summary>
	public const float MinUiCanvasSize = 32f;

	/// <summary>Meta key carrying a node's Lua-facing handle. Public because the constraint
	/// persistence (§3.6) maps nodes to handles by it, the same map handles are assigned through.</summary>
	public const string HandleMeta = "openlot_handle";

#if DEBUG
	/// <summary>Physics frames to wait before running the heavy integration suite (see _Process).</summary>
	private const int IntegrationSuitePhysicsDelay = 8;
	private bool _integrationSuiteDone;
	private bool _editorSuiteDone;
	private bool _constraintSuiteDone;

	/// <summary>
	/// Heavy self-tests (a 20k-encode loop, an 8 MB out-of-memory, a ~10 MB allocation) run only when
	/// explicitly requested via the environment variable OPENLOT_HEAVY_TESTS (set to anything but
	/// empty/"0"). They are slow and one deliberately poisons its own private VM, so they stay out of
	/// the always-on fast path. Everything they touch is a private Lua state — never the lot VM.
	/// </summary>
	private static bool HeavyTestsEnabled()
	{
		string value = System.Environment.GetEnvironmentVariable("OPENLOT_HEAVY_TESTS");
		return !string.IsNullOrEmpty(value) && value != "0";
	}
#endif

	/// <summary>
	/// Size of the UI canvas in pixels — the displayed 3D image rect. Owned by ViewportWindow, which
	/// refreshes it every frame; the initial value matches the SubViewport's start size so clamping
	/// is already sane before the first layout pass. Lot UI elements are clamped inside this rect, so
	/// they can never cross outside the viewport picture frame.
	/// </summary>
	public Vector2 UiCanvasBounds { get; private set; } = new Vector2(960f, 540f);

	/// <summary>
	/// Updates the UI canvas rect and re-clamps every element that no longer fits, so shrinking the
	/// viewport cannot strand elements outside the picture frame. Degenerate sizes are ignored.
	/// </summary>
	public void SetUiCanvasBounds(Vector2 bounds)
	{
		if (bounds.X < MinUiCanvasSize || bounds.Y < MinUiCanvasSize) return;
		if (bounds == UiCanvasBounds) return;

		UiCanvasBounds = bounds;
		if (ClampUiElementsToCanvas()) Builder.Instance?.MarkDirty();
	}

	/// <summary>Re-clamps every UI element into the canvas. Returns true when anything moved.</summary>
	private bool ClampUiElementsToCanvas()
	{
		if (LotUIRoot == null) return false;
		bool changed = false;
		int count = LotUIRoot.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			LotUIElement element = LotUIRoot.GetChild(i) as LotUIElement;
			if (element == null) continue;

			Vector2 pos = element.Position;
			Vector2 size = element.Size;
			UiGizmoMath.ClampRect(ref pos, ref size, UiCanvasBounds);
			if (pos == element.Position && size == element.Size) continue;

			element.Position = pos;
			element.Size = size;
			changed = true;
		}
		return changed;
	}

	private readonly Dictionary<int, Node> _handles = new Dictionary<int, Node>();
	private int _nextHandle = 1;

	public override void _Ready()
	{
		// A fresh session starts from the all-collide default (§3.5); a loaded lot overwrites this
		// from its archive in ReplaceLotFromArchive. Without the reset, a matrix edited while one lot
		// was open would leak into a brand-new one.
		LotCollisionGroups.ResetAllCollisions();
		BuildViewport();
		RegisterLotRootHandle();
		Api = new LotLuaApi(this);
		LuaManager.Instance.Initialize(Api);
		SpawnDefaultLot();

		// Handle coverage before any script runs, so a script can address any other entity
		// (step-5 option A) and handles follow the deterministic load order.
		AssignLoadOrderHandles();

		// Load the lot's scripts (step 4). The lot root is itself an entity (handle 0), so its
		// script runs here at lot load — a fresh lot can spawn its character/camera from Lua.
		StartScriptRuntime();

#if DEBUG
		// V1 verification of the Im3d translation-gizmo port: hand-computed math and state-machine
		// cases, printed as pass/fail lines. See GizmoSelfTest for what each check pins down.
		GizmoSelfTest.Run();
		// Milestone 3.2 (net.*) verification; step 1 covers NetSerializer, further suites land
		// with their implementation steps. See NetSelfTest for what each check pins down. Fast set
		// always runs on private Lua states; the heavy resource tests are opt-in (OPENLOT_HEAVY_TESTS).
		NetSelfTest.Run(HeavyTestsEnabled());
		// Milestone 2.3 image pipeline: cover-fit arithmetic plus the shared texture registry's
		// reference counting and the wallpaper's set/swap/clear bookkeeping.
		WallpaperSelfTest.Run();
		// Milestone 2.3 property system: the declaration table, the part properties and their
		// texture reference lifetimes.
		LotPropertySelfTest.Run();
		// Milestone 3.5 collision groups: the group table, the interaction matrix and how a part's
		// layer/mask are derived from them.
		CollisionGroupSelfTest.Run();
		// Milestone 2.5 environment model: the preset table (including the "Day == the original
		// fixed sky" contract) and the time-of-day sun curve. Pure data/math only — the rendered
		// result is an eye check, like the 2D wallpaper.
		EnvironmentSelfTest.Run();
		// Milestone 4.1 archive format: the version gate, the manifest / payload / environment JSON
		// round-trip, and the LotVfs streaming read path through a real zip on disk. Loads nothing
		// into the scene — that is milestone 4.2's business.
		LotArchiveSelfTest.Run();
		// Milestone 2.6 decals: the face/offset/scale placement math, the derived transform against
		// a live host (including a scaled one), the property declarations, and a capture/restore of
		// a decal with its host link and image intact.
		DecalSelfTest.Run();
		// Milestone 2.8 log sink: the bounded ring, its drop-oldest order, and the print/log
		// script-output path through the real VM.
		LotLogSelfTest.Run();
		// Integration fixture: drives the REAL LuaManager + ScriptRuntime with real script files
		// (stands in for hands-on testing until milestone 3.4).
		// The heavy integration suite is deferred to _Process (see IntegrationSuitePhysicsDelay):
		// running it here made its allocation/VM-rebuild cost perturb the physics smoke test's
		// step timing (verified by a control run).
#endif
	}

	/// <summary>
	/// Starts (or restarts) the per-entity script runtime: a fresh ScriptRuntime over the current
	/// VM, then one clean-slate LoadLot (net registries cleared; load-time-owned spawns from the
	/// previous run removed first — ScriptRuntime.LoadIntoBridge). This is the re-entry seam
	/// Test mode (§3.4) calls around a lot reload; _Ready is just the first entry.
	/// </summary>
	public void StartScriptRuntime()
	{
		if (!LuaManager.Instance.IsRuntimeAvailable) return;
		StopScriptRuntime();
		LuaScripts = new ScriptRuntime(() => LuaManager.Instance.NetBridge,
			// Every script-runtime diagnostic (load failures, dispatch errors, quarantines) reaches
			// the Output window and the engine console from this one place.
			message => { LotLog.Warn("script", message); GD.PushWarning(message); },
			LuaManager.Instance.ProcessDeferredRecreate, () => LuaManager.Instance.ScriptingSuspended);
		LuaScripts.LoadLot(LotRoot, EnsureEntityHandle, ReadScriptFile, DestroyEntity);
	}

	/// <summary>
	/// Stops the script runtime, dropping its bookkeeping and load context. Safe to call when it
	/// is not running. The VM itself stays up — only scene exit tears it down (_ExitTree) — so a
	/// start/stop cycle never pays the VM build cost twice.
	/// </summary>
	public void StopScriptRuntime()
	{
		if (LuaScripts == null) return;
		LuaScripts.Stop();
		LuaScripts = null;
	}

	// --- mechanical constraints (§3.6) -------------------------------------------------------------

	/// <summary>The live constraint session (joints + dynamic bodies), or null outside a player
	/// session. Owned here so every entry/exit/reload path tears the joints down with the world they
	/// were built against.</summary>
	public LotConstraintSession ConstraintSession { get { return _constraints; } }

	private LotConstraintSession _constraints;

	/// <summary>
	/// The authored world as it was when the running session started (§3.4/§3.6). Test mode runs on
	/// the live tree, so leaving the session restores every authored entity from this snapshot —
	/// that is what makes a session an instance of the lot rather than a mutation of it.
	/// </summary>
	private LotSessionSnapshot _sessionSnapshot;

	/// <summary>
	/// Builds the session's constraint state: swaps unanchored parts to simulated bodies and creates
	/// the joints for every active record. No-op outside a session, so callers (the picker, the undo
	/// stack, a Lua verb) can call it unconditionally after changing the table.
	/// </summary>
	public void RebuildConstraints()
	{
		if (!InTestMode) return;
		if (_constraints == null) _constraints = new LotConstraintSession(this);
		_constraints.Build(FindCharacter());
	}

	/// <summary>Frees the session's joints and returns every simulated part to its static body.</summary>
	public void TeardownConstraints()
	{
		if (_constraints == null) return;
		_constraints.Teardown();
		_constraints = null;
	}

	/// <summary>
	/// Replaces the live lot with the decoded contents of an archive (§4.2). The caller has already
	/// validated the archive; this is the mutating half, and the only place the scene-level consequences
	/// live — the old world and its script runtime come down first, the containers are rebuilt, handles
	/// are re-issued in the new load order, and the scripts run again.
	///
	/// Order is load-bearing: the runtime stops while the world it is bound to still exists, editor state
	/// that points at about-to-be-freed nodes is dropped before the containers are emptied, and the
	/// runtime restarts only once the new tree and its handles are in place.
	/// </summary>
	public LotSceneLoad.LoadReport ReplaceLotFromArchive(LotArchive.LotDocument document, LotVfs vfs)
	{
		StopScriptRuntime();
		// The incoming world replaces the nodes the joints were built against; freeing them first is
		// cheaper and safer than letting the cleanup walk find them one by one.
		TeardownConstraints();
		if (Builder.Instance != null)
		{
			Builder.Instance.Selection.ClearSelection();
			// The history holds detached nodes across undo; the whole world it described is going away.
			Builder.Instance.History.Clear();
		}

		EmptyContainer(LotRoot);
		EmptyContainer(LotUIRoot);

		LotSceneLoad.LoadReport report = new LotSceneLoad.LoadReport();
		// The restore fills this list index-aligned with the object records, which is what the
		// constraint load below resolves its saved indices against.
		System.Collections.Generic.List<Node> restoredObjects = new System.Collections.Generic.List<Node>();
		LotSceneLoad.Restore(LotRoot, LotUIRoot, document, vfs, report, restoredObjects);

		// An older lot may carry no environment block: reset rather than inherit, so loading one lot can
		// never leak another lot's sky into it.
		ApplyEnvironment(document.Environment ?? LotEnvironmentSettings.Default());

		// Collision groups are lot state too (§3.5): reset to the all-collide default then apply what
		// the archive carried. The refresh must run *after* the matrix is in place, because the parts
		// restored above derived their masks from whatever matrix was live at the time.
		LotCollisionGroups.FromJson(document.Collision);
		RefreshCollision();

		// Re-issued from a clean registry, so a loaded lot numbers its entities exactly as the same lot
		// built from scratch would — the deterministic handle order the net layer relies on (§3.2/§9).
		ResetHandleRegistry();
		AssignLoadOrderHandles();

		// Constraints (§3.6) resolve their saved object indices to handles, so they load after the
		// handles exist; a lot without the block clears the previous lot's links.
		LotConstraints.FromJson(document.Constraints, restoredObjects, EnsureEntityHandle, report.Warnings);

		StartScriptRuntime();
		return report;
	}

	/// <summary>
	/// Empties a lot container, forgetting each child's handle and dropping its texture references, so a
	/// replaced world leaves no stale registry entry or pinned image behind.
	///
	/// Free is immediate rather than queued: the restore needs a genuinely empty container in this same
	/// frame, and QueueFree would leave the old nodes in the tree until the frame ends — the new ones
	/// would then be added alongside them.
	/// </summary>
	private void EmptyContainer(Node container)
	{
		if (container == null) return;
		for (int i = container.GetChildCount() - 1; i >= 0; i--)
		{
			Node child = container.GetChild(i);
			CleanupRecursive(child);
			container.RemoveChild(child);
			child.Free();
		}
	}

	/// <summary>
	/// Binds the cameras to the shared input source. The Viewport window owns the ImGui layout
	/// state, so it is the one place the per-frame input conditions are resolved (§3.4); the
	/// freecam and the player camera both read from it and never both act.
	/// </summary>
	public void BindCameraInput(ICameraInputSource source)
	{
		_cameraInput = source;
		Freecam.Bind(source);
		if (PlayerCamera != null) PlayerCamera.Bind(source);
	}

	/// <summary>
	/// Enters the player session (§3.4). Test mode runs the lot in place; Game mode runs the same
	/// session with the creation-environment UI hidden. Both reload the lot's scripts first, so
	/// start() re-runs and script-owned spawns (the character among them) are re-created before the
	/// controller looks for them — the reload-on-transition decision.
	///
	/// The authored world is snapshotted first (<see cref="LotSessionSnapshot"/>), so leaving the
	/// session can put it back exactly as it was: the session is an instance of the lot, and
	/// whatever physics or scripts do to it stays in the session.
	/// </summary>
	public void EnterTestMode(bool gameMode)
	{
		if (InTestMode) return;
		InTestMode = true;
		InGameMode = gameMode;

		// Before the reload, so the snapshot is exactly the world the creator is looking at.
		_sessionSnapshot = LotSessionSnapshot.Capture(this);

		StartScriptRuntime();
		// Deliberately OUTSIDE StartScriptRuntime: the session's body swap and joints are physics,
		// and they must exist even when the Lua runtime is unavailable (in which case the call above
		// no-ops). Scripts have loaded by now, so script-spawned parts are in the tree to swap.
		RebuildConstraints();

		EnsurePlayerCamera();
		if (Player == null) Player = new CapsuleController(this);
		LotObject character = FindCharacter();
		if (character != null)
		{
			Player.Attach(character);
		}
		else
		{
			LotLog.Warn("session", "player session started with no capsule character in the lot; "
				+ "the player camera follows the lot origin (spawn one with Lot.SpawnCapsule or the Toolbox).");
			GD.PushWarning("[BuilderScene] player session started with no capsule character in the lot; " +
				"the player camera follows the lot origin (spawn one with Lot.SpawnCapsule or the Toolbox).");
		}

		// Seed the orbit from the freecam so the handoff does not snap the view.
		PlayerCamera.SeedOrbit(Freecam.RotationDegrees.Y, Freecam.RotationDegrees.X, Player.Position);
		PlayerCamera.SetTarget(Player.Position);

		// The handoff itself: exactly one camera answers the shared input source from here on.
		PlayerCamera.Current = true;
		Freecam.Current = false;
	}

	/// <summary>
	/// Leaves the player session and returns to the build-mode world: the character controller is
	/// detached, the freecam takes the view back, and the authored world is restored from the
	/// snapshot taken on entry — physics results, script edits, session spawns and table changes all
	/// stay in the session, so the lot is exactly as the creator left it before pressing Test.
	/// </summary>
	public void ExitTestMode()
	{
		if (!InTestMode) return;
		InTestMode = false;
		InGameMode = false;

		if (Player != null) Player.Detach();
		if (PlayerCamera != null) PlayerCamera.Current = false;
		Freecam.Current = true;

		// Constraints first: the joints reference the bodies this session swapped in, and the
		// runtime restart below destroys (and re-creates) script-owned parts.
		TeardownConstraints();
		StartScriptRuntime();

		// Last, so the reload's script-owned spawns are already in place to be recognised and kept.
		LotSessionSnapshot snapshot = _sessionSnapshot;
		_sessionSnapshot = null;
		if (snapshot != null) snapshot.Restore(this);
	}

	/// <summary>
	/// The lot's character: the first Capsule object in load order. A brand-new lot's default
	/// script spawns exactly one, so this is the character the player session takes over. Null when
	/// the lot has no capsule at all (the session still runs, just without a controllable player).
	/// </summary>
	public LotObject FindCharacter()
	{
		int count = LotRoot.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			LotObject obj = LotRoot.GetChild(i) as LotObject;
			if (obj == null || obj.IsQueuedForDeletion()) continue;
			if (obj.Kind == LotObjectKind.Capsule) return obj;
		}
		return null;
	}

	/// <summary>Creates the player camera on first use and keeps it bound to the shared input
	/// source. It lives in the lot viewport, a sibling of the freecam.</summary>
	private void EnsurePlayerCamera()
	{
		if (PlayerCamera != null && GodotObject.IsInstanceValid(PlayerCamera)) return;
		PlayerCamera = new OrbitalCamera();
		PlayerCamera.Name = "PlayerCamera";
		PlayerCamera.SetMeta(LotObject.InternalChildMeta, true);
		LotViewport.AddChild(PlayerCamera);
		if (_cameraInput != null) PlayerCamera.Bind(_cameraInput);
	}

#if DEBUG
	// DEBUG smoke-test state for part picking and dragging. Space-state queries and kinematic moves
	// are only legal during the physics step, so the props are added on one step and exercised a
	// couple of steps later, once their bodies have registered in the lot's physics space.
	private int _physicsSmokeStep;
	private LotObject _physicsSmokeProbe;
	private LotObject _physicsSmokeDragged;
	private LotObject _physicsSmokeBlocker;
	private LotObject _physicsSmokeCeiling;
	private CharacterBody3D _physicsSmokeMover;
	private CollisionShape3D _physicsSmokeMoverShape;

	private void RunPhysicsSmokeTest()
	{
		_physicsSmokeStep++;
		if (_physicsSmokeStep == 1)
		{
			_physicsSmokeProbe = SpawnSmokeObject("PickProbe", new Vector3(3f, 2f, 0f));
			_physicsSmokeDragged = SpawnSmokeObject("DragProbe", new Vector3(0f, 1f, 0f));
			_physicsSmokeBlocker = SpawnSmokeObject("DragBlocker", new Vector3(2f, 1f, 0f));
			_physicsSmokeCeiling = SpawnSmokeObject("DragCeiling", new Vector3(0f, 3f, 0f));
			_physicsSmokeMover = PartDragController.CreateMover(this, out _physicsSmokeMoverShape);
		}
		else if (_physicsSmokeStep == 3)
		{
			GizmoSelfTest.RunPhysicsPick(this, _physicsSmokeProbe);
			GizmoSelfTest.RunPhysicsDrag(this, _physicsSmokeMover, _physicsSmokeMoverShape, _physicsSmokeDragged, _physicsSmokeBlocker, _physicsSmokeCeiling);

			_physicsSmokeProbe.QueueFree();
			_physicsSmokeDragged.QueueFree();
			_physicsSmokeBlocker.QueueFree();
			_physicsSmokeCeiling.QueueFree();
			_physicsSmokeMover.QueueFree();
			_physicsSmokeProbe = null;
			_physicsSmokeDragged = null;
			_physicsSmokeBlocker = null;
			_physicsSmokeCeiling = null;
			_physicsSmokeMover = null;
			_physicsSmokeMoverShape = null;
		}
	}

	/// <summary>Invisible collision-only prop used by the physics smoke test.</summary>
	private LotObject SpawnSmokeObject(string name, Vector3 at)
	{
		LotObject obj = LotObject.Create(LotObjectKind.Cube, name, new Color(1f, 1f, 1f));
		obj.Position = at;
		obj.Visible = false; // collision geometry only; it must not flash in the viewport
		LotRoot.AddChild(obj);
		return obj;
	}
#endif

	/// <summary>
	/// Physics tick of the lot world. A player session's movement runs here, because kinematic moves
	/// are only legal during the physics step; the DEBUG physics smoke test is skipped during a
	/// session so its throwaway bodies cannot collide with the real character (§3.4 seam).
	/// </summary>
	public override void _PhysicsProcess(double delta)
	{
#if DEBUG
		if (!InTestMode) RunPhysicsSmokeTest();
#endif
		if (!InTestMode) return;

		// Simulated parts puppet their physics bodies (the body is a sibling, see
		// LotObject.SetSimulated), so this is what makes a falling cube actually fall on screen.
		if (_constraints != null) _constraints.SyncVisuals();

		// Movement is camera-relative: the controller reads the orbit yaw, then the camera follows
		// where the swept body ended up.
		Player.SetYaw(PlayerCamera.Yaw);
		Player.Tick(delta);
		PlayerCamera.SetTarget(Player.Position);
	}

	/// <summary>Frame tick for the scripting layer: starts the frame's instruction accounting,
	/// drains the net queue, then processes a deferred VM rebuild (see ScriptRuntime.Tick).</summary>
	public override void _Process(double delta)
	{
		if (LuaScripts != null) LuaScripts.Tick();
#if DEBUG
		// The net integration suite deliberately waits a few physics frames: its OOM churn and
		// ~5M-instruction script loads would otherwise slow the startup frame enough to change the
		// physics smoke test's step timing (control-run verified: it flips that test's resting-slide
		// assertion). Anything that heavy must not share a frame with it.
		if (!_integrationSuiteDone && Engine.GetPhysicsFrames() >= IntegrationSuitePhysicsDelay)
		{
			_integrationSuiteDone = true;
			NetIntegrationTest.Run(this);
		}
		// The editor workflow suite (milestone 2.4) needs Builder.Instance, which exists once the
		// parent Builder has finished _Ready — the same wait covers both.
		if (!_editorSuiteDone && Engine.GetPhysicsFrames() >= IntegrationSuitePhysicsDelay)
		{
			_editorSuiteDone = true;
			EditorWorkflowSelfTest.Run(this);
		}
		// The constraint suite (milestone 3.6) runs after the editor suite: it registers a scratch
		// Float property, and running last keeps that from shifting the property counts the _Ready
		// suites assert.
		if (!_constraintSuiteDone && Engine.GetPhysicsFrames() >= IntegrationSuitePhysicsDelay)
		{
			_constraintSuiteDone = true;
			ConstraintSelfTest.Run(this);
		}
		// The constraint suite's staged gravity probe needs physics frames to pass (a body only
		// moves when the engine steps), so it is advanced from here rather than inside the suite.
		ConstraintSelfTest.TickGravityProbe();
#endif
	}

	public override void _ExitTree()
	{
		// Script layer first (drops its bookkeeping), then the VM: object handles die with the lot
		// scene, so the Lua state dies with it too — a fresh state per session instead of a stale
		// singleton holding dead handles.
		StopScriptRuntime();
		LuaManager.Instance.Shutdown();
#if DEBUG
		// The integration suite waits for a few physics frames; a run short enough to quit before
		// that would otherwise look "green" while testing nothing.
		if (!_integrationSuiteDone)
			GD.PushWarning("[BuilderScene] net integration suite never ran (run ended before " +
				IntegrationSuitePhysicsDelay + " physics frames; use --quit-after 500 when verifying headless)");
		if (!_editorSuiteDone)
			GD.PushWarning("[BuilderScene] editor workflow suite never ran (run ended before " +
				IntegrationSuitePhysicsDelay + " physics frames; use --quit-after 500 when verifying headless)");
		if (!_constraintSuiteDone)
			GD.PushWarning("[BuilderScene] constraint suite never ran (run ended before " +
				IntegrationSuitePhysicsDelay + " physics frames; use --quit-after 500 when verifying headless)");
		if (_constraintSuiteDone && !ConstraintSelfTest.GravityProbeDone)
			GD.PushWarning("[BuilderScene] constraint gravity probe never finished (it needs ~1.4 s of wall-clock " +
				"runtime after the suite, so give the run room: --quit-after 500 verified headless)");
#endif
	}

	private void BuildViewport()
	{
		LotViewport = new SubViewport();
		LotViewport.OwnWorld3D = true;
		// The SubViewport is never part of the visible CanvasItem tree (its texture is blitted
		// through ImGui), so it must refresh itself every frame regardless of visibility.
		LotViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
		LotViewport.Size = new Vector2I(960, 540);
		AddChild(LotViewport);

		// The sky, sun and every value that describes them are set by ApplyEnvironment at the end of
		// this method, so milestone 2.5 has exactly one place that decides the look and this method
		// only builds the object graph.
		WorldEnvironment envNode = new WorldEnvironment();
		_environment = new Godot.Environment();
		_environment.BackgroundMode = Godot.Environment.BGMode.Sky;
		_environment.AmbientLightSource = Godot.Environment.AmbientSource.Sky;
		_skyMaterial = new ProceduralSkyMaterial();
		Sky sky = new Sky();
		sky.SkyMaterial = _skyMaterial;
		_environment.Sky = sky;
		envNode.Environment = _environment;
		LotViewport.AddChild(envNode);

		_sun = new DirectionalLight3D();
		_sun.ShadowEnabled = true;
		LotViewport.AddChild(_sun);

		Freecam = new FreecamController();
		Freecam.Position = new Vector3(0f, 3f, 8f);
		Freecam.RotationDegrees = new Vector3(-14f, 0f, 0f);
		Freecam.Current = true;
		LotViewport.AddChild(Freecam);

		LotRoot = new Node3D();
		LotRoot.Name = "LotContainer";
		LotViewport.AddChild(LotRoot);

		LotUIRoot = new Node();
		LotUIRoot.Name = "LotUICanvas";
		LotViewport.AddChild(LotUIRoot);

		// Sync the freshly built objects with the current settings (the Day default, so the first
		// frame looks exactly like the fixed sky milestone 2.5 replaced).
		ApplyEnvironment(Environment);
	}

	/// <summary>
	/// The single write path for the creation environment (milestone 2.5): pushes a settings
	/// snapshot into the lot world's Environment, ProceduralSkyMaterial and sun. The View menu only
	/// ever calls this, so what a setting does is readable in one place.
	///
	/// Session-only and not undoable by design — see <see cref="LotEnvironmentSettings"/>. Calling
	/// it before the viewport exists (during construction) only records the settings.
	/// </summary>
	public void ApplyEnvironment(LotEnvironmentSettings settings)
	{
		Environment = settings;
		if (_environment == null) return;

		// What is actually rendered. With the clock driving the sun this substitutes the keyframed sky
		// and sun colours — ProceduralSkyMaterial's own colours are static, so nothing would change
		// otherwise. The stored settings keep the manual values, so unticking the clock restores them.
		LotEnvironmentSettings render = LotEnvironmentSettings.Effective(settings);

		// The clock and the manual angle pair resolve to one sun direction here; the stored manual
		// angles are untouched, so switching the clock off restores them.
		_sun.RotationDegrees = LotEnvironmentSettings.SunRotationDegrees(render);
		_sun.LightColor = render.SunColor;
		_sun.LightEnergy = render.SunEnergy;

		// Ambient light is sourced from the sky (see BuildViewport), so these colours shade the lot
		// as well as forming the backdrop.
		_skyMaterial.SkyTopColor = render.SkyTopColor;
		_skyMaterial.SkyHorizonColor = render.SkyHorizonColor;
		_skyMaterial.GroundHorizonColor = render.GroundHorizonColor;
		_skyMaterial.GroundBottomColor = render.GroundBottomColor;

		_environment.FogEnabled = render.FogEnabled;
		_environment.FogLightColor = render.FogColor;
		_environment.FogDensity = render.FogDensity;
	}

	/// <summary>
	/// Re-derives every part's collision layer/mask from the shared group matrix (§3.5). The matrix
	/// and the parts are separate pieces of state, so this is the one walk that re-syncs them — called
	/// after the View menu's matrix editor changes a pair, after a script writes one, and after a lot
	/// loads. It is O(parts) and runs only on an explicit change, never per frame.
	/// </summary>
	public void RefreshCollision()
	{
		if (LotRoot == null) return;
		RefreshCollisionRecursive(LotRoot);
	}

	private static void RefreshCollisionRecursive(Node node)
	{
		int count = node.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			Node child = node.GetChild(i);
			// Parts can be nested (a reparented part), so this recurses rather than only walking
			// LotRoot's direct children.
			LotObject part = child as LotObject;
			if (part != null && !part.IsQueuedForDeletion()) part.RefreshCollision();
			RefreshCollisionRecursive(child);
		}
	}

	/// <summary>Default lot scaffolding: a ground plane so a brand-new lot reads as a world, plus the
	/// default character script so the lot is playable the moment a player session starts.</summary>
	private void SpawnDefaultLot()
	{
		LotObject ground = LotObject.Create(LotObjectKind.Plane, "Ground", new Color(0.45f, 0.44f, 0.42f));
		LotRoot.AddChild(ground);
		EnsureDefaultLotScript();
	}

	/// <summary>File name of the script a brand-new lot ships with. Public because it is also the
	/// single source of truth for "base content": <see cref="LotArchiveCollect"/> archives it under the
	/// secret code folder, and the two must not drift apart.</summary>
	public const string DefaultLotScriptName = "Character.lua";

	/// <summary>
	/// A brand-new lot ships with a lot-root script that spawns the default character, so it is
	/// playable as soon as Test mode starts with no manual scripting (§3.4). The file is written
	/// only when it does not exist yet, so a creator's edits are never clobbered.
	/// </summary>
	private void EnsureDefaultLotScript()
	{
		const string dir = "user://Scripts";
		if (!Godot.DirAccess.DirExistsAbsolute(dir)) Godot.DirAccess.MakeDirRecursiveAbsolute(dir);
		string path = dir + "/" + DefaultLotScriptName;
		if (!Godot.FileAccess.FileExists(path))
		{
			Godot.FileAccess file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write);
			if (file == null)
			{
				LotLog.Warn("lot", "could not create the default lot script at " + path);
				GD.PushWarning("[BuilderScene] could not create the default lot script at " + path);
				return;
			}
			file.StoreString(DefaultLotScriptCode);
			file.Dispose();
		}

		LotScriptNode node = new LotScriptNode();
		node.ScriptPath = path;
		node.DisplayName = DefaultLotScriptName;
		// Godot node names cannot contain dots; the readable ".lua" name lives on DisplayName.
		node.Name = DefaultLotScriptName.Replace('.', '_');
		LotRoot.AddChild(node);
	}

	/// <summary>
	/// Default character script for a new lot. The lot root is entity handle 0, so its script runs
	/// at lot load. The camera is created by the runtime when a player session starts — scripts
	/// cannot (and must not) touch Godot nodes (§1.1) — so this only spawns the character.
	///
	/// The capsule's origin is its centre (total height 2.0), so y = 1 stands it on the ground, and
	/// it spawns off to one side rather than at the lot origin: the DEBUG physics smoke test drives
	/// its probes through the origin region (a ray straight down at x=0, cubes at (0/2/3, 1..3, 0))
	/// and asserts exact contact geometry, so a body parked there would break it.
	/// </summary>
	private const string DefaultLotScriptCode =
		"-- Default OpenLot character: spawned at lot load so a new lot is playable immediately.\n" +
		"-- The camera is provided by the runtime when a player session (Test/Game mode) starts.\n" +
		"function start()\n" +
		"    Lot.SpawnCapsule(4, 1, 4)\n" +
		"end\n";

	// --- Lot object operations (shared by the toolbox, context menu and the Lua API) ---
	//
	// Editor operations push commands (milestone 2.4) so they are undoable; the Lua API keeps using
	// the raw create/destroy paths, because script-driven changes are runtime, not editing history.

	/// <summary>Creates a primitive WITHOUT attaching it. The editor attaches it inside a
	/// <see cref="CreateNodeCommand"/>; <see cref="SpawnPrimitive"/> is the runtime path.</summary>
	public LotObject CreatePrimitiveDetached(LotObjectKind kind, Vector3 at)
	{
		LotObject obj = LotObject.Create(kind, UniqueChildName(LotRoot, kind.ToString()), new Color(0.65f, 0.65f, 0.68f));
		obj.Position = at;
		return obj;
	}

	/// <summary>Creates and attaches a primitive (runtime/Lua path — not part of the undo history).</summary>
	public LotObject SpawnPrimitive(LotObjectKind kind, Vector3 at)
	{
		LotObject obj = CreatePrimitiveDetached(kind, at);
		LotRoot.AddChild(obj);
		return obj;
	}

	/// <summary>
	/// Creates a decal WITHOUT attaching it (editor undo path). It is named under its host so the
	/// hierarchy reads naturally; placement happens once it is attached (a decal derives its
	/// transform from the host on entry — see <see cref="LotObject.RefreshDecalTransform"/>).
	/// </summary>
	public LotObject CreateDecalDetached(LotObject host)
	{
		Node parent = host != null ? (Node)host : LotRoot;
		return LotObject.Create(LotObjectKind.Decal, UniqueChildName(parent, "Decal"), new Color(1f, 1f, 1f, 1f));
	}

	/// <summary>
	/// Creates and attaches a decal under its host part (runtime/Lua path — not part of the undo
	/// history). A null host attaches to the lot root, where the patch keeps its unit-size
	/// fallback placement until it is dropped into a part.
	/// </summary>
	public LotObject SpawnDecal(LotObject host)
	{
		LotObject decal = CreateDecalDetached(host);
		if (host != null)
		{
			host.AddChild(decal);
		}
		else
		{
			LotRoot.AddChild(decal);
		}
		return decal;
	}

	/// <summary>
	/// Editor path for a new decal (milestone 2.6): inserts one into the front (+Z) face of
	/// <paramref name="host"/> as a single undoable entry, selects it so the Inspector is
	/// immediately in front of the creator, and opens the image browser so the new panel is never
	/// left as a blank white square. Refused for a decal host — panels do not nest.
	/// </summary>
	public void InsertDecal(Builder builder, LotObject host)
	{
		InsertDecal(builder, host, DecalMath.DefaultFace, 0f, 0f);
	}

	/// <summary>
	/// The face-and-spot overload: the decal tool passes the face the creator clicked and where on
	/// it they clicked, so a panel lands exactly there. The offset is clamped by the decal itself.
	/// </summary>
	public void InsertDecal(Builder builder, LotObject host, int face, float offsetU, float offsetV)
	{
		if (builder == null || host == null || !GodotObject.IsInstanceValid(host)) return;
		if (host.Kind == LotObjectKind.Decal) return;

		LotObject decal = CreateDecalDetached(host);
		decal.SetDecalFace(face);
		decal.SetDecalOffsetU(offsetU);
		decal.SetDecalOffsetV(offsetV);
		builder.History.Push(new CreateNodeCommand("Create Decal", decal, host, host.GetChildCount(),
			b => b.Selection.Select(decal, false)));

		PromptDecalImage(builder, decal);
	}

	/// <summary>
	/// Opens the image browser for a decal's Image content, importing through the same single
	/// image path the part Texture property uses. Shared by the toolbox button, the hierarchy
	/// context menu and the decal tool's click-to-place, so a fresh decal always offers an image
	/// immediately; cancelling simply leaves the panel as-is.
	/// </summary>
	public static void PromptDecalImage(Builder builder, LotObject decal)
	{
		if (builder == null || decal == null) return;
		ImageFilePicker.Open(builder, "Choose a decal image", path =>
		{
			if (!GodotObject.IsInstanceValid(decal)) return;
			if (!builder.Properties.SetTextureFromFile(decal, path, out string error) && error.Length > 0)
			{
				LotLog.Warn("decal", error);
				GD.PushWarning("[Decal] " + error);
			}
		});
	}

	/// <summary>Creates a UI element WITHOUT attaching it (editor undo path).</summary>
	public LotUIElement CreateUIElementDetached(LotUIKind kind, Vector2 pos, Vector2 size, string label)
	{
		return LotUIElement.Create(kind, pos, size, label);
	}

	/// <summary>Creates and attaches a UI element (runtime/Lua path — not part of the undo history).</summary>
	public LotUIElement SpawnUIElement(LotUIKind kind, Vector2 pos, Vector2 size, string label)
	{
		LotUIElement el = CreateUIElementDetached(kind, pos, size, label);
		LotUIRoot.AddChild(el);
		return el;
	}

	/// <summary>
	/// Editor delete (milestone 2.4). The command detaches the node instead of freeing it, so undo
	/// restores the same node into the same slot; the node is freed only if the entry is dropped
	/// while the deletion is still in effect.
	/// </summary>
	public void DeleteNode(Node target)
	{
		if (target == null || !GodotObject.IsInstanceValid(target)) return;
		Node parent = target.GetParent();
		if (parent == null) return;
		Builder.Instance.History.Push(new DeleteNodeCommand(target, parent, target.GetIndex()));
	}

	/// <summary>
	/// The single entity-destroy path (F2 cleanup + the entity-destroy hook): forgets every handle in
	/// the subtree, clears each handle's net declarations / circuit-breaker record / script name from
	/// the VM and router, then frees the node. Editor deletion and the reload cleanup both use it.
	/// </summary>
	public void DestroyEntity(Node node)
	{
		if (node == null || !GodotObject.IsInstanceValid(node)) return;
		CleanupRecursive(node);
		node.QueueFree();
	}

	/// <summary>
	/// The single subtree-cleanup walk over a node being destroyed: forgets every handle (and its
	/// net/router bookkeeping) and drops every part's texture reference, so a deleted part cannot
	/// pin an image in <see cref="LotTextureCache"/>.
	/// </summary>
	private void CleanupRecursive(Node node)
	{
		LotObject part = node as LotObject;
		if (part != null) part.ReleaseTexture();

		if (node.HasMeta(HandleMeta))
		{
			int handle = node.GetMeta(HandleMeta).AsInt32();
			if (handle != ScriptRuntime.LotRootHandle)
			{
				ForgetHandle(handle);
				LuaNetBridge bridge = LuaManager.Instance.NetBridge;
				if (bridge != null) bridge.ForgetEntity(handle);
				// A constraint must never outlive a part it links (a joint whose body node is gone
				// is a crash), so the records go first and the live joints follow them.
				LotConstraints.RemoveFor(handle);
				if (_constraints != null) _constraints.RemoveJointsFor(handle);
				// A simulated part's body lives at the lot root (physics is its parent), so freeing
				// the part does not free it — release it or it outlives its part as a ghost collider.
				if (part != null) part.ReleaseSimulatedBody();
			}
		}
		int count = node.GetChildCount();
		for (int i = 0; i < count; i++) CleanupRecursive(node.GetChild(i));
	}

	/// <summary>Duplicates a lot node and returns the copy DETACHED — the caller attaches it, and the
	/// editor does so inside a <see cref="CreateNodeCommand"/> so the duplicate is undoable. Returns
	/// null when the node kind cannot be duplicated (script placeholders are file references).</summary>
	public Node DuplicateNode(Node src)
	{
		LotObject lotObject = src as LotObject;
		if (lotObject != null)
		{
			if (lotObject.Kind == LotObjectKind.Decal)
			{
				// A decal copy carries the patch state, not a transform: placement is derived from
				// the host, and both copies refresh against the same host when they attach.
				LotObject host = lotObject.GetParent() as LotObject;
				LotObject decalCopy = CreateDecalDetached(host);
				CopyDecalState(lotObject, decalCopy);
				return decalCopy;
			}

			Node3D parent = lotObject.GetParent() as Node3D;
			if (parent == null) parent = LotRoot;
			LotObject copy = LotObject.Create(lotObject.Kind, UniqueChildName(parent, lotObject.Name.ToString() + "_Copy"), lotObject.Color);
			copy.Position = lotObject.Position;
			copy.RotationDegrees = lotObject.RotationDegrees;
			copy.Scale = lotObject.Scale;
			// Properties travel with the copy, so duplicating a configured part is not a silent reset.
			copy.SetAnchored(lotObject.Anchored);
			copy.SetCanCollide(lotObject.CanCollide);
			copy.SetCollisionGroup(lotObject.CollisionGroup);
			if (lotObject.TextureId.Length > 0) copy.SetTexture(lotObject.TextureId);
			return copy;
		}
		LotUIElement uiElement = src as LotUIElement;
		if (uiElement != null)
		{
			Vector2 pos = uiElement.Position + new Vector2(16f, 16f);
			Vector2 size = uiElement.Size;
			UiGizmoMath.ClampRect(ref pos, ref size, UiCanvasBounds);
			LotUIElement copy = LotUIElement.Create(uiElement.Kind, pos, size, uiElement.Label);
			copy.Color = uiElement.Color;
			copy.Visible = uiElement.Visible;
			return copy;
		}
		if (src is LotScriptNode) return null;

		Node3D group = src as Node3D;
		if (group != null)
		{
			Node parent = group.GetParent();
			if (parent == null) parent = LotRoot;
			Node copy = group.Duplicate();
			copy.Name = UniqueChildName(parent, group.Name.ToString() + "_Copy");
			return copy;
		}
		return null;
	}

	/// <summary>
	/// Copies an authored decal's state (not its transform, which is derived) onto a detached copy.
	/// The tint is applied before the opacity because a tint write on a decal preserves the
	/// material's existing alpha (see <see cref="LotObject.Color"/>).
	/// </summary>
	private static void CopyDecalState(LotObject source, LotObject copy)
	{
		Color tint = source.Color;
		copy.Color = new Color(tint.R, tint.G, tint.B, 1f);
		copy.SetDecalFace(source.DecalFace);
		copy.SetDecalScaleU(source.DecalScaleU);
		copy.SetDecalScaleV(source.DecalScaleV);
		copy.SetDecalOffsetU(source.DecalOffsetU);
		copy.SetDecalOffsetV(source.DecalOffsetV);
		copy.SetDecalOpacity(source.DecalOpacity);
		if (source.TextureId.Length > 0) copy.SetTexture(source.TextureId);
		copy.RefreshDecalTransform();
	}

	/// <summary>Reorders a UI element within its parent; later children draw on top of earlier ones.
	/// Recorded on the history so the Inspector buttons and the context menu are undoable.</summary>
	public void MoveUiOrder(LotUIElement element, int direction)
	{
		if (element == null || !GodotObject.IsInstanceValid(element)) return;
		Node parent = element.GetParent();
		if (parent == null) return;

		int from = element.GetIndex();
		int to = from + direction;
		if (to < 0 || to >= parent.GetChildCount()) return;

		Builder.Instance.History.Push(new MoveNodeCommand("Reorder UI", element, parent, from, parent, to));
	}

	/// <summary>Wraps a 3D target in an empty "X_Model" group node, preserving its world transform.
	/// Undo unwraps it and puts the target back in its original slot; the group is held (empty) for
	/// redo and freed when the entry is dropped.</summary>
	public Node3D TurnToModel(Node3D target)
	{
		if (target == null || !GodotObject.IsInstanceValid(target)) return null;
		Node parent = target.GetParent();
		if (parent == null) return null;

		int index = target.GetIndex();
		Node3D[] holder = new Node3D[1];

		Builder.Instance.History.Push(new DelegateCommand("Turn to Model",
			b =>
			{
				if (holder[0] == null || !GodotObject.IsInstanceValid(holder[0]))
				{
					holder[0] = new Node3D();
					holder[0].Name = UniqueChildName(parent, target.Name.ToString() + "_Model");
				}
				if (holder[0].GetParent() == null) parent.AddChild(holder[0]);
				EditTreeOps.Move(target, holder[0], holder[0].GetChildCount());
			},
			b =>
			{
				EditTreeOps.Move(target, parent, index);
				EditTreeOps.Detach(holder[0]);
			},
			b =>
			{
				if (holder[0] != null && GodotObject.IsInstanceValid(holder[0]) && holder[0].GetParent() == null)
					holder[0].QueueFree();
			}));

		return holder[0];
	}

	/// <summary>Creates a .lua file and a hierarchy placeholder node under target (or the lot root),
	/// then opens it in the code editor. Recorded, so undo removes the placeholder and redo puts the
	/// same node back.</summary>
	public void InsertScriptUnder(Builder builder, Node target)
	{
		string baseName = target != null ? target.Name.ToString() : "Script";
		string path = builder.Scripts.CreateNewScript(baseName);
		Node parent = target != null ? target : LotRoot;

		LotScriptNode scriptNode = new LotScriptNode();
		scriptNode.ScriptPath = path;
		scriptNode.DisplayName = System.IO.Path.GetFileName(path);
		// Godot node names cannot contain dots; the readable ".lua" name lives on DisplayName.
		scriptNode.Name = scriptNode.DisplayName.Replace('.', '_');

		builder.History.Push(new CreateNodeCommand("Insert Script", scriptNode, parent, parent.GetChildCount()));
		builder.Scripts.OpenScript(path);
	}

	// --- Handle registry (Lua-facing object addressing) ---

	/// <summary>Seeds the registry with the lot root entity at its reserved handle (design doc §1),
	/// so net calls targeting the root resolve and the root's script has an identity.</summary>
	private void RegisterLotRootHandle()
	{
		LotRoot.SetMeta(HandleMeta, ScriptRuntime.LotRootHandle);
		_handles[ScriptRuntime.LotRootHandle] = LotRoot;
	}

	/// <summary>
	/// Forgets every handle and restarts the counter, then re-registers the lot root at its reserved
	/// handle 0. Used when the world is replaced wholesale (§4.2), so a loaded lot's entities are numbered
	/// exactly as the same lot built from scratch would be — the deterministic order the script runtime
	/// and the net layer depend on.
	/// </summary>
	private void ResetHandleRegistry()
	{
		_handles.Clear();
		_nextHandle = 1;
		RegisterLotRootHandle();
	}

	/// <summary>
	/// Gives every loaded lot node a handle, in deterministic tree order (step-5 decision, option
	/// A): the lot root keeps the reserved handle 0, then LotRoot's descendants, then the UI canvas
	/// and its elements. Deterministic order is what keeps static handles identical across peers
	/// (§9) and is what lets one script address another entity's net functions by handle.
	/// Objects created after load get handles when the Lua spawn path registers them.
	/// </summary>
	public void AssignLoadOrderHandles()
	{
		if (LotRoot != null) AssignHandlesUnder(LotRoot);
		if (LotUIRoot != null) AssignHandlesUnder(LotUIRoot);
	}

	private void AssignHandlesUnder(Node parent)
	{
		int count = parent.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			Node child = parent.GetChild(i);
			if (child.HasMeta(LotObject.InternalChildMeta)) continue; // outline hulls, collision bodies
			if (child is LotScriptNode) continue;                    // placeholders, not entities
			// HasMeta first: GetMeta(name, default) still logs a Godot error for a missing key.
			if (!child.HasMeta(HandleMeta))
				RegisterHandle(child);
			AssignHandlesUnder(child);
		}
	}

	/// <summary>Entity handle for the script runtime: the node's existing registry handle, or a
	/// fresh one. Assignment happens in deterministic tree-walk order, which is what makes static
	/// handles reproducible across peers (§9).</summary>
	public int EnsureEntityHandle(Node node)
	{
		if (!node.HasMeta(HandleMeta)) return RegisterHandle(node);
		return node.GetMeta(HandleMeta).AsInt32();
	}

	/// <summary>Reads a creator script file; null when it cannot be opened (the script runtime
	/// reports that instead of throwing).</summary>
	public static string ReadScriptFile(string path)
	{
		if (string.IsNullOrEmpty(path)) return null;
		Godot.FileAccess file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
		if (file == null) return null;
		string code = file.GetAsText();
		file.Dispose();
		return code;
	}

	public int RegisterHandle(Node node)
	{
		int handle = _nextHandle++;
		node.SetMeta(HandleMeta, handle);
		_handles[handle] = node;
		return handle;
	}

	public void ForgetHandle(int handle)
	{
		_handles.Remove(handle);
	}

	public Node GetByHandle(int handle)
	{
		Node node;
		if (!_handles.TryGetValue(handle, out node)) return null;
		if (!GodotObject.IsInstanceValid(node))
		{
			_handles.Remove(handle);
			return null;
		}
		return node;
	}

	/// <summary>
	/// Resolves an entity name to its handle for the Lua API (step 5, item 5): the FIRST entity with
	/// that name, searched in the same deterministic order handles are assigned in (lot root, then
	/// LotRoot's descendants, then the UI canvas and its elements). "First" therefore always means
	/// "lowest handle", which is stable and reproducible across peers (§9). Names are only unique
	/// among siblings (<see cref="UniqueChildName"/>), so two objects under different parents may
	/// legitimately share a name — the earliest one in lot order wins, silently (chosen rule A).
	/// Only entities (nodes holding a handle) are matched; lot-script placeholders and internal
	/// children (outline/collision) are invisible. Returns -1 when no entity matches.
	/// </summary>
	public int FindHandleByName(string name)
	{
		if (string.IsNullOrEmpty(name)) return -1;
		int found = FindHandleByNameUnder(LotRoot, name);
		if (found != -1) return found;
		return FindHandleByNameUnder(LotUIRoot, name);
	}

	private int FindHandleByNameUnder(Node node, string name)
	{
		if (node == null) return -1;
		// HasMeta first (never GetMeta(name, default): that logs a Godot error for a missing key).
		if (node.HasMeta(HandleMeta) && node.Name == name) return node.GetMeta(HandleMeta).AsInt32();
		int count = node.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			Node child = node.GetChild(i);
			if (child.HasMeta(LotObject.InternalChildMeta)) continue; // outline hulls, collision bodies
			if (child is LotScriptNode) continue;                    // placeholders, not entities
			int found = FindHandleByNameUnder(child, name);
			if (found != -1) return found;
		}
		return -1;
	}

	/// <summary>First free child name of the given pattern ("Cube", "Cube_1", ...).</summary>
	public string UniqueChildName(Node parent, string baseName)
	{
		if (string.IsNullOrEmpty(baseName)) baseName = "Object";
		bool taken = false;
		for (int i = 0; i < parent.GetChildCount(); i++)
		{
			if (parent.GetChild(i).Name == baseName) { taken = true; break; }
		}
		if (!taken) return baseName;
		for (int i = 1; i < 1000; i++)
		{
			string candidate = baseName + "_" + i;
			taken = false;
			for (int j = 0; j < parent.GetChildCount(); j++)
			{
				if (parent.GetChild(j).Name == candidate) { taken = true; break; }
			}
			if (!taken) return candidate;
		}
		return baseName + "_" + _nextHandle;
	}
}