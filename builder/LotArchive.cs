using Godot;
using Godot.Collections;

/// <summary>
/// The `.lot` archive format (milestone 4.1): the single-file document an OpenLot creation is saved
/// as. A `.lot` is a plain Zip bundle with a fixed entry layout, so one portable file carries the
/// whole lot instead of a folder tree that has to be copied around:
///
/// <code>
/// MyLot.lot  (a zip by another name)
///   manifest.json   the ID card: format version, name, author, timestamps, maxPlayers, entryScript
///   lot.json        the scene payload: objects, lot UI, environment settings
///   scripts/        the lot's Lua sources, referenced by archive-relative path
///   assets/         embedded binaries (textures today; models/audio as later milestones land)
/// </code>
///
/// This file owns the format: the plain data records, their JSON encoding, and the version gate.
/// Byte-level access to an archive (and only that) lives in <see cref="LotVfs"/>, which is the single
/// read path every asset consumer uses; nothing else opens a `.lot` directly.
///
/// Layering (§1.1): this is Godot-infrastructure-facing code. It exposes plain data outward and never
/// reaches into Lua; a script that needs an embedded asset asks a bound API method, which asks
/// <see cref="LotVfs"/>.
/// </summary>
public static class LotArchive
{
	/// <summary>The format version this build writes and accepts. Bump only alongside a migration.</summary>
	public const int CurrentFormatVersion = 1;

	/// <summary>Fixed archive entry names. Declared once so the writer and reader cannot disagree.</summary>
	public const string ManifestEntry = "manifest.json";
	public const string LotEntry = "lot.json";
	public const string ScriptsPrefix = "scripts/";
	public const string AssetsPrefix = "assets/";

	/// <summary>
	/// The "secret" counterparts of <see cref="ScriptsPrefix"/> / <see cref="AssetsPrefix"/>. These hold
	/// base content the creator does not normally edit — base character code and base character formats
	/// — and the editor hides them by default while a reveal toggle (or opening the file directly) still
	/// reaches them.
	///
	/// They are the SAME format as the normal folders: same writer, same reader, same VFS path. The only
	/// difference is derived from the path prefix by <see cref="IsSecretPath"/>, so no per-entry flag
	/// field was added to the v1 record and an old lot stays readable.
	/// </summary>
	public const string SecretScriptsPrefix = "Secret_Code/";
	public const string SecretAssetsPrefix = "Secret_Asset/";

	/// <summary>True for an archive entry inside one of the secret folders (see the prefix docs).</summary>
	public static bool IsSecretPath(string entryPath)
	{
		if (string.IsNullOrEmpty(entryPath)) return false;
		return entryPath.StartsWith(SecretScriptsPrefix, System.StringComparison.Ordinal)
			|| entryPath.StartsWith(SecretAssetsPrefix, System.StringComparison.Ordinal);
	}

	/// <summary>
	/// Record kind tag for a script placeholder. Scripts are recorded rather than inferred from the
	/// archive's script entries because a script belongs to a specific entity (the context menu's
	/// "Insert Script" attaches one to any node, and the script runtime binds it to that entity's
	/// handle) — inferring them would flatten every script onto the lot root and silently change what
	/// the lot does when it runs.
	/// </summary>
	public const string ScriptRecordKind = "script";

	/// <summary>
	/// Property key on a script record carrying the script's file name. The name (not a path) is stored
	/// because it is also what the collection pass routes the entry under, so the loader recomputes the
	/// archive path with the same rule instead of duplicating it — and a machine-local path is
	/// deliberately never written into a lot.
	/// </summary>
	public const string ScriptNameProperty = "scriptName";

	/// <summary>Version verdict for a manifest read off disk, so a caller can report a readable reason.</summary>
	public enum VersionStatus
	{
		/// <summary>The manifest is this build's format and can be loaded as-is.</summary>
		Ok,
		/// <summary>A newer format version than this build understands — reject, never half-load.</summary>
		TooNew,
		/// <summary>An older format version that no migration covers (there are none yet, v1 is the first).</summary>
		TooOld
	}

	/// <summary>
	/// The archive's ID card (`manifest.json`). Small and intentionally flat: it is what a host reads
	/// to declare itself (entry script, player cap) without loading the whole lot, and what the
	/// version gate reads to decide whether the rest of the archive can be opened at all.
	/// </summary>
	public sealed class LotManifest
	{
		/// <summary>Format version of the archive this was read from / will be written as.</summary>
		public int FormatVersion = CurrentFormatVersion;

