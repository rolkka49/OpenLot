using Godot;

/// <summary>3D transform tool modes (Unity ToolboxController parity). The Im3d-ported gizmo handles
/// in GizmoController implement Translate/Rotate/Scale; Select means "no gizmo, direct part drag",
/// and Decal is the face tool: click a face to place a decal panel on it, click an existing panel
/// to pick it up, drag to slide it across the face (milestone 2.6).</summary>
public enum GizmoMode
{
	Select,
	Translate,
	Rotate,
	Scale,
	Decal
}

/// <summary>
/// UI tool modes for the 2D overlay editor (UI mode only). Move drags an element's body; Resize
/// exposes the eight edge/corner handles. There is deliberately no rotate mode — lot UI is
/// axis-aligned by design.
/// </summary>
public enum UiToolMode
{
	Move,
	Resize
}

/// <summary>
/// Context side panel (port of Unity's ToolboxController): primitive spawning when nothing is
/// selected, UI-element spawning when the UI toolset is active (a UI element is selected or the
/// top-bar UI button was pressed), and gizmo mode buttons when a 3D object is selected.
/// </summary>
public class ToolboxPanel
{
	private static readonly Color ActiveButtonColor = new Color(0.18f, 0.52f, 0.89f);

	private readonly Builder _builder;
	public GizmoMode CurrentGizmoMode { get; private set; } = GizmoMode.Select;
	public UiToolMode CurrentUiToolMode { get; private set; } = UiToolMode.Move;

	/// <summary>
	/// Gizmo snap configuration (§2.2). Session-only — nothing persists yet (lot files are §8.1),
	/// so this resets with the builder. Off by default, which keeps an untouched toolbox identical
	/// to the pre-snap behavior (Im3d's 0 = disabled).
	/// </summary>
	public bool SnapEnabled { get; private set; }

	/// <summary>Move increment in world units; all axes and planes share it, like Im3d's SnapTranslation.</summary>
	public float MoveSnap { get; private set; } = 0.5f;

	/// <summary>Rotate increment in degrees; GizmoController converts it to radians for Im3d.</summary>
	public float RotateSnapDegrees { get; private set; } = 15f;

	/// <summary>Scale increment, in the same units the Im3d scale behavior expects.</summary>
	public float ScaleSnap { get; private set; } = 0.1f;

	public ToolboxPanel(Builder builder)
	{
		_builder = builder;
	}

	public void Draw(Builder builder)
	{
		// Height stays at the pre-snap default: the toolbox is meant to sit above the Inspector,
		// whose own default position is y=300. Growing this window would overlap it, so the extra
		// snap rows simply scroll into view (the panel is user-resizable for anyone who wants
		// them all visible at once).
		ImGui.SetNextWindowSize(280f, 240f, ImGui.CondFirstUseEver);
		ImGui.SetNextWindowPos(1260f, 30f, ImGui.CondFirstUseEver);

		bool open = ImGui.Begin("Toolbox", EditorChrome.PanelWindowFlags);
		if (!open)
		{
			ImGui.End();
			return;
		}

		if (builder.Mode == BuilderMode.UI)
		{
			// UI mode is a dedicated canvas editor: the panel always shows the UI tools, regardless
			// of what happens to be selected (a 3D selection no longer switches it back).
			DrawUiTools(builder);
		}
		else
		{
			Node selected = builder.Selection.GetFirstValid();
			if (selected is LotUIElement) DrawUiTools(builder);
			else if (selected is LotObject decalPart && decalPart.Kind == LotObjectKind.Decal)
			{
				// The tool switcher stays reachable while a decal is selected, so switching back to
				// Move/Rotate to work on the host is one click and never leaves the creator stuck.
				Draw3DToolButtons();
				DrawDecalTools(builder, decalPart);
			}
			else if (selected != null)
			{
				Draw3DToolButtons();
				DrawGizmoTools(builder);
			}
			else DrawSpawnPanel(builder);
			DrawConstraintTools(builder);
		}

		ImGui.End();
	}

