using Godot;

/// <summary>
/// Rebuilds a live lot from a decoded `.lot` archive (milestone 4.2) — the mirror of
/// <see cref="LotSave"/>. Like the save pipeline it is scene-free in shape (it takes the two container
/// roots rather than a <see cref="BuilderScene"/>), so the whole restore path is testable against plain
/// fixtures; the caller owns the scene-level concerns (environment, handles, script runtime).
///
/// Restore runs in four phases, in this order, and the ordering is load-bearing:
///   1. nodes — parts and script placeholders, attached to the parent each record points at;
///   2. lot UI — flat elements under the UI root;
///   3. asset bytes — streamed out of the archive and registered in <see cref="LotTextureCache"/> under
///      the id the record already references;
///   4. properties — applied last, because a part's Texture property only accepts an id the cache knows.
///
/// The caller must have emptied the containers first: a restore adds a world, it does not merge one.
/// Anything the archive references but cannot supply is reported in <see cref="LoadReport"/> rather than
/// aborting the load, so one missing texture cannot cost the creator the rest of their lot.
/// </summary>
public static class LotSceneLoad
{
	/// <summary>What a restore did, and everything it could not do. A load that reports warnings still
	/// yielded a usable world — the warnings are what the caller shows the creator.</summary>
	public sealed class LoadReport
	{
		public readonly System.Collections.Generic.List<string> Warnings = new System.Collections.Generic.List<string>();
		public int PartsCreated;
		public int UiCreated;
		public int ScriptsCreated;
		public int AssetsLoaded;

		public void Warn(string message)
		{
			Warnings.Add(message);
		}
	}

	/// <summary>A part and the record it came from, held until phase 4 applies its properties.</summary>
	private sealed class PendingPart
	{
		public LotObject Node;
		public LotArchive.LotNodeRecord Record;
	}

	/// <summary>
	/// Restores <paramref name="document"/> into <paramref name="lotRoot"/> / <paramref name="uiRoot"/>,
	/// reading script text and asset bytes out of the open <paramref name="vfs"/>.
	///
	/// Returns false only for a caller error (missing root or document). Per-node and per-asset problems
	/// are collected in <paramref name="report"/> and the rest of the lot still loads.
	/// </summary>
	public static bool Restore(Node3D lotRoot, Node uiRoot, LotArchive.LotDocument document, LotVfs vfs,
		LoadReport report, System.Collections.Generic.List<Node> objectsByIndex = null)
	{
		if (lotRoot == null || report == null) return false;
		if (document == null) return false;

		System.Collections.Generic.List<PendingPart> parts = new System.Collections.Generic.List<PendingPart>();
		RestoreRecords(document.Objects, lotRoot, vfs, report, parts, true, objectsByIndex);
		if (uiRoot != null) RestoreRecords(document.UiElements, uiRoot, vfs, report, parts, false, null);
		LoadAssets(vfs, report);
		ApplyProperties(parts, report);
		return true;
	}

	/// <summary>
	/// Phase 1 and 2: creates each record's node and attaches it to the parent its
	/// <see cref="LotArchive.LotNodeRecord.ParentIndex"/> names, which is always an earlier record (the
	/// capture writes parents first).
	///
	/// A record whose kind this build does not understand is skipped, and — importantly — its *children*
	/// still load: the skipped index maps to the skipped node's own parent, so descendants re-home to the
	/// nearest loaded ancestor instead of vanishing. That mirrors the capture walk, which does the same
	/// for an unmodelled group.
	/// </summary>
	private static void RestoreRecords(System.Collections.Generic.List<LotArchive.LotNodeRecord> records,
		Node container, LotVfs vfs, LoadReport report,
		System.Collections.Generic.List<PendingPart> pendingParts, bool objectLayer,
		System.Collections.Generic.List<Node> createdByIndex)
	{
		if (records == null) return;

		// Index -> the node a later record's ParentIndex resolves to.
		System.Collections.Generic.List<Node> parents = new System.Collections.Generic.List<Node>(records.Count);

		for (int i = 0; i < records.Count; i++)
		{
			LotArchive.LotNodeRecord record = records[i];
			Node parent = ResolveParent(record.ParentIndex, parents, container);
			Node node = CreateNode(record, parent, vfs, report, pendingParts, objectLayer);
			if (node == null)
			{
				parents.Add(parent);
				// A skipped record keeps its slot (null) so the index-aligned list callers build
				// from it — the constraint load — can tell "index 3 was skipped" from "index 3 is
				// index 4's node".
				if (createdByIndex != null) createdByIndex.Add(null);
				continue;
			}
			parent.AddChild(node);
			parents.Add(node);
			if (createdByIndex != null) createdByIndex.Add(node);
		}
	}

