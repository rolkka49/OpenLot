using Godot;

/// <summary>
/// Turns the live lot scene into the plain <see cref="LotArchive.LotDocument"/> the `.lot` format
/// stores, and rebuilds it again (milestone 4.1). This is the seam between Godot's scene tree and the
/// format: <see cref="LotArchive"/> knows nothing about nodes, and this type knows nothing about zips
/// or JSON. Milestone 4.2's save/load is what will call it from the File menu.
///
/// Property values are read through <see cref="LotPropertyRegistry"/> + <see cref="LotPropertyService"/>,
/// never by touching a part's fields directly, so a property declared by a Tier-2 plugin serializes
/// with no change here — the same rule the Inspector follows.
///
/// Script nodes are deliberately NOT walked here: their payload is file text, which belongs to the
/// archive collection pass, not to the scene record list.
/// </summary>
public static class LotSceneWalk
{
	/// <summary>
	/// Captures <paramref name="root"/>'s child tree as records. Order is depth-first with parents
	/// before children (so a record's <see cref="LotArchive.LotNodeRecord.ParentIndex"/> always points
	/// backwards), siblings in child order. Nodes marked <see cref="LotObject.InternalChildMeta"/>
	/// (outline hulls, collision bodies, drag movers) are implementation details and are skipped, as
	/// are script placeholders (see the type summary).
	/// </summary>
	public static void CaptureChildren(Node root, System.Collections.Generic.List<LotArchive.LotNodeRecord> into,
		System.Collections.Generic.List<Node> capturedNodes = null)
	{
		if (root == null) return;
		// -1 means "attached to the container itself" — the container is not itself a record.
		WalkChildren(root, -1, into, capturedNodes);
	}

