using System.Collections.Generic;
using Godot;

/// <summary>
/// Shared runtime image registry: maps an opaque asset id string to a loaded GPU texture, so
/// creator-facing properties and OpenLot's own UI chrome never hand a Godot object across a
/// boundary and can persist a plain string instead (milestone 2.3, feeding §5.2 / §12.6).
///
/// This is the single image-loading path: the wallpaper (UI chrome) and the part Texture property
/// both resolve their image here, and the post-MLP asset browser (§12.6) is expected to populate
/// this same table rather than introduce a second loader.
///
/// Asset ids are opaque and deterministic — a hash of the source path, so a lot saved with a
/// texture reference resolves to the same id when it is loaded again (§8.1). Callers must never
/// parse an id; they pass it back verbatim.
///
/// Memory discipline: entries are reference counted and a texture is disposed as soon as its last
/// holder releases it. One limit is documented rather than hidden: a texture handed to ImGui
/// cannot be unregistered (the dear-imgui addon exposes no unregister_texture call), so the
/// native side keeps it alive for the rest of the session.
/// </summary>
public static class LotTextureCache
{
	private sealed class Entry
	{
		public Texture2D Texture;
		public string SourcePath;
		public int Refs;
		/// <summary>ImGui-side registration, filled on first use. Negative means "not registered yet"
		/// (0 cannot be the sentinel: a registration id of 0 is legal).</summary>
		public long ImGuiId = -1;
	}

	private static readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
	// Sorted id list, rebuilt on demand so the Inspector's Texture dropdown has a stable order
	// without paying a sort per frame.
	private static readonly List<string> _sortedIds = new List<string>();
	// Array view of the same ids, for the ImGui combo API. Rebuilt with _sortedIds, so listing the
	// options every frame allocates nothing.
	private static string[] _idsArray = System.Array.Empty<string>();
	private static bool _sortedIdsDirty = true;

	/// <summary>Every registered asset id in stable (ordinal) order. Reused, so do not mutate it.</summary>
	public static IReadOnlyList<string> Ids
	{
		get
		{
			if (_sortedIdsDirty)
			{
				_sortedIds.Clear();
				foreach (KeyValuePair<string, Entry> pair in _entries) _sortedIds.Add(pair.Key);
				_sortedIds.Sort(System.StringComparer.Ordinal);
				_idsArray = _sortedIds.ToArray();
				_sortedIdsDirty = false;
			}
			return _sortedIds;
		}
	}

	/// <summary>
	/// The ids as a flat array for ImGui's combo/list APIs. Backed by the cache's own buffer, so do
	/// not mutate it; it is rebuilt only when the table changes.
	/// </summary>
	public static string[] IdsAsArray
	{
		get
		{
			// Touching Ids first is what guarantees the array is current.
			IReadOnlyList<string> ids = Ids;
			return _idsArray.Length == ids.Count ? _idsArray : _sortedIds.ToArray();
		}
	}

	/// <summary>Number of registered assets (for tests and diagnostics).</summary>
	public static int Count { get { return _entries.Count; } }

	public static bool Contains(string assetId)
	{
		return !string.IsNullOrEmpty(assetId) && _entries.ContainsKey(assetId);
	}

	/// <summary>The source file an asset was loaded from, or "" when unknown.</summary>
	public static string GetSourcePath(string assetId)
	{
		Entry entry;
		if (string.IsNullOrEmpty(assetId) || !_entries.TryGetValue(assetId, out entry)) return "";
		return entry.SourcePath;
	}

	/// <summary>The texture behind an asset id, or null when the id is unknown.</summary>
	public static Texture2D Get(string assetId)
	{
		Entry entry;
		if (string.IsNullOrEmpty(assetId) || !_entries.TryGetValue(assetId, out entry)) return null;
		return entry.Texture;
	}

