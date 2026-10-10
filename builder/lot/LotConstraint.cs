using System;
using System.Collections.Generic;
using Godot;
using Godot.Collections;

/// <summary>What kind of mechanical link a constraint is (milestone 3.6). v1 ships exactly two.</summary>
public enum LotConstraintKind
{
	/// <summary>Rigid link: the parts keep their relative position and orientation.</summary>
	Weld,
	/// <summary>Single-axis pivot: the parts rotate around one axis relative to each other.</summary>
	Hinge
}

/// <summary>How a hinge is driven. Off is a free hinge; Spin is a velocity motor.</summary>
public enum HingeMotorMode
{
	Off,
	Spin
}

/// <summary>
/// One mechanical link between two parts (milestone 3.6). A plain data record — the Godot joint node
/// that enforces it is a session artifact built from this (<see cref="LotConstraintSession"/>), never
/// the source of truth.
///
/// Endpoints are part HANDLES (the same integer identity the Lua API addresses objects with).
/// <see cref="LotConstraintRecord.WorldHandle"/> as <see cref="B"/> means a hinge pinned to the world
/// (a door in a wall that has no wall part) — the one asymmetry between the two kinds: a weld has no
/// world form.
/// </summary>
public sealed class LotConstraintRecord
{
	/// <summary>Handle value standing for "the world" on a hinge's second side.</summary>
	public const int WorldHandle = -1;

	/// <summary>Session-unique id the bound Lua API addresses the constraint by. Not persisted.</summary>
	public int Id;

	public LotConstraintKind Kind;

	/// <summary>Handle of the first part.</summary>
	public int A;

	/// <summary>Handle of the second part, or <see cref="WorldHandle"/> for a hinge to the world.</summary>
	public int B;

	/// <summary>
	/// Creator switch. Disabling keeps the record (and its hinge settings) but removes the link;
	/// an inactive weld (both sides anchored, see <see cref="LotConstraints.ComputeActivity"/>)
	/// is a different, engine-computed state that leaves this flag alone.
	/// </summary>
	public bool Enabled = true;

	// --- hinge-only payload (ignored on a weld) ---

	/// <summary>
	/// Hinge pivot stored in the FIRST part's local space, so moving the part (or the whole lot)
	/// carries the hinge with it — the attachment-offset behaviour a creator expects.
	/// </summary>
	public Vector3 Pivot;

	/// <summary>
	/// World-space direction the hinge rotates around, stored normalized and resolved against the
	/// part's frame when the joint is built. A free direction rather than one of three world axes,
	/// so an angled hinge is expressible; the editor's sharp tools (<see cref="HingeAxisMath"/>)
	/// are what keep it exact when the creator wants vertical or horizontal.
	/// </summary>
	public Vector3 Axis = Vector3.Up;

	/// <summary>Whether the angular limits below are enforced.</summary>
	public bool LimitsEnabled;

	/// <summary>Lower rotation limit in degrees, relative to the pose captured when the joint was built.</summary>
	public float LowerDeg = -45f;

	/// <summary>Upper rotation limit in degrees.</summary>
	public float UpperDeg = 45f;

	/// <summary>Motor mode. Spin drives the hinge at <see cref="MotorVelocity"/>.</summary>
	public HingeMotorMode Motor = HingeMotorMode.Off;

	/// <summary>Target angular velocity in degrees per second (Spin mode).</summary>
	public float MotorVelocity;

	/// <summary>Impulse cap of the motor — the most push it may apply (Spin mode).</summary>
	public float MotorMaxPush;

	/// <summary>True when this record links <paramref name="handle"/> (either side).</summary>
	public bool Touches(int handle)
	{
		return A == handle || B == handle;
	}
}