		public string Name = "Untitled Lot";
		public string Author = "";

		/// <summary>Unix seconds (UTC). 0 means "unknown".</summary>
		public long CreatedUnix;
		public long ModifiedUnix;

		/// <summary>Declared player cap. Read by a headless host (§8.1) before any lot code runs.</summary>
		public int MaxPlayers = 8;

		/// <summary>
		/// Archive-relative path of the script a host boots to start the lot ("" for none). Declaring
		/// the entry point in the archive itself is what lets a dedicated host know where to start
		/// without a hardcoded path (§8.1).
		/// </summary>
		public string EntryScript = "";

		public Dictionary ToJson()
		{
			return new Dictionary
			{
				{ "formatVersion", FormatVersion },
				{ "name", Name ?? "" },
				{ "author", Author ?? "" },
				{ "createdUnix", CreatedUnix },
				{ "modifiedUnix", ModifiedUnix },
				{ "maxPlayers", MaxPlayers },
				{ "entryScript", EntryScript ?? "" }
			};
		}

		public static LotManifest FromJson(Variant value)
		{
			Dictionary d = value.AsGodotDictionary();
			LotManifest manifest = new LotManifest();
			if (d == null) return manifest;
			manifest.FormatVersion = ReadInt(d, "formatVersion", CurrentFormatVersion);
			manifest.Name = ReadString(d, "name", "Untitled Lot");
			manifest.Author = ReadString(d, "author", "");
			manifest.CreatedUnix = ReadLong(d, "createdUnix", 0L);
			manifest.ModifiedUnix = ReadLong(d, "modifiedUnix", 0L);
			manifest.MaxPlayers = ReadInt(d, "maxPlayers", 8);
			manifest.EntryScript = ReadString(d, "entryScript", "");
			return manifest;
		}

		/// <summary>
		/// The version gate: whether this build can open <paramref name="version"/>, and if not, why.
		/// The caller must surface the reason and refuse the load rather than attempting a partial
		/// read — a half-loaded lot is worse than a clear error.
		/// </summary>
		public static VersionStatus CheckVersion(int version)
		{
			if (version > CurrentFormatVersion) return VersionStatus.TooNew;
			if (version < CurrentFormatVersion) return VersionStatus.TooOld;
			return VersionStatus.Ok;
		}

		/// <summary>Human-readable reason for a version verdict, for the error a caller prints.</summary>
		public static string DescribeVersion(int version)
		{
			VersionStatus status = CheckVersion(version);
			if (status == VersionStatus.TooNew)
				return "lot format version " + version + " is newer than this build supports ("
					+ CurrentFormatVersion + "); update OpenLot to open this lot";
			if (status == VersionStatus.TooOld)
				return "lot format version " + version + " is older and no migration is available for "
					+ "it in this build (" + CurrentFormatVersion + ")";
			return "lot format version " + version + " is supported";
		}
	}

	/// <summary>
	/// The scene payload (`lot.json`): everything about the lot that is not the ID card or the packed
	/// binaries. This is deliberately plain data — node descriptions, not Godot nodes — so the writer
	/// and reader share one shape and a later loader can rebuild the world from it in one pass.
	///
	/// The §2.3 property values travel inside <see cref="LotNodeRecord"/> keyed by their stable
	/// <see cref="LotPropertyDescriptor.Id"/>, which is why the property registry and this format
	/// agree on vocabulary without either importing the other.
	/// </summary>
	public sealed class LotDocument
	{
		/// <summary>3D objects under LotRoot, in depth-first load order (parents before children).</summary>
		public System.Collections.Generic.List<LotNodeRecord> Objects = new System.Collections.Generic.List<LotNodeRecord>();

		/// <summary>2D lot UI elements under LotUIRoot, in sibling order.</summary>
		public System.Collections.Generic.List<LotNodeRecord> UiElements = new System.Collections.Generic.List<LotNodeRecord>();

		/// <summary>
		/// Creation-environment lighting, or null when the lot never set one (an older lot, or a lot
		/// saved before §2.5 existed). Null means "use the builder default", so persistence can never
		/// silently change the look of a lot that never touched these controls.
		/// </summary>
		public LotEnvironmentSettings? Environment;

