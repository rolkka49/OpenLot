using Godot;

/// <summary>
/// The archive collection pass (milestone 4.1 Step B): gathers the lot's script sources and embedded
/// asset bytes into the two dictionaries <see cref="LotArchiveWriter"/> takes, so a saved lot carries
/// its own code and images instead of pointing at files only the author has.
///
/// Split from <see cref="LotSceneWalk"/> on purpose: the walk describes the scene (transforms,
/// properties), the collection pass gathers file content. Both are plain in/out helpers, so 4.2's save
/// path is a short sequence of calls with no hidden state.
///
/// Base content — the scripts and formats a brand-new lot ships with — archives under the secret
/// folders (see <see cref="LotArchive.SecretScriptsPrefix"/>). Everything a creator adds goes in the
/// normal folders. The rule is stated once here so it cannot drift.
/// </summary>
public static class LotArchiveCollect
{
	/// <summary>Property id carrying a part's texture asset id (the §2.3 Texture descriptor). Public
	/// because the archive load path reads it back to check the image actually arrived.</summary>
	public const string TexturePropertyId = "texture";

	/// <summary>Property id carrying a decal's image asset id (the §2.6 Image descriptor). A decal
	/// stores its image through the same LotObject texture field, but under its own property id, so
	/// the collection pass reads both before deciding a record has no image.</summary>
	public const string DecalTexturePropertyId = "decal_texture";

	/// <summary>
	/// Script file names that count as base content. Today that is exactly the script a new lot ships
	/// with; reading it from <see cref="BuilderScene.DefaultLotScriptName"/> rather than repeating the
	/// string is what stops the two disagreeing.
	/// </summary>
	public static readonly string[] BaseScriptNames = { BuilderScene.DefaultLotScriptName };

	/// <summary>True when a script (by its file name) is base content and so archives under the secret
	/// code folder. Case-insensitive so a differently-cased duplicate still routes consistently.</summary>
	public static bool IsBaseScript(string scriptFileName)
	{
		if (string.IsNullOrEmpty(scriptFileName)) return false;
		for (int i = 0; i < BaseScriptNames.Length; i++)
		{
			if (string.Equals(BaseScriptNames[i], scriptFileName, System.StringComparison.OrdinalIgnoreCase)) return true;
		}
		return false;
	}

	/// <summary>Archive path for a script file name, routed to the secret folder for base content.</summary>
	public static string ScriptEntryPath(string fileName, bool baseContent)
	{
		string prefix = baseContent ? LotArchive.SecretScriptsPrefix : LotArchive.ScriptsPrefix;
		return prefix + SanitizeEntryName(fileName);
	}
	/// <summary>
	/// Gathers every script placeholder under <paramref name="lotRoot"/> into <paramref name="into"/>,
	/// keyed by archive-relative path. A script whose file has gone is skipped and reported in
	/// <paramref name="missing"/>, so a save surfaces the loss instead of shipping a broken reference or
	/// fabricating an empty script in its place.
	/// </summary>
	public static void Scripts(Node lotRoot, System.Collections.Generic.Dictionary<string, string> into,
		System.Collections.Generic.List<string> missing)
	{
		if (lotRoot == null || into == null) return;
		CollectScriptsRecursive(lotRoot, into, missing);
	}