/// <summary>
/// The lot's mechanical-constraint table (milestone 3.6) — the single source of truth for welds and
/// hinges, in the same shape as <see cref="LotCollisionGroups"/>: lot-level session state, one table
/// per client, serialized into the `.lot` archive's `lot.json` and never held on a node.
///
/// The registry knows nothing about Godot or the scene: it stores records and derives the one piece
/// of policy the engine owns — the Roblox anchor rule, implemented in
/// <see cref="ComputeActivity"/> — from a caller-supplied "is this part anchored" lookup. The joint
/// nodes that enforce the links are built separately per session by <see cref="LotConstraintSession"/>.
///
/// Layering (§1.1): Godot-infrastructure-facing code. It exposes plain records and booleans; Lua
/// reaches it only through <see cref="LotLuaApi"/> verbs.
/// </summary>
public static class LotConstraints
{
	private static readonly List<LotConstraintRecord> _all = new List<LotConstraintRecord>();
	private static int _nextId = 1;

	/// <summary>Every record, in creation order. Do not mutate.</summary>
	public static IReadOnlyList<LotConstraintRecord> All => _all;

	public static int Count => _all.Count;

	public static LotConstraintRecord Find(int id)
	{
		for (int i = 0; i < _all.Count; i++)
		{
			if (_all[i].Id == id) return _all[i];
		}
		return null;
	}

	/// <summary>Adds a record, assigning it the next id. Returns the stored record.</summary>
	public static LotConstraintRecord Add(LotConstraintRecord record)
	{
		if (record == null) return null;
		if (record.Id <= 0) record.Id = _nextId++;
		else if (record.Id >= _nextId) _nextId = record.Id + 1;
		_all.Add(record);
		return record;
	}

	public static bool Remove(int id)
	{
		for (int i = 0; i < _all.Count; i++)
		{
			if (_all[i].Id == id)
			{
				_all.RemoveAt(i);
				return true;
			}
		}
		return false;
	}

	public static void Clear()
	{
		_all.Clear();
		_nextId = 1;
	}

	/// <summary>Drops every constraint that touches <paramref name="handle"/>. Returns how many.</summary>
	public static int RemoveFor(int handle)
	{
		int removed = 0;
		for (int i = _all.Count - 1; i >= 0; i--)
		{
			if (!_all[i].Touches(handle)) continue;
			_all.RemoveAt(i);
			removed++;
		}
		return removed;
	}

	/// <summary>The canonical pair order (lower handle first), so a weld on (a, b) is the same as (b, a).</summary>
	public static void CanonicalPair(int a, int b, out int first, out int second)
	{
		first = a <= b ? a : b;
		second = a <= b ? b : a;
	}

	/// <summary>
	/// The weld between two handles, or null. Pair order does not matter on either side: the search
	/// matches the stored record whichever way round it was written, so a hand-edited file whose
	/// endpoints are reversed still resolves.
	/// </summary>
	public static LotConstraintRecord FindWeld(int a, int b)
	{
		for (int i = 0; i < _all.Count; i++)
		{
			LotConstraintRecord record = _all[i];
			if (record.Kind != LotConstraintKind.Weld) continue;
			if ((record.A == a && record.B == b) || (record.A == b && record.B == a)) return record;
		}
		return null;
	}

	/// <summary>True when an enabled weld holds between the two handles.</summary>
	public static bool IsWelded(int a, int b)
	{
		LotConstraintRecord record = FindWeld(a, b);
		return record != null && record.Enabled;
	}

	/// <summary>
	/// Replaces the whole table with a snapshot (the test-mode exit path, §3.6): a session's Lua
	/// edits to the table are undone, and records the session deleted come back. Records whose
	/// parts no longer exist are dropped — a session-spawned part died in the exit reload and its
	/// replica carries a new handle, so such a link cannot be re-bound (the same rule the reload
	/// cleanup already applies).
	/// </summary>
	public static void RestoreRecords(IReadOnlyList<LotConstraintRecord> snapshot, Func<int, bool> handleExists)
	{
		Clear();
		if (snapshot == null) return;

		int dropped = 0;
		for (int i = 0; i < snapshot.Count; i++)
		{
			LotConstraintRecord record = snapshot[i];
			if (handleExists != null)
			{
				if (!handleExists(record.A)) { dropped++; continue; }
				if (record.B != LotConstraintRecord.WorldHandle && !handleExists(record.B)) { dropped++; continue; }
			}
			Add(record);
		}
		if (dropped > 0)
		{
			GD.PushWarning("[LotConstraints] " + dropped + " link(s) referenced session-spawned parts "
				+ "and were dropped when leaving Test mode");
		}
	}