	/// <summary>
	/// The weld/hinge tools (milestone 3.6). Always offered in build mode, whatever is selected,
	/// because a two-click link gesture does not depend on a selection; the hint line tells the
	/// creator what the armed tool wants next. Arming a tool owns viewport clicks until it finishes
	/// or Escape cancels it.
	/// </summary>
	private void DrawConstraintTools(Builder builder)
	{
		ImGui.Spacing();
		ImGui.Text("Constraints");
		ImGui.Separator();
		bool weldArmed = builder.ConstraintPick.Armed == ConstraintPicker.Tool.Weld;
		bool hingeArmed = builder.ConstraintPick.Armed == ConstraintPicker.Tool.Hinge;
		if (ToolButton("Weld", weldArmed)) builder.ConstraintPick.Arm(ConstraintPicker.Tool.Weld);
		if (ToolButton("Hinge", hingeArmed)) builder.ConstraintPick.Arm(ConstraintPicker.Tool.Hinge);
		ImGui.NewLine(); // ToolButton leaves a pending SameLine; the next row gets its own line

		// The hinge's placement step: the marker is in the view, these finish or abandon it.
		if (builder.ConstraintPick.IsPlacingHinge)
		{
			if (ToolButton("Confirm", false)) builder.ConstraintPick.ConfirmHinge();
			if (ToolButton("Cancel", false)) builder.ConstraintPick.Cancel();
			ImGui.NewLine();

			// Sharp axis tools: exact presets and 45° steps; the rod in the view follows each press.
			float half = (ImGui.GetContentRegionAvail().X - 8f) * 0.5f;
			if (AxisButton("Vertical", half, "Point the hinge axis straight up."))
				builder.ConstraintPick.MakeHingeAxisVertical();
			ImGui.SameLine();
			if (AxisButton("Horizontal", half, "Flatten the hinge axis onto the ground plane."))
				builder.ConstraintPick.MakeHingeAxisHorizontal();
			if (AxisButton("Tilt 45", half, "Rotate the axis 45 degrees in its own vertical plane (horizontal -> diagonal -> vertical)."))
				builder.ConstraintPick.TiltHingeAxis();
			ImGui.SameLine();
			if (AxisButton("Yaw 45", half, "Rotate the axis 45 degrees around the vertical."))
				builder.ConstraintPick.YawHingeAxis();
			ImGui.NewLine();
		}

		if (builder.ConstraintPick.IsArmed)
			ImGui.TextDisabled(builder.ConstraintPick.Hint);
		else
			ImGui.TextDisabled("Weld two parts, or hinge one (empty space = the world).");
	}

	/// <summary>A fixed-size button with a hover tooltip, for the axis tools — ToolButton's default
	/// width would clip their labels.</summary>
	private static bool AxisButton(string label, float width, string tooltip)
	{
		bool clicked = ImGui.Button(label, width, 24f);
		if (ImGui.IsItemHovered() && tooltip != null) ImGui.SetTooltip(tooltip);
		return clicked;
	}

	private void DrawSpawnPanel(Builder builder)
	{
		ImGui.Text("Create Part");
		ImGui.Separator();
		if (ImGui.Button("Cube", 88f, 44f)) Spawn3D(builder, LotObjectKind.Cube);
		ImGui.SameLine();
		if (ImGui.Button("Sphere", 88f, 44f)) Spawn3D(builder, LotObjectKind.Sphere);
		ImGui.SameLine();
		if (ImGui.Button("Cylinder", 88f, 44f)) Spawn3D(builder, LotObjectKind.Cylinder);
		ImGui.SameLine();
		if (ImGui.Button("Capsule", 88f, 44f)) Spawn3D(builder, LotObjectKind.Capsule);
	}