		/// <summary>
		/// The §3.5 named-collision-group interaction matrix, or null when the lot carries none (an
		/// older lot, or one saved before groups existed). Null means "every group collides", so an
		/// absent block can never change how a lot behaves. The shape is owned by
		/// <see cref="LotCollisionGroups"/> (encode/decode live there), not spelled out here.
		/// </summary>
		public Array Collision;

		/// <summary>
		/// The §3.6 welds and hinges, or null when the lot carries none. Records reference parts by
		/// their index in <see cref="Objects"/> (handles are session-local), and the shape is owned
		/// by <see cref="LotConstraints"/> (encode/decode live there) — same split as the collision
		/// matrix, so this format knows nothing about constraints beyond "there is an array".
		/// </summary>
		public Array Constraints;

		public Dictionary ToJson()
		{
			Dictionary d = new Dictionary
			{
				{ "objects", RecordsToJson(Objects) },
				{ "uiElements", RecordsToJson(UiElements) }
			};
			if (Environment.HasValue) d["environment"] = EnvironmentToJson(Environment.Value);
			if (Collision != null) d["collision"] = Collision;
			if (Constraints != null && Constraints.Count > 0) d["constraints"] = Constraints;
			return d;
		}

		public static LotDocument FromJson(Variant value)
		{
			LotDocument doc = new LotDocument();
			Dictionary d = value.AsGodotDictionary();
			if (d == null) return doc;
			doc.Objects = RecordsFromJson(ReadArray(d, "objects"));
			doc.UiElements = RecordsFromJson(ReadArray(d, "uiElements"));
			if (d.ContainsKey("environment")) doc.Environment = EnvironmentFromJson(d["environment"]);
			if (d.ContainsKey("collision") && d["collision"].VariantType == Variant.Type.Array)
				doc.Collision = d["collision"].AsGodotArray();
			if (d.ContainsKey("constraints") && d["constraints"].VariantType == Variant.Type.Array)
				doc.Constraints = d["constraints"].AsGodotArray();
			return doc;
		}

		private static Array RecordsToJson(System.Collections.Generic.List<LotNodeRecord> records)
		{
			Array arr = new Array();
			for (int i = 0; i < records.Count; i++) arr.Add(records[i].ToJson());
			return arr;
		}

		private static System.Collections.Generic.List<LotNodeRecord> RecordsFromJson(Array arr)
		{
			System.Collections.Generic.List<LotNodeRecord> list = new System.Collections.Generic.List<LotNodeRecord>();
			if (arr == null) return list;
			foreach (Variant v in arr) list.Add(LotNodeRecord.FromJson(v));
			return list;
		}
	}

	/// <summary>
	/// One saved scene node: a transform plus the declared property values that apply to it. Kept as a
	/// small flat record with a property bag rather than a field per property, so a new §2.3 property
	/// needs no format change and a lot saved by an older build simply carries fewer keys.
	/// </summary>
	public sealed class LotNodeRecord
	{
		/// <summary>Stable kind tag: "cube"/"sphere"/"cylinder"/"plane"/"capsule" for parts, the
		/// LotUIKind name for lot UI, or "group" for a plain container node.</summary>
		public string Kind = "group";

		public string Name = "";
		public Vector3 Position = Vector3.Zero;
		public Vector3 RotationDegrees = Vector3.Zero;
		public Vector3 Scale = Vector3.One;

		public Color Color = new Color(1f, 1f, 1f);

		/// <summary>Index of this record's parent within its list, or -1 for a root-level node. Only
		/// meaningful for 3D objects; lot UI elements are flat siblings.</summary>
		public int ParentIndex = -1;

		/// <summary>Declared §2.3 property values, keyed by <see cref="LotPropertyDescriptor.Id"/>,
		/// stored as their natural Variant (bool or string).</summary>
		public Dictionary Properties = new Dictionary();

		public Dictionary ToJson()
		{
			return new Dictionary
			{
				{ "kind", Kind ?? "group" },
				{ "name", Name ?? "" },
				{ "position", Position },
				{ "rotation", RotationDegrees },
				{ "scale", Scale },
				{ "color", Color },
				{ "parentIndex", ParentIndex },
				{ "properties", Properties }
			};
		}

