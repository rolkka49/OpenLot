using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// The single read path for a `.lot` archive's contents (milestone 4.1). It wraps Godot's
/// <see cref="ZipReader"/> so entry bytes are handed straight out of the open archive — nothing is
/// ever extracted to disk, which is what keeps a lot a single portable file and leaves no temporary
/// copies behind.
///
/// Scope: this type owns byte access and nothing else. It does not parse JSON, build nodes, or decide
/// what an entry means — <see cref="LotArchive"/> owns the format, and a later loader owns rebuilding
/// the scene. Every asset consumer (the §2.3 texture path today; §6.2 models and §7.2 audio later)
/// reads through here, so there is exactly one loader and one disposal rule.
///
/// Layering (§1.1): Godot-infrastructure-facing code. The methods are narrow and verb-first, and Lua
/// never touches this type directly — a script asks a bound API method, which asks the VFS. The
/// archive's index is read once at <see cref="Open"/>; entry bytes are read on demand.
/// </summary>
public sealed class LotVfs : IDisposable
{
	private ZipReader _reader;
	private string _path = "";
	private List<string> _entries;

	/// <summary>Archive path this VFS reads, or "" when nothing is open.</summary>
	public string Path { get { return _path; } }

	/// <summary>True while an archive is open and usable.</summary>
	public bool IsOpen { get { return _reader != null; } }

	/// <summary>Every entry path in the archive, in archive order. Empty when nothing is open.</summary>
	public IReadOnlyList<string> Entries
	{
		get { return _entries ?? (IReadOnlyList<string>)_emptyEntries; }
	}

	private static readonly string[] _emptyEntries = new string[0];

	/// <summary>
	/// Opens <paramref name="archivePath"/> and caches its entry index. Returns false (with an
	/// <paramref name="error"/> message) when the file is missing or is not a readable zip, so a
	/// caller can report a readable reason instead of surfacing a Godot error code.
	/// </summary>
	public bool Open(string archivePath, out string error)
	{
		error = "";
		Close();
		if (string.IsNullOrEmpty(archivePath))
		{
			error = "no lot path given";
			return false;
		}
		if (!Godot.FileAccess.FileExists(archivePath))
		{
			error = "no lot file at " + archivePath;
			return false;
		}

		ZipReader reader = new ZipReader();
		Error err = reader.Open(archivePath);
		if (err != Error.Ok)
		{
			error = "could not open lot archive " + archivePath + " (" + err + ")";
			reader.Dispose();
			return false;
		}

		_reader = reader;
		_path = archivePath;
		// Godot's packer writes a directory entry for every folder it creates, and those names end with
		// '/'. A directory entry is not something a caller can read, so the index keeps files only:
		// every consumer that iterates Entries gets exactly the entries ReadEntry can return.
		_entries = new List<string>();
		foreach (string entry in reader.GetFiles())
		{
			if (!string.IsNullOrEmpty(entry) && !entry.EndsWith("/", System.StringComparison.Ordinal))
				_entries.Add(entry);
		}
		return true;
	}

	/// <summary>Closes the archive and frees the reader. Safe to call twice.</summary>
	public void Close()
	{
		if (_reader != null)
		{
			_reader.Close();
			_reader.Dispose();
			_reader = null;
		}
		_path = "";
		_entries = null;
	}

	public void Dispose()
	{
		Close();
	}

	/// <summary>Whether <paramref name="entryPath"/> exists inside the open archive.</summary>
	public bool HasEntry(string entryPath)
	{
		if (_reader == null || string.IsNullOrEmpty(entryPath)) return false;
		return _reader.FileExists(entryPath);
	}

	/// <summary>
	/// Reads one entry's raw bytes, or null when it is absent. The bytes are handed straight to a
	/// Godot buffer loader by the caller (<c>Image.Load*FromBuffer</c> today), which is the whole
	/// point of streaming out of the archive instead of extracting first.
	/// </summary>
	public byte[] ReadEntry(string entryPath)
	{
		if (_reader == null || string.IsNullOrEmpty(entryPath)) return null;
		if (!_reader.FileExists(entryPath)) return null;
		return _reader.ReadFile(entryPath);
	}

	/// <summary>Reads one entry as UTF-8 text, or null when it is absent or not valid text.</summary>
	public string ReadEntryText(string entryPath)
	{
		byte[] bytes = ReadEntry(entryPath);
		if (bytes == null) return null;
		return System.Text.Encoding.UTF8.GetString(bytes);
	}
}
