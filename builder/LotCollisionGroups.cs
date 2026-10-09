using Godot;
using Godot.Collections;

/// <summary>
/// Named collision groups (roadmap milestone 3.5) — the single source of truth for what a part's
/// Godot physics-layer bit means and which groups are allowed to interact.
///
/// Before this, every part was on one hardcoded layer (<see cref="LotObject.PartLayer"/>) with a
/// zero mask, so a creator had no say in what collided with what. Here, each assignable group owns
/// one Godot layer bit (index i -> bit 1&lt;&lt;i, matching how Godot numbers layers 1..32) and a
/// symmetric matrix says which pairs of groups interact.
///
/// Session state, like <see cref="SecretContent.Revealed"/> and <see cref="LotPropertyRegistry"/>:
/// one table per client. It is serialized into the `.lot` archive's `lot.json` (§4.1) rather than
/// held on a node, so it is the lot's, not a part's — `LotObject` reads it to derive each body's
/// mask, and <see cref="BuilderScene.RefreshCollision"/> re-derives them after an edit.
///
/// Layering (clinerules 1.1): this is a Godot-infrastructure-facing table. It exposes plain names,
/// bits and booleans; nothing here reaches into Lua or the plugin system.
/// </summary>
public static class LotCollisionGroups
{
	/// <summary>How many creator-assignable groups exist. Index i owns layer bit (1 &lt;&lt; i).</summary>
	public const int Count = 8;

	/// <summary>
	/// The creator-assignable groups, in bit order. Index 0 is "Default", so an existing part that
	/// never chose a group keeps today's <see cref="LotObject.PartLayer"/> exactly. The rest are a
	/// fixed vocabulary the matrix decides the meaning of — no gameplay is hardcoded per name.
	/// Do not mutate: this array is handed straight to the Inspector's combo.
	/// </summary>
	public static readonly string[] Names =
	{
		"Default", "Terrain", "Character", "Prop", "Decoration", "Trigger", "Projectile", "Effect"
	};

	/// <summary>The implicit group of a part that never chose one (index 0).</summary>
	public const string DefaultGroup = "Default";
	public const int DefaultIndex = 0;

	/// <summary>
	/// The group the player character conceptually belongs to. The invisible player body is not
	/// itself a collision target (layer 0, so no editor raycast can pick it), so it does not "wear"
	/// this group's bit — it wears this group's <b>row</b> of the matrix as its mask, which is what
	/// makes the character's interactions creator-controlled like any part's.
	/// </summary>
	public const string CharacterGroup = "Character";

	/// <summary>Index of <see cref="CharacterGroup"/> in <see cref="Names"/>, resolved once.</summary>
	public static readonly int CharacterIndex = IndexOf(CharacterGroup);

	/// <summary>
	/// Layer bit a part parks on while <see cref="LotObject.CanCollide"/> is false. Outside every
	/// group mask (and only in the editor pick mask), exactly as before this milestone — the shape
	/// stays live and pickable, but nothing solid sees it. Never creator-assignable.
	/// </summary>
	public const int NoCollideIndex = 8;
	public const uint NoCollideBit = 1u << NoCollideIndex; // 256

	/// <summary>Every assignable group bit OR'd together (bits 1..8). The "bump against any part"
	/// mask the editor drag mover and the drag-plane surface query use, so dragging works across
	/// every group.</summary>
	public const uint AllBits = 0xFFu;

	/// <summary>Bit for <see cref="DefaultGroup"/> — kept a const so <see cref="LotObject.PartLayer"/>
	/// can alias it without drift.</summary>
	public const uint DefaultBit = 1u << DefaultIndex;

	/// <summary>What the editor's picking raycasts test: every part whatever its group, plus the
	/// no-collide layer, so a part stays selectable/draggable (milestone 2.3).</summary>
	public const uint PickMask = AllBits | NoCollideBit; // 0x1FF

	private static readonly bool[,] _collides = new bool[Count, Count];

	static LotCollisionGroups()
	{
		ResetAllCollisions();

		// Keep the const masks honest against the name list. A drift here would silently mis-mask
		// parts, which is exactly the kind of bug that is invisible until physics misbehaves.
		uint all = 0u;
		for (int i = 0; i < Count; i++) all |= (1u << i);
		if (all != AllBits || Names.Length != Count)
			GD.PushError("[LotCollisionGroups] group table is out of sync with its bit masks");
	}

	/// <summary>Index of a group name (case-insensitive), or -1 when unknown.</summary>
	public static int IndexOf(string name)
	{
		if (string.IsNullOrEmpty(name)) return -1;
		for (int i = 0; i < Names.Length; i++)
		{
			if (string.Equals(Names[i], name, System.StringComparison.OrdinalIgnoreCase)) return i;
		}
		return -1;
	}

	/// <summary>True when <paramref name="name"/> is an assignable group.</summary>
	public static bool IsKnown(string name)
	{
		return IndexOf(name) >= 0;
	}