	// --- persistence (§4.1 lot.json) --------------------------------------------------------------
	//
	// Records reference parts by their index in the document's object list, NOT by handle: handles
	// are session-local and shift when the lot changes, so a handle written to disk would dangle on
	// the next load. Indices are stable for exactly as long as the capture and the restore walk the
	// same tree — the same rule the node records' parent references already use. Ids are not
	// persisted: a loaded lot numbers its constraints afresh, exactly like it numbers its handles.

	private const string WeldTag = "weld";
	private const string HingeTag = "hinge";

	/// <summary>
	/// Encodes every record whose two sides resolve to a node in <paramref name="capturedNodes"/>
	/// (the capture walk's node list, index-aligned with the object records). A side that does not
	/// resolve is warned about and that record is dropped from the file — the alternative would be
	/// writing an index that the loader resolves to some other part.
	/// </summary>
	public static Godot.Collections.Array ToJson(IReadOnlyList<Node> capturedNodes)
	{
		Godot.Collections.Array arr = new Godot.Collections.Array();
		System.Collections.Generic.Dictionary<int, int> indexOfHandle = new System.Collections.Generic.Dictionary<int, int>();
		if (capturedNodes != null)
		{
			for (int i = 0; i < capturedNodes.Count; i++)
			{
				int handle = HandleOf(capturedNodes[i]);
				if (handle >= 0) indexOfHandle[handle] = i;
			}
		}

		for (int i = 0; i < _all.Count; i++)
		{
			LotConstraintRecord record = _all[i];
			int indexA;
			if (!indexOfHandle.TryGetValue(record.A, out indexA))
			{
				GD.PushWarning("[LotConstraints] constraint " + record.Id + " references a part that is "
					+ "not in the scene; it was not saved");
				continue;
			}

			Dictionary d = new Dictionary
			{
				{ "kind", record.Kind == LotConstraintKind.Weld ? WeldTag : HingeTag },
				{ "a", indexA },
				{ "enabled", record.Enabled }
			};

			if (record.Kind == LotConstraintKind.Weld)
			{
				int indexB;
				if (!indexOfHandle.TryGetValue(record.B, out indexB))
				{
					GD.PushWarning("[LotConstraints] weld " + record.Id + " references a part that is "
						+ "not in the scene; it was not saved");
					continue;
				}
				d["b"] = indexB;
			}
			else
			{
				int indexB = -1;
				if (record.B != LotConstraintRecord.WorldHandle && !indexOfHandle.TryGetValue(record.B, out indexB))
				{
					GD.PushWarning("[LotConstraints] hinge " + record.Id + " references a part that is "
						+ "not in the scene; it was not saved");
					continue;
				}
				d["b"] = indexB;
				d["pivot"] = record.Pivot;
				d["axis"] = AxisToJson(record.Axis);
				d["limits"] = record.LimitsEnabled;
				d["lower"] = record.LowerDeg;
				d["upper"] = record.UpperDeg;
				d["motor"] = record.Motor == HingeMotorMode.Spin ? "spin" : "off";
				d["velocity"] = record.MotorVelocity;
				d["maxPush"] = record.MotorMaxPush;
			}
			arr.Add(d);
		}
		return arr;
	}

	private static int HandleOf(Node node)
	{
		if (node == null || !GodotObject.IsInstanceValid(node)) return -1;
		if (!node.HasMeta(BuilderScene.HandleMeta)) return -1;
		return node.GetMeta(BuilderScene.HandleMeta).AsInt32();
	}

