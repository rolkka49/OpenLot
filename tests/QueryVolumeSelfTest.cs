using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// Milestone 3.13's object layer — the roadmap's tracked remainder: the placeable query volume
/// (kind tag, property declaration split, preview/pick proxy, the pure frame-to-frame diff behind
/// its evaluation) plus a staged session probe that places volumes and verifies `entered` /
/// `exited` deliveries end to end: a part inside from the start (the empty-baseline rule), a part
/// moved in and out from here, and the session's own character standing in a region (player
/// attribution). Runs after the spatial-query suite, from BuilderScene._Process under #if DEBUG.
/// </summary>
public static class QueryVolumeSelfTest
{
	private static int _checks;
	private static int _failures;

	// Staged session probe (mirrors EventSelfTest's shape: the staged probes drive Test mode one
	// at a time, so this one starts only once the spatial-query probe is done).
	private static int _probeStage;
	private static int _probeChecks;
	private static int _probeFailures;
	private static ulong _probeDeadlineMsec;
	private static BuilderScene _scene;
	private static LotObject _volumeA;
	private static LotObject _volumeB;
	private static LotObject _volumeC;
	private static LotObject _partInside;
	private static LotObject _partOutside;
	private static int _handleA;
	private static int _handleB;
	private static int _handleC;
	private static int _handleInside;
	private static int _handleOutside;

	private const string ScratchLotPath = "user://qvselftest_probe.lot";
	private static readonly string ProbeScriptDir = ScriptManager.ScriptsDir;

	public static bool ProbeDone { get { return _probeStage >= 5; } }

