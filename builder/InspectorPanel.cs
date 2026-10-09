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
	// Row labels for a Choice property's combo. Row 0 clears the property; the last row opens the
	// image browser, so a texture can be chosen and imported from the same widget.
	private const string NoChoiceLabel = "(none)";
	private const string ImportChoiceLabel = "Import...";

	private readonly Builder _builder;
	private Vector3 _editPos;
	private Vector3 _editRotDeg;
	private Vector3 _editScl;
	private Vector2 _editUiPos;
	private Vector2 _editUiSize;
	private string _editUiLabel = "";
	private string _editName = "";
	private ulong _trackedNode = 0;

	// Edit-session state for the undo stack (milestone 2.4): a field's "before" is captured when it
	// becomes active and the entry is pushed when it deactivates, so dragging/typing is one command.
	private Transform3D _transformBefore;
	private bool _transformSession;
	private Vector2 _uiRectBeforePos;
	private Vector2 _uiRectBeforeSize;
	private bool _uiRectSession;
	private Color _colorBefore;
	private bool _colorSession;
	private string _nameBefore = "";
	private string _labelBefore = "";

	// Float property edit session (milestone 3.6): the "before" value captured on activation, and
	// the id of the property being edited, so a widget that loses the edit commits exactly once.
	private float _floatEditBefore;
	private bool _floatEditActive;
	private string _floatEditId = "";

	// The Text field's edit session (same begin/commit split as the Float fields): the text applies
	// live while typing and one history entry is pushed when the field deactivates.
	private string _textEditBefore = "";
	private bool _textEditActive;
	private string _textEditId = "";

	// Reused per frame so the property loop allocates nothing. Combo option arrays are cached per
	// property id, so a node carrying both the Texture and Collision Group dropdowns does not
	// rebuild a buffer every frame; a buffer is rebuilt only when its options actually change.
	private readonly System.Collections.Generic.List<LotPropertyDescriptor> _propertyBuffer =
		new System.Collections.Generic.List<LotPropertyDescriptor>();
	private readonly System.Collections.Generic.Dictionary<string, string[]> _optionBuffers =
		new System.Collections.Generic.Dictionary<string, string[]>();

	public InspectorPanel(Builder builder)
	{
		_builder = builder;
	}

	public void Draw(Builder builder)
	{
		ImGui.SetNextWindowSize(300f, 420f, ImGui.CondFirstUseEver);
		ImGui.SetNextWindowPos(1260f, 300f, ImGui.CondFirstUseEver);

		bool open = ImGui.Begin("Inspector", EditorChrome.PanelWindowFlags);
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

		// Everything below the header scrolls. The header stays pinned because it is the only way
		// to rename or disable the node, and long property lists must not push it out of reach.
		if (ImGui.BeginChild("InspectorScroll", 0f, 0f))
		{
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

			DrawProperties(builder, selected);

			// Welds and hinges on the selected part (§3.6). A link is lot-level state, not a node
			// property, so it has its own small section rather than rows in the property table.
			LotObject selectedPart = selected as LotObject;
			if (selectedPart != null) ConstraintControls.Draw(builder, selectedPart);

			if (selected is LotScriptNode)
				ImGui.TextDisabled("Lua script — double-click in the hierarchy to open.");

			ImGui.EndChild();
		}

		ImGui.End();
	}

	/// <summary>
	/// Draws every declared property that applies to the selected node. The widget is chosen from
	/// the descriptor's kind, so adding a property never adds Inspector code — the same loop serves
	/// part properties today and lot-UI properties in §4.3.
	/// </summary>
	private void DrawProperties(Builder builder, Node selected)
	{
		LotPropertyRegistry.CollectFor(selected, _propertyBuffer);
		if (_propertyBuffer.Count == 0) return;

		ImGui.Spacing();
		string category = null;
		for (int i = 0; i < _propertyBuffer.Count; i++)
		{
			LotPropertyDescriptor descriptor = _propertyBuffer[i];
			if (descriptor.Category != category)
			{
				category = descriptor.Category;
				ImGui.Separator();
				ImGui.TextDisabled(category);
			}

			if (descriptor.Kind == LotPropertyKind.Bool) DrawBoolProperty(builder, selected, descriptor);
			else if (descriptor.Kind == LotPropertyKind.Choice) DrawChoiceProperty(builder, selected, descriptor);
			else if (descriptor.Kind == LotPropertyKind.Float) DrawFloatProperty(builder, selected, descriptor);
			else if (descriptor.Kind == LotPropertyKind.Text) DrawTextProperty(builder, selected, descriptor);
		}
	}

	private void DrawBoolProperty(Builder builder, Node selected, LotPropertyDescriptor descriptor)
	{
		bool current = builder.Properties.ReadBool(selected, descriptor);
		bool edited = ImGui.Checkbox(descriptor.DisplayName, current);
		if (descriptor.Doc != null && ImGui.IsItemHovered()) ImGui.SetTooltip(descriptor.Doc);
		if (edited != current) builder.Properties.WriteBool(selected, descriptor, edited);
	}

	/// <summary>
	/// A Float property as one undo entry per edit session: the value applies live while the widget
	/// is active (dragging updates the scene immediately), and the entry is pushed on deactivation —
	/// the same begin/commit split the transform fields use, so a drag cannot flood the history.
	/// </summary>
	private void DrawFloatProperty(Builder builder, Node selected, LotPropertyDescriptor descriptor)
	{
		float current = builder.Properties.ReadFloat(selected, descriptor);
		float edited = ImGui.InputFloat(descriptor.DisplayName, current, descriptor.Speed, 0f);
		if (descriptor.Doc != null && ImGui.IsItemHovered()) ImGui.SetTooltip(descriptor.Doc);

		if (ImGui.IsItemActivated())
		{
			_floatEditActive = true;
			_floatEditId = descriptor.Id;
			_floatEditBefore = current;
		}
		if (edited != current && _floatEditActive && _floatEditId == descriptor.Id)
			builder.Properties.ApplyFloat(selected, descriptor, edited);
		if (!_floatEditActive || _floatEditId != descriptor.Id) return;

		if (ImGui.IsItemDeactivatedAfterEdit())
		{
			_floatEditActive = false;
			float after = builder.Properties.ReadFloat(selected, descriptor);
			builder.Properties.PushFloatEdit(selected, descriptor, _floatEditBefore, after);
		}
	}

	/// <summary>
	/// A Text property as one undo entry per edit session, mirroring the Float field: the value
	/// applies live while the field is being typed in and the session is committed on deactivation,
	/// so typing a label does not push one history entry per keystroke.
	/// </summary>
	private void DrawTextProperty(Builder builder, Node selected, LotPropertyDescriptor descriptor)
	{
		string current = builder.Properties.ReadText(selected, descriptor);
		string edited = ImGui.InputText(descriptor.DisplayName, current, 256, 0);
		if (descriptor.Doc != null && ImGui.IsItemHovered()) ImGui.SetTooltip(descriptor.Doc);

		if (ImGui.IsItemActivated())
		{
			_floatEditActive = false;
			_textEditActive = true;
			_textEditId = descriptor.Id;
			_textEditBefore = current;
		}
		if (_textEditActive && _textEditId == descriptor.Id && edited != current)
			builder.Properties.ApplyText(selected, descriptor, edited);
		if (!_textEditActive || _textEditId != descriptor.Id) return;

		if (ImGui.IsItemDeactivatedAfterEdit())
		{
			_textEditActive = false;
			string after = builder.Properties.ReadText(selected, descriptor);
			builder.Properties.PushTextEdit(selected, descriptor, _textEditBefore, after);
		}
	}

	private void DrawChoiceProperty(Builder builder, Node selected, LotPropertyDescriptor descriptor)
	{
		string current = builder.Properties.ReadChoice(selected, descriptor);
		string[] options = descriptor.ListOptions != null ? descriptor.ListOptions() : System.Array.Empty<string>();
		string[] items = BuildOptionArray(descriptor, options);

		// With the texture affordances, row 0 is "(none)" and the last row is "Import...", so the
		// value rows are shifted by one. Without them, row i is simply options[i].
		int offset = descriptor.HasImportAction ? 1 : 0;
		int currentIndex = 0;
		for (int i = 0; i < options.Length; i++)
		{
			if (options[i] == current) { currentIndex = i + offset; break; }
		}

		int chosen = ImGui.Combo(descriptor.DisplayName, currentIndex, items);
		if (descriptor.Doc != null && ImGui.IsItemHovered()) ImGui.SetTooltip(descriptor.Doc);

		if (chosen == currentIndex) return;
		if (!descriptor.HasImportAction)
		{
			if (chosen >= 0 && chosen < options.Length)
				builder.Properties.WriteChoice(selected, descriptor, options[chosen]);
			return;
		}
		if (chosen == 0) builder.Properties.WriteChoice(selected, descriptor, "");
		else if (chosen == items.Length - 1) RequestTextureImport(builder, selected);
		else builder.Properties.WriteChoice(selected, descriptor, items[chosen]);
	}

	/// <summary>
	/// The string array handed to the combo, cached per property id so it is rebuilt only when its
	/// contents change (not once per frame). For a texture-style Choice it is
	/// <c>["(none)", ...options, "Import..."]</c>; otherwise it is exactly <c>options</c>.
	/// </summary>
	private string[] BuildOptionArray(LotPropertyDescriptor descriptor, string[] options)
	{
		bool withImport = descriptor.HasImportAction;
		int prefix = withImport ? 1 : 0;
		int suffix = withImport ? 1 : 0;
		int wanted = options.Length + prefix + suffix;

		string[] cached;
		if (_optionBuffers.TryGetValue(descriptor.Id, out cached) && MatchesOptions(cached, options, withImport))
			return cached;

		string[] buffer = new string[wanted];
		if (withImport) buffer[0] = NoChoiceLabel;
		System.Array.Copy(options, 0, buffer, prefix, options.Length);
		if (withImport) buffer[wanted - 1] = ImportChoiceLabel;
		_optionBuffers[descriptor.Id] = buffer;
		return buffer;
	}

	/// <summary>True when a cached buffer already matches the wanted options, so no rebuild is needed.</summary>
	private static bool MatchesOptions(string[] buffer, string[] options, bool withImport)
	{
		int prefix = withImport ? 1 : 0;
		int suffix = withImport ? 1 : 0;
		if (buffer.Length != options.Length + prefix + suffix) return false;
		if (withImport && (buffer[0] != NoChoiceLabel || buffer[buffer.Length - 1] != ImportChoiceLabel)) return false;
		for (int i = 0; i < options.Length; i++)
		{
			if (buffer[i + prefix] != options[i]) return false;
		}
		return true;
	}

	/// <summary>
	/// Opens the image browser for a part's Texture property. The import and the cache reference
	/// bookkeeping both live in <see cref="LotPropertyService.SetTextureFromFile"/>, so this only
	/// wires the dialog to it.
	/// </summary>
	private void RequestTextureImport(Builder builder, Node selected)
	{
		LotObject part = selected as LotObject;
		if (part == null) return;

		ImageFilePicker.Open(builder, "Choose an image", path =>
		{
			if (!GodotObject.IsInstanceValid(part)) return;
			if (!builder.Properties.SetTextureFromFile(part, path, out string error))
			{
				Godot.GD.PushWarning("[Inspector] " + error);
				LotLog.Warn("inspector", error);
			}
		});
	}

	private void DrawHeader(Builder builder, Node selected)
	{
		// Base content (milestone 4.1): say so before any control is touched. The hierarchy keeps its own
		// copy in a separate section, and this banner is the other place a creator could otherwise edit a
		// shared, proprietary file without noticing which one they had selected.
		if (SecretContent.IsBaseContent(selected as LotScriptNode))
		{
			ImGui.TextColored(SecretContent.CautionColor, SecretContent.SectionHeading);
			ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
			ImGui.TextDisabled(SecretContent.SectionNote);
			ImGui.PopTextWrapPos();
			ImGui.Spacing();
		}

		bool visible = GetVisible(selected);
		bool newVisible = ImGui.Checkbox("Active", visible);
		if (newVisible != visible)
		{
			builder.History.Push(new DelegateCommand("Toggle Active",
				b => SetVisible(selected, newVisible),
				b => SetVisible(selected, visible)));
		}
		ImGui.SameLine();

		string currentName = DisplayName(selected);
		string editedName = ImGui.InputText("Name", _editName, 64);
		if (ImGui.IsItemActivated()) _nameBefore = currentName;
		if (ImGui.IsItemDeactivatedAfterEdit())
		{
			if (editedName.Length > 0 && editedName != currentName)
				_editName = CommitName(builder, selected, editedName, _nameBefore);
		}
		else
		{
			_editName = editedName;
		}
	}

	private void Draw3DTransform(Builder builder, Node3D obj)
	{
		_editPos = ImGui.InputFloat3("Pos", _editPos);
		BeginTransformIfActivated(obj);
		if (ImGui.IsItemEdited()) obj.Position = _editPos;
		else if (!ImGui.IsItemActive()) _editPos = obj.Position;
		if (ImGui.IsItemDeactivatedAfterEdit()) CommitTransform(builder, obj, "Move");

		_editRotDeg = ImGui.InputFloat3("Rot", _editRotDeg);
		BeginTransformIfActivated(obj);
		if (ImGui.IsItemEdited()) obj.RotationDegrees = _editRotDeg;
		else if (!ImGui.IsItemActive()) _editRotDeg = obj.RotationDegrees;
		if (ImGui.IsItemDeactivatedAfterEdit()) CommitTransform(builder, obj, "Rotate");

		_editScl = ImGui.InputFloat3("Scale", _editScl);
		BeginTransformIfActivated(obj);
		if (ImGui.IsItemEdited()) obj.Scale = _editScl;
		else if (!ImGui.IsItemActive()) _editScl = obj.Scale;
		if (ImGui.IsItemDeactivatedAfterEdit()) CommitTransform(builder, obj, "Scale");

		LotObject lotObject = obj as LotObject;
		if (lotObject != null)
		{
			Color color = ImGui.ColorEdit3("Color", lotObject.Color);
			if (ImGui.IsItemActivated()) { _colorBefore = lotObject.Color; _colorSession = true; }
			if (ImGui.IsItemEdited() && color != lotObject.Color) lotObject.Color = color;
			if (ImGui.IsItemDeactivatedAfterEdit()) CommitColor(builder, lotObject);
		}
	}

	private void DrawUiTransform(Builder builder, LotUIElement ui)
	{
		Vector2 bounds = builder.Scene.UiCanvasBounds;

		_editUiPos = ImGui.InputFloat2("Pos", _editUiPos);
		BeginUiRectIfActivated(ui);
		if (ImGui.IsItemEdited())
		{
			// Numeric entry is clamped exactly like a drag, so typed coordinates cannot push an
			// element outside the picture frame either.
			Vector2 pos = _editUiPos;
			Vector2 size = ui.Size;
			UiGizmoMath.ClampRect(ref pos, ref size, bounds);
			ui.Position = pos;
		}
		else if (!ImGui.IsItemActive()) _editUiPos = ui.Position;
		if (ImGui.IsItemDeactivatedAfterEdit()) CommitUiRect(builder, ui, "Move UI");

		_editUiSize = ImGui.InputFloat2("Size", _editUiSize);
		BeginUiRectIfActivated(ui);
		if (ImGui.IsItemEdited())
		{
			Vector2 pos = ui.Position;
			Vector2 size = _editUiSize;
			UiGizmoMath.ClampRect(ref pos, ref size, bounds);
			ui.Position = pos;
			ui.Size = size;
		}
		else if (!ImGui.IsItemActive()) _editUiSize = ui.Size;
		if (ImGui.IsItemDeactivatedAfterEdit()) CommitUiRect(builder, ui, "Resize UI");

		if (ui.Kind == LotUIKind.Button || ui.Kind == LotUIKind.Text)
		{
			string editedLabel = ImGui.InputText("Label", _editUiLabel, 128);
			if (ImGui.IsItemActivated()) _labelBefore = ui.Label;
			if (ImGui.IsItemDeactivatedAfterEdit())
			{
				if (editedLabel != ui.Label)
				{
					string after = editedLabel;
					string before = _labelBefore;
					builder.History.Push(new DelegateCommand("Rename UI",
						b => { if (GodotObject.IsInstanceValid(ui)) ui.Label = after; },
						b => { if (GodotObject.IsInstanceValid(ui)) ui.Label = before; }));
				}
			}
			else
			{
				_editUiLabel = editedLabel;
			}
		}

		Color color = ImGui.ColorEdit4("Color", ui.Color);
		if (ImGui.IsItemActivated()) { _colorBefore = ui.Color; _colorSession = true; }
		if (ImGui.IsItemEdited() && color != ui.Color) ui.Color = color;
		if (ImGui.IsItemDeactivatedAfterEdit()) CommitUiColor(builder, ui);

		// Draw order is child order: later children render on top.
		ImGui.Spacing();
		if (ImGui.Button("Bring Forward", 130f, 0f)) builder.Scene.MoveUiOrder(ui, 1);
		ImGui.SameLine();
		if (ImGui.Button("Send Backward", 130f, 0f)) builder.Scene.MoveUiOrder(ui, -1);
	}

	// --- Undo-session commit helpers (milestone 2.4) ---

	private void BeginTransformIfActivated(Node3D obj)
	{
		if (ImGui.IsItemActivated())
		{
			_transformBefore = obj.Transform;
			_transformSession = true;
		}
	}

	private void CommitTransform(Builder builder, Node3D node, string label)
	{
		bool session = _transformSession;
		Transform3D before = _transformBefore;
		_transformSession = false;
		if (!session || node == null || !GodotObject.IsInstanceValid(node)) return;
		if (!TransformCommand.Changed(before, node.Transform)) return;
		builder.History.Push(new TransformCommand(label, node, before, node.Transform));
	}

	private void BeginUiRectIfActivated(LotUIElement ui)
	{
		if (ImGui.IsItemActivated())
		{
			_uiRectBeforePos = ui.Position;
			_uiRectBeforeSize = ui.Size;
			_uiRectSession = true;
		}
	}

	private void CommitUiRect(Builder builder, LotUIElement ui, string label)
	{
		bool session = _uiRectSession;
		Vector2 beforePos = _uiRectBeforePos;
		Vector2 beforeSize = _uiRectBeforeSize;
		_uiRectSession = false;
		if (!session || ui == null || !GodotObject.IsInstanceValid(ui)) return;

		Vector2 afterPos = ui.Position;
		Vector2 afterSize = ui.Size;
		if (beforePos == afterPos && beforeSize == afterSize) return;
		builder.History.Push(new DelegateCommand(label,
			b => { if (GodotObject.IsInstanceValid(ui)) { ui.Position = afterPos; ui.Size = afterSize; } },
			b => { if (GodotObject.IsInstanceValid(ui)) { ui.Position = beforePos; ui.Size = beforeSize; } }));
	}

	private void CommitColor(Builder builder, LotObject obj)
	{
		bool session = _colorSession;
		Color before = _colorBefore;
		_colorSession = false;
		if (!session || obj == null || !GodotObject.IsInstanceValid(obj)) return;

		Color after = obj.Color;
		if (before == after) return;
		builder.History.Push(new DelegateCommand("Color",
			b => { if (GodotObject.IsInstanceValid(obj)) obj.Color = after; },
			b => { if (GodotObject.IsInstanceValid(obj)) obj.Color = before; }));
	}

	private void CommitUiColor(Builder builder, LotUIElement ui)
	{
		bool session = _colorSession;
		Color before = _colorBefore;
		_colorSession = false;
		if (!session || ui == null || !GodotObject.IsInstanceValid(ui)) return;

		Color after = ui.Color;
		if (before == after) return;
		builder.History.Push(new DelegateCommand("Color UI",
			b => { if (GodotObject.IsInstanceValid(ui)) ui.Color = after; },
			b => { if (GodotObject.IsInstanceValid(ui)) ui.Color = before; }));
	}

	private void SyncFrom(Node node)
	{
		// A new selection invalidates any half-finished edit session.
		_transformSession = false;
		_uiRectSession = false;
		_colorSession = false;
		_floatEditActive = false;
		_textEditActive = false;
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

	private string CommitName(Builder builder, Node node, string newName, string beforeName)
	{
		newName = newName.Trim();
		if (newName.Length == 0) return DisplayName(node);

		LotScriptNode script = node as LotScriptNode;
		if (script != null)
		{
			// Renaming a script node renames its file; RenameScriptFile records its own command.
			builder.Scripts.RenameScriptFile(script, newName);
			return script.DisplayName;
		}

		string after = newName;
		builder.History.Push(new DelegateCommand("Rename",
			b => { if (GodotObject.IsInstanceValid(node)) node.Name = after; },
			b => { if (GodotObject.IsInstanceValid(node)) node.Name = beforeName; }));
		return after;
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