	/// <summary>
	/// Loads an image file and registers it, holding one reference on the caller's behalf.
	/// Re-importing the same path returns the existing id and hands over exactly one more
	/// reference. Returns "" and fills <paramref name="error"/> when the file cannot be used, so a
	/// bad file never registers a half-built entry.
	///
	/// The bytes are read here and decoded by sniffing the file's magic number rather than by
	/// handing the path to <see cref="Image.LoadFromFile"/>, for two reasons:
	///   * The loader Godot picks is chosen from the file EXTENSION. A misleading name (a WebP or
	///     JPEG saved as ".png" — common with downloaded images) made Godot's PNG driver reject the
	///     file and print three engine ERROR blocks to the console before failing. Sniffing the
	///     actual content means such a file now simply loads.
	///   * Content that is not an image at all is rejected here with one readable message, without
	///     ever invoking an engine loader, so a wrong pick in the browser is a quiet failure.
	/// </summary>
	public static string ImportFromFile(string path, out string error)
	{
		error = "";
		if (string.IsNullOrEmpty(path))
		{
			error = "no file path was given";
			return "";
		}

		// Reuse an identical import (same source file) instead of decoding the bytes twice.
		string existing = FindBySourcePath(path);
		if (existing.Length > 0)
		{
			Acquire(existing);
			return existing;
		}

		Image image = DecodeImageFile(path, out error);
		if (image == null) return "";

		// Normalize to RGBA8: the rest of the pipeline expects a known layout, and a decoded
		// but exotic format would fail at GPU upload time instead of here.
		if (image.GetFormat() != Image.Format.Rgba8) image.Convert(Image.Format.Rgba8);

		ImageTexture texture = ImageTexture.CreateFromImage(image);
		image.Dispose();
		if (texture == null)
		{
			error = "the image could not be uploaded to the GPU";
			return "";
		}

		string assetId = MakeAssetId(path);
		_entries[assetId] = new Entry { Texture = texture, SourcePath = path, Refs = 1 };
		_sortedIdsDirty = true;
		return assetId;
	}

	/// <summary>
	/// Registers image bytes under a caller-supplied asset id — the archive load path (§4.2). Returns
	/// false and fills <paramref name="error"/> when the bytes are not a usable image.
	///
	/// The id is supplied rather than derived because a lot's records store the id their texture was
	/// saved under, and <see cref="MakeAssetId"/> hashes the *source path*: on another machine that path
	/// does not exist, so hashing one would mint a different id and leave every saved texture dangling.
	/// Registering the archived bytes under the recorded id is what makes a loaded lot's
	/// <c>texture</c> property resolve, and it keeps a save→load→save round-trip stable.
	///
	/// <paramref name="sourceLabel"/> is the archive entry the bytes came from, kept only so a human
	/// (and the diagnostics) can see where an image originated; it is never read back as a file.
	/// </summary>
	public static bool ImportFromBuffer(string assetId, byte[] bytes, string sourceLabel, out string error)
	{
		error = "";
		if (string.IsNullOrEmpty(assetId))
		{
			error = "no asset id was given";
			return false;
		}

		// Already registered (two records can reference one archived image): take another reference so
		// one entry serves both, instead of decoding and uploading the same bytes twice.
		if (_entries.ContainsKey(assetId))
		{
			Acquire(assetId);
			return true;
		}

		Image image = DecodeBytes(bytes, sourceLabel, out error);
		if (image == null) return false;

		if (image.GetFormat() != Image.Format.Rgba8) image.Convert(Image.Format.Rgba8);
		ImageTexture texture = ImageTexture.CreateFromImage(image);
		image.Dispose();
		if (texture == null)
		{
			error = "the image could not be uploaded to the GPU: " + sourceLabel;
			return false;
		}

		_entries[assetId] = new Entry { Texture = texture, SourcePath = sourceLabel ?? "", Refs = 1 };
		_sortedIdsDirty = true;
		return true;
	}

	/// <summary>Adds a reference to an existing asset. Returns the new count, or 0 when unknown.</summary>
	public static int Acquire(string assetId)
	{
		Entry entry;
		if (string.IsNullOrEmpty(assetId) || !_entries.TryGetValue(assetId, out entry)) return 0;
		entry.Refs++;
		return entry.Refs;
	}

	/// <summary>
	/// Drops one reference. The texture is disposed and the entry removed when the count reaches
	/// zero. Returns true when the asset is now free (or was already gone), false while it is
	/// still held.
	/// </summary>
	public static bool Release(string assetId)
	{
		Entry entry;
		if (string.IsNullOrEmpty(assetId) || !_entries.TryGetValue(assetId, out entry)) return true;

		entry.Refs--;
		if (entry.Refs > 0) return false;

		_entries.Remove(assetId);
		_sortedIdsDirty = true;
		if (entry.Texture != null && GodotObject.IsInstanceValid(entry.Texture)) entry.Texture.Dispose();
		return true;
	}

