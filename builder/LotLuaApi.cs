using Godot;

/// <summary>
/// The creator-facing Lua API surface — the exact contract the xLua binding (roadmap milestone
/// 3.1) will register. Verb-first, primitives-only signatures so lot scripts never receive Godot
/// nodes; objects are addressed through integer handles from BuilderScene's registry. This class
/// is the ONLY sanctioned way for scripts to touch the lot tree (clinerules 1.1 / 2.2).
/// Coordinates: 3D position/rotation/scale are local to the lot (rotation in degrees), colors are
/// 0..1 floats, UI rects are pixels offset from the viewport's top-left corner (Y down).
/// </summary>
public class LotLuaApi
{
	private readonly BuilderScene _scene;

	public LotLuaApi(BuilderScene scene)
	{
		_scene = scene;
	}

	// --- 3D parts ---

	/// <summary>Spawns a unit cube at (x, y, z). Returns its object handle, or -1 on failure.</summary>
	public int SpawnCube(float x, float y, float z) { return Spawn3D(LotObjectKind.Cube, new Vector3(x, y, z)); }

	/// <summary>Spawns a unit sphere at (x, y, z). Returns its object handle, or -1 on failure.</summary>
	public int SpawnSphere(float x, float y, float z) { return Spawn3D(LotObjectKind.Sphere, new Vector3(x, y, z)); }

	/// <summary>Spawns a unit cylinder at (x, y, z). Returns its object handle, or -1 on failure.</summary>
	public int SpawnCylinder(float x, float y, float z) { return Spawn3D(LotObjectKind.Cylinder, new Vector3(x, y, z)); }

	/// <summary>
	/// Spawns the default capsule character (radius 0.5, total height 2.0) at (x, y, z).
	/// Returns its object handle, or -1 on failure.
	///
	/// Contract notes (milestone 3.4): the capsule's ORIGIN is its centre, so spawning at y = 1
	/// puts its feet on the ground plane. The spawned object is a plain LotObject with a static
	/// collision body — it does NOT move on its own. Making it controllable is the character
	/// controller's job (C# side, in the player camera's runtime), not this binding's: scripts
	/// drive it through SetPosition like any other part, which is what keeps the §1.1 boundary
	/// intact (no Godot node ever reaches Lua).
	/// </summary>
	public int SpawnCapsule(float x, float y, float z) { return Spawn3D(LotObjectKind.Capsule, new Vector3(x, y, z)); }

	/// <summary>Moves the object to (x, y, z), relative to the lot origin.</summary>
	public void SetPosition(int handle, float x, float y, float z)
	{
		Node3D node = AsNode3D(handle, "SetPosition");
		if (node != null) node.Position = new Vector3(x, y, z);
	}

	public float GetPositionX(int handle) { Node3D n = AsNode3D(handle, "GetPositionX"); return n != null ? n.Position.X : 0f; }
	public float GetPositionY(int handle) { Node3D n = AsNode3D(handle, "GetPositionY"); return n != null ? n.Position.Y : 0f; }
	public float GetPositionZ(int handle) { Node3D n = AsNode3D(handle, "GetPositionZ"); return n != null ? n.Position.Z : 0f; }

	/// <summary>Sets the object's rotation in degrees, relative to the lot origin.</summary>
	public void SetRotationDeg(int handle, float x, float y, float z)
	{
		Node3D node = AsNode3D(handle, "SetRotationDeg");
		if (node != null) node.RotationDegrees = new Vector3(x, y, z);
	}

	public float GetRotationXDeg(int handle) { Node3D n = AsNode3D(handle, "GetRotationXDeg"); return n != null ? n.RotationDegrees.X : 0f; }
	public float GetRotationYDeg(int handle) { Node3D n = AsNode3D(handle, "GetRotationYDeg"); return n != null ? n.RotationDegrees.Y : 0f; }
	public float GetRotationZDeg(int handle) { Node3D n = AsNode3D(handle, "GetRotationZDeg"); return n != null ? n.RotationDegrees.Z : 0f; }

	/// <summary>Sets the object's scale (1 = natural size).</summary>
	public void SetScale(int handle, float x, float y, float z)
	{
		Node3D node = AsNode3D(handle, "SetScale");
		if (node != null) node.Scale = new Vector3(x, y, z);
	}

	public float GetScaleX(int handle) { Node3D n = AsNode3D(handle, "GetScaleX"); return n != null ? n.Scale.X : 1f; }
	public float GetScaleY(int handle) { Node3D n = AsNode3D(handle, "GetScaleY"); return n != null ? n.Scale.Y : 1f; }
	public float GetScaleZ(int handle) { Node3D n = AsNode3D(handle, "GetScaleZ"); return n != null ? n.Scale.Z : 1f; }

