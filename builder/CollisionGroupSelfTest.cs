using Godot;
using Godot.Collections;

/// <summary>
/// Verification for milestone 3.5 (named collision groups): the group table
/// (<see cref="LotCollisionGroups"/>), the interaction matrix, how a part derives its layer/mask
/// from them (<see cref="LotObject"/>), the `.lot` persistence shape, and the bound Lua verbs.
///
/// Follows the project's self-test convention: no framework, runs from <c>BuilderScene._Ready</c>
/// under <c>#if DEBUG</c> and prints pass/fail lines. The matrix is process-wide state, so the
/// suite resets it at the start and end to avoid leaking a modified matrix into another suite.
/// </summary>
public static class CollisionGroupSelfTest
{
	private static int _failures;
	private static int _checks;

	public static int Run()
	{
		_failures = 0;
		_checks = 0;

		LotCollisionGroups.ResetAllCollisions();

		TestTable();
		TestMatrix();
		TestPartDerivation();
		TestPersistence();
		TestLuaBindings();

		// Leave the shared matrix as the other suites expect it (all-collide).
		LotCollisionGroups.ResetAllCollisions();

		GD.Print("[CollisionGroupSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
		return _failures;
	}

	/// <summary>The static table: names, bits and the const masks that must stay in agreement.</summary>
	private static void TestTable()
	{
		int n = LotCollisionGroups.Count;
		Check("table: the name list matches the declared count", LotCollisionGroups.Names.Length == n);

		uint allBits = 0u;
		for (int i = 0; i < n; i++) allBits |= LotCollisionGroups.BitForIndex(i);
		Check("table: AllBits is every assignable group bit", allBits == LotCollisionGroups.AllBits);

		Check("table: Default is index 0", LotCollisionGroups.DefaultIndex == 0);
		Check("table: the Default bit aliases the part layer",
			LotCollisionGroups.DefaultBit == LotObject.PartLayer);
		Check("table: the no-collide bit aliases the no-collide layer",
			LotCollisionGroups.NoCollideBit == LotObject.NoCollideLayer);
		Check("table: the pick mask is every group plus no-collide",
			LotCollisionGroups.PickMask == (LotCollisionGroups.AllBits | LotCollisionGroups.NoCollideBit));
		Check("table: the editor pick mask aliases the group table",
			LotObject.EditorPickMask == LotCollisionGroups.PickMask);
		Check("table: the no-collide bit is not an assignable group bit",
			(LotCollisionGroups.NoCollideBit & LotCollisionGroups.AllBits) == 0);

		Check("table: CharacterIndex resolves to the Character group",
			LotCollisionGroups.CharacterIndex >= 0 &&
			LotCollisionGroups.Names[LotCollisionGroups.CharacterIndex] == LotCollisionGroups.CharacterGroup);
		Check("table: name lookup is case-insensitive",
			LotCollisionGroups.IndexOf("decoration") == LotCollisionGroups.IndexOf("Decoration"));
		Check("table: an unknown name has no index", LotCollisionGroups.IndexOf("Nope") == -1);
		Check("table: Normalize maps an unknown name to Default",
			LotCollisionGroups.Normalize("Nope") == LotCollisionGroups.DefaultGroup);
		Check("table: Normalize canonicalises case",
			LotCollisionGroups.Normalize("prop") == "Prop");
		Check("table: IsKnown distinguishes real groups",
			LotCollisionGroups.IsKnown("Prop") && !LotCollisionGroups.IsKnown("Nope"));

		bool unique = true;
		for (int i = 0; i < n && unique; i++)
		{
			for (int j = i + 1; j < n; j++)
			{
				if (LotCollisionGroups.Names[i] == LotCollisionGroups.Names[j]) unique = false;
			}
		}
		Check("table: every group name is unique", unique);
	}

	/// <summary>The interaction matrix: default, symmetry, masks and validation.</summary>
	private static void TestMatrix()
	{
		LotCollisionGroups.ResetAllCollisions();
		Check("matrix: every pair collides by default",
			LotCollisionGroups.GetCollides(0, 5) && LotCollisionGroups.GetCollides(5, 0));
		Check("matrix: the default mask is every group",
			LotCollisionGroups.MaskForName(LotCollisionGroups.DefaultGroup) == LotCollisionGroups.AllBits);
		Check("matrix: an out-of-range index does not collide",
			!LotCollisionGroups.GetCollides(-1, 0) && !LotCollisionGroups.GetCollides(0, 999));
		Check("matrix: writing an out-of-range index fails",
			!LotCollisionGroups.SetCollides(0, 999, false));

		Check("matrix: a pair write is reported as a success",
			LotCollisionGroups.SetCollidesByName("Terrain", "Effect", false));
		Check("matrix: a pair change is symmetric",
			!LotCollisionGroups.GetCollidesByName("Terrain", "Effect") &&
			!LotCollisionGroups.GetCollidesByName("Effect", "Terrain"));
		Check("matrix: the row mask reflects the change",
			(LotCollisionGroups.MaskForName("Terrain") & LotCollisionGroups.BitForName("Effect")) == 0);
		Check("matrix: the mirrored row also reflects it",
			(LotCollisionGroups.MaskForName("Effect") & LotCollisionGroups.BitForName("Terrain")) == 0);
		Check("matrix: an untouched pair is unaffected",
			LotCollisionGroups.GetCollidesByName("Terrain", "Prop"));
		Check("matrix: an unknown name is refused",
			!LotCollisionGroups.SetCollidesByName("Nope", "Terrain", false) &&
			!LotCollisionGroups.GetCollidesByName("Nope", "Terrain"));

		LotCollisionGroups.ResetAllCollisions();
		Check("matrix: reset restores every pair",
			LotCollisionGroups.MaskForName("Terrain") == LotCollisionGroups.AllBits);
	}

	/// <summary>How a part derives its layer and mask from its group and CanCollide.</summary>
	private static void TestPartDerivation()
	{
		LotCollisionGroups.ResetAllCollisions();
		LotObject part = LotObject.Create(LotObjectKind.Cube, "GroupTest", new Color(1f, 1f, 1f));
		try
		{
			Check("part: a new part is in the Default group",
				part.CollisionGroup == LotCollisionGroups.DefaultGroup);
			Check("part: a new part sits on the Default bit",
				part.CollisionBody.CollisionLayer == LotCollisionGroups.DefaultBit);
			Check("part: a new part's mask is every group",
				part.CollisionBody.CollisionMask == LotCollisionGroups.AllBits);

			part.SetCollisionGroup("Decoration");
			Check("part: the group is recorded", part.CollisionGroup == "Decoration");
			Check("part: the layer becomes the group's bit",
				part.CollisionBody.CollisionLayer == LotCollisionGroups.BitForName("Decoration"));

			// Decoration stops colliding with Character: the part's mask must lose that bit but keep
			// the groups it still collides with.
			LotCollisionGroups.SetCollidesByName("Decoration", "Character", false);
			part.RefreshCollision();
			uint mask = part.CollisionBody.CollisionMask;
			Check("part: the mask drops a group the matrix now excludes",
				(mask & LotCollisionGroups.BitForName("Character")) == 0);
			Check("part: the mask keeps a group the matrix still includes",
				(mask & LotCollisionGroups.BitForName("Terrain")) != 0);

			// CanCollide false must win over the group, and toggling back must restore the group bit.
			part.SetCanCollide(false);
			Check("part: no-collide parks on the reserved layer whatever the group",
				part.CollisionBody.CollisionLayer == LotObject.NoCollideLayer);
			Check("part: no-collide keeps the group recorded", part.CollisionGroup == "Decoration");
			part.SetCanCollide(true);
			Check("part: toggling collision back restores the group's bit",
				part.CollisionBody.CollisionLayer == LotCollisionGroups.BitForName("Decoration"));

			// An unknown group must fall back to Default rather than a dead layer.
			part.SetCollisionGroup("NotAGroup");
			Check("part: an unknown group falls back to Default",
				part.CollisionGroup == LotCollisionGroups.DefaultGroup);
			Check("part: an unknown group still leaves a valid layer",
				part.CollisionBody.CollisionLayer == LotCollisionGroups.DefaultBit);

			// A part in a non-default group must still be editable.
			Check("part: a non-default group stays pickable",
				(LotObject.EditorPickMask & LotCollisionGroups.BitForName("Decoration")) != 0);
			Check("part: a non-collidable part stays pickable",
				(LotObject.EditorPickMask & LotObject.NoCollideLayer) != 0);
		}
		finally
		{
			LotCollisionGroups.ResetAllCollisions();
			Free(part);
		}
	}

	/// <summary>The `.lot` encoding: round-trip, and the reset-on-unreadable contract.</summary>
	private static void TestPersistence()
	{
		LotCollisionGroups.ResetAllCollisions();
		LotCollisionGroups.SetCollidesByName("Character", "Decoration", false);

		Array encoded = LotCollisionGroups.ToJson();
		Check("persist: the matrix encodes one row per group", encoded.Count == LotCollisionGroups.Count);

		LotCollisionGroups.ResetAllCollisions();
		Check("persist: the reset really cleared the change",
			LotCollisionGroups.GetCollidesByName("Character", "Decoration"));
		LotCollisionGroups.FromJson(encoded);
		Check("persist: a round-trip restores the changed pair",
			!LotCollisionGroups.GetCollidesByName("Character", "Decoration"));
		Check("persist: a round-trip keeps the untouched pairs",
			LotCollisionGroups.GetCollidesByName("Default", "Terrain"));

		// A lot that carries no block (or one this build cannot read) must reset, never inherit.
		LotCollisionGroups.FromJson(default(Variant));
		Check("persist: a missing block resets to all-collide",
			LotCollisionGroups.MaskForName("Character") == LotCollisionGroups.AllBits);
		LotCollisionGroups.FromJson(new Array { 1, 2 });
		Check("persist: a wrong-shaped block resets to all-collide",
			LotCollisionGroups.MaskForName("Character") == LotCollisionGroups.AllBits);

		// A hand-edited asymmetric file must still yield a symmetric matrix (the row pair is OR'd).
		int fullRow = (1 << LotCollisionGroups.Count) - 1;
		Array asymmetric = new Array();
		for (int i = 0; i < LotCollisionGroups.Count; i++) asymmetric.Add(i == 1 ? fullRow & ~1 : fullRow);
		LotCollisionGroups.FromJson(asymmetric);
		Check("persist: an asymmetric file is symmetrised",
			LotCollisionGroups.GetCollides(0, 1) && LotCollisionGroups.GetCollides(1, 0));

		LotCollisionGroups.ResetAllCollisions();
	}

	/// <summary>
	/// Drives the real bound Lua API: the group verbs must be reachable from a script and must
	/// actually change the lot. The scratch chunk runs against the live VM; the probe part spawns far
	/// from the origin so it cannot perturb the DEBUG physics smoke test's origin-region assertions.
	/// </summary>
	private static void TestLuaBindings()
	{
		LuaManager lua = LuaManager.Instance;
		if (lua == null || !lua.IsRuntimeAvailable)
		{
			// No Lua runtime in this environment (the native library is not packaged yet), so the
			// binding cannot be exercised. Reported rather than silently skipped.
			Check("lua: collision group verbs are bound and behave (runtime unavailable, skipped)", true);
			return;
		}

		// The chunk deliberately passes an unknown group once: the binding warns (a WARNING in the
		// log, not an error) and must leave the part's group unchanged, which the assert right after
		// it pins down.
		const string script =
			"local h = Lot.SpawnCube(44, 8, 44)\n" +
			"assert(type(h) == 'number' and h >= 0, 'spawn failed')\n" +
			"assert(Lot.GetCollisionGroup(h) == 'Default', 'a new part should be Default')\n" +
			"Lot.SetCollisionGroup(h, 'Decoration')\n" +
			"assert(Lot.GetCollisionGroup(h) == 'Decoration', 'SetCollisionGroup did not stick')\n" +
			"Lot.SetCollisionGroup(h, 'Nope')\n" +
			"assert(Lot.GetCollisionGroup(h) == 'Decoration', 'an unknown group must be ignored')\n" +
			"assert(Lot.GetGroupsCollidable('Default', 'Decoration') == true, 'groups collide by default')\n" +
			"assert(Lot.SetGroupsCollidable('Default', 'Decoration', false) == true, 'a valid pair should set')\n" +
			"assert(Lot.GetGroupsCollidable('Default', 'Decoration') == false, 'the pair change should stick')\n" +
			"assert(Lot.SetGroupsCollidable('Default', 'Nope', false) == false, 'an unknown group must be refused')\n" +
			"Lot.SetGroupsCollidable('Default', 'Decoration', true)\n" +
			"Lot.DestroyObject(h)";

		bool ran = lua.RunString(script, "collision_group_selftest");
		Check("lua: collision group verbs are bound and behave" +
			(ran ? "" : " (" + lua.LastError + ")"), ran);
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
			GD.Print("[CollisionGroupSelfTest] PASS  " + name);
		}
		else
		{
			_failures++;
			GD.PrintErr("[CollisionGroupSelfTest] FAIL  " + name);
		}
	}
}
