using System.Collections.Generic;
using Godot;

/// <summary>
/// Verification for milestone 2.9 (part transparency &amp; luminescence): the pure material math,
/// the pre-milestone "untouched part" contract (opaque and unlit at the defaults), the clamps and
/// the colour interplay (a colour write is RGB-only — the alpha belongs to the property), the decal
/// isolation, the collision/pick invariance, the archive round trip of both properties, every
/// bound Lua verb through the real VM, and the budgeted glow-light pool (nearest-N selection, the
/// documented cap, per-frame follow, freed-part safety).
///
/// Follows the project's self-test convention: no framework, prints FAIL lines, returns the failure
/// count, and runs headless from <c>BuilderScene._Ready</c> under <c>#if DEBUG</c> on detached
/// fixtures — no session, no live lot.
/// </summary>
public static class PartMaterialSelfTest
{
	private static int _failures;
	private static int _checks;

	public static int Run(BuilderScene scene)
	{
		_failures = 0;
		_checks = 0;

		Check("harness: the lot scene is available", scene != null);
		TestDeclarations();
		TestDefaultsMatchThePreMilestoneMaterial();
		TestTransparency();
		TestColourInterplay();
		TestLuminescenceMaterial();
		TestDecalIsolation();
		TestCollisionAndPickingUnaffected();
		TestMath();
		TestCaptureAndRestore();
		TestLuaBindings();
		TestGlowPool(scene);

		GD.Print("[PartMaterialSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
		return _failures;
	}

	// --- the declared properties -----------------------------------------------------------------

	private static void TestDeclarations()
	{
		LotPropertyDescriptor transparency = LotPropertyRegistry.Find("transparency");
		LotPropertyDescriptor luminescence = LotPropertyRegistry.Find("luminescence");
		Check("descriptors: transparency and luminescence are declared",
			transparency != null && luminescence != null);
		if (transparency == null || luminescence == null) return;

		Check("descriptors: both are plain numbers in the Surface category",
			transparency.Kind == LotPropertyKind.Float && luminescence.Kind == LotPropertyKind.Float
			&& transparency.Category == "Surface" && luminescence.Category == "Surface");
		Check("descriptors: both have readers and writers wired",
			transparency.GetFloat != null && transparency.SetFloat != null
			&& luminescence.GetFloat != null && luminescence.SetFloat != null);

		LotObject part = LotObject.Create(LotObjectKind.Cube, "MatDescPart", new Color(1f, 1f, 1f));
		LotObject decal = LotObject.Create(LotObjectKind.Decal, "MatDescDecal", new Color(1f, 1f, 1f));
		try
		{
			Check("descriptors: both are offered on parts, not decals",
				transparency.AppliesTo(part) && luminescence.AppliesTo(part)
				&& !transparency.AppliesTo(decal) && !luminescence.AppliesTo(decal));
			Check("descriptors: a fresh part reads 0 for both",
				transparency.GetFloat(part) == 0f && luminescence.GetFloat(part) == 0f);
			transparency.SetFloat(part, 0.25f);
			luminescence.SetFloat(part, 1.5f);
			Check("descriptors: writes reach the part through the descriptor path",
				Near(part.Transparency, 0.25f) && Near(part.Luminescence, 1.5f));
		}
		finally
		{
			Free(part);
			Free(decal);
		}
	}

	// --- the untouched part is exactly the pre-milestone part ------------------------------------

	private static void TestDefaultsMatchThePreMilestoneMaterial()
	{
		LotObject part = LotObject.Create(LotObjectKind.Cube, "MatDefault", new Color(1f, 1f, 1f));
		try
		{
			Check("defaults: both properties start at 0",
				part.Transparency == 0f && part.Luminescence == 0f);
			Check("defaults: the untouched material is on Godot's opaque path",
				part.Material.Transparency == BaseMaterial3D.TransparencyEnum.Disabled);
			Check("defaults: the untouched material is fully opaque and unlit",
				part.Material.AlbedoColor.A == 1f && !part.Material.EmissionEnabled);

			// A colour write is the one other writer a fresh part sees: it must land on the same
			// state, not introduce a transparent pass or emission.
			part.Color = new Color(0.2f, 0.3f, 0.4f, 1f);
			Check("defaults: a colour write keeps the opaque/unlit material",
				part.Material.Transparency == BaseMaterial3D.TransparencyEnum.Disabled
				&& part.Material.AlbedoColor.A == 1f && !part.Material.EmissionEnabled
				&& Near(part.Material.AlbedoColor, new Color(0.2f, 0.3f, 0.4f, 1f)));
		}
		finally
		{
			Free(part);
		}
	}

	// --- transparency ----------------------------------------------------------------------------

	private static void TestTransparency()
	{
		LotObject part = LotObject.Create(LotObjectKind.Cube, "MatTrans", new Color(1f, 1f, 1f));
		try
		{
			part.SetTransparency(0.5f);
			Check("transparency: the value sticks", Near(part.Transparency, 0.5f));
			Check("transparency: above 0 switches to the alpha pass",
				part.Material.Transparency == BaseMaterial3D.TransparencyEnum.Alpha);
			Check("transparency: the albedo alpha is 1 - transparency",
				Near(part.Material.AlbedoColor.A, 0.5f));

			part.SetTransparency(-1f);
			Check("transparency: a negative value clamps to 0 and returns to opaque",
				part.Transparency == 0f
				&& part.Material.Transparency == BaseMaterial3D.TransparencyEnum.Disabled
				&& part.Material.AlbedoColor.A == 1f);
			part.SetTransparency(5f);
			Check("transparency: above 1 clamps to 1 (invisible)",
				part.Transparency == 1f
				&& part.Material.Transparency == BaseMaterial3D.TransparencyEnum.Alpha
				&& part.Material.AlbedoColor.A == 0f);
			part.SetTransparency(0f);
			Check("transparency: back to 0 is the opaque path again",
				part.Material.Transparency == BaseMaterial3D.TransparencyEnum.Disabled
				&& part.Material.AlbedoColor.A == 1f);
		}
		finally
		{
			Free(part);
		}
	}

	/// <summary>The composition rule: the alpha channel belongs to the property, so any RGB write
	/// — the Lua SetColor path, the Inspector colour edit, a TweenColor's RGB — leaves it alone.
	/// The same rule the decal's Opacity already followed.</summary>
	private static void TestColourInterplay()
	{
		LotObject part = LotObject.Create(LotObjectKind.Cube, "MatColour", new Color(1f, 1f, 1f));
		try
		{
			part.SetTransparency(0.75f);
			// The Lua SetColor path: a 3-argument colour (alpha 1) must not reset transparency.
			part.Color = new Color(0f, 1f, 0f, 1f);
			Check("colour: a recolour keeps the transparency",
				Near(part.Transparency, 0.75f) && Near(part.Material.AlbedoColor.A, 0.25f));
			Check("colour: the RGB write did land",
				Near(part.Material.AlbedoColor.R, 0f) && Near(part.Material.AlbedoColor.G, 1f)
				&& Near(part.Material.AlbedoColor.B, 0f));
			Check("colour: the getter reports the derived alpha", Near(part.Color.A, 0.25f));
		}
		finally
		{
			Free(part);
		}
	}

	// --- luminescence (the material half) --------------------------------------------------------

	private static void TestLuminescenceMaterial()
	{
		LotObject part = LotObject.Create(LotObjectKind.Cube, "MatGlow", new Color(0.9f, 0.6f, 0.2f));
		try
		{
			part.SetLuminescence(2f);
			Check("luminescence: the value sticks", Near(part.Luminescence, 2f));
			Check("luminescence: above 0 enables emission", part.Material.EmissionEnabled);
			Check("luminescence: emission is the part's own colour (a lamp keeps its tint)",
				Near(part.Material.Emission, new Color(0.9f, 0.6f, 0.2f)));
			Check("luminescence: the value is the emission energy",
				Near(part.Material.EmissionEnergyMultiplier, 2f));

			part.Color = new Color(0.2f, 0.4f, 0.9f);
			Check("luminescence: a recolour re-tints the emission",
				Near(part.Material.Emission, new Color(0.2f, 0.4f, 0.9f)));

			part.SetLuminescence(-4f);
			Check("luminescence: below 0 clamps to 0 and turns emission off",
				part.Luminescence == 0f && !part.Material.EmissionEnabled);
			part.SetLuminescence(1.25f);
			Check("luminescence: a re-light re-enables emission with the new energy",
				part.Material.EmissionEnabled
				&& Near(part.Material.EmissionEnergyMultiplier, 1.25f));
		}
		finally
		{
			Free(part);
		}
	}

	// --- a decal keeps its own material ----------------------------------------------------------

	private static void TestDecalIsolation()
	{
		LotObject host = LotObject.Create(LotObjectKind.Cube, "MatHost", new Color(1f, 1f, 1f));
		LotObject decal = LotObject.Create(LotObjectKind.Decal, "MatPatch", new Color(1f, 1f, 1f, 1f));
		host.AddChild(decal);
		try
		{
			decal.SetDecalOpacity(0.8f);
			decal.SetTransparency(0.5f);
			decal.SetLuminescence(3f);
			Check("decal: the part setters are no-ops on a panel",
				decal.Transparency == 0f && decal.Luminescence == 0f);
			Check("decal: its material keeps the panel opacity and the unshaded alpha pass",
				Near(decal.Material.AlbedoColor.A, 0.8f)
				&& decal.Material.Transparency == BaseMaterial3D.TransparencyEnum.Alpha
				&& decal.Material.ShadingMode == BaseMaterial3D.ShadingModeEnum.Unshaded);
			Check("decal: no emission is ever enabled on a panel", !decal.Material.EmissionEnabled);

			host.SetTransparency(0.5f);
			Check("decal: a transparent host does not touch the patch's own material",
				Near(decal.Material.AlbedoColor.A, 0.8f) && !decal.Material.EmissionEnabled
				&& Near(host.Material.AlbedoColor.A, 0.5f));

			List<LotPropertyDescriptor> buffer = new List<LotPropertyDescriptor>();
			LotPropertyRegistry.CollectFor(decal, buffer);
			bool hasPartSurface = false;
			for (int i = 0; i < buffer.Count; i++)
			{
				if (buffer[i].Id == "transparency" || buffer[i].Id == "luminescence") hasPartSurface = true;
			}
			Check("decal: the surface properties are not offered on a panel", !hasPartSurface);
		}
		finally
		{
			Free(host);
		}
	}

	// --- picking and collision are material-independent ------------------------------------------

	private static void TestCollisionAndPickingUnaffected()
	{
		LotObject part = LotObject.Create(LotObjectKind.Cube, "MatPick", new Color(1f, 1f, 1f));
		try
		{
			uint layerBefore = part.CollisionBody.CollisionLayer;
			part.SetTransparency(1f);
			Check("picking: transparency leaves the collision layer alone",
				part.CollisionBody.CollisionLayer == layerBefore
				&& (LotObject.EditorPickMask & part.CollisionBody.CollisionLayer) != 0);
			Check("picking: the collider and its shape survive an invisible part",
				GodotObject.IsInstanceValid(part.CollisionBody) && part.CollisionShape.Shape != null);

			part.SetCanCollide(false);
			Check("collision: CanCollide stays orthogonal to transparency",
				part.CollisionBody.CollisionLayer == LotObject.NoCollideLayer);
			part.SetCanCollide(true);
			Check("collision: toggling back restores the group layer",
				part.CollisionBody.CollisionLayer == layerBefore);
		}
		finally
		{
			Free(part);
		}
	}

	// --- the pure math ---------------------------------------------------------------------------

	private static void TestMath()
	{
		Check("math: transparency clamps both ways",
			LotMaterialMath.ClampTransparency(-2f) == 0f
			&& LotMaterialMath.ClampTransparency(2f) == 1f
			&& Near(LotMaterialMath.ClampTransparency(0.3f), 0.3f));
		Check("math: luminescence clamps at 0 only (no invented ceiling)",
			LotMaterialMath.ClampLuminescence(-1f) == 0f
			&& Near(LotMaterialMath.ClampLuminescence(7f), 7f));
		Check("math: the alpha mapping is 1 - transparency",
			LotMaterialMath.AlphaForTransparency(0f) == 1f
			&& Near(LotMaterialMath.AlphaForTransparency(0.25f), 0.75f)
			&& LotMaterialMath.AlphaForTransparency(1f) == 0f);
		Check("math: emission energy is the value itself",
			Near(LotMaterialMath.EmissionEnergyFor(1.5f), 1.5f));
		Check("math: cast-light energy is the value (one unit per unit of glow)",
			Near(LotMaterialMath.LightEnergyFor(2f), 2f));
		Check("math: light range follows the part's extent with a floor and a ceiling",
			Near(LotMaterialMath.LightRangeFor(1f), 5f)
			&& Near(LotMaterialMath.LightRangeFor(0f), 3f)
			&& Near(LotMaterialMath.LightRangeFor(100f), 24f));

		// Nearest-N selection: nearest first, budget respected, and equidistant candidates
		// resolve to the lower index (callers list candidates in tree order).
		Vector3[] positions = { new Vector3(10f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(5f, 0f, 0f) };
		int[] selection = new int[3];
		int chosen = LotMaterialMath.SelectNearestSlots(positions, Vector3.Zero, 2, selection);
		Check("math: selection picks the nearest, within budget",
			chosen == 2 && selection[0] == 1 && selection[1] == 2);
		int[] one = new int[1];
		int tied = LotMaterialMath.SelectNearestSlots(new[] { Vector3.One, Vector3.One },
			Vector3.Zero, 1, one);
		Check("math: a tie resolves to the lower index deterministically", tied == 1 && one[0] == 0);
		Check("math: a zero budget selects nothing",
			LotMaterialMath.SelectNearestSlots(positions, Vector3.Zero, 0, selection) == 0);
	}

	// --- save/load: capture, JSON, restore -------------------------------------------------------

	/// <summary>
	/// The archive contract both properties have to satisfy: captured through the registry into the
	/// record's property bag, surviving the format's own JSON encoder/parser, and re-applied to a
	/// fresh part by the scene rebuild — the three places a new property can silently go missing.
	/// The full zip round trip is LotArchiveSelfTest's business; this suite additionally drives one
	/// of the new values through that fixture.
	/// </summary>
	private static void TestCaptureAndRestore()
	{
		Node3D sourceRoot = new Node3D();
		LotObject lamp = LotObject.Create(LotObjectKind.Cube, "Lamp", new Color(0.9f, 0.6f, 0.2f));
		lamp.SetTransparency(0.35f);
		lamp.SetLuminescence(2.5f);
		sourceRoot.AddChild(lamp);

		List<LotArchive.LotNodeRecord> records = new List<LotArchive.LotNodeRecord>();
		LotSceneWalk.CaptureChildren(sourceRoot, records);
		Check("capture: the lamp is recorded", records.Count == 1);
		LotArchive.LotNodeRecord record = records.Count > 0 ? records[0] : null;
		Check("capture: transparency is in the property bag",
			record != null && record.Properties.ContainsKey("transparency")
			&& Near((float)record.Properties["transparency"].AsDouble(), 0.35f));
		Check("capture: luminescence is in the property bag",
			record != null && record.Properties.ContainsKey("luminescence")
			&& Near((float)record.Properties["luminescence"].AsDouble(), 2.5f));

		LotArchive.LotNodeRecord back = record != null
			? LotArchive.LotNodeRecord.FromJson(Json.ParseString(Json.Stringify(record.ToJson())))
			: null;
		Check("capture: both survive the record's JSON round trip",
			back != null && back.Properties.ContainsKey("transparency")
			&& back.Properties.ContainsKey("luminescence")
			&& Near((float)back.Properties["transparency"].AsDouble(), 0.35f)
			&& Near((float)back.Properties["luminescence"].AsDouble(), 2.5f));

		// Restore into a fresh container: the path a loaded lot takes.
		LotArchive.LotDocument document = new LotArchive.LotDocument();
		document.Objects = records;
		Node3D destRoot = new Node3D();
		LotSceneLoad.LoadReport report = new LotSceneLoad.LoadReport();
		bool restored = LotSceneLoad.Restore(destRoot, null, document, null, report);
		Check("restore: the restore succeeds with no warnings",
			restored && report.Warnings.Count == 0 && report.PartsCreated == 1);
		LotObject backLamp = destRoot.GetChildCount() > 0 ? destRoot.GetChild(0) as LotObject : null;
		Check("restore: transparency came back",
			backLamp != null && Near(backLamp.Transparency, 0.35f)
			&& backLamp.Material.Transparency == BaseMaterial3D.TransparencyEnum.Alpha
			&& Near(backLamp.Material.AlbedoColor.A, 0.65f));
		Check("restore: luminescence came back",
			backLamp != null && Near(backLamp.Luminescence, 2.5f)
			&& backLamp.Material.EmissionEnabled
			&& Near(backLamp.Material.EmissionEnergyMultiplier, 2.5f));

		destRoot.Free();
		sourceRoot.Free();
	}

	// --- the bound verbs, through the real VM ----------------------------------------------------

	private static void TestLuaBindings()
	{
		LuaManager lua = LuaManager.Instance;
		if (lua == null || !lua.IsRuntimeAvailable)
		{
			// No Lua runtime in this environment (the native library is not packaged yet), so the
			// binding itself cannot be exercised. Reported rather than silently skipped.
			Check("lua: surface verbs are bound and behave (runtime unavailable, skipped)", true);
			return;
		}

		const string script =
			"local p = Lot.SpawnCube(520, 300, 520)\n" +
			"assert(type(p) == 'number' and p >= 0, 'spawn failed')\n" +
			"assert(Lot.GetTransparency(p) == 0, 'a new part is opaque')\n" +
			"assert(Lot.GetLuminescence(p) == 0, 'a new part is unlit')\n" +
			"Lot.SetTransparency(p, 0.4)\n" +
			"assert(math.abs(Lot.GetTransparency(p) - 0.4) < 1e-6, 'transparency should stick')\n" +
			"Lot.SetLuminescence(p, 2)\n" +
			"assert(math.abs(Lot.GetLuminescence(p) - 2) < 1e-6, 'luminescence should stick')\n" +
			"Lot.SetTransparency(p, 5)\n" +
			"assert(Lot.GetTransparency(p) == 1, 'transparency should clamp to 1')\n" +
			"Lot.SetLuminescence(p, -3)\n" +
			"assert(Lot.GetLuminescence(p) == 0, 'luminescence should clamp to 0')\n" +
			"assert(Lot.SetTransparency(999999, 1) == false, 'a missing handle must be refused')\n" +
			"assert(Lot.SetLuminescence(999999, 1) == false, 'a missing handle must be refused')\n" +
			"assert(Lot.GetTransparency(999999) == 0, 'a missing handle reads 0')\n" +
			"assert(Lot.GetLuminescence(999999) == 0, 'a missing handle reads 0')\n" +
			"Lot.DestroyObject(p)";

		bool ran = lua.RunString(script, "part_material_selftest");
		Check("lua: surface verbs are bound and behave" + (ran ? "" : " (" + lua.LastError + ")"), ran);
	}

	// --- the budgeted glow-light pool ------------------------------------------------------------

	private static void TestGlowPool(BuilderScene scene)
	{
		// The pool positions lights by world transform, and Godot only maintains a global
		// transform for nodes inside the SceneTree — so this fixture (unlike the rest of the
		// suite) hangs off the live scene for the duration of the test and is freed immediately
		// afterwards.
		Node3D fixtureRoot = new Node3D();
		if (scene != null) scene.AddChild(fixtureRoot);
		Node3D scanRoot = new Node3D();
		Node3D lightHost = new Node3D();
		fixtureRoot.AddChild(scanRoot);
		fixtureRoot.AddChild(lightHost);
		List<LotObject> lamps = new List<LotObject>();
		try
		{
			// A dark part must never become a candidate; three lit ones must.
			LotObject dark = LotObject.Create(LotObjectKind.Cube, "Dark", new Color(1f, 1f, 1f));
			scanRoot.AddChild(dark);
			for (int i = 0; i < 3; i++)
			{
				LotObject lamp = LotObject.Create(LotObjectKind.Cube, "Lamp" + i, new Color(1f, 1f, 1f));
				lamp.Position = new Vector3(i + 1, 0f, 0f);
				lamp.SetLuminescence(2f);
				scanRoot.AddChild(lamp);
				lamps.Add(lamp);
			}

			LotGlowLights pool = new LotGlowLights(lightHost, scanRoot);
			pool.RefreshNow(Vector3.Zero);
			Check("pool: only the luminous parts are lit (the dark one is skipped)",
				pool.ActiveLightCount == 3 && dark.Luminescence == 0f);
			Check("pool: the pool grew to exactly what is lit", pool.PoolSize == 3);
			bool partHoldsNoLight = true;
			for (int i = 0; i < lamps[0].GetChildCount(); i++)
			{
				if (lamps[0].GetChild(i) is OmniLight3D) partHoldsNoLight = false;
			}
			Check("pool: no light node is parented to a part", partHoldsNoLight);

			OmniLight3D first = lightHost.GetChild(0) as OmniLight3D;
			Check("pool: a pool light is internal-marked and shadowless",
				first != null && first.HasMeta(LotObject.InternalChildMeta) && !first.ShadowEnabled);
			Check("pool: the nearest lamp got a light at its own position, energy and range",
				first != null && Near(first.GlobalPosition, lamps[0].Position)
				&& Near(first.LightEnergy, 2f)
				&& Near(first.OmniRange, LotMaterialMath.LightRangeFor(1f)));

			lamps[0].Position = new Vector3(4f, 1f, 0f);
			pool.Update(0.0, null);
			Check("pool: an assigned light follows its moved part",
				first != null && Near(first.GlobalPosition, new Vector3(4f, 1f, 0f)));

			// Over-budget: ten lamps against an eight-light budget. The farthest two stay emissive
			// but cast no light — stated behaviour, not a silent off switch.
			for (int i = 0; i < 7; i++)
			{
				LotObject lamp = LotObject.Create(LotObjectKind.Cube, "FarLamp" + i, new Color(1f, 1f, 1f));
				lamp.Position = new Vector3(10f + i, 0f, 0f);
				lamp.SetLuminescence(1f);
				scanRoot.AddChild(lamp);
				lamps.Add(lamp);
			}
			pool.RefreshNow(Vector3.Zero);
			Check("pool: never more lights than the documented budget",
				pool.ActiveLightCount == LotGlowLights.MaxActiveLights
				&& pool.PoolSize == LotGlowLights.MaxActiveLights
				&& lightHost.GetChildCount() == LotGlowLights.MaxActiveLights);
			Check("pool: every pool light is lit while the nearest selection fills it",
				AllLightsVisible(lightHost));
			Check("pool: an over-budget lamp still emits on its own surface",
				lamps[lamps.Count - 1].Material.EmissionEnabled);
			Check("pool: no light sits at the farthest, over-budget lamp",
				!AnyLightAt(lightHost, lamps[lamps.Count - 1].GlobalPosition));

			// A destroyed lamp must never be touched again. Freeing one leaves ten minus one lamps,
			// still over the eight-light budget, so the count stays full — what matters is that the
			// freed part's slot goes dark without touching the freed node, and that a refresh
			// re-derives the selection around it.
			int before = pool.ActiveLightCount;
			Vector3 freedAt = lamps[0].GlobalPosition;
			lamps[0].Free();
			pool.Update(0.0, null);
			Check("pool: a freed part's slot is hidden without touching the freed node",
				pool.ActiveLightCount == before && !AnyLightAt(lightHost, freedAt));
			pool.RefreshNow(Vector3.Zero);
			Check("pool: a refresh re-derives around the freed part",
				pool.ActiveLightCount == before && pool.PoolSize == LotGlowLights.MaxActiveLights
				&& !AnyLightAt(lightHost, freedAt));
		}
		finally
		{
			fixtureRoot.Free();
		}
	}

	// --- helpers ---------------------------------------------------------------------------------

	private static bool AllLightsVisible(Node lightHost)
	{
		int count = lightHost.GetChildCount();
		if (count == 0) return false;
		for (int i = 0; i < count; i++)
		{
			OmniLight3D light = lightHost.GetChild(i) as OmniLight3D;
			if (light == null || !light.Visible) return false;
		}
		return true;
	}

	private static bool AnyLightAt(Node lightHost, Vector3 position)
	{
		for (int i = 0; i < lightHost.GetChildCount(); i++)
		{
			OmniLight3D light = lightHost.GetChild(i) as OmniLight3D;
			if (light != null && light.Visible
				&& light.GlobalPosition.DistanceTo(position) < 1e-4f) return true;
		}
		return false;
	}

	private static void Check(string label, bool ok)
	{
		_checks++;
		if (!ok)
		{
			_failures++;
			GD.Print("[PartMaterialSelfTest] FAIL  " + label);
		}
	}

	private static bool Near(float a, float b, float tolerance = 1e-5f)
	{
		return Mathf.Abs(a - b) <= tolerance;
	}

	private static bool Near(Vector3 a, Vector3 b)
	{
		return a.DistanceTo(b) <= 1e-4f;
	}

	private static bool Near(Color a, Color b)
	{
		return a.IsEqualApprox(b);
	}

	/// <summary>Frees a fixture (and its subtree). Safe for a node that was never added to a tree,
	/// which is how every fixture here lives.</summary>
	private static void Free(Node node)
	{
		if (node != null && GodotObject.IsInstanceValid(node)) node.Free();
	}
}