		public static LotNodeRecord FromJson(Variant value)
		{
			Dictionary d = value.AsGodotDictionary();
			LotNodeRecord rec = new LotNodeRecord();
			if (d == null) return rec;
			rec.Kind = ReadString(d, "kind", "group");
			rec.Name = ReadString(d, "name", "");
			rec.Position = ReadVector3(d, "position", Vector3.Zero);
			rec.RotationDegrees = ReadVector3(d, "rotation", Vector3.Zero);
			rec.Scale = ReadVector3(d, "scale", Vector3.One);
			rec.Color = ReadColor(d, "color", new Color(1f, 1f, 1f));
			rec.ParentIndex = ReadInt(d, "parentIndex", -1);
			if (d.ContainsKey("properties") && d["properties"].VariantType == Variant.Type.Dictionary)
				rec.Properties = d["properties"].AsGodotDictionary();
			return rec;
		}
	}

	// --- Environment settings <-> JSON ----------------------------------------------------------
	// A dedicated encoder rather than a reflection-driven one: the field set is small and fixed, and
	// spelling each key out here is what makes a format bump obvious when a field is added.

	private static Dictionary EnvironmentToJson(in LotEnvironmentSettings s)
	{
		return new Dictionary
		{
			{ "sunElevationDeg", s.SunElevationDeg },
			{ "sunAzimuthDeg", s.SunAzimuthDeg },
			{ "sunColor", s.SunColor },
			{ "sunEnergy", s.SunEnergy },
			{ "skyTopColor", s.SkyTopColor },
			{ "skyHorizonColor", s.SkyHorizonColor },
			{ "groundHorizonColor", s.GroundHorizonColor },
			{ "groundBottomColor", s.GroundBottomColor },
			{ "fogEnabled", s.FogEnabled },
			{ "fogColor", s.FogColor },
			{ "fogDensity", s.FogDensity },
			{ "followTimeOfDay", s.FollowTimeOfDay },
			{ "timeOfDayHours", s.TimeOfDayHours }
		};
	}

	private static LotEnvironmentSettings EnvironmentFromJson(Variant value)
	{
		Dictionary d = value.AsGodotDictionary();
		LotEnvironmentSettings s = LotEnvironmentSettings.Default();
		if (d == null) return s;
		s.SunElevationDeg = ReadFloat(d, "sunElevationDeg", s.SunElevationDeg);
		s.SunAzimuthDeg = ReadFloat(d, "sunAzimuthDeg", s.SunAzimuthDeg);
		s.SunColor = ReadColor(d, "sunColor", s.SunColor);
		s.SunEnergy = ReadFloat(d, "sunEnergy", s.SunEnergy);
		s.SkyTopColor = ReadColor(d, "skyTopColor", s.SkyTopColor);
		s.SkyHorizonColor = ReadColor(d, "skyHorizonColor", s.SkyHorizonColor);
		s.GroundHorizonColor = ReadColor(d, "groundHorizonColor", s.GroundHorizonColor);
		s.GroundBottomColor = ReadColor(d, "groundBottomColor", s.GroundBottomColor);
		s.FogEnabled = ReadBool(d, "fogEnabled", s.FogEnabled);
		s.FogColor = ReadColor(d, "fogColor", s.FogColor);
		s.FogDensity = ReadFloat(d, "fogDensity", s.FogDensity);
		s.FollowTimeOfDay = ReadBool(d, "followTimeOfDay", s.FollowTimeOfDay);
		s.TimeOfDayHours = ReadFloat(d, "timeOfDayHours", s.TimeOfDayHours);
		return s;
	}

	// --- Dictionary readers ---------------------------------------------------------------------
	// Every reader tolerates a missing key or a wrong type by returning the caller's default, so a
	// lot written by an older build (fewer keys) loads instead of throwing. A missing value is never
	// a silent semantic change because each default is the caller's own current value.

	internal static int ReadInt(Dictionary d, string key, int fallback)
	{
		if (d == null || !d.ContainsKey(key)) return fallback;
		return Mathf.RoundToInt((float)ReadDouble(d[key], fallback));
	}

	internal static long ReadLong(Dictionary d, string key, long fallback)
	{
		if (d == null || !d.ContainsKey(key)) return fallback;
		return (long)ReadDouble(d[key], fallback);
	}

	internal static float ReadFloat(Dictionary d, string key, float fallback)
	{
		if (d == null || !d.ContainsKey(key)) return fallback;
		return (float)ReadDouble(d[key], fallback);
	}

