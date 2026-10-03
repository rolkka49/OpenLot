using Godot;

/// <summary>
/// Immediate-mode inspector for the first selected lot node (port of Unity's
/// InspectorPanelController): active toggle, name, and transform rows. 3D nodes edit their full
/// Node3D transform (rotation shown in degrees, applied as RotationDegrees); UI elements edit
/// their 2D rect only — the Z axis is hidden for UI exactly like the Unity panel's UI mode, and
/// rotation is not exposed because the ImGui draw layer is axis-aligned for now.
/// Widget values are cached fields drawn each frame; edits apply straight to the node, and the
/// cache refreshes from the node whenever the user is not editing — no focus juggling.
/// </summary>
public class InspectorPanel
{
	private readonly Builder _builder;
	private Vector3 _editPos;
	private Vector3 _editRotDeg;
	private Vector3 _editScl;
	private Vector2 _editUiPos;
	private Vector2 _editUiSize;
	private string _editUiLabel = "";
	private string _editName = "";
	private ulong _trackedNode = 0;

	public InspectorPanel(Builder builder)
	{
		_builder = builder;
	}

	public void Draw(Builder builder)
	{
		ImGui.SetNextWindowSize(300f, 420f, ImGui.CondFirstUseEver);
		ImGui.SetNextWindowPos(1260f, 300f, ImGui.CondFirstUseEver);

		bool open = ImGui.Begin("Inspector");
		if (!open)
		{
			ImGui.End();
			return;
		}

		Node selected = builder.Selection.GetFirstValid();
		if (selected == null)
		{
			_trackedNode = 0;
			ImGui.BeginDisabled(true);
			ImGui.InputText("Name", "No Selection", 64);
			ImGui.Checkbox("Active", false);
			ImGui.InputFloat3("Pos", new Vector3(0f, 0f, 0f));
			ImGui.InputFloat3("Rot", new Vector3(0f, 0f, 0f));
			ImGui.InputFloat3("Scale", new Vector3(1f, 1f, 1f));
			ImGui.EndDisabled();
			ImGui.End();
			return;
		}

		// Refresh the edit widgets when the selection changes.
		if (_trackedNode != selected.GetInstanceId())
		{
			_trackedNode = selected.GetInstanceId();
			SyncFrom(selected);
		}

		DrawHeader(builder, selected);

		LotUIElement uiElement = selected as LotUIElement;
		if (uiElement != null)
		{
			DrawUiTransform(builder, uiElement);
		}
		else
		{
			Node3D node3d = selected as Node3D;
			if (node3d != null) Draw3DTransform(builder, node3d);
		}

		if (selected is LotScriptNode)
			ImGui.TextDisabled("Lua script — double-click in the hierarchy to open.");

		ImGui.End();
	}

	private void DrawHeader(Builder builder, Node selected)
	{
		bool visible = GetVisible(selected);
		bool newVisible = ImGui.Checkbox("Active", visible);
		if (newVisible != visible)
		{
			SetVisible(selected, newVisible);
			builder.MarkDirty();
		}
		ImGui.SameLine();

		string currentName = DisplayName(selected);
		string editedName = ImGui.InputText("Name", _editName, 64);
		if (ImGui.IsItemDeactivatedAfterEdit())
		{
			if (editedName.Length > 0 && editedName != currentName)
				_editName = CommitName(builder, selected, editedName);
		}
		else
		{
			_editName = editedName;
		}
	}

	private void Draw3DTransform(Builder builder, Node3D obj)
	{
		_editPos = ImGui.InputFloat3("Pos", _editPos);
		if (ImGui.IsItemEdited()) { obj.Position = _editPos; builder.MarkDirty(); }
		else _editPos = obj.Position;

		_editRotDeg = ImGui.InputFloat3("Rot", _editRotDeg);
		if (ImGui.IsItemEdited()) { obj.RotationDegrees = _editRotDeg; builder.MarkDirty(); }
		else _editRotDeg = obj.RotationDegrees;

		_editScl = ImGui.InputFloat3("Scale", _editScl);
		if (ImGui.IsItemEdited()) { obj.Scale = _editScl; builder.MarkDirty(); }
		else _editScl = obj.Scale;

		LotObject lotObject = obj as LotObject;
		if (lotObject != null)
		{
			Color color = ImGui.ColorEdit3("Color", lotObject.Color);
			if (ImGui.IsItemEdited() && color != lotObject.Color)
			{
				lotObject.Color = color;
				builder.MarkDirty();
			}
		}
	}