	/// <summary>The canonical name for a possibly-unknown/oddly-cased input, falling back to
	/// <see cref="DefaultGroup"/> so a bad name can never leave a part on bit 0.</summary>
	public static string Normalize(string name)
	{
		int index = IndexOf(name);
		return index >= 0 ? Names[index] : DefaultGroup;
	}

	/// <summary>Layer bit for a group index, or 0 for an out-of-range index.</summary>
	public static uint BitForIndex(int index)
	{
		return index >= 0 && index < Count ? (1u << index) : 0u;
	}

	/// <summary>Layer bit for a group name (0 for an unknown name).</summary>
	public static uint BitForName(string name)
	{
		return BitForIndex(IndexOf(name));
	}

	// --- The interaction matrix --------------------------------------------------------------
	// Symmetric: GetCollides(a, b) == GetCollides(b, a). All-true by default, which reproduces the
	// pre-3.5 behaviour (every part collidable with every other) so an untouched lot is unchanged.

	/// <summary>Makes every pair of groups interact (the default).</summary>
	public static void ResetAllCollisions()
	{
		for (int i = 0; i < Count; i++)
		{
			for (int j = 0; j < Count; j++) _collides[i, j] = true;
		}
	}

	/// <summary>Whether two group indices interact. False for an out-of-range index.</summary>
	public static bool GetCollides(int a, int b)
	{
		if (a < 0 || a >= Count || b < 0 || b >= Count) return false;
		return _collides[a, b];
	}

	/// <summary>Sets whether two group indices interact, keeping the matrix symmetric. Returns false
	/// for an out-of-range index (so an unknown name can report the failure rather than no-op).</summary>
	public static bool SetCollides(int a, int b, bool collides)
	{
		if (a < 0 || a >= Count || b < 0 || b >= Count) return false;
		_collides[a, b] = collides;
		_collides[b, a] = collides;
		return true;
	}

	/// <summary>Name-based pair write for the bound Lua API / settings UI. False when either name is
	/// unknown, so the caller can report it instead of silently doing nothing.</summary>
	public static bool SetCollidesByName(string a, string b, bool collides)
	{
		int ia = IndexOf(a);
		int ib = IndexOf(b);
		if (ia < 0 || ib < 0) return false;
		return SetCollides(ia, ib, collides);
	}

	/// <summary>Name-based pair read. False when either name is unknown.</summary>
	public static bool GetCollidesByName(string a, string b)
	{
		int ia = IndexOf(a);
		int ib = IndexOf(b);
		if (ia < 0 || ib < 0) return false;
		return GetCollides(ia, ib);
	}

	/// <summary>The collision mask a part in a group should wear: every group bit it interacts with.
	/// This is the one place a part's mask is derived from the matrix, so the matrix is genuinely the
	/// single source of truth for interactions.</summary>
	public static uint MaskForIndex(int index)
	{
		if (index < 0 || index >= Count) return MaskForIndex(DefaultIndex);
		uint mask = 0u;
		for (int j = 0; j < Count; j++)
		{
			if (_collides[index, j]) mask |= (1u << j);
		}
		return mask;
	}

	/// <summary>Collision mask for a group name (an unknown name is treated as
	/// <see cref="DefaultGroup"/>).</summary>
	public static uint MaskForName(string name)
	{
		return MaskForIndex(IndexOf(name) >= 0 ? IndexOf(name) : DefaultIndex);
	}

	// --- Persistence (§4.1 lot.json) ---------------------------------------------------------
	// One integer per group: that group's row as a bitmask. Symmetric, so the array is redundant —
	// but it is trivial to read in a diff and needs no version gate of its own. An absent or
	// wrong-shaped value means "the all-collide default".

	/// <summary>The matrix as an array of <see cref="Count"/> row bitmasks, for the `.lot` writer.</summary>
	public static Array ToJson()
	{
		Array rows = new Array();
		for (int i = 0; i < Count; i++)
		{
			int row = 0;
			for (int j = 0; j < Count; j++)
			{
				if (_collides[i, j]) row |= (1 << j);
			}
			rows.Add(row);
		}
		return rows;
	}

	/// <summary>
	/// Restores the matrix from a saved array, resetting to the all-collide default first so a lot
	/// that carries no block (or one this build cannot read) never inherits the previous lot's
	/// interactions. Symmetry is re-imposed by OR-ing each pair, so a hand-edited asymmetric file
	/// still yields a valid matrix.
	/// </summary>
	public static void FromJson(Variant value)
	{
		ResetAllCollisions();
		if (value.VariantType != Variant.Type.Array) return;

		Array rows = value.AsGodotArray();
		if (rows.Count != Count) return;

		for (int i = 0; i < Count; i++)
		{
			for (int j = 0; j < Count; j++)
			{
				bool a = (rows[i].AsInt32() & (1 << j)) != 0;
				bool b = (rows[j].AsInt32() & (1 << i)) != 0;
				_collides[i, j] = a || b;
			}
		}
	}
}