	public static void Run(BuilderScene scene)
	{
		_checks = 0;
		_failures = 0;
		TestKindAndDeclarations();
		TestDiff();
		GD.Print("[QueryVolumeSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
		_scene = scene;
		_probeStage = 0;
		// The staged session probe starts here, on the suite's own turn in the chain — never from
		// the unconditionally-called TickProbe, which runs from frame one.
		StartProbe();
	}

	private static void Check(string label, bool condition)
	{
		_checks++;
		if (condition) return;
		_failures++;
		GD.PushWarning("[QueryVolumeSelfTest] FAIL: " + label);
	}

	private static bool Near(float a, float b)
	{
		return Mathf.Abs(a - b) <= 1e-4f;
	}

	private static bool ListHas(List<LotPropertyDescriptor> list, string id)
	{
		for (int i = 0; i < list.Count; i++)
		{
			if (list[i].Id == id) return true;
		}
		return false;
	}

	/// <summary>The pure diff behind a volume's evaluation, with hand sets: the newcomer enters,
	/// the leaver exits, both ascending; an unchanged frame fires nothing; the empty baseline
	/// reports everything inside on the first evaluation.</summary>
	private static void TestDiff()
	{
		List<int> entered = new List<int>();
		List<int> exited = new List<int>();
		HashSet<int> previous = new HashSet<int> { 1, 2 };
		HashSet<int> current = new HashSet<int> { 2, 3 };
		QueryVolumeSession.Diff(previous, current, entered, exited);
		Check("diff: the newcomer enters and the leaver exits, ascending",
			entered.Count == 1 && entered[0] == 3 && exited.Count == 1 && exited[0] == 1);
		Check("diff: the previous set becomes the current contents",
			previous.Count == 2 && previous.Contains(2) && previous.Contains(3));

		QueryVolumeSession.Diff(previous, current, entered, exited);
		Check("diff: an unchanged frame fires nothing", entered.Count == 0 && exited.Count == 0);

		previous.Clear();
		current.Clear();
		current.Add(9);
		current.Add(5);
		QueryVolumeSession.Diff(previous, current, entered, exited);
		Check("diff: a first evaluation reports everything inside, ascending",
			entered.Count == 2 && entered[0] == 5 && entered[1] == 9 && exited.Count == 0);

		current.Clear();
		QueryVolumeSession.Diff(previous, current, entered, exited);
		Check("diff: an emptied volume reports its leavers, ascending",
			entered.Count == 0 && exited.Count == 2 && exited[0] == 5 && exited[1] == 9);
	}

	/// <summary>The kind tag, the shape vocabulary and the declaration split: a volume's Inspector
	/// is Shape + Radius + Size and none of the part set; a cube gains none of the volume rows;
	/// a box hides the Radius row (its extents are the Size row).</summary>
	private static void TestKindAndDeclarations()
	{
		Check("tag: a query volume round-trips its kind tag",
			LotSceneWalk.LotKindTag(LotObjectKind.QueryVolume) == "queryvolume"
			&& LotSceneWalk.TryParseLotKind("queryvolume", out LotObjectKind parsed)
			&& parsed == LotObjectKind.QueryVolume);
		Check("shape names: the Choice spelling and the enum agree",
			LotObject.QueryShapeFromName("Box") == QueryVolumeShape.Box
			&& LotObject.QueryShapeFromName("Sphere") == QueryVolumeShape.Sphere
			&& LotObject.QueryShapeFromName("nonsense") == QueryVolumeShape.Sphere);

		List<LotPropertyDescriptor> list = new List<LotPropertyDescriptor>();
		LotObject volume = LotObject.Create(LotObjectKind.QueryVolume, "QVDecl", new Color(1f, 1f, 1f));
		LotObject cube = LotObject.Create(LotObjectKind.Cube, "QVDeclCube", new Color(1f, 1f, 1f));
		try
		{
			Check("create: a volume is sized and starts as a 2 m sphere",
				volume.HasSize && volume.QueryShape == QueryVolumeShape.Sphere && Near(volume.QueryRadius, 2f));
			Check("create: the pick proxy is on the no-collide layer with a sphere shape",
				volume.CollisionBody != null && volume.CollisionBody.CollisionLayer == LotObject.NoCollideLayer
				&& volume.CollisionShape != null && volume.CollisionShape.Shape is SphereShape3D);
			Check("create: the preview child exists by name",
				volume.GetNodeOrNull<MeshInstance3D>("QueryVisual") != null);

			LotPropertyRegistry.CollectFor(volume, list);
			Check("declarations: a sphere volume offers Shape and Radius",
				ListHas(list, "query_shape") && ListHas(list, "query_radius"));
			Check("declarations: the part rows stay off a volume",
				!ListHas(list, "anchored") && !ListHas(list, "canCollide") && !ListHas(list, "texture")
				&& !ListHas(list, "friction") && !ListHas(list, "tilesPerUnit") && !ListHas(list, "stretchTexture"));

			LotPropertyRegistry.CollectFor(cube, list);
			Check("declarations: the volume rows stay off a cube, which keeps its own",
				!ListHas(list, "query_shape") && !ListHas(list, "query_radius")
				&& ListHas(list, "anchored") && ListHas(list, "tilesPerUnit"));

			volume.SetQueryShape(QueryVolumeShape.Box);
			LotPropertyRegistry.CollectFor(volume, list);
			Check("declarations: a box volume keeps the Radius row (stored, inert for a box)",
				ListHas(list, "query_radius") && ListHas(list, "query_shape"));
			Check("box: the proxy follows the authored size",
				volume.SetSize(new Vector3(4f, 2f, 1f))
				&& volume.CollisionShape.Shape is BoxShape3D box
				&& box.Size.IsEqualApprox(new Vector3(4f, 2f, 1f)));
			Check("box: the preview mesh follows the size",
				volume.GetNodeOrNull<MeshInstance3D>("QueryVisual").Mesh is BoxMesh preview
				&& preview.Size.IsEqualApprox(new Vector3(4f, 2f, 1f)));

			volume.SetQueryShape(QueryVolumeShape.Sphere);
			Check("sphere: the proxy swaps back", volume.CollisionShape.Shape is SphereShape3D);

			volume.SetQueryRadius(0f);
			Check("radius: floored at 0.1 m", Near(volume.QueryRadius, 0.1f));
			volume.SetQueryRadius(3f);
			Check("radius: a write moves the proxy",
				volume.CollisionShape.Shape is SphereShape3D grown && Near(grown.Radius, 3f));

			volume.SetCanCollide(true);
			volume.RefreshCollision();
			Check("collision: the proxy stays on the no-collide layer whatever CanCollide says",
				volume.CollisionBody.CollisionLayer == LotObject.NoCollideLayer);

			volume.SetQueryVolumeVisualVisible(false);
			bool hidden = !volume.QueryVolumeVisualVisible;
			volume.SetQueryVolumeVisualVisible(true);
			Check("preview: visibility toggles", hidden && volume.QueryVolumeVisualVisible);
		}
		finally
		{
			volume.Free();
			cube.Free();
		}
	}

	private const string ProbeScriptA = @"function start()
    Subscribe(this.Handle, ""entered"", function(other, player)
        Lot.Log(""QVA in "" .. tostring(other) .. "" "" .. tostring(net.fromPlayer))
    end)
    Subscribe(this.Handle, ""exited"", function(other)
        Lot.Log(""QVA out "" .. tostring(other))
    end)
end";

	private const string ProbeScriptB = @"function start()
    Subscribe(this.Handle, ""entered"", function(other, player)
        Lot.Log(""QVB in "" .. tostring(other) .. "" "" .. tostring(net.fromPlayer))
    end)
end";

	/// <summary>One staged step per deadline: the probe never blocks a frame.</summary>
	private static bool WaitDone()
	{
		return Time.GetTicksMsec() >= _probeDeadlineMsec;
	}

	private static void ProbeCheck(string label, bool condition)
	{
		_probeChecks++;
		if (condition) return;
		_probeFailures++;
		GD.PushWarning("[QueryVolumeSelfTest] probe FAIL: " + label);
	}

	/// <summary>The probe's observable: script output through Lot.Log (a bounded window; the ring
	/// drops old entries).</summary>
	private static int CountLog(string exact)
	{
		int count = 0;
		int start = Math.Max(0, LotLog.Count - 200);
		for (int i = start; i < LotLog.Count; i++)
		{
			if (LotLog.At(i).Text == exact) count++;
		}
		return count;
	}

	private static bool HasLog(string exact)
	{
		return CountLog(exact) > 0;
	}

	private static void WriteFixture(string fileName, string code)
	{
		if (!DirAccess.DirExistsAbsolute(ProbeScriptDir)) DirAccess.MakeDirRecursiveAbsolute(ProbeScriptDir);
		FileAccess file = FileAccess.Open(ProbeScriptDir + "/" + fileName, FileAccess.ModeFlags.Write);
		if (file != null)
		{
			file.StoreString(code);
			file.Dispose();
		}
	}

	private static void AttachFixture(Node parent, string fileName)
	{
		LotScriptNode node = new LotScriptNode();
		node.ScriptPath = ProbeScriptDir + "/" + fileName;
		node.DisplayName = fileName;
		node.Name = fileName.Replace('.', '_');
		parent.AddChild(node);
	}

	private static void RemoveFixture(string fileName)
	{
		if (FileAccess.FileExists(ProbeScriptDir + "/" + fileName))
			DirAccess.RemoveAbsolute(ProbeScriptDir + "/" + fileName);
	}

	private static LotObject NewScratchVolume(BuilderScene scene, string name, Vector3 at, float radius)
	{
		LotObject volume = LotObject.Create(LotObjectKind.QueryVolume, name, new Color(0.4f, 0.8f, 1f));
		volume.Position = at;
		volume.SetQueryRadius(radius);
		scene.LotRoot.AddChild(volume);
		return volume;
	}

	private static LotObject NewScratchPart(BuilderScene scene, string name, Vector3 at)
	{
		LotObject part = LotObject.Create(LotObjectKind.Cube, name, new Color(0.6f, 0.8f, 0.6f));
		part.Position = at;
		scene.LotRoot.AddChild(part);
		return part;
	}

	/// <summary>A node's handle, registering it when the spawn path has not (the character).</summary>
	private static int HandleFor(BuilderScene scene, Node node)
	{
		if (node == null) return -1;
		if (!node.HasMeta(BuilderScene.HandleMeta)) scene.RegisterHandle(node);
		return node.GetMeta(BuilderScene.HandleMeta).AsInt32();
	}

	private static void DestroyEntity(Node node)
	{
		if (node != null && GodotObject.IsInstanceValid(node)) _scene.DestroyEntity(node);
	}

	/// <summary>Removes fixtures a previous run left in the tree (a straggler would sit in a
	/// region and change what it reports).</summary>
	private static void DestroyLeftovers(string prefix)
	{
		List<Node> doomed = new List<Node>();
		CollectByNamePrefix(_scene.LotRoot, prefix, doomed);
		for (int i = 0; i < doomed.Count; i++) DestroyEntity(doomed[i]);
	}

	private static void CollectByNamePrefix(Node node, string prefix, List<Node> into)
	{
		int count = node.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			Node child = node.GetChild(i);
			if (child.Name.ToString().StartsWith(prefix, StringComparison.Ordinal)) into.Add(child);
			CollectByNamePrefix(child, prefix, into);
		}
	}

