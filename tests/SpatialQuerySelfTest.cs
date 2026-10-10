using System;
using Godot;

/// <summary>
/// Verification for the spatial query primitives (milestone 3.13, primitives layer): the pure
/// explosion falloff math and the verb refusals run synchronously with the other delayed suites;
/// a staged probe then drives the real queries against live geometry inside a session — sphere and
/// box overlap, the swept sphere (melee), and an explosion whose two bodies move with the expected
/// falloff ratio. Headless-only, like the other probes.
/// </summary>
public static class SpatialQuerySelfTest
{
	private static int _checks;
	private static int _failures;

	public static int Run(BuilderScene scene)
	{
		_checks = 0;
		_failures = 0;
		TestExplosionImpulse();
		TestRefusals(scene);
		GD.Print("[SpatialQuerySelfTest] " + _checks + " checks, " + _failures + " failure(s).");
		StartProbe(scene);
		return _failures;
	}

	private static void Check(string name, bool condition)
	{
		_checks++;
		if (condition)
		{
			GD.Print("[SpatialQuerySelfTest] PASS  " + name);
		}
		else
		{
			_failures++;
			GD.PrintErr("[SpatialQuerySelfTest] FAIL  " + name);
		}
	}

	private static void CheckNear(string name, float actual, float expected)
	{
		Check(name + " (got " + actual.ToString("0.####") + ", want " + expected.ToString("0.####") + ")",
			Mathf.Abs(actual - expected) <= 1e-3f);
	}

	// --- the pure falloff math -------------------------------------------------------------------

	private static void TestExplosionImpulse()
	{
		// Halfway out: half power, along the outward direction.
		Vector3 half = SpatialQuery.ExplosionImpulse(Vector3.Zero, new Vector3(0f, 0f, 2f), 4f, 40f);
		CheckNear("blast: halfway out is half power", half.Z, 20f);
		CheckNear("blast: no sideways component", half.X, 0f);

		// On the rim and beyond: nothing.
		Check("blast: the rim receives nothing", SpatialQuery.ExplosionImpulse(Vector3.Zero, new Vector3(0f, 0f, 4f), 4f, 40f) == Vector3.Zero);
		Check("blast: past the rim receives nothing", SpatialQuery.ExplosionImpulse(Vector3.Zero, new Vector3(0f, 0f, 5f), 4f, 40f) == Vector3.Zero);
		Check("blast: the exact centre receives nothing (no direction exists there)",
			SpatialQuery.ExplosionImpulse(Vector3.Zero, Vector3.Zero, 4f, 40f) == Vector3.Zero);
		Check("blast: a non-positive radius or power pushes nothing",
			SpatialQuery.ExplosionImpulse(Vector3.Zero, new Vector3(0f, 0f, 1f), 0f, 40f) == Vector3.Zero &&
			SpatialQuery.ExplosionImpulse(Vector3.Zero, new Vector3(0f, 0f, 1f), 4f, 0f) == Vector3.Zero);

		// Magnitude is power times falloff, independent of direction length.
		Vector3 diagonal = SpatialQuery.ExplosionImpulse(Vector3.Zero, new Vector3(3f, 0f, 4f), 20f, 100f);
		CheckNear("blast: the magnitude is power times falloff (75 over a 5 m reach)", diagonal.Length(), 75f);
	}

	// --- the refusals, outside a session (the gate every query shares) ---------------------------

	private static void TestRefusals(BuilderScene scene)
	{
		Check("query: a sphere query outside a session refuses with nil", scene.Api.QuerySphere(0f, 0f, 0f, 1f) == null);
		Check("query: a box query outside a session refuses with nil", scene.Api.QueryBox(0f, 0f, 0f, 1f, 1f, 1f) == null);
		Check("query: a sweep outside a session refuses with nil", scene.Api.SweepSphere(0f, 0f, 0f, 1f, 0f, 0f, 1f, 5f) == null);
		Check("query: an explosion outside a session pushes nothing", scene.Api.Explode(0f, 0f, 0f, 4f, 10f) == 0);
	}

	// --- the staged probe: real queries against live geometry in a session -----------------------

	private const string ProbeScriptName = "__sqselftest_probe.lua";
	private const string ProbeScriptDir = "user://Scripts";
	private const long ProbeQueryWaitMsec = 900;
	private const long ProbeBlastWaitMsec = 60;
	private const long ProbeExitWaitMsec = 400;

	private static BuilderScene _probeScene;
	private static LotScriptNode _probeScriptNode;
	private static ulong _probeDeadlineMsec;
	private static int _probeStage; // 0 = not started, 1 = in session, 2 = blasted, 3 = exited, 4 = finished
	private static int _probeChecks;
	private static int _probeFailures;
	private static int _nearHandle;
	private static int _farHandle;
	private static int _offHandle;
	private static int _blastOneHandle;
	private static int _blastTwoHandle;
	private static readonly System.Collections.Generic.List<LotObject> _probeParts = new System.Collections.Generic.List<LotObject>();

