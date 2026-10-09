using System.Collections.Generic;
using Godot;

/// <summary>
/// Verification for the milestone 2.3 property system: the declaration table
/// (<see cref="LotPropertyRegistry"/>), the part properties on <see cref="LotObject"/> and their
/// Lua-facing verbs on <see cref="LotLuaApi"/>.
///
/// Follows the project's self-test convention: no test framework exists, so this runs from
/// BuilderScene._Ready() under #if DEBUG and prints pass/fail lines. It deliberately does not touch
/// <see cref="Builder"/>, because Godot readies children before parents and Builder.Instance is
/// therefore still null at that point; the descriptor accessors it exercises are exactly what
/// <see cref="LotPropertyService"/> wraps.
/// </summary>
public static class LotPropertySelfTest
{
	private static int _failures;
	private static int _checks;

	public static int Run()
	{
		_failures = 0;
		_checks = 0;

		TestRegistry();
		TestPartDefaults();
		TestCanCollideLayers();
		TestTextureLifecycle();
		TestLuaBindings();

		GD.Print("[LotPropertySelfTest] " + _checks + " checks, " + _failures + " failure(s).");
		return _failures;
	}

	private static void TestRegistry()
	{
		Check("registry: the anchor property is declared", LotPropertyRegistry.Find("anchored") != null);
		Check("registry: the collision property is declared", LotPropertyRegistry.Find("canCollide") != null);
		Check("registry: the texture property is declared", LotPropertyRegistry.Find("texture") != null);
		Check("registry: an unknown id resolves to null", LotPropertyRegistry.Find("nope") == null);
		Check("registry: a null id resolves to null", LotPropertyRegistry.Find(null) == null);

		// A duplicate declaration must not shadow the core one (first wins).
		LotPropertyRegistry.Register(new LotPropertyDescriptor { Id = "anchored", DisplayName = "Bogus" });
		Check("registry: a duplicate id does not replace the core property",
			LotPropertyRegistry.Find("anchored").DisplayName == "Anchored");

		// Every declared property must have the accessors its kind promises, or the Inspector would
		// draw a dead widget.
		bool accessorsComplete = true;
		IReadOnlyList<LotPropertyDescriptor> all = LotPropertyRegistry.All;
		for (int i = 0; i < all.Count; i++)
		{
			LotPropertyDescriptor descriptor = all[i];
			if (descriptor.Kind == LotPropertyKind.Bool && (descriptor.GetBool == null || descriptor.SetBool == null)) accessorsComplete = false;
			if (descriptor.Kind == LotPropertyKind.Choice && (descriptor.GetChoice == null || descriptor.SetChoice == null || descriptor.ListOptions == null)) accessorsComplete = false;
			if (descriptor.Kind == LotPropertyKind.Float && (descriptor.GetFloat == null || descriptor.SetFloat == null)) accessorsComplete = false;
			if (descriptor.Kind == LotPropertyKind.Text && (descriptor.GetText == null || descriptor.SetText == null)) accessorsComplete = false;
			if (descriptor.AppliesTo == null) accessorsComplete = false;
		}
		Check("registry: every declared property has complete accessors", accessorsComplete);

		// CollectFor is the Inspector's path: it must pick only the properties that apply.
		List<LotPropertyDescriptor> buffer = new List<LotPropertyDescriptor>();
		LotPropertyRegistry.CollectFor(null, buffer);
		Check("registry: a null node collects nothing", buffer.Count == 0);

		LotUIElement uiElement = LotUIElement.Create(LotUIKind.Frame, Vector2.Zero, new Vector2(10f, 10f), "");
		LotPropertyRegistry.CollectFor(uiElement, buffer);
		Check("registry: a UI element collects nothing yet (no UI properties declared)", buffer.Count == 0);
		Free(uiElement);
	}

	/// <summary>Writes a Bool property through its descriptor and reads it back.</summary>
	private static bool SetBoolAndRead(LotObject part, string propertyId, bool value)
	{
		LotPropertyDescriptor descriptor = LotPropertyRegistry.Find(propertyId);
		if (descriptor == null || descriptor.SetBool == null || descriptor.GetBool == null) return false;
		descriptor.SetBool(part, value);
		return descriptor.GetBool(part) == value;
	}

	/// <summary>
	/// Drives the real bound Lua API: the property verbs must be reachable from a script and must
	/// actually change the lot. This is the end-to-end half the C#-level checks cannot cover, since
	/// the binding is produced by reflection over LotLuaApi's public methods.
	///
	/// The scratch chunk runs against the live VM. The probe part is spawned far from the origin on
	/// purpose: the DEBUG physics smoke test asserts exact contact geometry in the origin region, and
	/// a part parked there would flip its assertions.
	/// </summary>
	private static void TestLuaBindings()
	{
		LuaManager lua = LuaManager.Instance;
		if (lua == null || !lua.IsRuntimeAvailable)
		{
			// No Lua runtime in this environment (the native library is not packaged yet), so the
			// binding itself cannot be exercised. Reported rather than silently skipped.
			Check("lua: part property verbs are bound and behave (runtime unavailable, skipped)", true);
			return;
		}

		const string script =
			"local h = Lot.SpawnCube(40, 8, 40)\n" +
			"assert(type(h) == 'number' and h >= 0, 'spawn failed')\n" +
			"assert(Lot.IsAnchored(h) == true, 'a new part should be anchored')\n" +
			"Lot.SetAnchored(h, false)\n" +
			"assert(Lot.IsAnchored(h) == false, 'SetAnchored did not stick')\n" +
			"assert(Lot.GetCanCollide(h) == true, 'a new part should collide')\n" +
			"Lot.SetCanCollide(h, false)\n" +
			"assert(Lot.GetCanCollide(h) == false, 'SetCanCollide did not stick')\n" +
			"assert(Lot.GetTexture(h) == '', 'a new part should have no texture')\n" +
			"assert(Lot.SetTexture(h, 'no-such-asset') == false, 'an unknown asset must be refused')\n" +
			"Lot.SetAnchored(h, true)\n" +
			"Lot.SetPosition(h, 1, 2, 3)\n" +
			"assert(Lot.GetPositionX(h) == 1, 'position writes must work on an anchored part')\n" +
			"Lot.DestroyObject(h)";

		bool ran = lua.RunString(script, "lot_property_selftest");
		Check("lua: part property verbs are bound and behave" +
			(ran ? "" : " (" + lua.LastError + ")"), ran);
	}

