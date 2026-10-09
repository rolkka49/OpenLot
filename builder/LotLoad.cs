using Godot;

/// <summary>
/// The single load pipeline (milestone 4.2). Phase 1 decodes an archive and validates it <b>without
/// touching the scene</b>; phase 2 replaces the lot only once phase 1 succeeded. That split is the whole
/// point: a newer-than-this-build file, a missing payload, or a corrupt zip is reported and the
/// creator's current work is left exactly as it was — the same "reject cleanly, never half-load" rule the
/// version gate already enforces, extended to cover mutating the world.
/// </summary>
public static class LotLoad
{
	/// <summary>
	/// Loads <paramref name="archivePath"/> into <paramref name="scene"/>, handing back the manifest
	/// (the caller applies lot identity) and a report of anything that could not be restored.
	/// Returns false with a readable <paramref name="error"/> and leaves the scene untouched.
	/// </summary>
	public static bool Load(BuilderScene scene, string archivePath, out LotArchive.LotManifest manifest,
		out LotSceneLoad.LoadReport report, out string error)
	{
		manifest = null;
		report = null;
		error = "";

		if (scene == null)
		{
			error = "there is no lot scene to load into";
			return false;
		}

		// Phase 1: decode and validate everything first. Nothing below this point has changed the scene,
		// so any failure can simply return.
		if (!LotArchiveReader.Read(archivePath, out manifest, out LotArchive.LotDocument document, out error))
			return false;

		LotVfs vfs = new LotVfs();
		try
		{
			if (!vfs.Open(archivePath, out error)) return false;

			// A manifest naming an entry script the archive does not carry is a broken lot, not a
			// partially loadable one — a host (§8.1) would boot nothing.
			if (!string.IsNullOrEmpty(manifest.EntryScript) && !vfs.HasEntry(manifest.EntryScript))
			{
				error = "this lot's entry script (" + manifest.EntryScript + ") is missing from the archive";
				return false;
			}

			// Phase 2: replace the lot.
			report = scene.ReplaceLotFromArchive(document, vfs);
			return true;
		}
		finally
		{
			vfs.Dispose();
		}
	}
}