	/// <summary>Whether the staged probe completed; the scene warns when a run ends too early.</summary>
	public static bool ProbeDone { get { return _probeStage >= 4; } }

	private static void StartProbe(BuilderScene scene)
	{
		if (DisplayServer.GetName() != "headless")
		{
			_probeStage = 4;
			return;
		}
		_probeScene = scene;
		_probeChecks = 0;
		_probeFailures = 0;
		_probeParts.Clear();

		// Query fixture: P1 sits at the sphere's centre, P2 is 6 m out (inside the box at
		// (6,1,0) and on the sweep axis), P3 is 6 m off that axis (outside both).
		_nearHandle = AddProbePart(scene, "SQNear", new Vector3(0f, 1f, 0f), true);
		_farHandle = AddProbePart(scene, "SQFar", new Vector3(6f, 1f, 0f), true);
		_offHandle = AddProbePart(scene, "SQOff", new Vector3(0f, 1f, 6f), true);
		// Blast fixture, away from the query fixture: two same-size free cubes at 1 m and 3 m
		// from the blast point in a 4 m radius — 0.75 and 0.25 of its power, so the velocity
		// ratio is about 3:1. The fixture brings its own floor: the default lot's ground does not
		// reach (30,30), and a free cube with nothing under it falls out of the blast radius
		// before the test fires.
		LotObject blastFloor = LotObject.Create(LotObjectKind.Cube, "SQBlastFloor", new Color(0.4f, 0.4f, 0.4f));
		blastFloor.Position = new Vector3(30f, 0f, 30f);
		blastFloor.Scale = new Vector3(12f, 1f, 12f);
		scene.LotRoot.AddChild(blastFloor);
		scene.RegisterHandle(blastFloor);
		_probeParts.Add(blastFloor);
		_blastOneHandle = AddProbePart(scene, "SQBlastOne", new Vector3(30f, 2f, 31f), false);
		_blastTwoHandle = AddProbePart(scene, "SQBlastTwo", new Vector3(30f, 2f, 33f), false);

		string script =
			"function start()\n" +
			"    local sphere = Lot.QuerySphere(0, 1, 0, 3)\n" +
			"    local near = false\n" +
			"    local far = false\n" +
			"    if sphere ~= nil then\n" +
			"        for i = 1, #sphere do\n" +
			"            if sphere[i].handle == " + _nearHandle + " then near = true end\n" +
			"            if sphere[i].handle == " + _farHandle + " then far = true end\n" +
			"        end\n" +
			"    end\n" +
			"    Lot.Log('SQ sphere near ' .. tostring(near))\n" +
			"    Lot.Log('SQ sphere far ' .. tostring(far))\n" +
			"    local box = Lot.QueryBox(6, 1, 0, 3, 3, 3)\n" +
			"    local boxFar = false\n" +
			"    local boxOff = false\n" +
			"    if box ~= nil then\n" +
			"        for i = 1, #box do\n" +
			"            if box[i].handle == " + _farHandle + " then boxFar = true end\n" +
			"            if box[i].handle == " + _offHandle + " then boxOff = true end\n" +
			"        end\n" +
			"    end\n" +
			"    Lot.Log('SQ box far ' .. tostring(boxFar))\n" +
			"    Lot.Log('SQ box off ' .. tostring(boxOff))\n" +
			"    local sweep = Lot.SweepSphere(0, 1, 0, 1, 0, 0, 1.5, 15)\n" +
			"    local swept = false\n" +
			"    local sweptOff = false\n" +
			"    if sweep ~= nil then\n" +
			"        for i = 1, #sweep do\n" +
			"            if sweep[i].handle == " + _farHandle + " then swept = true end\n" +
			"            if sweep[i].handle == " + _offHandle + " then sweptOff = true end\n" +
			"        end\n" +
			"    end\n" +
			"    Lot.Log('SQ sweep hit ' .. tostring(swept))\n" +
			"    Lot.Log('SQ sweep off ' .. tostring(sweptOff))\n" +
			"    Lot.Log('SQ bad ' .. tostring(Lot.QuerySphere(0, 0, 0, -1) == nil) .. ' ' .. tostring(Lot.SweepSphere(0, 0, 0, 0, 0, 0, 1, 5) == nil))\n" +
			"end\n";
		if (!DirAccess.DirExistsAbsolute(ProbeScriptDir)) DirAccess.MakeDirRecursiveAbsolute(ProbeScriptDir);
		FileAccess file = FileAccess.Open(ProbeScriptDir + "/" + ProbeScriptName, FileAccess.ModeFlags.Write);
		if (file != null)
		{
			file.StoreString(script);
			file.Dispose();
		}

		// The lot root is itself an entity, so a script attached to it runs at session load —
		// the same path the default character script uses.
		_probeScriptNode = new LotScriptNode();
		_probeScriptNode.ScriptPath = ProbeScriptDir + "/" + ProbeScriptName;
		_probeScriptNode.DisplayName = ProbeScriptName;
		_probeScriptNode.Name = "SQ_probe_lua";
		scene.LotRoot.AddChild(_probeScriptNode);

		_probeDeadlineMsec = Time.GetTicksMsec() + ProbeQueryWaitMsec;
		_probeStage = 1;
		GD.Print("[SpatialQuerySelfTest] session probe started");
		scene.EnterTestMode(false);
	}