	private static void TestPartDefaults()
	{
		LotObject part = LotObject.Create(LotObjectKind.Cube, "PropTest", new Color(1f, 1f, 1f));
		try
		{
			Check("part: anchored by default", part.Anchored);
			Check("part: collidable by default", part.CanCollide);
			Check("part: no texture by default", part.TextureId.Length == 0);
			Check("part: the default collision layer is the part layer", part.CollisionBody.CollisionLayer == LotObject.PartLayer);

			List<LotPropertyDescriptor> buffer = new List<LotPropertyDescriptor>();
			LotPropertyRegistry.CollectFor(part, buffer);
			// anchored / canCollide / texture (milestone 2.3) plus collisionGroup (milestone 3.5).
			Check("part: all four core properties apply", buffer.Count == 4);

			// The Inspector reads through the descriptor and writes through its SetBool.
			LotPropertyDescriptor anchored = LotPropertyRegistry.Find("anchored");
			Check("part: the anchor descriptor reads the part's state", anchored.GetBool(part));
			anchored.SetBool(part, false);
			Check("part: the anchor descriptor writes the part's state", !part.Anchored);
			Check("part: setting anchored back works", SetBoolAndRead(part, "anchored", true));
		}
		finally
		{
			Free(part);
		}
	}

	private static void TestCanCollideLayers()
	{
		LotObject part = LotObject.Create(LotObjectKind.Cube, "LayerTest", new Color(1f, 1f, 1f));
		try
		{
			Check("collision: the editor mask covers both layers",
				(LotObject.EditorPickMask & LotObject.PartLayer) != 0 && (LotObject.EditorPickMask & LotObject.NoCollideLayer) != 0);
			Check("collision: the no-collide layer is not the gameplay layer",
				(LotObject.NoCollideLayer & LotObject.PartLayer) == 0);

			part.SetCanCollide(false);
			Check("collision: off moves the body to the no-collide layer", part.CollisionBody.CollisionLayer == LotObject.NoCollideLayer);
			Check("collision: the shape stays enabled so the part is still pickable", !part.CollisionShape.Disabled);
			Check("collision: off is reported by the property", !part.CanCollide);

			part.SetCanCollide(true);
			Check("collision: on restores the part layer", part.CollisionBody.CollisionLayer == LotObject.PartLayer);
		}
		finally
		{
			Free(part);
		}
	}

	private static void TestTextureLifecycle()
	{
		string path = TestImages.WritePng("user://propertyselftest_texture.png", new Color(0.9f, 0.3f, 0.1f, 1f));
		if (path.Length == 0)
		{
			Check("texture: test image could be written", false);
			return;
		}

		LotObject part = LotObject.Create(LotObjectKind.Cube, "TextureTest", new Color(1f, 1f, 1f));
		try
		{
			string assetId = LotTextureCache.ImportFromFile(path, out string _);
			Check("texture: the asset imported", assetId.Length > 0);

			Check("texture: applying a known asset succeeds", part.SetTexture(assetId));
			Check("texture: the part records the asset id", part.TextureId == assetId);
			Check("texture: the material now has the image", part.Material.AlbedoTexture != null);

			// The part took its own reference, so handing the import reference back leaves the asset
			// alive on the part's account (Release reports false while it is still held).
			Check("texture: releasing the import reference leaves the part's hold", !LotTextureCache.Release(assetId));
			Check("texture: the asset is still held by the part", LotTextureCache.Contains(assetId));

			Check("texture: an unknown asset id is refused", !part.SetTexture("tex-does-not-exist"));
			Check("texture: a refused id leaves the part unchanged", part.TextureId == assetId);

			// Clearing must drop the part's reference and free the image.
			Check("texture: clearing succeeds", part.SetTexture(""));
			Check("texture: clearing forgets the id", part.TextureId.Length == 0);
			Check("texture: clearing removes the material's image", part.Material.AlbedoTexture == null);
			Check("texture: clearing frees the asset", !LotTextureCache.Contains(assetId));

			// ReleaseTexture is the destroy-path hook: it must free the asset, and be harmless when
			// there is nothing left to release.
			string again = LotTextureCache.ImportFromFile(path, out string _);
			part.SetTexture(again);
			LotTextureCache.Release(again);
			part.ReleaseTexture();
			Check("texture: the destroy-path release forgets the id", part.TextureId.Length == 0);
			Check("texture: the destroy-path release frees the asset", !LotTextureCache.Contains(again));

			part.ReleaseTexture();
			Check("texture: releasing twice is harmless", part.TextureId.Length == 0);
		}
		finally
		{
			Free(part);
		}
	}

	private static void Free(Node node)
	{
		if (node != null && GodotObject.IsInstanceValid(node)) node.Free();
	}

	private static void Check(string name, bool condition)
	{
		_checks++;
		if (condition)
		{
			GD.Print("[LotPropertySelfTest] PASS  " + name);
		}
		else
		{
			_failures++;
			GD.PrintErr("[LotPropertySelfTest] FAIL  " + name);
		}
	}
}