	/// <summary>Sets the mesh color (0..1 floats). Only applies to 3D parts.</summary>
	public void SetColor(int handle, float r, float g, float b)
	{
		Node node = Resolve(handle, "SetColor");
		LotObject lotObject = node as LotObject;
		if (lotObject != null) lotObject.Color = new Color(r, g, b);
		else WarnMissing(handle, "SetColor");
	}

	/// <summary>Renames the object in the hierarchy.</summary>
	public void SetName(int handle, string name)
	{
		Node node = Resolve(handle, "SetName");
		if (node != null && !string.IsNullOrWhiteSpace(name)) node.Name = name.Trim();
	}

	public string GetName(int handle)
	{
		Node node = Resolve(handle, "GetName");
		return node != null ? node.Name.ToString() : "";
	}

	/// <summary>Resolves an entity name to its handle, so a script can address another entity by
	/// name rather than a hardcoded number. Returns the FIRST entity with that name in lot order
	/// (the lowest handle) and -1 when nothing matches. Names are only unique among siblings, so
	/// when several entities share a name the earliest one wins silently (rule A). Entities only —
	/// script placeholders and internal children are not addressable. The inverse of
	/// <see cref="GetName(int)"/>.</summary>
	public int GetHandle(string name)
	{
		if (_scene == null || string.IsNullOrEmpty(name)) return -1;
		return _scene.FindHandleByName(name);
	}

	/// <summary>Destroys the object. Its handle becomes invalid afterwards.</summary>
	public void DestroyObject(int handle)
	{
		Node node = Resolve(handle, "DestroyObject");
		if (node != null)
		{
			node.QueueFree();
			_scene.ForgetHandle(handle);
		}
	}

	// --- UI elements ---

	/// <summary>Spawns a UI frame at (x, y) with the given size. Returns its handle, or -1.</summary>
	public int SpawnUIFrame(float x, float y, float w, float h) { return SpawnUiInternal(LotUIKind.Frame, new Vector2(x, y), new Vector2(w, h), ""); }

	/// <summary>Spawns a UI button with the given label. Returns its handle, or -1.</summary>
	public int SpawnUIButton(float x, float y, float w, float h, string label) { return SpawnUiInternal(LotUIKind.Button, new Vector2(x, y), new Vector2(w, h), label); }

	/// <summary>Spawns a UI text label at (x, y). Returns its handle, or -1.</summary>
	public int SpawnUIText(float x, float y, string label) { return SpawnUiInternal(LotUIKind.Text, new Vector2(x, y), LotUiRenderer.DefaultSize(LotUIKind.Text), label); }

	/// <summary>Spawns a UI scrollbar at (x, y) with the given size. Returns its handle, or -1.</summary>
	public int SpawnUIScrollbar(float x, float y, float w, float h) { return SpawnUiInternal(LotUIKind.Scrollbar, new Vector2(x, y), new Vector2(w, h), ""); }

	/// <summary>Sets the label text of a Button or Text element.</summary>
	public void SetUIText(int handle, string text)
	{
		LotUIElement el = AsUiElement(handle, "SetUIText");
		if (el != null) el.Label = text;
	}

	public string GetUIText(int handle)
	{
		LotUIElement el = AsUiElement(handle, "GetUIText");
		return el != null ? el.Label : "";
	}

	/// <summary>Sets the element color (0..1 floats).</summary>
	public void SetUIColor(int handle, float r, float g, float b, float a)
	{
		LotUIElement el = AsUiElement(handle, "SetUIColor");
		if (el != null) el.Color = new Color(r, g, b, a);
	}

	/// <summary>Sets the element rect (x, y from the viewport top-left corner, Y down). Clamped so a
	/// script cannot place an element outside the viewport picture frame.</summary>
	public void SetUIRect(int handle, float x, float y, float w, float h)
	{
		LotUIElement el = AsUiElement(handle, "SetUIRect");
		if (el != null)
		{
			Vector2 pos = new Vector2(x, y);
			Vector2 size = new Vector2(w, h);
			UiGizmoMath.ClampRect(ref pos, ref size, _scene.UiCanvasBounds);
			el.Position = pos;
			el.Size = size;
		}
	}

	/// <summary>Prints a message to the engine console (creator-side logging).</summary>
	public void Log(string message)
	{
		GD.Print("[Lot] " + message);
	}

	// --- net.* plumbing ---
	//
	// These two are NOT creator API: LuaManager registers them as globals (the bootstrap calls
	// them by name) but keeps them out of the documented Lot table. The bootstrap is the only
	// caller — see LuaBootstrap.Chunk for the sugar that reaches them.

	/// <summary>The net bridge, assigned by LuaManager once the runtime is up (null before then).</summary>
	public LuaNetBridge Net { get; set; }

