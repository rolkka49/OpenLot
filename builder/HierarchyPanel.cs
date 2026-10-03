using System.Collections.Generic;
using Godot;

/// <summary>
/// Immediate-mode scene hierarchy over LotRoot (3D objects) and LotUIRoot (UI elements), ported
/// from Unity's HierarchyPanelController + HierarchyNodeUI. The tree is rebuilt from live scene
/// state every frame — no refresh callbacks or observer nodes are needed; fold state persists
/// through ImGui's per-node id storage. Double-click on a script node opens the code editor
/// (manual double-click timing; the wrapper exposes no double-click query).
/// </summary>
public class HierarchyPanel
{
	private readonly Builder _builder;

	// Reused per-depth scratch buffers so per-frame traversal allocates nothing.
	private readonly List<Node>[] _scratch = new List<Node>[24];

	private double _lastClickTime = -1.0;
	private ulong _lastClickedNode = 0;

	/// <summary>A drawn hierarchy row's screen rectangle, captured so the marquee can hit-test it.</summary>
	private struct RowRect
	{
		public Node Node;
		public Vector2 Min;
		public Vector2 Max;
	}

	// Reused per-frame scratch: one entry per row ImGui actually laid out this frame.
	private readonly List<RowRect> _rows = new List<RowRect>();
	private readonly List<Node> _marqueeHits = new List<Node>();

	private bool _marqueeActive;
	private Vector2 _marqueeStart;
	private bool _marqueeAdditive;

	/// <summary>Screen Y of the window's content top, so a title-bar click does not start a marquee.</summary>
	private float _contentTopY;

	public HierarchyPanel(Builder builder)
	{
		_builder = builder;
		for (int i = 0; i < _scratch.Length; i++) _scratch[i] = new List<Node>();
	}

	public void Draw(Builder builder)
	{
		ImGui.SetNextWindowSize(260f, 760f, ImGui.CondFirstUseEver);
		ImGui.SetNextWindowPos(0f, 30f, ImGui.CondFirstUseEver);

		bool open = ImGui.Begin("Scene Hierarchy");
		if (!open)
		{
			_marqueeActive = false;
			ImGui.End();
			return;
		}

		BuilderScene scene = builder.Scene;

		// Captured before anything is drawn: this is the content top, below the title bar.
		_contentTopY = ImGui.GetCursorScreenPos().Y;

		_rows.Clear();

		ImGui.TextDisabled("3D Objects");
		DrawChildren(scene.LotRoot, 0);
		ImGui.Separator();
		ImGui.TextDisabled("UI Canvas");
		DrawChildren(scene.LotUIRoot, 0);

		DrawMarquee(builder);

		// Right-click on empty space → root menu (Unity parity).
		if (ImGui.BeginPopupContextWindow("hierarchy_bg_ctx", ImGui.PopupMouseButtonRight | ImGui.PopupNoOpenOverItems))
		{
			ContextMenuUI.DrawRootMenu(builder, scene.LotRoot);
			ImGui.EndPopup();
		}
		ImGui.End();
	}

	private void DrawChildren(Node parent, int depth)
	{
		List<Node> buf = _scratch[depth & 23];
		buf.Clear();
		int count = parent.GetChildCount();
		for (int i = 0; i < count; i++) buf.Add(parent.GetChild(i));
		for (int i = 0; i < buf.Count; i++) DrawNode(buf[i], depth);
	}

	private void DrawNode(Node node, int depth)
	{
		if (IsInternalChild(node)) return; // mesh/outline/collision/mover: not a hierarchy entry

		bool hasChildren = HasVisibleChildren(node);
		bool selected = _builder.Selection.IsSelected(node);

		ImGui.PushID((int)node.GetInstanceId());

		if (hasChildren)
		{
			// Expandable row. TreePop is strictly paired with the actual TreeNodeEx return —
			// gating it on our own child-count guess desyncs the binding's tree stack, trips
			// its end-of-frame guard and freezes all builder input (the "Ground still open" crash).
			int flags = ImGui.TreeNodeOpenOnArrow | ImGui.TreeNodeSpanAvailWidth;
			if (selected) flags |= ImGui.TreeNodeSelected;

			bool treeOpen = ImGui.TreeNodeEx(DisplayName(node), flags);
			RecordRow(node);
			HandleClicks(node);
			if (ImGui.BeginPopupContextItem("node_ctx"))
			{
				if (!selected) _builder.Selection.Select(node, false);
				ContextMenuUI.DrawForNode(_builder, node);
				ImGui.EndPopup();
			}
			if (treeOpen)
			{
				DrawChildren(node, depth + 1);
				ImGui.TreePop();
			}
		}
		else
		{
			// Childless rows are plain selectables: nothing is pushed onto the tree stack, so a
			// missing pop is structurally impossible — and clicking selects instead of toggling
			// an empty expandable node.
			ImGui.Selectable(DisplayName(node), selected, ImGui.SelectableSpanAvailWidth);
			RecordRow(node);
			HandleClicks(node);
			if (ImGui.BeginPopupContextItem("node_ctx"))
			{
				if (!selected) _builder.Selection.Select(node, false);
				ContextMenuUI.DrawForNode(_builder, node);
				ImGui.EndPopup();
			}
		}

		ImGui.PopID();
	}

