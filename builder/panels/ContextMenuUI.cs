using System.Collections.Generic;
using Godot;

/// <summary>
/// Right-click menu content for hierarchy nodes (port of Unity's ContextMenuUI, rebuilt as
/// immediate-mode draw helpers). Object entries: Duplicate / Delete / Turn to Model (3D only) /
/// Insert Script. Root entries: Insert Script only.
/// </summary>
public static class ContextMenuUI
{
	/// <summary>One-element list for the delete path — reused so a right-click delete on an
	/// unselected row (which cannot happen through the hierarchy's own menu, but is possible from a
	/// future caller) allocates nothing.</summary>
	private static readonly List<Node> _singleTarget = new List<Node>(1);

	public static void DrawForNode(Builder builder, Node target)
	{
		bool isUi = target is LotUIElement;
		bool isScript = target is LotScriptNode;
		// A decal is a leaf decoration whose transform is derived from its host: wrapping it in a
		// model group would break that link, so the operation is not offered for it.
		bool isDecal = target is LotObject lotTarget && lotTarget.Kind == LotObjectKind.Decal;

		ImGui.BeginDisabled(isScript);
		if (ImGui.MenuItem("Duplicate"))
		{
			// DuplicateNode returns a detached copy; attaching it is the undoable part.
			Node parent = target.GetParent();
			Node copy = parent != null ? builder.Scene.DuplicateNode(target) : null;
			if (copy != null)
			{
				builder.History.Push(new CreateNodeCommand("Duplicate", copy, parent, target.GetIndex() + 1,
					b => b.Selection.Select(copy, false)));
			}
		}
		ImGui.EndDisabled();
		// Delete acts on the whole selection, not just the right-clicked row — the hierarchy selects
		// an unselected row before opening this menu, so the multi-selection is the set the creator
		// right-clicked into. One history entry for the set, so a single Ctrl+Z brings it all back.
		bool inSelection = builder.Selection.IsSelected(target);
		int count = builder.Selection.Count;
		string deleteLabel = inSelection && count > 1 ? "Delete " + count + " Objects" : "Delete";
		if (ImGui.MenuItem(deleteLabel))
		{
			if (inSelection) builder.DeleteSelection();
			else
			{
				_singleTarget.Clear();
				_singleTarget.Add(target);
				builder.Scene.DeleteNodes(_singleTarget);
			}
		}
		ImGui.BeginDisabled(isUi || isScript || isDecal);
		if (ImGui.MenuItem("Turn to Model"))
			builder.Scene.TurnToModel((Node3D)target);
		ImGui.EndDisabled();

		if (isUi)
		{
			LotUIElement uiElement = (LotUIElement)target;
			if (ImGui.MenuItem("Bring Forward")) builder.Scene.MoveUiOrder(uiElement, 1);
			if (ImGui.MenuItem("Send Backward")) builder.Scene.MoveUiOrder(uiElement, -1);
		}

		ImGui.Separator();
		if (!isUi && !isScript && !isDecal && ImGui.MenuItem("Insert Decal"))
			builder.Scene.InsertDecal(builder, target as LotObject);
		if (ImGui.MenuItem("Insert Script"))
			builder.Scene.InsertScriptUnder(builder, target);
	}

	public static void DrawRootMenu(Builder builder, Node lotRoot)
	{
		if (ImGui.MenuItem("Insert Script"))
			builder.Scene.InsertScriptUnder(builder, lotRoot);
	}
}