	/// <summary>
	/// The stored form of an axis: a short letter ("x", "-y", ...) for an axis-aligned direction —
	/// the same spelling v1 wrote, so old lots stay readable and a hand-edited file stays friendly —
	/// and the vector itself otherwise (the JSON encoder writes it as "(x, y, z)", which
	/// <see cref="ReadVector3"/> parses back).
	/// </summary>
	private static Variant AxisToJson(Vector3 axis)
	{
		if (axis.IsEqualApprox(Vector3.Right)) return "x";
		if (axis.IsEqualApprox(Vector3.Left)) return "-x";
		if (axis.IsEqualApprox(Vector3.Up)) return "y";
		if (axis.IsEqualApprox(Vector3.Down)) return "-y";
		if (axis.IsEqualApprox(Vector3.Back)) return "z";
		if (axis.IsEqualApprox(Vector3.Forward)) return "-z";
		return axis;
	}

	/// <summary>
	/// Restores records from a saved array, clearing the table first so a lot that carries no block
	/// (or one this build cannot read) never inherits the previous lot's links.
	///
	/// <paramref name="objectsByIndex"/> is the restore walk's node list (index-aligned with the
	/// object records; a null entry is a record that could not be created) and
	/// <paramref name="handleOf"/> resolves a node to its handle — the caller runs this AFTER
	/// handles are issued. Anything unreadable is reported into <paramref name="warnings"/> and that
	/// one record is skipped, never guessed at.
	/// </summary>
	public static void FromJson(Godot.Collections.Array json, IReadOnlyList<Node> objectsByIndex,
		Func<Node, int> handleOf, List<string> warnings)
	{
		Clear();
		if (json == null) return;
		if (objectsByIndex == null)
		{
			if (json.Count > 0) Warn(warnings, "this lot carries constraints but its objects could not be resolved");
			return;
		}

		foreach (Variant value in json)
		{
			if (value.VariantType != Variant.Type.Dictionary)
			{
				Warn(warnings, "a constraint entry in this lot is not readable; it was skipped");
				continue;
			}
			Dictionary d = value.AsGodotDictionary();
			string kind = ReadString(d, "kind", "");
			if (kind != WeldTag && kind != HingeTag)
			{
				Warn(warnings, "this lot contains a '" + kind + "' constraint that this build does not "
					+ "understand; it was skipped");
				continue;
			}

			int a;
			if (!TryResolveHandle(d, "a", false, objectsByIndex, handleOf, warnings, out a)) continue;
			int b;
			if (!TryResolveHandle(d, "b", kind == HingeTag, objectsByIndex, handleOf, warnings, out b)) continue;

			LotConstraintRecord record = new LotConstraintRecord
			{
				Kind = kind == WeldTag ? LotConstraintKind.Weld : LotConstraintKind.Hinge,
				A = a,
				B = b,
				Enabled = ReadBool(d, "enabled", true)
			};

			if (record.Kind == LotConstraintKind.Hinge && !ReadHingeFields(d, record, warnings)) continue;
			Add(record);
		}
	}

	/// <summary>Reads a hinge's payload into the record. False (with a warning) for an unreadable field.</summary>
	private static bool ReadHingeFields(Dictionary d, LotConstraintRecord record, List<string> warnings)
	{
		record.Pivot = ReadVector3(d, "pivot", Vector3.Zero);
		Vector3 axis;
		if (!TryReadAxis(d, warnings, out axis)) return false;
		record.Axis = axis;
		record.LimitsEnabled = ReadBool(d, "limits", false);
		record.LowerDeg = ReadFloat(d, "lower", -45f);
		record.UpperDeg = ReadFloat(d, "upper", 45f);
		string motor = ReadString(d, "motor", "off");
		if (motor == "spin") record.Motor = HingeMotorMode.Spin;
		else if (motor != "off")
		{
			Warn(warnings, "a hinge in this lot uses an unknown motor mode '" + motor + "'; it was skipped");
			return false;
		}
		record.MotorVelocity = ReadFloat(d, "velocity", 0f);
		record.MotorMaxPush = ReadFloat(d, "maxPush", 0f);
		return true;
	}

