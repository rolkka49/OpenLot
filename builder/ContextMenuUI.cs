using Godot;

/// <summary>
/// Right-click menu content for hierarchy nodes (port of Unity's ContextMenuUI, rebuilt as
/// immediate-mode draw helpers). Object entries: Duplicate / Delete / Turn to Model (3D only) /
/// Insert Script / Insert FastPlug. Root entries: Insert Script / Insert FastPlug only.
/// </summary>
public static class ContextMenuUI
{
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
		if (ImGui.MenuItem("Delete"))
			builder.Scene.DeleteNode(target);
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
		if (ImGui.MenuItem("Insert FastPlug"))
			GD.Print("[ContextMenu] Insert FastPlug requested on: " + target.Name.ToString() + " (placeholder)");
	}

	public static void DrawRootMenu(Builder builder, Node lotRoot)
	{
		if (ImGui.MenuItem("Insert Script"))
			builder.Scene.InsertScriptUnder(builder, lotRoot);
		if (ImGui.MenuItem("Insert FastPlug"))
			GD.Print("[ContextMenu] Insert FastPlug requested on lot root (placeholder)");
	}
}