	/// <summary>The node a record attaches to: its named parent record, or the container for a
	/// root-level record. An out-of-range index (only reachable from a hand-edited or corrupt file) falls
	/// back to the container rather than throwing.</summary>
	private static Node ResolveParent(int parentIndex, System.Collections.Generic.List<Node> parents, Node container)
	{
		if (parentIndex < 0 || parentIndex >= parents.Count) return container;
		Node parent = parents[parentIndex];
		return parent ?? container;
	}

	/// <summary>Builds one record's node, or null when the record cannot be honoured.</summary>
	private static Node CreateNode(LotArchive.LotNodeRecord record, Node parent, LotVfs vfs, LoadReport report,
		System.Collections.Generic.List<PendingPart> pendingParts, bool objectLayer)
	{
		string kind = record.Kind ?? "";

		if (kind == LotArchive.ScriptRecordKind) return CreateScript(record, vfs, report);

		if (!objectLayer) return CreateUiElement(record, report);

		if (!LotSceneWalk.TryParseLotKind(kind, out LotObjectKind partKind))
		{
			report.Warn("this lot contains a '" + kind + "' object that this build does not understand; "
				+ "it was skipped (its children were kept)");
			return null;
		}

		LotObject part = LotObject.Create(partKind, UniqueChildName(parent, record.Name), record.Color);
		part.Position = record.Position;
		part.RotationDegrees = record.RotationDegrees;
		part.Scale = record.Scale;
		pendingParts.Add(new PendingPart { Node = part, Record = record });
		report.PartsCreated++;
		return part;
	}

	/// <summary>
	/// Recreates a script placeholder from its record and writes its text back to the local scripts
	/// directory, so the script runtime — which reads scripts from disk — finds it exactly as it does for
	/// a lot that was never saved.
	///
	/// The archive wins over whatever is on disk: a lot ships its own code, and a stale local copy must
	/// not silently change what the lot does. Returns null (with a warning) when the archive does not
	/// actually carry the script, because a placeholder pointing at a file that does not exist is worse
	/// than no placeholder.
	/// </summary>
	private static Node CreateScript(LotArchive.LotNodeRecord record, LotVfs vfs, LoadReport report)
	{
		string fileName = ReadStringProperty(record, LotArchive.ScriptNameProperty);
		if (fileName.Length == 0)
		{
			report.Warn("a script entry in this lot has no file name; it was skipped");
			return null;
		}

		string entryPath = LotArchiveCollect.ScriptEntryPath(fileName, LotArchiveCollect.IsBaseScript(fileName));
		string localPath = WriteScriptFile(fileName, entryPath, vfs, report);
		if (localPath == null) return null;

		LotScriptNode node = new LotScriptNode();
		node.DisplayName = fileName;
		// Godot node names cannot contain dots; the readable ".lua" name lives on DisplayName, exactly
		// as the default lot script is named.
		node.Name = fileName.Replace('.', '_');
		node.ScriptPath = localPath;
		report.ScriptsCreated++;
		return node;
	}

	/// <summary>Writes a script's archive text to the local scripts directory, returning the local path
	/// or null when the archive does not carry it.</summary>
	private static string WriteScriptFile(string fileName, string entryPath, LotVfs vfs, LoadReport report)
	{
		string text = vfs.ReadEntryText(entryPath);
		if (text == null)
		{
			report.Warn("script '" + fileName + "' is recorded in this lot but missing from the archive ("
				+ entryPath + "); it was not restored");
			return null;
		}

		DirAccess.MakeDirRecursiveAbsolute(ScriptManager.ScriptsDir);
		string localPath = ScriptManager.ScriptsDir + "/" + fileName;
		Godot.FileAccess file = Godot.FileAccess.Open(localPath, Godot.FileAccess.ModeFlags.Write);
		if (file == null)
		{
			report.Warn("script '" + fileName + "' could not be written to " + localPath);
			return null;
		}
		file.StoreString(text);
		file.Dispose();
		return localPath;
	}