	/// <summary>
	/// Resolves one endpoint index to a handle. On a hinge, index -1 (and therefore
	/// <paramref name="allowWorld"/>) means "the world"; any other failure warns and returns false so
	/// the whole record is skipped.
	/// </summary>
	private static bool TryResolveHandle(Dictionary d, string key, bool allowWorld,
		IReadOnlyList<Node> objectsByIndex, Func<Node, int> handleOf, List<string> warnings, out int handle)
	{
		handle = LotConstraintRecord.WorldHandle;
		int index = ReadInt(d, key, -2);
		if (index == -2)
		{
			Warn(warnings, "a constraint entry in this lot has no '" + key + "' endpoint; it was skipped");
			return false;
		}
		if (index == -1 && allowWorld) return true;

		if (index < 0 || index >= objectsByIndex.Count || objectsByIndex[index] == null)
		{
			Warn(warnings, "a constraint in this lot references object " + index
				+ ", which was not restored; it was skipped");
			return false;
		}
		handle = handleOf != null ? handleOf(objectsByIndex[index]) : -1;
		if (handle < 0)
		{
			Warn(warnings, "a constraint in this lot references an object with no handle; it was skipped");
			return false;
		}
		return true;
	}

	/// <summary>
	/// Reads a stored axis: the v1 letters ("x", "y", "z" and their negatives), the tuple string the
	/// JSON encoder writes for a vector ("(0, 0.7, 0.7)"), or a native Vector3 (in-memory paths).
	/// False — with a warning, so the record is skipped — for anything else, and for a zero vector:
	/// a direction is never guessed.
	/// </summary>
	private static bool TryReadAxis(Dictionary d, List<string> warnings, out Vector3 axis)
	{
		axis = Vector3.Up;
		if (d == null || !d.ContainsKey("axis")) return true; // absent: the record's own default

		Variant value = d["axis"];
		if (value.VariantType == Variant.Type.String)
		{
			string shortForm = value.AsString().Trim().ToLowerInvariant();
			if (shortForm == "x") { axis = Vector3.Right; return true; }
			if (shortForm == "-x") { axis = Vector3.Left; return true; }
			if (shortForm == "y") { axis = Vector3.Up; return true; }
			if (shortForm == "-y") { axis = Vector3.Down; return true; }
			if (shortForm == "z") { axis = Vector3.Back; return true; }
			if (shortForm == "-z") { axis = Vector3.Forward; return true; }
		}

		// Not a letter: either the encoder's "(x, y, z)" tuple or a native vector.
		Vector3 stored = ReadVector3(d, "axis", Vector3.Zero);
		if (!HingeAxisMath.TryNormalize(stored, out axis))
		{
			Warn(warnings, "a hinge in this lot uses an unreadable axis ('" + value
				+ "'); it was skipped");
			return false;
		}
		return true;
	}

	private static void Warn(List<string> warnings, string message)
	{
		if (warnings != null) warnings.Add(message);
		else GD.PushWarning("[LotConstraints] " + message);
	}

	// --- JSON readers (the same conventions LotArchive uses: a missing key is the caller's default,
	// and a number may arrive as an int or a float because that is how Godot's parser returns them) --

	private static int ReadInt(Dictionary d, string key, int fallback)
	{
		if (d == null || !d.ContainsKey(key)) return fallback;
		Variant v = d[key];
		if (v.VariantType == Variant.Type.Int) return v.AsInt32();
		if (v.VariantType == Variant.Type.Float) return (int)v.AsDouble();
		return fallback;
	}

