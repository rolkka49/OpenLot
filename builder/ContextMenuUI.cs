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

		ImGui.BeginDisabled(isScript);
		if (ImGui.MenuItem("Duplicate"))
		{
			builder.Scene.DuplicateNode(target);
			builder.MarkDirty();
		}
		ImGui.EndDisabled();
		if (ImGui.MenuItem("Delete"))
		{
			builder.Scene.DeleteNode(target);
			builder.MarkDirty();
		}
		ImGui.BeginDisabled(isUi || isScript);
		if (ImGui.MenuItem("Turn to Model"))
		{
			builder.Scene.TurnToModel((Node3D)target);
			builder.MarkDirty();
		}
		ImGui.EndDisabled();

		if (isUi)
		{
			LotUIElement uiElement = (LotUIElement)target;
			if (ImGui.MenuItem("Bring Forward")) builder.Scene.MoveUiOrder(uiElement, 1);
			if (ImGui.MenuItem("Send Backward")) builder.Scene.MoveUiOrder(uiElement, -1);
		}

		ImGui.Separator();
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