	/// <summary>
	/// ImGui texture id for an asset, registered on first request and cached afterwards. Must be
	/// called from inside the layout pass (ImGui requires registrations during a frame).
	/// Returns -1 for an unknown asset.
	/// </summary>
	public static long GetImGuiId(string assetId)
	{
		Entry entry;
		if (string.IsNullOrEmpty(assetId) || !_entries.TryGetValue(assetId, out entry)) return -1;
		if (entry.ImGuiId < 0) entry.ImGuiId = ImGui.RegisterTexture(entry.Texture);
		return entry.ImGuiId;
	}

	/// <summary>Disposes every texture and forgets every asset. Scene teardown / tests only.</summary>
	public static void ClearAll()
	{
		foreach (KeyValuePair<string, Entry> pair in _entries)
		{
			Entry entry = pair.Value;
			if (entry.Texture != null && GodotObject.IsInstanceValid(entry.Texture)) entry.Texture.Dispose();
		}
		_entries.Clear();
		_sortedIds.Clear();
		_sortedIdsDirty = true;
	}

	private static string FindBySourcePath(string path)
	{
		foreach (KeyValuePair<string, Entry> pair in _entries)
		{
			if (pair.Value.SourcePath == path) return pair.Key;
		}
		return "";
	}

	/// <summary>Image formats this loader recognises by content.</summary>
	private enum ImageKind
	{
		Unknown,
		Png,
		Jpeg,
		Webp,
		Bmp,
		Tga
	}

	/// <summary>
	/// Largest file this loader will pull into memory. The decoded pixels dominate a normal image's
	/// footprint anyway, but a cap keeps a pathological pick (a mis-renamed archive, a multi-GB
	/// file) from being read into RAM before the format check can reject it.
	/// </summary>
	private const int MaxImportBytes = 64 * 1024 * 1024;

	/// <summary>
	/// Reads the file and decodes it with the loader matching its actual content. Returns null and
	/// fills <paramref name="error"/> with a readable reason on every failure path.
	///
	/// Content that matches no known signature is rejected before any engine loader runs, so a wrong
	/// pick cannot print a wall of engine errors. A file whose signature matches but whose data is
	/// truncated still goes through Godot, which reports that itself.
	/// </summary>
	private static Image DecodeImageFile(string path, out string error)
	{
		error = "";
		if (!Godot.FileAccess.FileExists(path))
		{
			error = "there is no file at " + path;
			return null;
		}

		byte[] bytes = Godot.FileAccess.GetFileAsBytes(path);
		if (bytes == null || bytes.Length == 0)
		{
			error = "the file is empty or could not be read: " + path;
			return null;
		}
		return DecodeBytes(bytes, path, out error);
	}

	/// <summary>
	/// Decodes image bytes with the loader matching their actual content, without touching the
	/// filesystem. Split out of <see cref="DecodeImageFile"/> so the archive loader can decode the
	/// bytes it streams out of a `.lot` (§4.2) through this one sniff-and-dispatch path instead of
	/// growing a second image loader. <paramref name="label"/> is what failure messages name: a file
	/// path for a normal import, an archive entry path for a loaded one.
	/// </summary>
	private static Image DecodeBytes(byte[] bytes, string label, out string error)
	{
		error = "";
		if (bytes == null || bytes.Length == 0)
		{
			error = "the image is empty or could not be read: " + label;
			return null;
		}
		if (bytes.Length > MaxImportBytes)
		{
			error = "the image is larger than " + (MaxImportBytes / (1024 * 1024)) + " MB: " + label;
			return null;
		}

		ImageKind kind = SniffImageKind(bytes);
		if (kind == ImageKind.Unknown)
		{
			error = "the content is not a recognised image (expected PNG, JPEG, WebP, BMP or TGA): " + label;
			return null;
		}

		Image image = DecodeByKind(kind, bytes);
		if (image == null)
		{
			error = "the content looks like " + KindName(kind) + " but could not be decoded (truncated or corrupt): " + label;
			return null;
		}
		return image;
	}