	private static float ReadFloat(Dictionary d, string key, float fallback)
	{
		if (d == null || !d.ContainsKey(key)) return fallback;
		Variant v = d[key];
		if (v.VariantType == Variant.Type.Float) return (float)v.AsDouble();
		if (v.VariantType == Variant.Type.Int) return v.AsInt32();
		return fallback;
	}

	private static bool ReadBool(Dictionary d, string key, bool fallback)
	{
		if (d == null || !d.ContainsKey(key)) return fallback;
		Variant v = d[key];
		return v.VariantType == Variant.Type.Bool ? v.AsBool() : fallback;
	}

	private static string ReadString(Dictionary d, string key, string fallback)
	{
		if (d == null || !d.ContainsKey(key)) return fallback;
		Variant v = d[key];
		return v.VariantType == Variant.Type.String ? v.AsString() : fallback;
	}

	/// <summary>
	/// Rebuilds a Vector3 from its JSON form. Godot's encoder writes a Vector3 as the string
	/// "(x, y, z)" — JSON has no vector literal — so this parses that tuple itself, exactly like
	/// <see cref="LotArchive"/> does for the same reason. The native-Vector3 branch covers values
	/// passed straight in (self-tests).
	/// </summary>
	private static Vector3 ReadVector3(Dictionary d, string key, Vector3 fallback)
	{
		if (d == null || !d.ContainsKey(key)) return fallback;
		Variant v = d[key];
		if (v.VariantType == Variant.Type.Vector3) return v.AsVector3();
		if (v.VariantType != Variant.Type.String) return fallback;

		string text = v.AsString().Trim();
		if (text.Length < 2 || text[0] != '(' || text[text.Length - 1] != ')') return fallback;
		string[] tokens = text.Substring(1, text.Length - 2).Split(',');
		if (tokens.Length < 3) return fallback;
		float[] parts = new float[3];
		for (int i = 0; i < 3; i++)
		{
			if (!float.TryParse(tokens[i].Trim(), System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out parts[i]))
				return fallback;
		}
		return new Vector3(parts[0], parts[1], parts[2]);
	}

	// --- anchor rule (Roblox semantics, the one piece of policy v1 adopts verbatim) ---------------
	//
	// Weld: parts joined by welds form islands. One anchored part in an island freezes the whole
	// island; a weld that would join two already-anchored islands cannot hold and goes inactive
	// (Roblox's "the assembly splits"), which is deterministic here because welds are processed in
	// creation order. Hinge: it does not merge islands, so it is inactive only while BOTH of its
	// sides are anchored — a hinge whose free side has nothing to move is meaningless.
	//
	// The scratch arrays are static and reused because ComputeActivity may run per frame while the
	// Inspector shows a selected part's constraints.

	private static readonly List<int> _dsuParent = new List<int>();
	private static readonly List<int> _dsuAnchored = new List<int>();
	private static readonly System.Collections.Generic.Dictionary<int, int> _dsuIndex = new System.Collections.Generic.Dictionary<int, int>();

	/// <summary>
	/// Fills <paramref name="into"/> with each record's computed active state, keyed by record id.
	/// <paramref name="isAnchored"/> answers "is this part anchored?" for a handle; a hinge's world
	/// side always counts as anchored. A record the caller disabled reports false.
	/// </summary>
	public static void ComputeActivity(Func<int, bool> isAnchored, System.Collections.Generic.Dictionary<int, bool> into)
	{
		if (into == null) return;
		into.Clear();
		_dsuParent.Clear();
		_dsuAnchored.Clear();
		_dsuIndex.Clear();

		for (int i = 0; i < _all.Count; i++)
		{
			LotConstraintRecord record = _all[i];
			if (!record.Enabled)
			{
				into[record.Id] = false;
				continue;
			}

			if (record.Kind == LotConstraintKind.Hinge)
			{
				into[record.Id] = !(Anchored(record.A, isAnchored) && Anchored(record.B, isAnchored));
				continue;
			}

			int ia = DsuFind(DsuEnsure(record.A, Anchored(record.A, isAnchored)));
			int ib = DsuFind(DsuEnsure(record.B, Anchored(record.B, isAnchored)));
			if (ia == ib)
			{
				into[record.Id] = true;
				continue;
			}
			if (_dsuAnchored[ia] > 0 && _dsuAnchored[ib] > 0)
			{
				into[record.Id] = false; // two anchored islands cannot be joined by a weld
				continue;
			}
			DsuUnion(ia, ib);
			into[record.Id] = true;
		}
	}

