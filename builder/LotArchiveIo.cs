using System;
using Godot;

/// <summary>
/// Writes a `.lot` archive to disk (milestone 4.1). Rides Godot's <see cref="ZipPacker"/> only; the
/// entry layout and the JSON encoding are <see cref="LotArchive"/>'s business, so this type is a thin
/// byte sink and nothing here decides what a lot is.
///
/// There is exactly one writer, and §4.1's autosave will call <see cref="Write"/> with a different
/// destination path rather than a second code path — so autosave can never emit a file the reader
/// rejects. An existing file is overwritten: saving a lot is replace-in-place, not append.
/// </summary>
public static class LotArchiveWriter
{
	/// <summary>
	/// Writes a complete archive to <paramref name="archivePath"/>. <paramref name="scripts"/> maps an
	/// archive-relative script path (e.g. "scripts/Character.lua") to its source text;
	/// <paramref name="assets"/> maps an archive-relative asset path (e.g. "assets/logo.png") to its
	/// raw bytes. Both may be null.
	///
	/// Returns false (with a readable <paramref name="error"/>) when the destination cannot be
	/// created, so the caller can surface the reason rather than a bare Godot error code.
	/// </summary>
	public static bool Write(string archivePath, LotArchive.LotManifest manifest, LotArchive.LotDocument document,
		System.Collections.Generic.Dictionary<string, string> scripts,
		System.Collections.Generic.Dictionary<string, byte[]> assets,
		out string error)
	{
		error = "";
		if (string.IsNullOrEmpty(archivePath))
		{
			error = "no destination path given";
			return false;
		}
		if (manifest == null) manifest = new LotArchive.LotManifest();
		if (document == null) document = new LotArchive.LotDocument();

		ZipPacker packer = new ZipPacker();
		Error err = packer.Open(archivePath, ZipPacker.ZipAppend.Create);
		if (err != Error.Ok)
		{
			error = "could not create lot archive " + archivePath + " (" + err + ")";
			packer.Dispose();
			return false;
		}

		try
		{
			// manifest.json first so a host can read the ID card without the rest of the archive.
			if (!WriteText(packer, LotArchive.ManifestEntry, Json.Stringify(manifest.ToJson(), "  "), out error)) return false;
			if (!WriteText(packer, LotArchive.LotEntry, Json.Stringify(document.ToJson(), "  "), out error)) return false;

			if (scripts != null)
			{
				foreach (System.Collections.Generic.KeyValuePair<string, string> entry in scripts)
				{
					if (!WriteText(packer, entry.Key, entry.Value ?? "", out error)) return false;
				}
			}
			if (assets != null)
			{
				foreach (System.Collections.Generic.KeyValuePair<string, byte[]> entry in assets)
				{
					if (!WriteBytes(packer, entry.Key, entry.Value ?? new byte[0], out error)) return false;
				}
			}
		}
		finally
		{
			Error closeErr = packer.Close();
			packer.Dispose();
			if (closeErr != Error.Ok && error.Length == 0)
				error = "could not finalise lot archive " + archivePath + " (" + closeErr + ")";
		}

		return error.Length == 0;
	}

	private static bool WriteText(ZipPacker packer, string entryPath, string text, out string error)
	{
		error = "";
		byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
		return WriteBytes(packer, entryPath, bytes, out error);
	}

	private static bool WriteBytes(ZipPacker packer, string entryPath, byte[] bytes, out string error)
	{
		error = "";
		if (string.IsNullOrEmpty(entryPath))
		{
			error = "empty archive entry path";
			return false;
		}

		Error err = packer.StartFile(entryPath);
		if (err != Error.Ok)
		{
			error = "could not start archive entry " + entryPath + " (" + err + ")";
			return false;
		}

		err = packer.WriteFile(bytes);
		if (err != Error.Ok)
		{
			error = "could not write archive entry " + entryPath + " (" + err + ")";
			return false;
		}

		err = packer.CloseFile();
		if (err != Error.Ok)
		{
			error = "could not close archive entry " + entryPath + " (" + err + ")";
			return false;
		}
		return true;
	}
}

/// <summary>
/// Reads a `.lot` archive's manifest and payload back (milestone 4.1). All byte access goes through
/// <see cref="LotVfs"/>; this type only turns the archive's two JSON entries into the plain records
/// <see cref="LotArchive"/> defines.
///
/// A rejected version is reported with a readable reason and no payload is returned — the version
/// gate exists so an unopenable lot fails clearly instead of half-loading.
/// </summary>
public static class LotArchiveReader
{
	/// <summary>
	/// Opens <paramref name="archivePath"/>, checks its format version, and decodes the manifest and
	/// payload. Returns false (with a readable <paramref name="error"/>) when the file is missing, is
	/// not a zip, is a newer format than this build supports, or has an unreadable payload.
	/// </summary>
	public static bool Read(string archivePath, out LotArchive.LotManifest manifest, out LotArchive.LotDocument document, out string error)
	{
		manifest = null;
		document = null;
		error = "";

		LotVfs vfs = new LotVfs();
		try
		{
			if (!vfs.Open(archivePath, out error)) return false;

			string manifestText = vfs.ReadEntryText(LotArchive.ManifestEntry);
			if (manifestText == null)
			{
				error = "lot archive " + archivePath + " has no " + LotArchive.ManifestEntry;
				return false;
			}

			Variant parsedManifest = Json.ParseString(manifestText);
			if (parsedManifest.VariantType != Variant.Type.Dictionary)
			{
				error = "lot archive " + archivePath + " has an unreadable " + LotArchive.ManifestEntry;
				return false;
			}

			LotArchive.LotManifest read = LotArchive.LotManifest.FromJson(parsedManifest);
			LotArchive.VersionStatus status = LotArchive.LotManifest.CheckVersion(read.FormatVersion);
			if (status != LotArchive.VersionStatus.Ok)
			{
				error = LotArchive.LotManifest.DescribeVersion(read.FormatVersion);
				return false;
			}
			manifest = read;

			string lotText = vfs.ReadEntryText(LotArchive.LotEntry);
			if (lotText == null)
			{
				error = "lot archive " + archivePath + " has no " + LotArchive.LotEntry;
				return false;
			}
			document = LotArchive.LotDocument.FromJson(Json.ParseString(lotText));
			return true;
		}
		finally
		{
			vfs.Dispose();
		}
	}
}