	/// <summary>Records a net.* declaration so the router can warn when two scripts share a name.
	/// Called by the bootstrap's __newindex trap only.</summary>
	public void NetRegister(int handle, string direction, string name, string scriptName)
	{
		if (Net == null) return;
		Net.RegisterHandler(handle, direction, name, scriptName);
	}

	/// <summary>
	/// Cross-entity call (step 5): invokes the TARGET handle's net.server function through the same
	/// router/queue/envelope path as the caller's own net.* table. From a client the call goes to
	/// the host; from the host it is queued locally. Unknown handles/names warn and drop.
	/// Creator-facing form is the vararg sugar in the bootstrap (Lot.CallServer(handle, name, ...));
	/// this raw form takes a packed { n = count, ... } table.
	/// </summary>
	public void CallServer(int handle, string name, NLua.LuaTable args)
	{
		RouteCrossEntity(handle, NetDirection.Server, name, args);
	}

	/// <summary>Cross-entity call to the TARGET handle's net.client function: the host broadcasts
	/// it (and runs it locally, since the host is a player too); a client runs it locally for its
	/// own view only — clients never route to other clients.</summary>
	public void CallClient(int handle, string name, NLua.LuaTable args)
	{
		RouteCrossEntity(handle, NetDirection.Client, name, args);
	}

	private void RouteCrossEntity(int handle, NetDirection direction, string name, NLua.LuaTable args)
	{
		if (Net == null)
		{
			GD.PushWarning("[LotLuaApi] net bridge is not ready; dropped cross-entity call " + name);
			if (args != null) args.Dispose();
			return;
		}
		Net.InvokeCrossEntity(handle, direction, name, args);
	}

	/// <summary>Funnels one net.* call from the bootstrap (args packed as { n = count, ... }).
	/// Serialization errors throw NetSerializationException, which surfaces in the calling script.</summary>
	public void NetInvoke(int handle, string direction, string name, NLua.LuaTable args)
	{
		if (Net == null)
		{
			GD.PushWarning("[LotLuaApi] net bridge is not ready; dropped net." + direction + "." + name);
			if (args != null) args.Dispose();
			return;
		}
		Net.InvokeFromLua(handle, direction, name, args);
	}

	// --- internals ---

	private int Spawn3D(LotObjectKind kind, Vector3 at)
	{
		if (_scene == null) return -1;
		LotObject obj = _scene.SpawnPrimitive(kind, at);
		int handle = _scene.RegisterHandle(obj);
		TagLoadTimeSpawn(obj);
		return handle;
	}

	/// <summary>
	/// Marks a node spawned while a script's initial load was running as owned by that script (F2):
	/// the reload cleanup deletes owned nodes before re-running the script, which is what stops a
	/// "spawn the capsule in the root script" pattern from duplicating them. Handler-time spawns are
	/// deliberately left untagged (they are runtime state).
	/// </summary>
	private void TagLoadTimeSpawn(Node node)
	{
		LuaNetBridge net = Net;
		if (net == null || node == null) return;
		if (net.LoadOwnerHandle < 0) return;
		node.SetMeta(LoadSpawnOwnerMeta, net.LoadOwnerHandle);
		node.SetMeta(LoadSpawnOwnerScriptMeta, net.LoadOwnerScript ?? "");
	}

	/// <summary>Meta marking a load-time spawn's owner (entity handle).</summary>
	public const string LoadSpawnOwnerMeta = "openlot_spawn_owner";

	/// <summary>Meta recording which script's load spawned the node.</summary>
	public const string LoadSpawnOwnerScriptMeta = "openlot_spawn_owner_script";

	private int SpawnUiInternal(LotUIKind kind, Vector2 pos, Vector2 size, string label)
	{
		if (_scene == null) return -1;
		// Same frame constraint as the editor: spawned UI always lands inside the picture frame.
		UiGizmoMath.ClampRect(ref pos, ref size, _scene.UiCanvasBounds);
		LotUIElement el = _scene.SpawnUIElement(kind, pos, size, label);
		int handle = _scene.RegisterHandle(el);
		TagLoadTimeSpawn(el);
		return handle;
	}

	private Node Resolve(int handle, string api)
	{
		Node node = _scene != null ? _scene.GetByHandle(handle) : null;
		if (node == null) GD.PushWarning("[LotLuaApi] " + api + ": no lot object for handle " + handle);
		return node;
	}

	private Node3D AsNode3D(int handle, string api)
	{
		return Resolve(handle, api) as Node3D;
	}

	private LotUIElement AsUiElement(int handle, string api)
	{
		return Resolve(handle, api) as LotUIElement;
	}

	private void WarnMissing(int handle, string api)
	{
		GD.PushWarning("[LotLuaApi] " + api + ": no lot object for handle " + handle);
	}
}