	/// <summary>Recreates one lot UI element from its record (a flat list under the UI root).</summary>
	private static Node CreateUiElement(LotArchive.LotNodeRecord record, LoadReport report)
	{
		if (!System.Enum.TryParse(record.Kind, out LotUIKind uiKind) || !System.Enum.IsDefined(typeof(LotUIKind), uiKind))
		{
			report.Warn("this lot contains a '" + record.Kind + "' UI element that this build does not understand; "
				+ "it was skipped");
			return null;
		}

		LotUIElement element = LotUIElement.Create(uiKind,
			new Vector2(record.Position.X, record.Position.Y),
			new Vector2(record.Scale.X, record.Scale.Y),
			ReadStringProperty(record, "label"));
		element.Color = record.Color;
		element.Visible = ReadBoolProperty(record, "visible", true);
		report.UiCreated++;
		return element;
	}

	/// <summary>
	/// A name no sibling already uses. Godot's own de-duplication would rename a clash to something
	/// opaque, and the hierarchy reads node names, so this keeps the restored tree legible.
	/// </summary>
	private static string UniqueChildName(Node parent, string desired)
	{
		string name = string.IsNullOrEmpty(desired) ? "Node" : desired.Replace('.', '_');
		if (parent == null || !parent.HasNode(name)) return name;

		int index = 2;
		while (parent.HasNode(name + "_" + index)) index++;
		return name + "_" + index;
	}

	/// <summary>Reads a string property, or "" when it is absent or of another type.</summary>
	private static string ReadStringProperty(LotArchive.LotNodeRecord record, string key)
	{
		Variant value;
		if (record == null || record.Properties == null || !record.Properties.TryGetValue(key, out value)) return "";
		return value.VariantType == Variant.Type.String ? (value.AsString() ?? "") : "";
	}

	/// <summary>Reads a bool property, or <paramref name="fallback"/> when it is absent or of another type.</summary>
	private static bool ReadBoolProperty(LotArchive.LotNodeRecord record, string key, bool fallback)
	{
		Variant value;
		if (record == null || record.Properties == null || !record.Properties.TryGetValue(key, out value)) return fallback;
		return value.VariantType == Variant.Type.Bool ? value.AsBool() : fallback;
	}

	/// <summary>
	/// Phase 3: streams every archived image into <see cref="LotTextureCache"/> under the id the entries
	/// are named for — which is the id the records reference, so no remapping table is needed (see
	/// <see cref="LotTextureCache.ImportFromBuffer"/> for why the id is preserved rather than re-derived).
	///
	/// An image that cannot be read or decoded is reported and skipped; the rest still load.
	/// </summary>
	private static void LoadAssets(LotVfs vfs, LoadReport report)
	{
		if (vfs == null || !vfs.IsOpen) return;

		System.Collections.Generic.IReadOnlyList<string> entries = vfs.Entries;
		for (int i = 0; i < entries.Count; i++)
		{
			string entry = entries[i];
			if (!TryGetAssetId(entry, out string assetId)) continue;

			byte[] bytes = vfs.ReadEntry(entry);
			if (bytes == null || bytes.Length == 0)
			{
				report.Warn("image '" + entry + "' could not be read from the archive");
				continue;
			}
			if (!LotTextureCache.ImportFromBuffer(assetId, bytes, entry, out string error))
			{
				report.Warn(error);
				continue;
			}
			report.AssetsLoaded++;
		}
	}

	/// <summary>
	/// The asset id an archive entry carries, or false when it is not an asset entry. The id is the entry's
	/// file name without its extension — exactly how the collection pass named it — so the id a record
	/// stores is the id this recovers.
	/// </summary>
	private static bool TryGetAssetId(string entryPath, out string assetId)
	{
		assetId = "";
		if (string.IsNullOrEmpty(entryPath)) return false;
		if (!entryPath.StartsWith(LotArchive.AssetsPrefix, System.StringComparison.Ordinal)
			&& !entryPath.StartsWith(LotArchive.SecretAssetsPrefix, System.StringComparison.Ordinal)) return false;

		string fileName = entryPath.Substring(entryPath.LastIndexOf('/') + 1);
		int dot = fileName.LastIndexOf('.');
		assetId = dot > 0 ? fileName.Substring(0, dot) : fileName;
		return assetId.Length > 0;
	}