	private static double ReadDouble(Variant v, double fallback)
	{
		if (v.VariantType == Variant.Type.Int || v.VariantType == Variant.Type.Float) return v.AsDouble();
		return fallback;
	}

	internal static bool ReadBool(Dictionary d, string key, bool fallback)
	{
		if (d == null || !d.ContainsKey(key)) return fallback;
		Variant v = d[key];
		if (v.VariantType == Variant.Type.Bool) return v.AsBool();
		return fallback;
	}

	internal static string ReadString(Dictionary d, string key, string fallback)
	{
		if (d == null || !d.ContainsKey(key)) return fallback;
		Variant v = d[key];
		if (v.VariantType == Variant.Type.String) return v.AsString();
		return fallback;
	}

	private static Array ReadArray(Dictionary d, string key)
	{
		if (d == null || !d.ContainsKey(key)) return new Array();
		Variant v = d[key];
		if (v.VariantType == Variant.Type.Array) return v.AsGodotArray();
		return new Array();
	}

	/// <summary>
	/// Rebuilds a Vector3 from its JSON form. Godot's <see cref="Json"/> encoder writes a Vector3 as a
	/// <b>string</b> — <c>"(1, 2, 3)"</c> — and the parser leaves it a string, because JSON has no
	/// vector literal. That is deliberately not the Variant text form (<c>Vector3(1, 2, 3)</c>), so
	/// <see cref="GD.StrToVar"/> cannot read it back and <see cref="ReadTuple"/> does. The
	/// native-Vector3 branch is kept for values passed straight in (self-tests, or a future in-memory
	/// path), so this reader is correct for both sources.
	/// </summary>
	private static Vector3 ReadVector3(Variant value, Vector3 fallback)
	{
		if (value.VariantType == Variant.Type.Vector3) return value.AsVector3();
		if (value.VariantType != Variant.Type.String) return fallback;

		float[] parts = ReadTuple(value.AsString());
		if (parts == null || parts.Length < 3) return fallback;
		return new Vector3(parts[0], parts[1], parts[2]);
	}

	private static Vector3 ReadVector3(Dictionary d, string key, Vector3 fallback)
	{
		if (d == null || !d.ContainsKey(key)) return fallback;
		return ReadVector3(d[key], fallback);
	}

	/// <summary>
	/// Rebuilds a Color from its JSON form. Same rule as <see cref="ReadVector3(Variant, Vector3)"/>:
	/// the encoder writes <c>"(0.2, 0.4, 0.6, 1)"</c> and the parser hands it back as a string.
	/// </summary>
	private static Color ReadColor(Variant value, Color fallback)
	{
		if (value.VariantType == Variant.Type.Color) return value.AsColor();
		if (value.VariantType != Variant.Type.String) return fallback;

		float[] parts = ReadTuple(value.AsString());
		if (parts == null || parts.Length < 3) return fallback;
		return new Color(parts[0], parts[1], parts[2], parts.Length >= 4 ? parts[3] : 1f);
	}

	private static Color ReadColor(Dictionary d, string key, Color fallback)
	{
		if (d == null || !d.ContainsKey(key)) return fallback;
		return ReadColor(d[key], fallback);
	}

	/// <summary>
	/// Parses the parenthesised tuple Godot's JSON encoder emits for a vector or colour —
	/// <c>"(1, 2, 3)"</c> / <c>"(0.2, 0.4, 0.6, 1)"</c> — into its numeric parts, or null when the
	/// text is not that shape. Returns any component count; callers decide what they need. Invariant
	/// culture is used so the parse cannot vary with the host's locale.
	/// </summary>
	private static float[] ReadTuple(string text)
	{
		if (string.IsNullOrEmpty(text)) return null;
		string trimmed = text.Trim();
		if (trimmed.Length < 2 || trimmed[0] != '(' || trimmed[trimmed.Length - 1] != ')') return null;

		string[] tokens = trimmed.Substring(1, trimmed.Length - 2).Split(',');
		float[] parts = new float[tokens.Length];
		for (int i = 0; i < tokens.Length; i++)
		{
			string token = tokens[i].Trim();
			if (token.Length == 0 || !float.TryParse(token, System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out parts[i]))
				return null;
		}
		return parts;
	}
}
