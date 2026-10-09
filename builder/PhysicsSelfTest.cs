using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using NLua;

/// <summary>
/// Verification for milestone 3.11 (physics API): the friction/bounce/mass descriptors and the
/// <c>MassForBody</c> rule (pure), the bound verbs through the real API and the NLua hit-table
/// mechanism on a private VM, and a staged session probe that drives a real script through raycasts,
/// impulses and material changes — asserting the logged numbers and the sampled trajectories.
///
/// Follows the project's self-test convention: no framework, prints PASS/FAIL lines, returns the
/// failure count. Runs from <c>BuilderScene._Process</c> after <see cref="CameraSelfTest.ProbeDone"/>
/// — the staged probes drive Test mode one at a time — and the probe is headless-only.
/// </summary>
public static class PhysicsSelfTest
{
	private static int _failures;
	private static int _checks;

	public static int Run(BuilderScene scene)
	{
		_failures = 0;
		_checks = 0;

		Check("harness: the lot scene is available", scene != null);
		if (scene == null)
		{
			GD.Print("[PhysicsSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
			return _failures;
		}

		TestDescriptors();
		TestMaterialState();
		TestLuaSurface(scene);

		GD.Print("[PhysicsSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
		StartProbe(scene);
		return _failures;
	}

	private static void Check(string name, bool condition)
	{
		_checks++;
		if (condition)
		{
			GD.Print("[PhysicsSelfTest] PASS  " + name);
		}
		else
		{
			_failures++;
			GD.PrintErr("[PhysicsSelfTest] FAIL  " + name);
		}
	}

	private static bool Near(float a, float b, float epsilon)
	{
		return Mathf.Abs(a - b) <= epsilon;
	}

	// --- pure: the declared properties ----------------------------------------------------------

	private static void TestDescriptors()
	{
		LotPropertyDescriptor friction = LotPropertyRegistry.Find("friction");
		LotPropertyDescriptor bounce = LotPropertyRegistry.Find("bounce");
		LotPropertyDescriptor mass = LotPropertyRegistry.Find("mass");
		Check("descriptors: friction, bounce and mass are declared",
			friction != null && bounce != null && mass != null);
		Check("descriptors: all three are plain numbers",
			friction.Kind == LotPropertyKind.Float && bounce.Kind == LotPropertyKind.Float &&
			mass.Kind == LotPropertyKind.Float);
		Check("descriptors: the reader and writer are wired",
			friction.GetFloat != null && friction.SetFloat != null &&
			bounce.GetFloat != null && bounce.SetFloat != null &&
			mass.GetFloat != null && mass.SetFloat != null);

		LotObject part = LotObject.Create(LotObjectKind.Cube, "PhysDesc", new Color(1f, 1f, 1f));
		LotObject decal = LotObject.Create(LotObjectKind.Decal, "PhysDescDecal", new Color(1f, 1f, 1f));
		try
		{
			Check("descriptors: all three are offered on parts, not decals",
				friction.AppliesTo != null && mass.AppliesTo != null &&
				friction.AppliesTo(part) && mass.AppliesTo(part) && bounce.AppliesTo(part) &&
				!friction.AppliesTo(decal) && !mass.AppliesTo(decal) && !bounce.AppliesTo(decal));
		}
		finally
		{
			part.Free();
			decal.Free();
		}
	}

	// --- the bound verbs, and the hit-table mechanism on a private VM ---------------------------

	private static void TestLuaSurface(BuilderScene scene)
	{
		LotObject part = LotObject.Create(LotObjectKind.Cube, "PhysApiTest", new Color(1f, 1f, 1f));
		scene.LotRoot.AddChild(part);
		int handle = scene.RegisterHandle(part);
		try
		{
			// The material verbs are lot state: callable outside a session, exactly like SetAnchored.
			Check("api: SetFriction writes through the bound verb", scene.Api.SetFriction(handle, 0.25f));
			Check("api: GetFriction reads it back", Near(scene.Api.GetFriction(handle), 0.25f, 1e-6f));
			Check("api: SetBounce clamps through the bound verb",
				scene.Api.SetBounce(handle, 5f) && Near(scene.Api.GetBounce(handle), 1f, 1e-6f));
			Check("api: SetMass writes and GetMass reads (0 = auto)",
				scene.Api.SetMass(handle, 3f) && Near(scene.Api.GetMass(handle), 3f, 1e-6f) &&
				scene.Api.SetMass(handle, 0f) && scene.Api.GetMass(handle) == 0f);
			Check("api: a missing handle is refused, not silently accepted",
				!scene.Api.SetFriction(999999, 1f) && !scene.Api.SetMass(999999, 1f));
			Check("api: GetFriction on a missing handle is 0", scene.Api.GetFriction(999999) == 0f);

			// The action verbs need the session's physics world: outside one they refuse.
			Check("api: ApplyImpulse refuses outside a session", !scene.Api.ApplyImpulse(handle, 0f, 1f, 0f));
			Check("api: SetVelocity refuses outside a session", !scene.Api.SetVelocity(handle, 0f, 0f, 0f));
			Check("api: GetVelocityX is 0 outside a session", scene.Api.GetVelocityX(handle) == 0f);
			Check("api: Raycast returns nil outside a session",
				scene.Api.Raycast(0f, 10f, 0f, 0f, -1f, 0f, 10f) == null);
		}
		finally
		{
			scene.DestroyEntity(part);
		}

		TestHitTableMechanism();
	}

	/// <summary>The mint mechanism <see cref="LotLuaApi.Raycast"/> uses, exercised on a private VM
	/// inside the real sandbox: a bound method returns a freshly minted table, the table crosses
	/// into an env intact, and two results never alias. (NLua 1.7.9 hands a returned Dictionary
	/// back as opaque userdata — spike-verified before this milestone relied on the alternative.)</summary>
	private sealed class HitTableStub
	{
		public Lua Owner;

		public object Mint()
		{
			Owner.NewTable("__test_hit");
			NLua.LuaTable table = Owner.GetTable("__test_hit");
			table["handle"] = 42;
			table["y"] = 5.0;
			table["ny"] = 1.0;
			table["distance"] = 15.0;
			return table;
		}
	}

	private static void TestHitTableMechanism()
	{
		Lua state = new Lua();
		try
		{
			List<string> logs = new List<string>();
			state.RegisterFunction("__testLog", logs, typeof(List<string>).GetMethod("Add"));
			HitTableStub stub = new HitTableStub();
			stub.Owner = state;
			state.RegisterFunction("__testMint", stub, typeof(HitTableStub).GetMethod("Mint"));
			// The stub rides in through the Lot table (the only channel a sandboxed env exposes —
			// a bare global is invisible inside one): the bootstrap wraps this table in the
			// read-only proxy, whose __index forwards to it live.
			state.DoString("Lot = { Log = function(message) __testLog(tostring(message)) end, " +
				"Mint = __testMint }", "testlot");
			LuaBootstrap.Apply(state);

			bool ran = RunChunk(state,
				"local a = Lot.Mint()\n" +
				"local b = Lot.Mint()\n" +
				"Lot.Log(type(a) .. ' ' .. tostring(a.handle == 42) .. ' ' .. tostring(a.y == 5.0) .. ' ' .. " +
				"tostring(a.ny == 1.0) .. ' ' .. tostring(a.distance == 15.0))\n" +
				"if b ~= nil and a ~= b then a.handle = 99 end\n" +
				"Lot.Log('alias ' .. tostring(a.handle == 99) .. ' ' .. tostring(b.handle == 42))");
			if (!ran || logs.Count < 2)
				GD.PrintErr("[PhysicsSelfTest] mint diag: ran=" + ran + ", logs=" + logs.Count +
					(logs.Count > 0 ? " [" + string.Join(" | ", logs) + "]" : ""));
			Check("lua: a minted hit table crosses into an env as a table with its fields",
				ran && logs.Count >= 1 && logs[0] == "table true true true true");
			Check("lua: two results are independent tables (writing one leaves the other)",
				logs.Count >= 2 && logs[1] == "alias true true");
		}
		finally
		{
			state.Dispose();
		}
	}

	/// <summary>Runs one chunk inside a fresh env through the real env loader and pcall, so a
	/// fixture error is a failed check rather than an exception in the suite.</summary>
	private static bool RunChunk(Lua state, string chunk)
	{
		string script = "local env = __openlot_newEnv(7)\n" +
			"local fn, err = __openlot_load([=[\n" + chunk + "\n]=], 'testchunk', env)\n" +
			"if fn == nil then return false end\n" +
			"return pcall(fn)";
		try
		{
			object[] results = state.DoString(script, "physicsselftest");
			return results != null && results.Length > 0 && results[0] is bool ok && ok;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static void TestMaterialState()
	{
		LotObject part = LotObject.Create(LotObjectKind.Cube, "PhysTest", new Color(1f, 1f, 1f));
		try
		{
			Check("defaults: friction 1, bounce 0, mass auto (0)",
				part.Friction == LotObject.DefaultFriction &&
				part.Bounce == LotObject.DefaultBounce && part.Mass == 0f);

			// The defaults are Godot's own, so an untouched part carries no material at all.
			StaticBody3D staticBody = part.CollisionBody as StaticBody3D;
			Check("defaults: an untouched part allocates no material override",
				staticBody != null && staticBody.PhysicsMaterialOverride == null);

			part.SetFriction(-5f);
			Check("clamp: friction never goes below 0", part.Friction == 0f);
			part.SetFriction(0.35f);
			part.SetBounce(2f);
			Check("clamp: bounce stops at 1", Near(part.Bounce, 1f, 1e-6f));
			part.SetBounce(-1f);
			Check("clamp: bounce stops at 0", part.Bounce == 0f);

			part.SetBounce(0.5f);
			StaticBody3D withMaterial = part.CollisionBody as StaticBody3D;
			Check("material: a non-default value reaches the part's static body",
				withMaterial != null && withMaterial.PhysicsMaterialOverride != null &&
				Near(withMaterial.PhysicsMaterialOverride.Friction, 0.35f, 1e-6f) &&
				Near(withMaterial.PhysicsMaterialOverride.Bounce, 0.5f, 1e-6f));
			part.SetFriction(1f);
			part.SetBounce(0f);
			Check("material: backing out to the defaults clears the override again",
				(part.CollisionBody as StaticBody3D).PhysicsMaterialOverride == null);

			part.SetMass(2.5f);
			Check("mass: an explicit value is kept", Near(part.Mass, 2.5f, 1e-6f));
			Check("mass: an explicit value is what a body carries",
				Near(part.MassForBody(), 2.5f, 1e-6f));
			part.SetMass(0.001f);
			Check("mass: a tiny positive value is floored (stability)",
				Near(part.Mass, LotObject.MinExplicitMass, 1e-9f));
			part.SetMass(0f);
			Check("mass: 0 means auto", part.Mass == 0f);
			Check("mass: auto derives from the mesh's volume (a unit cube = 1)",
				Near(part.MassForBody(), 1f, 1e-4f));
			part.Scale = new Vector3(2f, 2f, 2f);
			Check("mass: auto follows the part's size (a 2x cube = 8)",
				Near(part.MassForBody(), 8f, 1e-3f));

			List<LotPropertyDescriptor> buffer = new List<LotPropertyDescriptor>();
			LotPropertyRegistry.CollectFor(part, buffer);
			bool sawAll = false;
			for (int i = 0; i < buffer.Count; i++)
			{
				if (buffer[i].Id == "friction" || buffer[i].Id == "bounce" || buffer[i].Id == "mass")
					sawAll = true;
			}
			Check("descriptors: a part collects the physics set", sawAll);
		}
		finally
		{
			part.Free();
		}
	}

	// --- staged session probe (a script casts, pushes and changes material for real) -------------

	/// <summary>The scenario's wall clock: the fixture raycasts at 0.2 s, kicks at 0.4 s, writes
	/// materials at 0.6 s, launches the sliders at 0.8 s and reports travel and rebound at 2.0 s —
	/// 2.6 s of margin covers the last report plus a slow frame.</summary>
	private const ulong ProbeWorkspaceWaitMsec = 2600;

	/// <summary>Wait after leaving the session, for the restore to settle.</summary>
	private const ulong ProbeExitWaitMsec = 500;

	private const string ProbeScriptDir = "user://Scripts";
	private const string ProbeScriptName = "__physicsselftest.lua";

	/// <summary>
	/// The fixture script (attached to the lot root): finds the scenario parts by name, then walks
	/// a wall-clock timeline — a raycast down at the target and one up into the sky, then kicks:
	/// one +X impulse each to a mass-auto cube and a SetMass(4) cube. The kicks are read a few
	/// frames later, because the engine applies an impulse during the physics step (a same-frame
	/// read is stale — Jolt queues it), and along +X so free fall can never pollute the number.
	/// Then material writes with read-backs, two sliders launched at the same speed with different
	/// friction, and two balls with different bounce. Every number the probe asserts is logged as
	/// `PHY ...`; script output is the observable.
	/// </summary>
	private const string ProbeScript =
		"local t = 0\n" +
		"local phase = 0\n" +
		"local floor = Lot.GetHandle(\"PhysicsProbeFloor\")\n" +
		"local target = Lot.GetHandle(\"PhysicsProbeTarget\")\n" +
		"local impA = Lot.GetHandle(\"PhysicsProbeImpA\")\n" +
		"local impB = Lot.GetHandle(\"PhysicsProbeImpB\")\n" +
		"local ballHi = Lot.GetHandle(\"PhysicsProbeBallHi\")\n" +
		"local ballLo = Lot.GetHandle(\"PhysicsProbeBallLo\")\n" +
		"local sliderHi = Lot.GetHandle(\"PhysicsProbeSliderHi\")\n" +
		"local sliderLo = Lot.GetHandle(\"PhysicsProbeSliderLo\")\n" +
		"local hiMax, loMax, lauHi, lauLo = 0, 0, 0, 0\n" +
		"function onFrame(dt)\n" +
		"    t = t + dt\n" +
		"    if t >= 1.1 and t <= 2.0 then\n" +
		"        local y = Lot.GetPositionY(ballHi)\n" +
		"        if y > hiMax then hiMax = y end\n" +
		"        y = Lot.GetPositionY(ballLo)\n" +
		"        if y > loMax then loMax = y end\n" +
		"    end\n" +
		"    if phase == 0 and t >= 0.2 then\n" +
		"        phase = 1\n" +
		"        local down = Lot.Raycast(200, 20, 200, 0, -1, 0, 100)\n" +
		"        if down ~= nil then\n" +
		"            Lot.Log(\"PHY ray \" .. down.handle .. \" \" .. down.y .. \" \" .. down.ny .. \" \" .. down.distance)\n" +
		"        else\n" +
		"            Lot.Log(\"PHY ray-nil\")\n" +
		"        end\n" +
		"        local up = Lot.Raycast(200, 20, 200, 0, 1, 0, 100)\n" +
		"        if up == nil then Lot.Log(\"PHY miss\") else Lot.Log(\"PHY miss-table\") end\n" +
		"    elseif phase == 1 and t >= 0.4 then\n" +
		"        phase = 2\n" +
		"        Lot.SetMass(impB, 4)\n" +
		"        Lot.ApplyImpulse(impA, 8, 0, 0)\n" +
		"        Lot.ApplyImpulse(impB, 8, 0, 0)\n" +
		"        Lot.Log(\"PHY mass \" .. Lot.GetMass(impB))\n" +
		"        Lot.SetFriction(floor, 0.7)\n" +
		"        Lot.SetFriction(sliderHi, 1)\n" +
		"        Lot.SetFriction(sliderLo, 0)\n" +
		"        Lot.SetBounce(ballHi, 0.9)\n" +
		"        Lot.SetBounce(ballLo, 0)\n" +
		"        Lot.Log(\"PHY mat \" .. Lot.GetFriction(floor) .. \" \" .. Lot.GetFriction(sliderLo) .. \" \" ..\n" +
		"            Lot.GetBounce(ballHi) .. \" \" .. Lot.GetBounce(ballLo))\n" +
		"    elseif phase == 2 and t >= 0.6 then\n" +
		"        phase = 3\n" +
		"        Lot.Log(\"PHY imp \" .. Lot.GetVelocityX(impA) .. \" \" .. Lot.GetVelocityX(impB))\n" +
		"    elseif phase == 3 and t >= 0.8 then\n" +
		"        phase = 4\n" +
		"        Lot.SetVelocity(sliderHi, 6, 0, 0)\n" +
		"        Lot.SetVelocity(sliderLo, 6, 0, 0)\n" +
		"        lauHi = Lot.GetPositionX(sliderHi)\n" +
		"        lauLo = Lot.GetPositionX(sliderLo)\n" +
		"        Lot.Log(\"PHY launch \" .. lauHi .. \" \" .. lauLo)\n" +
		"    elseif phase == 4 and t >= 2.0 then\n" +
		"        phase = 5\n" +
		"        Lot.Log(\"PHY travel \" .. (Lot.GetPositionX(sliderHi) - lauHi) .. \" \" ..\n" +
		"            (Lot.GetPositionX(sliderLo) - lauLo))\n" +
		"        Lot.Log(\"PHY balls \" .. hiMax .. \" \" .. loMax)\n" +
		"    end\n" +
		"end\n";

	private static BuilderScene _probeScene;
	private static Node _probeFixture;
	private static LotObject _probeSliderHi;
	private static LotObject _probeSliderLo;
	private static int _probeTargetHandle;
	private static ulong _probeDeadlineMsec;
	private static int _probeStage; // 1 = mid-scenario, 2 = out of the session, 3 = finished
	private static int _probeChecks;
	private static int _probeFailures;
	private static readonly List<string> _probeFiles = new List<string>();

	/// <summary>Whether the staged probe completed; the scene warns when a run ends too early.</summary>
	public static bool ProbeDone { get { return _probeStage >= 3; } }

	private static void StartProbe(BuilderScene scene)
	{
		if (DisplayServer.GetName() != "headless")
		{
			_probeStage = 3;
			return;
		}

		_probeScene = scene;
		_probeChecks = 0;
		_probeFailures = 0;

		// A straggler from an earlier run would sit in the walk path and break the trajectories.
		DestroyLeftovers(scene, "PhysicsProbe");

		// The scenario, on a 20 m floor slab at (200, .., 200) — far from every other probe's
		// region. The floor and the target stay anchored (static bodies); everything physics should
		// move starts unanchored, so the session's body swap (§3.6) makes it a RigidBody.
		NewScratchPart(scene, "PhysicsProbeFloor", new Vector3(200f, 0.5f, 200f), LotObjectKind.Cube)
			.Scale = new Vector3(20f, 1f, 20f);
		LotObject target = NewScratchPart(scene, "PhysicsProbeTarget", new Vector3(200f, 4f, 200f), LotObjectKind.Cube);
		target.Scale = new Vector3(2f, 2f, 2f);
		NewScratchPart(scene, "PhysicsProbeImpA", new Vector3(204f, 5f, 196f), LotObjectKind.Cube).SetAnchored(false);
		NewScratchPart(scene, "PhysicsProbeImpB", new Vector3(206f, 5f, 192f), LotObjectKind.Cube).SetAnchored(false);
		NewScratchPart(scene, "PhysicsProbeBallHi", new Vector3(204f, 6f, 204f), LotObjectKind.Sphere).SetAnchored(false);
		NewScratchPart(scene, "PhysicsProbeBallLo", new Vector3(206f, 6f, 204f), LotObjectKind.Sphere).SetAnchored(false);
		_probeSliderHi = NewScratchPart(scene, "PhysicsProbeSliderHi", new Vector3(198f, 1.5f, 206f), LotObjectKind.Cube);
		_probeSliderHi.SetAnchored(false);
		_probeSliderLo = NewScratchPart(scene, "PhysicsProbeSliderLo", new Vector3(198f, 1.5f, 208f), LotObjectKind.Cube);
		_probeSliderLo.SetAnchored(false);
		_probeTargetHandle = scene.RegisterHandle(target);

		WriteFixture(ProbeScriptName, ProbeScript);
		_probeFixture = AttachFixture(scene.LotRoot, ProbeScriptName);

		_probeDeadlineMsec = Time.GetTicksMsec() + ProbeWorkspaceWaitMsec;
		_probeStage = 1;
		GD.Print("[PhysicsSelfTest] session probe started (waiting " + ProbeWorkspaceWaitMsec + " ms)");
		// The real entry path a creator presses: this reloads the lot (the fixture loads and finds
		// the scenario by name) and starts the session, where the body swap happens.
		scene.EnterTestMode(false);
	}

	/// <summary>A scratch scenario part: created, placed, registered (the script finds it by name
	/// through the handle registry), and left to the caller to anchor or not.</summary>
	private static LotObject NewScratchPart(BuilderScene scene, string name, Vector3 at, LotObjectKind kind)
	{
		LotObject part = LotObject.Create(kind, name, new Color(0.7f, 0.8f, 0.9f));
		part.Position = at;
		scene.LotRoot.AddChild(part);
		scene.RegisterHandle(part);
		return part;
	}

	/// <summary>Advances the staged probe: stage 1 asserts the scenario from its logs and leaves
	/// the session; stage 2 proves the authored world (transform and material values) came back.</summary>
	public static void TickProbe()
	{
		if (_probeStage < 1 || _probeStage >= 3) return;
		if (Time.GetTicksMsec() < _probeDeadlineMsec) return;

		if (_probeStage == 1)
		{
			_probeStage = 2;
			CheckProbe();
			_probeScene.ExitTestMode();
			_probeDeadlineMsec = Time.GetTicksMsec() + ProbeExitWaitMsec;
			return;
		}

		// Stage 2: out of the session — the authored values are back and the parts are static again.
		_probeStage = 3;
		ProbeCheck("leaving the session restores the sliders to where they were authored",
			Near(_probeSliderHi.Position.X, 198f, 0.05f) && Near(_probeSliderLo.Position.X, 198f, 0.05f));
		ProbeCheck("leaving the session restores the authored friction value",
			Near(_probeSliderLo.Friction, 1f, 1e-5f));
		ProbeCheck("the parts' bodies are static again (the simulation ended)",
			!_probeSliderHi.IsSimulated && !_probeSliderLo.IsSimulated);

		Cleanup();
		_probeScene = null;
		GD.Print("[PhysicsSelfTest] session probe: " + _probeChecks + " checks, " + _probeFailures + " failure(s).");
	}

	private static void CheckProbe()
	{
		double[] ray;
		bool rayParsed = TryParseLog("PHY ray ", out ray);
		ProbeCheck("the raycast hit the target with handle/point/normal/distance (" +
			(rayParsed ? Format(ray) : "no line") + ")",
			rayParsed && ray.Length == 4 && (int)ray[0] == _probeTargetHandle &&
			Near((float)ray[1], 5f, 0.25f) && Near((float)ray[2], 1f, 0.05f) &&
			Near((float)ray[3], 15f, 0.4f));
		ProbeCheck("a ray into empty sky comes back nil",
			HasLog("PHY miss") && !HasLog("PHY miss-table"));

		double[] imp;
		bool impParsed = TryParseLog("PHY imp ", out imp);
		ProbeCheck("one kick moves the mass-auto cube 4x the mass-4 cube (" +
			(impParsed ? Format(imp) : "no line") + ")",
			impParsed && imp.Length == 2 &&
			Near((float)imp[0], 8f, 0.5f) && Near((float)imp[1], 2f, 0.25f) &&
			imp[0] > imp[1] * 3.4 && imp[0] < imp[1] * 4.6);
		double[] mass;
		bool massParsed = TryParseLog("PHY mass ", out mass);
		ProbeCheck("SetMass wrote through the bound verb and reads back as 4",
			massParsed && mass.Length == 1 && Near((float)mass[0], 4f, 0.01f));

		double[] mat;
		bool matParsed = TryParseLog("PHY mat ", out mat);
		ProbeCheck("friction and bounce round-trip through the bound verbs (" +
			(matParsed ? Format(mat) : "no line") + ")",
			matParsed && mat.Length == 4 &&
			Near((float)mat[0], 0.7f, 0.02f) && Near((float)mat[1], 0f, 0.001f) &&
			Near((float)mat[2], 0.9f, 0.02f) && Near((float)mat[3], 0f, 0.001f));

		double[] travel;
		bool travelParsed = TryParseLog("PHY travel ", out travel);
		ProbeCheck("the slippery slider out-slides the grippy one in the same time (" +
			(travelParsed ? Format(travel) : "no line") + ")",
			travelParsed && travel.Length == 2 && travel[0] > 0.2 && travel[0] < 5.0 &&
			travel[1] > 4.0 && travel[1] > travel[0] + 2.0);

		double[] balls;
		bool ballsParsed = TryParseLog("PHY balls ", out balls);
		ProbeCheck("the bouncy ball rebounds, the dead one never gets high (" +
			(ballsParsed ? Format(balls) : "no line") + ")",
			ballsParsed && balls.Length == 2 && balls[0] > 4.4 && balls[0] < 5.7 && balls[1] < 2.5);

		ProbeCheck("the session swapped the unanchored parts to simulated bodies",
			_probeSliderHi.IsSimulated && _probeSliderLo.IsSimulated);
	}

	private static void ProbeCheck(string name, bool condition)
	{
		_probeChecks++;
		if (condition)
		{
			GD.Print("[PhysicsSelfTest] PASS  (session probe) " + name);
		}
		else
		{
			_probeFailures++;
			GD.PrintErr("[PhysicsSelfTest] FAIL  (session probe) " + name);
		}
	}

	/// <summary>Formats a parsed log line's numbers for the check text (a calibration aid: a
	/// failing check prints the numbers it saw).</summary>
	private static string Format(double[] values)
	{
		System.Text.StringBuilder text = new System.Text.StringBuilder();
		for (int i = 0; i < values.Length; i++)
		{
			if (i > 0) text.Append(", ");
			text.Append(values[i].ToString("0.###", CultureInfo.InvariantCulture));
		}
		return text.ToString();
	}

	/// <summary>Finds the most recent log line starting with <paramref name="prefix"/> and parses
	/// its space-separated numbers. The window is bounded because the ring drops old entries.</summary>
	private static bool TryParseLog(string prefix, out double[] values)
	{
		values = null;
		int start = Math.Max(0, LotLog.Count - 200);
		for (int i = LotLog.Count - 1; i >= start; i--)
		{
			string text = LotLog.At(i).Text;
			if (!text.StartsWith(prefix, StringComparison.Ordinal)) continue;
			string[] parts = text.Substring(prefix.Length).Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length == 0) return false;
			double[] parsed = new double[parts.Length];
			for (int j = 0; j < parts.Length; j++)
			{
				if (!double.TryParse(parts[j], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[j]))
					return false;
			}
			values = parsed;
			return true;
		}
		return false;
	}

	/// <summary>True when a recent log line equals <paramref name="exact"/>.</summary>
	private static bool HasLog(string exact)
	{
		int start = Math.Max(0, LotLog.Count - 200);
		for (int i = start; i < LotLog.Count; i++)
		{
			if (LotLog.At(i).Text == exact) return true;
		}
		return false;
	}

	private static void Cleanup()
	{
		DestroyLeftovers(_probeScene, "PhysicsProbe");
		if (_probeFixture != null && GodotObject.IsInstanceValid(_probeFixture))
			_probeFixture.QueueFree();
		_probeFixture = null;
		for (int i = 0; i < _probeFiles.Count; i++)
		{
			string path = ProbeScriptDir + "/" + _probeFiles[i];
			if (FileAccess.FileExists(path)) DirAccess.RemoveAbsolute(path);
		}
		_probeFiles.Clear();
	}

	private static void DestroyLeftovers(BuilderScene scene, string prefix)
	{
		List<Node> doomed = new List<Node>();
		CollectByNamePrefix(scene.LotRoot, prefix, doomed);
		for (int i = 0; i < doomed.Count; i++) scene.DestroyEntity(doomed[i]);
	}

	private static void CollectByNamePrefix(Node node, string prefix, List<Node> found)
	{
		int count = node.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			Node child = node.GetChild(i);
			if (child.Name.ToString().StartsWith(prefix, StringComparison.Ordinal)) found.Add(child);
			else CollectByNamePrefix(child, prefix, found);
		}
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
		_probeFiles.Add(fileName);
	}

	private static Node AttachFixture(Node parent, string fileName)
	{
		LotScriptNode node = new LotScriptNode();
		node.ScriptPath = ProbeScriptDir + "/" + fileName;
		node.DisplayName = fileName;
		node.Name = fileName.Replace('.', '_');
		parent.AddChild(node);
		return node;
	}
}