	/// <summary>
	/// One staged step per call, from BuilderScene._Process. Stage 0 is idle — the probe is
	/// started by <see cref="Run"/> once the chain reaches this suite (EventSelfTest's contract);
	/// 1 checks the session-start deliveries (the empty baseline: the part already inside enters,
	/// and the character standing in region B enters player-attributed), then moves the outside
	/// part in; 2 checks its entrant and moves it out; 3 checks the leaver, the settle contract
	/// and leaves the session; 4 verifies the teardown and cleans up.
	/// </summary>
	public static void TickProbe()
	{
		if (_probeStage <= 0 || _probeStage >= 5 || !WaitDone()) return;
		switch (_probeStage)
		{
			case 1: CheckSessionStart(); break;
			case 2: CheckMovedIn(); break;
			case 3: CheckMovedOutAndExit(); break;
			case 4: VerifyExit(); break;
		}
	}

	private static void StartProbe()
	{
		if (DisplayServer.GetName() != "headless")
		{
			// The probe would hijack an interactive debug session; it is the headless gate's job.
			_probeStage = 5;
			return;
		}
		_probeChecks = 0;
		_probeFailures = 0;
		_probeStage = 1;

		// A previous run's fixtures (or a queued-for-free straggler) must not sit in a region.
		DestroyLeftovers("QVProbe");

		WriteFixture("qv_probe_a.lua", ProbeScriptA);
		WriteFixture("qv_probe_b.lua", ProbeScriptB);

		// Region A watches a fixed part at (10,1,10) and one that is moved across the boundary
		// from here; it is a BOX volume (6 x 3 x 6, with a stored radius of 7 that must survive
		// the save round trip even though a box does not use it), which exercises the box
		// evaluation path in-session. Region B sits on the default character's spawn (4,1,4) —
		// the session's own capsule is inside it, which pins the player attribution. Region C is
		// left WITHOUT a script: the placement-alone case (Build ensures its handle).
		_volumeA = NewScratchVolume(_scene, "QVProbeA", new Vector3(10f, 1f, 10f), 3f);
		_volumeA.SetQueryShape(QueryVolumeShape.Box);
		_volumeA.SetSize(new Vector3(6f, 3f, 6f));
		_volumeA.SetQueryRadius(7f);
		_volumeB = NewScratchVolume(_scene, "QVProbeB", new Vector3(4f, 1f, 4f), 3f);
		_volumeC = NewScratchVolume(_scene, "QVProbeC", new Vector3(60f, 1f, 60f), 2f);
		_partInside = NewScratchPart(_scene, "QVProbeInside", new Vector3(10f, 1f, 10f));
		_partOutside = NewScratchPart(_scene, "QVProbeOutside", new Vector3(40f, 1f, 40f));
		// The volumes deliberately get NO handle here — exactly like a toolbox placement — so the
		// session's own paths are what the probe exercises: the runtime's walk for the scripted
		// volumes and Build's ensure for the unscripted one. The parts keep their handles because
		// they stand in for scripted/Lua-spawned objects, the addressable set.
		_handleInside = _scene.RegisterHandle(_partInside);
		_handleOutside = _scene.RegisterHandle(_partOutside);
		AttachFixture(_volumeA, "qv_probe_a.lua");
		AttachFixture(_volumeB, "qv_probe_b.lua");

		GD.Print("[QueryVolumeSelfTest] session probe started (waiting 2000 ms)");
		_scene.EnterTestMode(false);
		_probeDeadlineMsec = Time.GetTicksMsec() + 2000;
	}

