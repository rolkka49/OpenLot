using Godot;

/// <summary>3D transform tool modes (Unity ToolboxController parity). The Im3d-ported gizmo handles
/// in GizmoController implement Translate/Rotate/Scale; Select means "no gizmo, direct part drag".</summary>
public enum GizmoMode
{
	Select,
	Translate,
	Rotate,
	Scale
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

	public ToolboxPanel(Builder builder)
	{
		_builder = builder;
	}

	public void Draw(Builder builder)
	{
		ImGui.SetNextWindowSize(280f, 240f, ImGui.CondFirstUseEver);
		ImGui.SetNextWindowPos(1260f, 30f, ImGui.CondFirstUseEver);

		bool open = ImGui.Begin("Toolbox");
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
			else if (selected != null) DrawGizmoTools();
			else DrawSpawnPanel(builder);
		}

		ImGui.End();
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

	private void DrawGizmoTools()
	{
		ImGui.Text("Transform Tool");
		ImGui.Separator();
		ModeButton("Select", GizmoMode.Select);
		ModeButton("Move", GizmoMode.Translate);
		ModeButton("Rotate", GizmoMode.Rotate);
		ModeButton("Scale", GizmoMode.Scale);
		ImGui.TextDisabled("Select drags parts; the other tools use the gizmo handles.");
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
		LotObject obj = builder.Scene.SpawnPrimitive(kind, at);
		builder.Selection.Select(obj, false);
		builder.MarkDirty();
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
		LotUIElement el = builder.Scene.SpawnUIElement(kind, pos, size, label);
		builder.Selection.Select(el, false);
		builder.MarkDirty();
	}
}