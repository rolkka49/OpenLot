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

	/// <summary>Meta key carrying a node's Lua-facing handle.</summary>
	private const string HandleMeta = "openlot_handle";

#if DEBUG
	/// <summary>Physics frames to wait before running the heavy integration suite (see _Process).</summary>
	private const int IntegrationSuitePhysicsDelay = 8;
	private bool _integrationSuiteDone;

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
		LuaScripts = new ScriptRuntime(() => LuaManager.Instance.NetBridge, GD.PushWarning,
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
	/// </summary>
	public void EnterTestMode(bool gameMode)
	{
		if (InTestMode) return;
		InTestMode = true;
		InGameMode = gameMode;

		StartScriptRuntime();

		EnsurePlayerCamera();
		if (Player == null) Player = new CapsuleController(this);
		LotObject character = FindCharacter();
		if (character != null)
		{
			Player.Attach(character);
		}
		else
		{
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
	/// detached, the freecam takes the view back, and the lot's scripts reload again so the editor
	/// sees the lot's load-time state rather than whatever the play session left behind.
	/// </summary>
	public void ExitTestMode()
	{
		if (!InTestMode) return;
		InTestMode = false;
		InGameMode = false;

		if (Player != null) Player.Detach();
		if (PlayerCamera != null) PlayerCamera.Current = false;
		Freecam.Current = true;

		StartScriptRuntime();
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
				IntegrationSuitePhysicsDelay + " physics frames; use --quit-after 40 when verifying headless)");
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

		WorldEnvironment envNode = new WorldEnvironment();
		Environment environment = new Environment();
		environment.BackgroundMode = Godot.Environment.BGMode.Sky;
		ProceduralSkyMaterial skyMaterial = new ProceduralSkyMaterial();
		skyMaterial.SkyTopColor = new Color(0.36f, 0.56f, 0.85f);
		skyMaterial.SkyHorizonColor = new Color(0.74f, 0.81f, 0.88f);
		skyMaterial.GroundHorizonColor = new Color(0.74f, 0.72f, 0.68f);
		skyMaterial.GroundBottomColor = new Color(0.40f, 0.37f, 0.33f);
		Sky sky = new Sky();
		sky.SkyMaterial = skyMaterial;
		environment.Sky = sky;
		environment.AmbientLightSource = Godot.Environment.AmbientSource.Sky;
		envNode.Environment = environment;
		LotViewport.AddChild(envNode);

		DirectionalLight3D sun = new DirectionalLight3D();
		sun.RotationDegrees = new Vector3(-48f, -28f, 0f);
		sun.ShadowEnabled = true;
		sun.LightEnergy = 1.2f;
		LotViewport.AddChild(sun);

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
	}

	/// <summary>Default lot scaffolding: a ground plane so a brand-new lot reads as a world, plus the
	/// default character script so the lot is playable the moment a player session starts.</summary>
	private void SpawnDefaultLot()
	{
		LotObject ground = LotObject.Create(LotObjectKind.Plane, "Ground", new Color(0.45f, 0.44f, 0.42f));
		LotRoot.AddChild(ground);
		EnsureDefaultLotScript();
	}

	/// <summary>File name of the script a brand-new lot ships with.</summary>
	private const string DefaultLotScriptName = "Character.lua";

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

	public LotObject SpawnPrimitive(LotObjectKind kind, Vector3 at)
	{
		LotObject obj = LotObject.Create(kind, UniqueChildName(LotRoot, kind.ToString()), new Color(0.65f, 0.65f, 0.68f));
		obj.Position = at;
		LotRoot.AddChild(obj);
		return obj;
	}

	public LotUIElement SpawnUIElement(LotUIKind kind, Vector2 pos, Vector2 size, string label)
	{
		LotUIElement el = LotUIElement.Create(kind, pos, size, label);
		LotUIRoot.AddChild(el);
		return el;
	}

	public void DeleteNode(Node target)
	{
		Builder.Instance.Selection.Deselect(target);
		DestroyEntity(target);
		Builder.Instance.MarkDirty();
	}

	/// <summary>
	/// The single entity-destroy path (F2 cleanup + the entity-destroy hook): forgets every handle in
	/// the subtree, clears each handle's net declarations / circuit-breaker record / script name from
	/// the VM and router, then frees the node. Editor deletion and the reload cleanup both use it.
	/// </summary>
	public void DestroyEntity(Node node)
	{
		if (node == null || !GodotObject.IsInstanceValid(node)) return;
		ForgetHandlesRecursive(node);
		node.QueueFree();
	}

	private void ForgetHandlesRecursive(Node node)
	{
		if (node.HasMeta(HandleMeta))
		{
			int handle = node.GetMeta(HandleMeta).AsInt32();
			if (handle != ScriptRuntime.LotRootHandle)
			{
				ForgetHandle(handle);
				LuaNetBridge bridge = LuaManager.Instance.NetBridge;
				if (bridge != null) bridge.ForgetEntity(handle);
			}
		}
		int count = node.GetChildCount();
		for (int i = 0; i < count; i++) ForgetHandlesRecursive(node.GetChild(i));
	}

	/// <summary>Duplicates a lot node. Returns the copy, or null when the node kind cannot be
	/// duplicated (script placeholders are references to files, not content).</summary>
	public Node DuplicateNode(Node src)
	{
		LotObject lotObject = src as LotObject;
		if (lotObject != null)
		{
			Node3D parent = lotObject.GetParent() as Node3D;
			if (parent == null) parent = LotRoot;
			LotObject copy = LotObject.Create(lotObject.Kind, UniqueChildName(parent, lotObject.Name.ToString() + "_Copy"), lotObject.Color);
			copy.Position = lotObject.Position;
			copy.RotationDegrees = lotObject.RotationDegrees;
			copy.Scale = lotObject.Scale;
			parent.AddChild(copy);
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
			LotUIRoot.AddChild(copy);
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
			parent.AddChild(copy);
			return copy;
		}
		return null;
	}

	/// <summary>Reorders a UI element within its parent; later children draw on top of earlier ones.</summary>
	public void MoveUiOrder(LotUIElement element, int direction)
	{
		Node parent = element.GetParent();
		if (parent == null) return;

		int target = element.GetIndex() + direction;
		if (target < 0 || target >= parent.GetChildCount()) return;

		parent.MoveChild(element, target);
		Builder.Instance.MarkDirty();
	}

	/// <summary>Wraps a 3D target in an empty "X_Model" group node, preserving its world transform.</summary>
	public Node3D TurnToModel(Node3D target)
	{
		Node parent = target.GetParent();
		if (parent == null) return null;
		Node3D model = new Node3D();
		model.Name = UniqueChildName(parent, target.Name.ToString() + "_Model");
		parent.AddChild(model);
		target.Reparent(model, true);
		return model;
	}

	/// <summary>Creates a .lua file and a hierarchy placeholder node under target (or the lot root),
	/// then opens it in the code editor.</summary>
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
		parent.AddChild(scriptNode);

		builder.Scripts.OpenScript(path);
		builder.MarkDirty();
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