	private void DrawUiTransform(Builder builder, LotUIElement ui)
	{
		Vector2 bounds = builder.Scene.UiCanvasBounds;

		_editUiPos = ImGui.InputFloat2("Pos", _editUiPos);
		if (ImGui.IsItemEdited())
		{
			// Numeric entry is clamped exactly like a drag, so typed coordinates cannot push an
			// element outside the picture frame either.
			Vector2 pos = _editUiPos;
			Vector2 size = ui.Size;
			UiGizmoMath.ClampRect(ref pos, ref size, bounds);
			ui.Position = pos;
			builder.MarkDirty();
		}
		else _editUiPos = ui.Position;

		_editUiSize = ImGui.InputFloat2("Size", _editUiSize);
		if (ImGui.IsItemEdited())
		{
			Vector2 pos = ui.Position;
			Vector2 size = _editUiSize;
			UiGizmoMath.ClampRect(ref pos, ref size, bounds);
			ui.Position = pos;
			ui.Size = size;
			builder.MarkDirty();
		}
		else _editUiSize = ui.Size;

		if (ui.Kind == LotUIKind.Button || ui.Kind == LotUIKind.Text)
		{
			string editedLabel = ImGui.InputText("Label", _editUiLabel, 128);
			if (ImGui.IsItemDeactivatedAfterEdit())
			{
				if (editedLabel != ui.Label)
				{
					ui.Label = editedLabel;
					builder.MarkDirty();
				}
			}
			else
			{
				_editUiLabel = editedLabel;
			}
		}

		Color color = ImGui.ColorEdit4("Color", ui.Color);
		if (ImGui.IsItemEdited() && color != ui.Color)
		{
			ui.Color = color;
			builder.MarkDirty();
		}

		// Draw order is child order: later children render on top.
		ImGui.Spacing();
		if (ImGui.Button("Bring Forward", 130f, 0f)) builder.Scene.MoveUiOrder(ui, 1);
		ImGui.SameLine();
		if (ImGui.Button("Send Backward", 130f, 0f)) builder.Scene.MoveUiOrder(ui, -1);
	}

	private void SyncFrom(Node node)
	{
		_editName = DisplayName(node);
		LotUIElement ui = node as LotUIElement;
		if (ui != null)
		{
			_editUiPos = ui.Position;
			_editUiSize = ui.Size;
			_editUiLabel = ui.Label;
			return;
		}
		Node3D n3d = node as Node3D;
		if (n3d != null)
		{
			_editPos = n3d.Position;
			_editRotDeg = n3d.RotationDegrees;
			_editScl = n3d.Scale;
		}
	}

	private string CommitName(Builder builder, Node node, string newName)
	{
		newName = newName.Trim();
		if (newName.Length == 0) return DisplayName(node);

		LotScriptNode script = node as LotScriptNode;
		if (script != null)
		{
			// Renaming a script node renames its file (Unity parity); RenameScriptFile marks dirty.
			builder.Scripts.RenameScriptFile(script, newName);
			return script.DisplayName;
		}
		node.Name = newName;
		builder.MarkDirty();
		return newName;
	}

	private static string DisplayName(Node node)
	{
		LotScriptNode script = node as LotScriptNode;
		return script != null ? script.DisplayName : node.Name.ToString();
	}

	private static bool GetVisible(Node node)
	{
		LotUIElement ui = node as LotUIElement;
		if (ui != null) return ui.Visible;
		Node3D n3d = node as Node3D;
		return n3d != null && n3d.Visible;
	}

	private static void SetVisible(Node node, bool visible)
	{
		LotUIElement ui = node as LotUIElement;
		if (ui != null)
		{
			ui.Visible = visible;
			return;
		}
		Node3D n3d = node as Node3D;
		if (n3d != null) n3d.Visible = visible;
	}
}