	private static void CollectScriptsRecursive(Node parent, System.Collections.Generic.Dictionary<string, string> into,
		System.Collections.Generic.List<string> missing)
	{
		int count = parent.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			Node child = parent.GetChild(i);
			LotScriptNode script = child as LotScriptNode;
			if (script != null)
			{
				CollectOneScript(script, into, missing);
				// A script placeholder is a leaf; nothing below it is content.
				continue;
			}
			CollectScriptsRecursive(child, into, missing);
		}
	}

	private static void CollectOneScript(LotScriptNode script, System.Collections.Generic.Dictionary<string, string> into,
		System.Collections.Generic.List<string> missing)
	{
		string fileName = string.IsNullOrEmpty(script.DisplayName) ? script.Name.ToString() : script.DisplayName;
		string entryPath = ScriptEntryPath(fileName, IsBaseScript(fileName));

		if (string.IsNullOrEmpty(script.ScriptPath) || !Godot.FileAccess.FileExists(script.ScriptPath))
		{
			if (missing != null) missing.Add(entryPath);
			return;
		}

		Godot.FileAccess file = Godot.FileAccess.Open(script.ScriptPath, Godot.FileAccess.ModeFlags.Read);
		if (file == null)
		{
			if (missing != null) missing.Add(entryPath);
			return;
		}
		string text = file.GetAsText();
		file.Dispose();
		into[entryPath] = text;
	}

	/// <summary>
	/// Gathers the bytes of every texture the captured document references, keyed by archive-relative
	/// path. A part stores its texture as an opaque asset id, so the bytes are re-read from the source
	/// file the cache imported it from (<see cref="LotTextureCache.GetSourcePath"/>). When that file has
	/// gone the id is reported in <paramref name="missing"/> and nothing is written for it.
	///
	/// A texture referenced by a Capsule record archives under the secret asset folder: the capsule is
	/// the base character, and character formats are base content.
	/// </summary>
	public static void Assets(LotArchive.LotDocument document, System.Collections.Generic.Dictionary<string, byte[]> into,
		System.Collections.Generic.List<string> missing)
	{
		if (document == null || into == null) return;
		System.Collections.Generic.List<LotArchive.LotNodeRecord> records = document.Objects;
		for (int i = 0; i < records.Count; i++)
		{
			LotArchive.LotNodeRecord record = records[i];
			string assetId = ReadAssetId(record);
			if (assetId.Length == 0) continue;

			string entryPath = AssetEntryPath(assetId, IsBaseAssetRecord(record));
			if (into.ContainsKey(entryPath)) continue;

			string sourcePath = LotTextureCache.GetSourcePath(assetId);
			if (string.IsNullOrEmpty(sourcePath) || !Godot.FileAccess.FileExists(sourcePath))
			{
				if (missing != null) missing.Add(assetId);
				continue;
			}

			byte[] bytes = Godot.FileAccess.GetFileAsBytes(sourcePath);
			if (bytes == null || bytes.Length == 0)
			{
				if (missing != null) missing.Add(assetId);
				continue;
			}
			into[entryPath] = bytes;
		}
	}

	/// <summary>True when a record is base content — today, the base character.</summary>
	public static bool IsBaseAssetRecord(LotArchive.LotNodeRecord record)
	{
		return record != null && record.Kind == "capsule";
	}

	/// <summary>Archive path for an asset id. The extension is kept from the source file, because the
	/// loader sniffs content anyway and a readable name helps a human inspect the archive.</summary>
	public static string AssetEntryPath(string assetId, bool baseContent)
	{
		string prefix = baseContent ? LotArchive.SecretAssetsPrefix : LotArchive.AssetsPrefix;
		return prefix + SanitizeEntryName(assetId) + ExtensionFor(assetId);
	}

	/// <summary>
	/// Strips any directory part from an entry name, so a display name can never introduce a path
	/// separator and let an entry escape its folder inside the archive (a zip-slip guard). An empty or
	/// relative-only name falls back to a fixed placeholder rather than writing a nameless entry.
	/// </summary>
	private static string SanitizeEntryName(string name)
	{
		if (string.IsNullOrEmpty(name)) return "unnamed";
		string flattened = name.Replace('\\', '/');
		int slash = flattened.LastIndexOf('/');
		if (slash >= 0) flattened = flattened.Substring(slash + 1);
		if (flattened.Length == 0 || flattened == "." || flattened == "..") return "unnamed";
		return flattened;
	}

	/// <summary>The source file's extension, lowercased, or "" when there is none to recover.</summary>
	private static string ExtensionFor(string assetId)
	{
		string source = LotTextureCache.GetSourcePath(assetId);
		if (string.IsNullOrEmpty(source)) return "";
		int dot = source.LastIndexOf('.');
		int slash = source.LastIndexOfAny(_separators);
		if (dot <= slash || dot == source.Length - 1) return "";
		return source.Substring(dot).ToLowerInvariant();
	}

	private static readonly char[] _separators = { '/', '\\' };

	/// <summary>
	/// The image asset id a record references, whether it is a part's whole-part Texture (§2.3) or
	/// a decal's Image patch (§2.6). One lookup path, so a new image-bearing record kind adds a
	/// constant rather than a second collection routine. "" when the record has no image.
	/// </summary>
	private static string ReadAssetId(LotArchive.LotNodeRecord record)
	{
		string assetId = ReadStringProperty(record, TexturePropertyId);
		if (assetId.Length == 0) assetId = ReadStringProperty(record, DecalTexturePropertyId);
		return assetId;
	}

	/// <summary>Reads a string property, or "" when it is absent or of another type.</summary>
	private static string ReadStringProperty(LotArchive.LotNodeRecord record, string propertyId)
	{
		if (record == null || record.Properties == null || !record.Properties.ContainsKey(propertyId)) return "";
		Variant value = record.Properties[propertyId];
		return value.VariantType == Variant.Type.String ? (value.AsString() ?? "") : "";
	}



	/// <summary>
	/// The manifest's <c>entryScript</c>: the archive path of the lot-root script (a script placeholder
	/// that is a direct child of the lot root), or "" when the lot has none. A host boots this (§8.1).
	/// </summary>
	public static string ResolveEntryScript(Node lotRoot)
	{
		if (lotRoot == null) return "";
		int count = lotRoot.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			LotScriptNode script = lotRoot.GetChild(i) as LotScriptNode;
			if (script == null) continue;
			string fileName = string.IsNullOrEmpty(script.DisplayName) ? script.Name.ToString() : script.DisplayName;
			return ScriptEntryPath(fileName, IsBaseScript(fileName));
		}
		return "";
	}
}