	private static int AddProbePart(BuilderScene scene, string name, Vector3 at, bool anchored)
	{
		LotObject part = LotObject.Create(LotObjectKind.Cube, name, new Color(0.6f, 0.6f, 0.9f));
		part.Position = at;
		if (!anchored) part.SetAnchored(false);
		scene.LotRoot.AddChild(part);
		_probeParts.Add(part);
		return scene.RegisterHandle(part);
	}

	private static void ProbeCheck(string name, bool condition)
	{
		_probeChecks++;
		if (condition)
		{
			GD.Print("[SpatialQuerySelfTest] PASS  (session probe) " + name);
		}
		else
		{
			_probeFailures++;
			GD.PrintErr("[SpatialQuerySelfTest] FAIL  (session probe) " + name);
		}
	}

	/// <summary>Advances the staged probe across real frames (called every frame from
	/// BuilderScene._Process): in-session queries → blast + velocity read → the exit + cleanup.</summary>
	public static void TickProbe()
	{
		if (_probeStage == 0 || _probeStage >= 4) return;
		if (Time.GetTicksMsec() < _probeDeadlineMsec) return;

		if (_probeStage == 1)
		{
			_probeStage = 2;
			ProbeCheck("the sphere found the near part and not the far one",
				HasLog("SQ sphere near true") && HasLog("SQ sphere far false"));
			ProbeCheck("the box found the far part and not the off-axis one",
				HasLog("SQ box far true") && HasLog("SQ box off false"));
			ProbeCheck("the sweep found the part in its path and not the off-axis one",
				HasLog("SQ sweep hit true") && HasLog("SQ sweep off false"));
			ProbeCheck("bad arguments refuse with nil in-session", HasLog("SQ bad true true"));

			int pushed = _probeScene.Api.Explode(30f, 1f, 30f, 4f, 60f);
			ProbeCheck("the blast pushed the two free cubes (" + pushed + ")", pushed == 2);
			_probeDeadlineMsec = Time.GetTicksMsec() + ProbeBlastWaitMsec;
			return;
		}

		if (_probeStage == 2)
		{
			_probeStage = 3;
			// One physics step after the impulse (Jolt queues it — §3.11's note), read before
			// friction blurs the ratio.
			float nearVelocity = _probeScene.Api.GetVelocityZ(_blastOneHandle);
			float farVelocity = _probeScene.Api.GetVelocityZ(_blastTwoHandle);
			ProbeCheck("both cubes were pushed outward (near " + nearVelocity.ToString("0.##") + ", far " + farVelocity.ToString("0.##") + ")",
				nearVelocity > 0.2f && farVelocity > 0.05f);
			ProbeCheck("the nearer cube carries the larger impulse (ratio " + (nearVelocity / Mathf.Max(farVelocity, 1e-3f)).ToString("0.##") + ")",
				nearVelocity > farVelocity && nearVelocity / Mathf.Max(farVelocity, 1e-3f) < 4.5f);

			_probeScene.ExitTestMode();
			_probeDeadlineMsec = Time.GetTicksMsec() + ProbeExitWaitMsec;
			return;
		}

		// Stage 3: out of the session — the exit restored the authored world, so now the fixtures
		// (and the probe script) are removed for good and the summary is printed.
		_probeStage = 4;
		for (int i = 0; i < _probeParts.Count; i++)
		{
			if (GodotObject.IsInstanceValid(_probeParts[i])) _probeScene.DestroyEntity(_probeParts[i]);
		}
		_probeParts.Clear();
		if (GodotObject.IsInstanceValid(_probeScriptNode)) _probeScriptNode.QueueFree();
		_probeScriptNode = null;
		if (FileAccess.FileExists(ProbeScriptDir + "/" + ProbeScriptName)) DirAccess.RemoveAbsolute(ProbeScriptDir + "/" + ProbeScriptName);
		_probeScene = null;
		GD.Print("[SpatialQuerySelfTest] session probe: " + _probeChecks + " checks, " + _probeFailures + " failure(s).");
	}

	private static bool HasLog(string exact)
	{
		int start = Math.Max(0, LotLog.Count - 200);
		for (int i = start; i < LotLog.Count; i++)
		{
			if (LotLog.At(i).Text == exact) return true;
		}
		return false;
	}
}
