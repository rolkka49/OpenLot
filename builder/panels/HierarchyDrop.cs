using Godot;

/// <summary>Where a hierarchy drop lands on the hovered row.</summary>
public enum HierarchyDropPlacement
{
	Before,
	Into,
	After
}

/// <summary>
/// Pure rules for hierarchy drag and drop (milestone 2.4), split out from the panel so the self-test
/// can pin them without an ImGui frame. A row is divided into three bands: the top quarter inserts
/// before the row, the bottom quarter inserts after it, and the middle drops into it (a plain reorder
/// for kinds that cannot nest).
/// </summary>
public static class HierarchyDrop
{
	/// <summary>Fraction of a row's height at each end that counts as "insert before/after".</summary>
	public const float EdgeBand = 0.25f;

	public static HierarchyDropPlacement ResolvePlacement(float itemTop, float itemHeight, float mouseY, bool targetAcceptsChildren)
	{
		if (itemHeight <= 0f)
			return targetAcceptsChildren ? HierarchyDropPlacement.Into : HierarchyDropPlacement.After;

		float t = (mouseY - itemTop) / itemHeight;
		if (t < EdgeBand) return HierarchyDropPlacement.Before;
		if (t > 1f - EdgeBand) return HierarchyDropPlacement.After;
		return targetAcceptsChildren ? HierarchyDropPlacement.Into : HierarchyDropPlacement.After;
	}

	/// <summary>
	/// Resolves a drop into (parent, index), rejecting the cases that must never happen: dropping a
	/// node into itself or its own subtree, onto internal children (mesh/outline/collision) or script
	/// placeholders, moving a script placeholder, and moves across the 3D/UI boundary.
	/// </summary>
	public static bool TryResolveTarget(Node source, Node target, HierarchyDropPlacement placement, out Node parent, out int index)
	{
		HierarchyDropPlacement ignored;
		return TryResolveTarget(source, target, placement, out parent, out index, out ignored);
	}

	/// <summary>
	/// Same as the 5-argument overload, and also reports the EFFECTIVE placement: a middle-band drop
	/// on a row that cannot nest is really an "after" insert, and the drag feedback has to show what
	/// will actually happen rather than the raw band.
	/// </summary>
	public static bool TryResolveTarget(Node source, Node target, HierarchyDropPlacement placement, out Node parent, out int index, out HierarchyDropPlacement effectivePlacement)
	{
		parent = null;
		index = 0;
		effectivePlacement = placement;
		if (source == null || target == null) return false;
		if (!GodotObject.IsInstanceValid(source) || !GodotObject.IsInstanceValid(target)) return false;
		if (source == target) return false;
		if (source is LotScriptNode) return false;
		if (IsAncestor(source, target)) return false;
		if (target.HasMeta(LotObject.InternalChildMeta)) return false;
		if (target is LotScriptNode) return false;

		bool sourceIsUi = source is LotUIElement;
		bool targetIsUi = target is LotUIElement;
		if (sourceIsUi != targetIsUi) return false;

		// Lot UI is flat (the renderer draws direct children only), and a decal is a leaf image
		// patch, so "into" becomes "after" for both: neither accepts children.
		bool targetIsDecal = target is LotObject targetPart && targetPart.Kind == LotObjectKind.Decal;
		bool acceptsChildren = !targetIsUi && !targetIsDecal;
		if (placement == HierarchyDropPlacement.Into && !acceptsChildren)
			placement = HierarchyDropPlacement.After;
		effectivePlacement = placement;

		if (placement == HierarchyDropPlacement.Into)
		{
			parent = target;
			index = target.GetChildCount();
			return true;
		}

		parent = target.GetParent();
		if (parent == null) return false;
		index = target.GetIndex() + (placement == HierarchyDropPlacement.After ? 1 : 0);
		return true;
	}

	/// <summary>True when <paramref name="ancestor"/> is a strict ancestor of <paramref name="node"/>.</summary>
	public static bool IsAncestor(Node ancestor, Node node)
	{
		Node walk = node == null ? null : node.GetParent();
		while (walk != null)
		{
			if (walk == ancestor) return true;
			walk = walk.GetParent();
		}
		return false;
	}

	/// <summary>Finds a node in the subtree by Godot instance id (the drag payload is that id).</summary>
	public static Node FindByInstanceId(Node root, ulong instanceId)
	{
		if (root == null || !GodotObject.IsInstanceValid(root)) return null;
		if (root.GetInstanceId() == instanceId) return root;
		int count = root.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			Node found = FindByInstanceId(root.GetChild(i), instanceId);
			if (found != null) return found;
		}
		return null;
	}
}