	/// <summary>
	/// Identifies the image format from its leading bytes — the file extension is deliberately not
	/// consulted, because extensions are exactly what mislabels downloaded images.
	/// </summary>
	private static ImageKind SniffImageKind(byte[] bytes)
	{
		// PNG: the 8-byte signature from the PNG specification.
		if (bytes.Length >= 8
			&& bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
			&& bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A) return ImageKind.Png;

		// JPEG: the start-of-image marker (any following segment marker makes it 0xFFD8FF).
		if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ImageKind.Jpeg;

		// WebP: a RIFF container whose form type is "WEBP".
		if (bytes.Length >= 12
			&& bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
			&& bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50) return ImageKind.Webp;

		// BMP: the "BM" file-type field.
		if (bytes.Length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x4D) return ImageKind.Bmp;

		// TGA has no leading magic; its reliable marker is the 18-byte footer.
		if (HasTgaFooter(bytes)) return ImageKind.Tga;

		return ImageKind.Unknown;
	}

	/// <summary>True when the file ends with the TGA v2 footer ("TRUEVISION-XFILE." + NUL).</summary>
	private static bool HasTgaFooter(byte[] bytes)
	{
		const string signature = "TRUEVISION-XFILE";
		// 18-byte footer, and an 18-byte header must precede it for the file to be a TGA at all.
		if (bytes.Length < 36) return false;

		int start = bytes.Length - 18;
		for (int i = 0; i < signature.Length; i++)
		{
			if (bytes[start + i] != (byte)signature[i]) return false;
		}
		return bytes[start + 16] == (byte)'.' && bytes[start + 17] == 0;
	}

	/// <summary>
	/// Decodes the buffer with the loader for the sniffed format. These loaders are instance methods
	/// that fill the target image and return an <see cref="Error"/>, so a failure is reported as
	/// null rather than as a half-loaded image.
	/// </summary>
	private static Image DecodeByKind(ImageKind kind, byte[] bytes)
	{
		Image image = new Image();
		Error result;
		switch (kind)
		{
			case ImageKind.Png: result = image.LoadPngFromBuffer(bytes); break;
			case ImageKind.Jpeg: result = image.LoadJpgFromBuffer(bytes); break;
			case ImageKind.Webp: result = image.LoadWebpFromBuffer(bytes); break;
			case ImageKind.Bmp: result = image.LoadBmpFromBuffer(bytes); break;
			case ImageKind.Tga: result = image.LoadTgaFromBuffer(bytes); break;
			default:
				image.Dispose();
				return null;
		}

		if (result != Error.Ok || image.IsEmpty() || image.GetWidth() <= 0 || image.GetHeight() <= 0)
		{
			image.Dispose();
			return null;
		}
		return image;
	}

	private static string KindName(ImageKind kind)
	{
		switch (kind)
		{
			case ImageKind.Png: return "a PNG";
			case ImageKind.Jpeg: return "a JPEG";
			case ImageKind.Webp: return "a WebP";
			case ImageKind.Bmp: return "a BMP";
			case ImageKind.Tga: return "a TGA";
			default: return "an image";
		}
	}

	/// <summary>
	/// Deterministic opaque id for a source path: FNV-1a over the path, hex encoded. A hash
	/// collision with a different path (vanishingly unlikely, but not impossible) is resolved by
	/// suffixing a counter rather than silently aliasing two images together.
	/// </summary>
	private static string MakeAssetId(string path)
	{
		uint hash = Fnv1a(path);
		string candidate = "tex-" + hash.ToString("x8");
		int suffix = 1;
		while (_entries.TryGetValue(candidate, out Entry clash) && clash.SourcePath != path)
		{
			candidate = "tex-" + hash.ToString("x8") + "-" + suffix;
			suffix++;
		}
		return candidate;
	}

	/// <summary>FNV-1a (32-bit) over a string's UTF-16 code units. Small, original, no dependency.</summary>
	private static uint Fnv1a(string value)
	{
		uint hash = 2166136261u;
		for (int i = 0; i < value.Length; i++)
		{
			hash ^= (byte)value[i];
			hash *= 16777619u;
			hash ^= (byte)(value[i] >> 8);
			hash *= 16777619u;
		}
		return hash;
	}
}