	/// <summary>
	/// Phase 4: applies each part's recorded properties. Runs last on purpose — a part's Texture property
	/// only accepts an id the cache already knows, which is only true once phase 3 has loaded the assets.
	///
	/// A property this build does not declare (a plugin's, or one a newer OpenLot wrote) is reported and
	/// skipped rather than guessed at.
	/// </summary>
	private static void ApplyProperties(System.Collections.Generic.List<PendingPart> parts, LoadReport report)
	{
		for (int i = 0; i < parts.Count; i++)
		{
			PendingPart pending = parts[i];
			ApplyRecordProperties(pending.Node, pending.Record, report);

			// A texture the archive never carried leaves the property unset. Say so, instead of letting
			// the part load silently untextured with no explanation. A decal's image (milestone 2.6)
			// is checked through the same rule, from its own property id.
			WarnIfImageMissing(pending.Record, LotArchiveCollect.TexturePropertyId, report);
			WarnIfImageMissing(pending.Record, LotArchiveCollect.DecalTexturePropertyId, report);
		}
	}

	/// <summary>Reports an image a record references but the archive did not supply, so a missing
	/// texture is explained rather than silently absent.</summary>
	private static void WarnIfImageMissing(LotArchive.LotNodeRecord record, string propertyId, LoadReport report)
	{
		string textureId = ReadStringProperty(record, propertyId);
		if (textureId.Length == 0 || LotTextureCache.Contains(textureId)) return;
		report.Warn("part '" + record.Name + "' references image '" + textureId
			+ "', which was not found in the archive");
	}

	private static void ApplyRecordProperties(Node node, LotArchive.LotNodeRecord record, LoadReport report)
	{
		if (record.Properties == null) return;
		foreach (System.Collections.Generic.KeyValuePair<Variant, Variant> pair in record.Properties)
		{
			if (pair.Key.VariantType != Variant.Type.String) continue;
			string id = pair.Key.AsString();
			LotPropertyDescriptor descriptor = LotPropertyRegistry.Find(id);
			if (descriptor == null)
			{
				report.Warn("property '" + id + "' is not declared in this build; it was skipped");
				continue;
			}
			ApplyOneProperty(node, descriptor, pair.Value, report);
		}
	}

	private static void ApplyOneProperty(Node node, LotPropertyDescriptor descriptor, Variant value, LoadReport report)
	{
		if (descriptor.Kind == LotPropertyKind.Bool && descriptor.SetBool != null)
		{
			if (value.VariantType != Variant.Type.Bool)
			{
				report.Warn("property '" + descriptor.Id + "' expected a true/false value; it was skipped");
				return;
			}
			descriptor.SetBool(node, value.AsBool());
			return;
		}

		if (descriptor.Kind == LotPropertyKind.Choice && descriptor.SetChoice != null)
		{
			if (value.VariantType != Variant.Type.String)
			{
				report.Warn("property '" + descriptor.Id + "' expected a text value; it was skipped");
				return;
			}
			descriptor.SetChoice(node, value.AsString());
			return;
		}

		if (descriptor.Kind == LotPropertyKind.Float && descriptor.SetFloat != null)
		{
			// Godot's parser hands an integral literal back as an int, so both number forms are valid.
			if (value.VariantType == Variant.Type.Float) descriptor.SetFloat(node, (float)value.AsDouble());
			else if (value.VariantType == Variant.Type.Int) descriptor.SetFloat(node, value.AsInt32());
			else report.Warn("property '" + descriptor.Id + "' expected a number; it was skipped");
			return;
		}

		if (descriptor.Kind == LotPropertyKind.Text && descriptor.SetText != null)
		{
			if (value.VariantType != Variant.Type.String)
			{
				report.Warn("property '" + descriptor.Id + "' expected text; it was skipped");
				return;
			}
			descriptor.SetText(node, value.AsString() ?? "");
		}
	}
}