	private static string DisplayName(Node node)
	{
		LotScriptNode script = node as LotScriptNode;
		return script != null ? script.DisplayName : node.Name.ToString();
	}

	private static bool HasVisibleChildren(Node node)
	{
		int count = node.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			if (IsInternalChild(node.GetChild(i))) continue;
			return true;
		}
		return false;
	}

	/// <summary>Captures a row's on-screen rectangle for the marquee hit test.</summary>
	private void RecordRow(Node node)
	{
		_rows.Add(new RowRect { Node = node, Min = ImGui.GetItemRectMin(), Max = ImGui.GetItemRectMax() });
	}

	/// <summary>True for implementation-detail children (mesh, outline, collision body, drag mover).</summary>
	private static bool IsInternalChild(Node node)
	{
		return node.HasMeta(LotObject.InternalChildMeta);
	}

	/// <summary>
	/// Marquee selection: press on empty hierarchy space and drag a rectangle; every row whose
	/// on-screen rect it crosses is selected on release (Ctrl/Shift adds to the existing selection).
	/// </summary>
	private void DrawMarquee(Builder builder)
	{
		Vector2 mouse = ImGui.GetMousePos();

		if (!_marqueeActive
			&& ImGui.IsWindowHovered()
			&& mouse.Y >= _contentTopY
			&& !ImGui.IsAnyItemHovered()
			&& ImGui.IsMouseClicked(ImGui.MouseButtonLeft))
		{
			_marqueeActive = true;
			_marqueeStart = mouse;
			_marqueeAdditive = Input.IsPhysicalKeyPressed(Key.Ctrl) || Input.IsPhysicalKeyPressed(Key.Shift);
		}

		if (!_marqueeActive)
		{
			return;
		}

		if (ImGui.IsMouseDown(ImGui.MouseButtonLeft))
		{
			GetMarqueeRect(mouse, out Vector2 min, out Vector2 max);
			ImGui.DrawRectFilled(min, max, new Color(0.18f, 0.52f, 0.89f, 0.16f));
			ImGui.DrawRect(min, max, new Color(0.18f, 0.52f, 0.89f, 0.85f), 0f, 1f);
			return;
		}

		CommitMarquee(builder, mouse);
		_marqueeActive = false;
	}

	private void CommitMarquee(Builder builder, Vector2 mouse)
	{
		GetMarqueeRect(mouse, out Vector2 min, out Vector2 max);

		_marqueeHits.Clear();
		for (int i = 0; i < _rows.Count; i++)
		{
			RowRect row = _rows[i];
			if (RectsOverlap(row.Min, row.Max, min, max))
			{
				_marqueeHits.Add(row.Node);
			}
		}

		if (_marqueeAdditive)
		{
			builder.Selection.AddToSelection(_marqueeHits);
		}
		else
		{
			builder.Selection.SetSelection(_marqueeHits);
		}
	}

	private void GetMarqueeRect(Vector2 mouse, out Vector2 min, out Vector2 max)
	{
		min = new Vector2(Mathf.Min(_marqueeStart.X, mouse.X), Mathf.Min(_marqueeStart.Y, mouse.Y));
		max = new Vector2(Mathf.Max(_marqueeStart.X, mouse.X), Mathf.Max(_marqueeStart.Y, mouse.Y));
	}

	/// <summary>
	/// Screen-space rectangle overlap for the marquee hit test. Edges that merely touch do not
	/// count as an overlap, so a marquee that stops exactly at a row's border does not select it.
	/// (A marquee can never start on a row — DrawMarquee requires no hovered item — so the
	/// degenerate point-inside-row case is not reachable.)
	/// </summary>
	public static bool RectsOverlap(Vector2 minA, Vector2 maxA, Vector2 minB, Vector2 maxB)
	{
		return minA.X < maxB.X && maxA.X > minB.X && minA.Y < maxB.Y && maxA.Y > minB.Y;
	}

	private void HandleClicks(Node node)
	{
		if (!ImGui.IsItemClicked(ImGui.MouseButtonLeft)) return;

		double now = ImGui.GetTime();
		bool doubleClick = _lastClickedNode == node.GetInstanceId() && now - _lastClickTime < 0.4;
		_lastClickTime = now;
		_lastClickedNode = node.GetInstanceId();

		LotScriptNode script = node as LotScriptNode;
		if (script != null && doubleClick)
		{
			_builder.Scripts.OpenScript(script.ScriptPath);
			_lastClickedNode = 0;
			return;
		}

		bool ctrl = Input.IsPhysicalKeyPressed(Key.Ctrl);
		_builder.Selection.Select(node, ctrl);
	}
}