	private static void WalkChildren(Node parent, int parentRecordIndex,
		System.Collections.Generic.List<LotArchive.LotNodeRecord> into,
		System.Collections.Generic.List<Node> capturedNodes)
	{
		int count = parent.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			Node child = parent.GetChild(i);
			if (!IsCapturable(child)) continue;

			LotArchive.LotNodeRecord record = CaptureNode(child, parentRecordIndex);
			if (record == null)
			{
				// An unmodelled branch (a plain group) still has its descendants walked, attached to
				// the nearest captured ancestor so nothing is orphaned.
				WalkChildren(child, parentRecordIndex, into, capturedNodes);
				continue;
			}

			// Parents are always added before their children, so this index is a safe back-reference.
			int recordIndex = into.Count;
			into.Add(record);
			// The node list is kept index-aligned with the records (only real records are added),
			// which is the index space §3.6's constraint capture serializes against.
			if (capturedNodes != null) capturedNodes.Add(child);
			WalkChildren(child, recordIndex, into, capturedNodes);
		}
	}

	/// <summary>True for a node the format records: a real object/UI element/script, not an internal
	/// implementation child.</summary>
	private static bool IsCapturable(Node node)
	{
		if (node == null || !GodotObject.IsInstanceValid(node)) return false;
		if (node.HasMeta(LotObject.InternalChildMeta)) return false;
		return node is Node3D || node is LotUIElement || node is LotScriptNode;
	}

	/// <summary>
	/// One node's record, or null for a kind the format does not model (a plain group container — its
	/// children are still captured, attached to this node's parent).
	/// </summary>
	private static LotArchive.LotNodeRecord CaptureNode(Node node, int parentIndex)
	{
		LotObject part = node as LotObject;
		if (part != null)
		{
			LotArchive.LotNodeRecord record = RecordFor(LotKindTag(part.Kind), node, parentIndex);
			record.Color = part.Color;
			CaptureProperties(node, record);
			return record;
		}

		LotUIElement element = node as LotUIElement;
		if (element != null)
		{
			LotArchive.LotNodeRecord record = RecordFor(element.Kind.ToString(), node, parentIndex);
			record.Color = element.Color;
			// The label and visibility ride in the property bag so every record keeps one shape
			// instead of the format gaining a field per UI kind.
			record.Properties["label"] = element.Label ?? "";
			record.Properties["visible"] = element.Visible;
			CaptureProperties(node, record);
			return record;
		}

		LotScriptNode script = node as LotScriptNode;
		if (script != null)
		{
			// Recorded so the script stays attached to the entity it belongs to. Only the file name is
			// stored: the collection pass routes the entry by the same name, and the loader recomputes
			// the archive path with that one rule (see LotArchive.ScriptNameProperty).
			LotArchive.LotNodeRecord record = RecordFor(LotArchive.ScriptRecordKind, node, parentIndex);
			record.Properties[LotArchive.ScriptNameProperty] = string.IsNullOrEmpty(script.DisplayName)
				? script.Name.ToString()
				: script.DisplayName;
			return record;
		}

		return null;
	}

	private static LotArchive.LotNodeRecord RecordFor(string kindTag, Node node, int parentIndex)
	{
		LotArchive.LotNodeRecord record = new LotArchive.LotNodeRecord
		{
			Kind = kindTag,
			Name = node.Name.ToString(),
			ParentIndex = parentIndex
		};

		Node3D node3d = node as Node3D;
		if (node3d != null)
		{
			record.Position = node3d.Position;
			record.RotationDegrees = node3d.RotationDegrees;
			record.Scale = node3d.Scale;
			return record;
		}

		// A 2D lot UI element: pixels. Position.Z and Scale.Z stay zero so both layers share one shape.
		LotUIElement element = node as LotUIElement;
		if (element != null)
		{
			record.Position = new Vector3(element.Position.X, element.Position.Y, 0f);
			record.Scale = new Vector3(element.Size.X, element.Size.Y, 0f);
		}
		return record;
	}

	/// <summary>
	/// The declared §2.3 property values that apply to <paramref name="node"/>, keyed by the
	/// descriptor's stable id. Driven by the registry, so a new property (core or plugin) needs no
	/// change here. Uses the registry's own collector and a reused buffer, the same way the Inspector
	/// does, so a capture does not allocate a list per node.
	/// </summary>
	private static void CaptureProperties(Node node, LotArchive.LotNodeRecord record)
	{
		LotPropertyRegistry.CollectFor(node, _descriptorBuffer);
		for (int i = 0; i < _descriptorBuffer.Count; i++)
		{
			LotPropertyDescriptor descriptor = _descriptorBuffer[i];
			if (descriptor.Kind == LotPropertyKind.Bool && descriptor.GetBool != null)
				record.Properties[descriptor.Id] = descriptor.GetBool(node);
			else if (descriptor.Kind == LotPropertyKind.Choice && descriptor.GetChoice != null)
				record.Properties[descriptor.Id] = descriptor.GetChoice(node) ?? "";
			else if (descriptor.Kind == LotPropertyKind.Float && descriptor.GetFloat != null)
				record.Properties[descriptor.Id] = descriptor.GetFloat(node);
			else if (descriptor.Kind == LotPropertyKind.Text && descriptor.GetText != null)
				record.Properties[descriptor.Id] = descriptor.GetText(node) ?? "";
		}
	}

	// Reused across nodes: CollectFor clears it, and CaptureProperties finishes with it before the
	// walk moves on, so recursion never observes a half-filled buffer.
	private static readonly System.Collections.Generic.List<LotPropertyDescriptor> _descriptorBuffer =
		new System.Collections.Generic.List<LotPropertyDescriptor>();

	/// <summary>Stable lowercase tag for an object kind, so the saved name never depends on the enum's
	/// numeric order (which is free to change). Paired with <see cref="TryParseLotKind"/>: the capture is
	/// the only writer and the load the only reader, and the self-test walks both directions.</summary>
	public static string LotKindTag(LotObjectKind kind)
	{
		switch (kind)
		{
			case LotObjectKind.Cube: return "cube";
			case LotObjectKind.Sphere: return "sphere";
			case LotObjectKind.Cylinder: return "cylinder";
			case LotObjectKind.Plane: return "plane";
			case LotObjectKind.Capsule: return "capsule";
			case LotObjectKind.Decal: return "decal";
			default: return "cube";
		}
	}

	/// <summary>Inverse of <see cref="LotKindTag"/>. False for a tag this build does not know, which is
	/// how a load recognises a node kind a newer OpenLot wrote and skips it instead of guessing.</summary>
	public static bool TryParseLotKind(string tag, out LotObjectKind kind)
	{
		kind = LotObjectKind.Cube;
		if (string.IsNullOrEmpty(tag)) return false;
		switch (tag)
		{
			case "cube": kind = LotObjectKind.Cube; return true;
			case "sphere": kind = LotObjectKind.Sphere; return true;
			case "cylinder": kind = LotObjectKind.Cylinder; return true;
			case "plane": kind = LotObjectKind.Plane; return true;
			case "capsule": kind = LotObjectKind.Capsule; return true;
			case "decal": kind = LotObjectKind.Decal; return true;
			default: return false;
		}
	}
}