	/// <summary>The tool switcher, shared by every 3D selection state so the mode is always one
	/// click away — including while a decal is selected, where the gizmo itself is not offered.</summary>
	private void Draw3DToolButtons()
	{
		ImGui.Text("Transform Tool");
		ImGui.Separator();
		ModeButton("Select", GizmoMode.Select);
		ModeButton("Move", GizmoMode.Translate);
		ModeButton("Rotate", GizmoMode.Rotate);
		ModeButton("Scale", GizmoMode.Scale);
		ModeButton("Decal", GizmoMode.Decal);
		ImGui.NewLine();
	}

	private void DrawGizmoTools(Builder builder)
	{
		if (CurrentGizmoMode == GizmoMode.Decal)
		{
			ImGui.TextWrapped("Click a part's face to place a decal panel there. Click an existing " +
				"panel to pick it up, drag to slide it across the face.");
		}
		else
		{
			ImGui.TextDisabled("Select drags parts; the other tools use the gizmo handles.");
		}

		ImGui.Spacing();
		DrawSnapControls();

		ImGui.Spacing();
		ImGui.Text("Decal");
		ImGui.Separator();
		DrawInsertDecalButton(builder);
		ImGui.TextDisabled("Face, content and text live in the Inspector.");
	}

	/// <summary>
	/// Adds a decal to the selected part on its front (+Z) face (milestone 2.6). The button lives
	/// next to the transform tools because a decal needs a host: this section only draws while a
	/// part is selected. The image browser opens right away so the new panel is not left blank.
	/// </summary>
	private static void DrawInsertDecalButton(Builder builder)
	{
		if (ImGui.Button("Add Decal", 88f, 24f))
		{
			builder.Scene.InsertDecal(builder, builder.Selection.GetFirstValid() as LotObject);
		}
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Adds a decal panel to the selected part and opens the image browser. " +
				"Use the Decal tool to place one on another face.");
		}
	}

	/// <summary>
	/// The decal-selected toolbox: no gizmo and no direct transform, because a panel's transform is
	/// derived from its face/offset/scale. Its gesture is the face slide — dragging on the host —
	/// which the Decal tool arms (see <see cref="PartDragController"/>).
	/// </summary>
	private void DrawDecalTools(Builder builder, LotObject decal)
	{
		ImGui.Text("Decal Panel");
		ImGui.Separator();
		if (CurrentGizmoMode == GizmoMode.Decal)
		{
			ImGui.TextWrapped("Drag on the host to slide the panel. Click another face to place a " +
				"new panel there.");
		}
		else
		{
			ImGui.TextWrapped("Switch to the Decal tool to slide it, or edit its values in the Inspector.");
		}
		ImGui.TextDisabled("Content, face, image, text and opacity live in the Inspector.");
		ImGui.TextDisabled("Select the host part to move or resize it.");
	}

	/// <summary>
	/// The snap section (§2.2): an on/off toggle plus the three increments. The increments are
	/// disabled while snapping is off so the panel reads as one control, and each is clamped
	/// strictly positive — Im3d's Snap() treats 0 as "disabled", so a 0 field would silently
	/// undo the toggle.
	///
	/// The "##snap" suffixes are load-bearing: ImGui derives an item's id from its label, and this
	/// window already has tool buttons labelled "Move" and "Scale". A plain "Move"/"Scale" input
	/// would take those ids and swallow the buttons' clicks, so the inputs get distinct ids while
	/// the "##" keeps the visible text unchanged.
	/// </summary>
	private void DrawSnapControls()
	{
		SnapEnabled = ImGui.Checkbox("Snap", SnapEnabled);

		ImGui.BeginDisabled(!SnapEnabled);
		MoveSnap = ClampIncrement(ImGui.InputFloat("Move##snap", MoveSnap, 0.1f, 1.0f), MoveSnap);
		RotateSnapDegrees = ClampIncrement(ImGui.InputFloat("Rotate deg##snap", RotateSnapDegrees, 1f, 15f), RotateSnapDegrees);
		ScaleSnap = ClampIncrement(ImGui.InputFloat("Scale##snap", ScaleSnap, 0.05f, 0.5f), ScaleSnap);
		ImGui.EndDisabled();

		ImGui.TextDisabled("Hold Alt to invert snapping while dragging.");
	}

	/// <summary>Falls back to the last good increment when the field is typed to 0 or below.</summary>
	private static float ClampIncrement(float value, float fallback)
	{
		return value > 0f ? value : fallback;
	}

	private void DrawUiTools(Builder builder)
	{
		ImGui.Text("Create UI Element");
		ImGui.Separator();
		if (ImGui.Button("Frame", 120f, 30f)) SpawnUi(builder, LotUIKind.Frame);
		ImGui.SameLine();
		if (ImGui.Button("Button", 120f, 30f)) SpawnUi(builder, LotUIKind.Button);
		if (ImGui.Button("Text", 120f, 30f)) SpawnUi(builder, LotUIKind.Text);
		ImGui.SameLine();
		if (ImGui.Button("Scrollbar", 120f, 30f)) SpawnUi(builder, LotUIKind.Scrollbar);

		ImGui.Spacing();
		ImGui.Text("UI Transform Tool");
		ImGui.Separator();
		UiModeButton("Move", UiToolMode.Move);
		UiModeButton("Resize", UiToolMode.Resize);
		ImGui.TextDisabled("Drag the body to move, the handles to resize.");
		ImGui.TextDisabled("Hold Alt to bypass snapping; Shift locks aspect.");
	}

	private void ModeButton(string label, GizmoMode mode)
	{
		if (ToolButton(label, CurrentGizmoMode == mode)) CurrentGizmoMode = mode;
	}

	private void UiModeButton(string label, UiToolMode mode)
	{
		if (ToolButton(label, CurrentUiToolMode == mode)) CurrentUiToolMode = mode;
	}

	/// <summary>Shared tool button: highlights while active and always leaves a pending SameLine.</summary>
	private static bool ToolButton(string label, bool active)
	{
		if (active)
		{
			ImGui.PushStyleColor(ImGui.ColButton, ActiveButtonColor);
			ImGui.PushStyleColor(ImGui.ColButtonHovered, ActiveButtonColor);
			ImGui.PushStyleColor(ImGui.ColButtonActive, ActiveButtonColor);
		}
		bool clicked = ImGui.Button(label, 64f, 30f);
		if (active) ImGui.PopStyleColor(3);
		ImGui.SameLine();
		return clicked;
	}

	private void Spawn3D(Builder builder, LotObjectKind kind)
	{
		FreecamController cam = builder.Scene.Freecam;
		Vector3 at = cam.GlobalPosition + (-cam.GlobalTransform.Basis.Z) * 5f;
		LotObject obj = builder.Scene.CreatePrimitiveDetached(kind, at);
		builder.History.Push(new CreateNodeCommand("Create " + kind, obj, builder.Scene.LotRoot,
			builder.Scene.LotRoot.GetChildCount(), b => b.Selection.Select(obj, false)));
	}

	private void SpawnUi(Builder builder, LotUIKind kind)
	{
		Vector2 bounds = builder.Scene.UiCanvasBounds;
		Vector2 size = LotUiRenderer.DefaultSize(kind);
		// Element positions are relative to the picture frame's top-left corner, so the spawn point
		// is half the frame size — adding the frame's screen origin here would offset every element.
		Vector2 pos = bounds * 0.5f - size * 0.5f;
		UiGizmoMath.ClampRect(ref pos, ref size, bounds);

		string label = kind == LotUIKind.Button ? "Button" : (kind == LotUIKind.Text ? "New Label" : "");
		LotUIElement el = builder.Scene.CreateUIElementDetached(kind, pos, size, label);
		builder.History.Push(new CreateNodeCommand("Create " + kind, el, builder.Scene.LotUIRoot,
			builder.Scene.LotUIRoot.GetChildCount(), b => b.Selection.Select(el, false)));
	}
}