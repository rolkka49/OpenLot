using Godot;

/// <summary>
/// The single save pipeline (milestone 4.1): turns the live lot into a `.lot` on disk. Manual save
/// (§4.2) and autosave both call this, so an autosave can never emit a file a manual save would not.
///
/// The heavy lifting lives in three separate types on purpose — <see cref="LotSceneWalk"/> describes
/// the scene, <see cref="LotArchiveCollect"/> gathers the file content, <see cref="LotArchiveWriter"/>
/// writes the bytes. This type is only the glue that runs them in order and builds the manifest.
/// </summary>
public static class LotSave
{
	/// <summary>
	/// Builds the archive's ID card for a save. <paramref name="createdUnix"/> is passed in rather than
	/// stamped here, so re-saving a loaded lot keeps its original creation time — §4.2 supplies it from
	/// the manifest it loaded. 0 means "unknown, use now".
	/// </summary>
	public static LotArchive.LotManifest BuildManifest(Node3D lotRoot, string lotName, long createdUnix)
	{
		long now = (long)Time.GetUnixTimeFromSystem();
		return new LotArchive.LotManifest
		{
			Name = string.IsNullOrWhiteSpace(lotName) ? "Untitled Lot" : lotName,
			MaxPlayers = 8,
			CreatedUnix = createdUnix > 0 ? createdUnix : now,
			ModifiedUnix = now,
			EntryScript = LotArchiveCollect.ResolveEntryScript(lotRoot)
		};
	}

	/// <summary>
	/// Writes the lot — its two containers plus its lighting — to <paramref name="archivePath"/>.
	/// Anything whose file has gone (an unreadable script, a texture with no source left) is reported in
	/// <paramref name="missing"/> instead of being silently dropped, so a caller can tell the creator
	/// rather than shipping a lot that quietly lost content.
	/// </summary>
	public static bool Write(Node3D lotRoot, Node uiRoot, in LotEnvironmentSettings environment,
		string archivePath, LotArchive.LotManifest manifest,
		System.Collections.Generic.List<string> missing, out string error)
	{
		error = "";
		if (lotRoot == null)
		{
			error = "no lot root to save";
			return false;
		}

		LotArchive.LotDocument document = new LotArchive.LotDocument();
		// The node list is index-aligned with document.Objects, which is exactly the index space the
		// constraint records serialize against (§3.6).
		System.Collections.Generic.List<Node> capturedObjects = new System.Collections.Generic.List<Node>();
		LotSceneWalk.CaptureChildren(lotRoot, document.Objects, capturedObjects);
		LotSceneWalk.CaptureChildren(uiRoot, document.UiElements);
		document.Environment = environment;
		// Collision groups are session-wide (§3.5), not per-node, so they are read from their single
		// owner — the same way LotSceneWalk reads LotPropertyRegistry — rather than threaded through
		// this signature. A lot that never touched the matrix still writes the all-collide default.
		document.Collision = LotCollisionGroups.ToJson();
		// Constraints (§3.6) are lot-level too; the array is empty for a lot with no links, and the
		// document drops an empty block so lot.json stays lean.
		document.Constraints = LotConstraints.ToJson(capturedObjects);

		System.Collections.Generic.Dictionary<string, string> scripts = new System.Collections.Generic.Dictionary<string, string>();
		System.Collections.Generic.Dictionary<string, byte[]> assets = new System.Collections.Generic.Dictionary<string, byte[]>();
		LotArchiveCollect.Scripts(lotRoot, scripts, missing);
		LotArchiveCollect.Assets(document, assets, missing);

		return LotArchiveWriter.Write(archivePath, manifest, document, scripts, assets, out error);
	}

	/// <summary>
	/// The scene-level save: the same single pipeline, taking the live lot directly. Manual save (§4.2)
	/// and autosave both go through this, so the two can never diverge — an autosave can only ever be a
	/// file a manual save would have produced.
	/// </summary>
	public static bool WriteScene(BuilderScene scene, string archivePath, LotArchive.LotManifest manifest,
		System.Collections.Generic.List<string> missing, out string error)
	{
		error = "";
		if (scene == null)
		{
			error = "no lot scene to save";
			return false;
		}
		return Write(scene.LotRoot, scene.LotUIRoot, scene.Environment, archivePath, manifest, missing, out error);
	}
}