	private static void CheckSessionStart()
	{
		// The volumes were placed without handles; by now the session has given every one of them
		// an identity — the scripted ones through the runtime's walk (their scripts' start() saw
		// the same values, pinned by the log lines below) and the unscripted one through Build.
		_handleA = BuilderScene.HandleOf(_volumeA);
		_handleB = BuilderScene.HandleOf(_volumeB);
		_handleC = BuilderScene.HandleOf(_volumeC);
		ProbeCheck("region: the session watches every placed volume",
			_scene.QueryVolumes != null && _scene.QueryVolumes.VolumeCount == 3
			&& _handleA >= 0 && _handleB >= 0);
		ProbeCheck("placement: an unscripted volume placed from the toolbox still gets an identity",
			_handleC >= 0);
		ProbeCheck("click: the editor's pick ray finds the volume through its proxy",
			PartDragController.Pick(_scene, new Vector3(10f, 20f, 10f), new Vector3(0f, -1f, 0f),
				out Vector3 hitPosition) == _volumeA && hitPosition.Y > 2f);
		ProbeCheck("baseline: the part already inside the box enters exactly once, not player-attributed",
			HasLog("QVA in " + _handleInside + " false")
			&& CountLog("QVA in " + _handleInside + " false") == 1);
		ProbeCheck("preview: the build-mode previews hide for the session",
			!_volumeA.QueryVolumeVisualVisible && !_volumeB.QueryVolumeVisualVisible
			&& !_volumeC.QueryVolumeVisualVisible);

		int charHandle = HandleFor(_scene, _scene.FindCharacter());
		ProbeCheck("player: the character standing in a region enters, player-attributed",
			charHandle >= 0 && HasLog("QVB in " + charHandle + " true"));
		ProbeCheck("self: a region never reports its own handle",
			!HasLog("QVB in " + _handleB + " false"));

		_partOutside.Position = new Vector3(10f, 1f, 10f);
		_probeDeadlineMsec = Time.GetTicksMsec() + 500;
		_probeStage = 2;
	}