	private static bool Anchored(int handle, Func<int, bool> isAnchored)
	{
		if (handle == LotConstraintRecord.WorldHandle) return true;
		return isAnchored != null && isAnchored(handle);
	}

	/// <summary>An island carrying more active links than this is warned about in a session: chain
	/// springiness grows with length, and the fix (anchor a root, split the build) is the creator's
	/// call — so this is deliberately a warning, never a cap.</summary>
	public const int ChainWarnThreshold = 8;

	private static readonly System.Collections.Generic.Dictionary<int, int> _islandLinks =
		new System.Collections.Generic.Dictionary<int, int>();

	/// <summary>
	/// The largest number of ACTIVE links sharing one welded island — the chain-length gauge
	/// <see cref="LotConstraintSession.Build"/> warns on. Pure (activity arrives through
	/// <paramref name="isActive"/>, keyed by record id) so the arithmetic is unit-testable without a
	/// scene. Welds define the islands; a hinge counts toward its first side's island without merging
	/// islands, mirroring how a hinge does not bind two assemblies into one.
	/// </summary>
	public static int LargestLinkIsland(Func<int, bool> isActive)
	{
		_dsuParent.Clear();
		_dsuAnchored.Clear();
		_dsuIndex.Clear();
		_islandLinks.Clear();

		// Pass 1: active welds merge islands.
		for (int i = 0; i < _all.Count; i++)
		{
			LotConstraintRecord record = _all[i];
			if (record.Kind != LotConstraintKind.Weld || !IsActive(record, isActive)) continue;
			int ia = DsuFind(DsuEnsure(record.A, false));
			int ib = DsuFind(DsuEnsure(record.B, false));
			if (ia != ib) DsuUnion(ia, ib);
		}

		// Pass 2: every active link counts toward its first side's island.
		int largest = 0;
		for (int i = 0; i < _all.Count; i++)
		{
			LotConstraintRecord record = _all[i];
			if (!IsActive(record, isActive)) continue;
			int root = DsuFind(DsuEnsure(record.A, false));
			int count;
			_islandLinks.TryGetValue(root, out count);
			count++;
			_islandLinks[root] = count;
			if (count > largest) largest = count;
		}
		return largest;
	}

	private static bool IsActive(LotConstraintRecord record, Func<int, bool> isActive)
	{
		if (record == null || !record.Enabled) return false;
		return isActive == null || isActive(record.Id);
	}

	private static int DsuEnsure(int handle, bool anchored)
	{
		int index;
		if (_dsuIndex.TryGetValue(handle, out index)) return index;
		index = _dsuParent.Count;
		_dsuParent.Add(index);
		_dsuAnchored.Add(anchored ? 1 : 0);
		_dsuIndex[handle] = index;
		return index;
	}

	private static int DsuFind(int index)
	{
		while (_dsuParent[index] != index)
		{
			_dsuParent[index] = _dsuParent[_dsuParent[index]]; // path halving
			index = _dsuParent[index];
		}
		return index;
	}

	private static void DsuUnion(int a, int b)
	{
		int rootA = DsuFind(a);
		int rootB = DsuFind(b);
		if (rootA == rootB) return;
		_dsuParent[rootB] = rootA;
		_dsuAnchored[rootA] += _dsuAnchored[rootB];
	}
}
