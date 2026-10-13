using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// Finds the lots this device can open — the source for <c>ls</c>, <c>openlot list</c> and the
/// server-list TUI's rows. Two folders only, because nothing else exists yet: the user's own lots
/// (<c>user://lots</c>) and the sample lots that ship with the client (<c>res://samples</c>). LAN
/// discovery (milestone 10.2 Tier A) and provider listings (§15) are the real remote sources and
/// land with their milestones.
///
/// Pure in the way that matters for tests: both folders are parameters, so a suite points them at
/// scratch directories instead of the real ones.
/// </summary>
public static class LotListing
{
	public const string UserLotsDir = "user://lots";
	public const string SamplesDir = "res://samples";

	/// <summary>One listable lot: its display name, the path a loader takes, and where it came from.</summary>
	public struct LotEntry
	{
		public string Name;      // file name without the .lot extension
		public string Path;      // the path `openlot`/File → Open Lot… hands to the loader
		public string Source;    // "local" (user://lots) or "sample" (res://samples)
		public long SizeBytes;
	}

	/// <summary>
	/// Collects both folders, name-ascending (case-insensitively; path breaks a tie) so listing
	/// order is deterministic — the same guarantee the command registry gives its own output.
	/// </summary>
	public static List<LotEntry> Collect(string userLotsPath, string samplesPath)
	{
		List<LotEntry> entries = new List<LotEntry>();
		CollectFrom(userLotsPath, "local", entries);
		CollectFrom(samplesPath, "sample", entries);
		entries.Sort((a, b) =>
		{
			int byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
			return byName != 0 ? byName : string.CompareOrdinal(a.Path, b.Path);
		});
		return entries;
	}

	private static void CollectFrom(string directory, string source, List<LotEntry> into)
	{
		if (string.IsNullOrEmpty(directory)) return;

		using DirAccess access = DirAccess.Open(directory);
		if (access == null) return; // a folder that does not exist yet is simply empty

		access.ListDirBegin();
		while (true)
		{
			string name = access.GetNext();
			if (string.IsNullOrEmpty(name)) break;
			if (access.CurrentIsDir()) continue;
			if (!name.EndsWith(".lot", StringComparison.OrdinalIgnoreCase)) continue;

			string path = directory.TrimEnd('/') + "/" + name;
			into.Add(new LotEntry
			{
				Name = name.Substring(0, name.Length - 4),
				Path = path,
				Source = source,
				SizeBytes = SizeOf(path)
			});
		}
		access.ListDirEnd();
	}

	private static long SizeOf(string path)
	{
		using FileAccess file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
		return file != null ? (long)file.GetLength() : 0L;
	}
}