	private static void CheckMovedIn()
	{
		ProbeCheck("entered: a part moved into the region reports, not player-attributed",
			HasLog("QVA in " + _handleOutside + " false"));
		_partOutside.Position = new Vector3(40f, 1f, 40f);
		_probeDeadlineMsec = Time.GetTicksMsec() + 500;
		_probeStage = 3;
	}

	private static void CheckMovedOutAndExit()
	{
		ProbeCheck("exited: the part that left reports", HasLog("QVA out " + _handleOutside));
		ProbeCheck("settled: it entered exactly once and did not re-fire while inside",
			CountLog("QVA in " + _handleOutside + " false") == 1);
		ProbeCheck("still: nothing that stayed inside reported an exit",
			!HasLog("QVA out " + _handleInside));
		_scene.ExitTestMode();
		_probeDeadlineMsec = Time.GetTicksMsec() + 500;
		_probeStage = 4;
	}

	private static void VerifyExit()
	{
		ProbeCheck("teardown: leaving the session drops the volume evaluation",
			_scene.QueryVolumes == null);
		ProbeCheck("preview: the build-mode previews come back",
			_volumeA.QueryVolumeVisualVisible && _volumeB.QueryVolumeVisualVisible
			&& _volumeC.QueryVolumeVisualVisible);
		ProbeCheck("persist: the volumes themselves survive the session round trip",
			GodotObject.IsInstanceValid(_volumeA) && GodotObject.IsInstanceValid(_volumeB)
			&& _volumeA.GetParent() == _scene.LotRoot);

		// A real save → reload: a placed region must survive the archive exactly as placed — the
		// property declarations are the capture/restore path, pinned end to end here.
		LotArchive.LotManifest manifest = LotSave.BuildManifest(_scene.LotRoot, "QueryVolumeProbe", 0L);
		System.Collections.Generic.List<string> missing = new System.Collections.Generic.List<string>();
		bool saved = LotSave.WriteScene(_scene, ScratchLotPath, manifest, missing, out string saveError);
		ProbeCheck("roundtrip: the lot with its volumes saves (error: '" + saveError + "')", saved);
		if (saved)
		{
			bool loaded = LotLoad.Load(_scene, ScratchLotPath, out LotArchive.LotManifest _,
				out LotSceneLoad.LoadReport _, out string loadError);
			ProbeCheck("roundtrip: the lot reloads (error: '" + loadError + "')", loaded);
			if (loaded)
			{
				LotObject back = FindByName(_scene.LotRoot, "QVProbeA");
				ProbeCheck("roundtrip: the volume comes back as a query volume",
					back != null && back.Kind == LotObjectKind.QueryVolume);
				if (back != null)
				{
					ProbeCheck("roundtrip: its box shape survives", back.QueryShape == QueryVolumeShape.Box);
					ProbeCheck("roundtrip: its radius survives even for a box", Near(back.QueryRadius, 7f));
					ProbeCheck("roundtrip: its size survives",
						back.Size.IsEqualApprox(new Vector3(6f, 3f, 6f)));
				}
			}
		}

		// The reload (when it happened) replaced the world: the probe's fixtures are removed by
		// name from whatever lot is current now — the old references may be dead — and the
		// scratch file and fixture scripts go with them.
		DestroyLeftovers("QVProbe");
		RemoveFixture("qv_probe_a.lua");
		RemoveFixture("qv_probe_b.lua");
		if (FileAccess.FileExists(ScratchLotPath)) DirAccess.RemoveAbsolute(ScratchLotPath);
		_probeStage = 5;
		GD.Print("[QueryVolumeSelfTest] probe: " + _probeChecks + " checks, " + _probeFailures + " failure(s).");
	}

	/// <summary>The node with the given name under the given root, or null.</summary>
	private static LotObject FindByName(Node node, string name)
	{
		int count = node.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			Node child = node.GetChild(i);
			if (child.Name.ToString() == name) return child as LotObject;
			LotObject found = FindByName(child, name);
			if (found != null) return found;
		}
		return null;
	}
}