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

	// Reused buffer for the base-content section's top nodes (milestone 4.1), so listing them allocates
	// nothing per frame.
	private readonly List<Node> _secretRoots = new List<Node>();

	/// <summary>Title-bar-only window movement; the body is reserved for the marquee (see the type).</summary>
	private readonly TitleBarWindowDrag _drag = new TitleBarWindowDrag();

	/// <summary>
	/// Fraction of a row's width that stays interactive (selectable, draggable, right-clickable). The
	/// remaining right-hand part is deliberately NOT part of any row item, so a press there is empty
	/// space and starts a marquee instead of grabbing the row.
	///
	/// This split exists because <see cref="ImGui.Selectable"/> auto-spans the full available width when
	/// its size is 0, so a row item otherwise covers its whole line and swallows the marquee press — and
	/// dragging it moves the node, which is what the split prevents.
	/// </summary>
	private const float RowTitleFraction = 0.5f;

	/// <summary>Right-hand, non-interactive bands recorded this frame for the marquee's start test.
	/// Only tree rows need a real item there (see <see cref="DrawNode"/>); leaf rows leave genuine
	/// empty space, which the marquee already accepts.</summary>
	private readonly List<Rect2> _marqueeZones = new List<Rect2>();

	private bool _marqueeActive;
	private Vector2 _marqueeStart;
	private bool _marqueeAdditive;

	/// <summary>Screen Y of the window's content top, so a title-bar click does not start a marquee.</summary>
	private float _contentTopY;

	/// <summary>ImGui drag-drop payload type for hierarchy rows (carries the node's instance id).</summary>
	private const string DragPayload = "openlot_hierarchy_node";

	// Drag feedback state: which node is being dragged and whether a drag is in progress. The source
	// is kept so the feedback can show exactly where the drop is allowed to land.
	private Node _dragSourceNode;
	private bool _dragActive;

	private static readonly Color DropAccent = new Color(0.18f, 0.52f, 0.89f, 1f);
	private static readonly Color DropIntoFill = new Color(0.18f, 0.52f, 0.89f, 0.30f);

	public HierarchyPanel(Builder builder)
	{
		_builder = builder;
		for (int i = 0; i < _scratch.Length; i++) _scratch[i] = new List<Node>();
	}

	public void Draw(Builder builder)
	{
		ImGui.SetNextWindowSize(260f, 760f, ImGui.CondFirstUseEver);
		// The window is begun with NoMove and re-granted movement for its title bar only, so dragging
		// inside the body is free for the marquee (see TitleBarWindowDrag for why the addon cannot use
		// ImGui's own title-bar-only option).
		_drag.BeforeBegin(new Vector2(0f, 30f));

		bool open = ImGui.Begin("Scene Hierarchy", EditorChrome.PanelWindowFlags | ImGui.WindowNoMove);
		_drag.AfterBegin(open);
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
		_marqueeZones.Clear();

		// A drag ends when the mouse button comes up: drop the feedback state for that frame.
		if (!ImGui.IsMouseDown(ImGui.MouseButtonLeft))
		{
			_dragActive = false;
			_dragSourceNode = null;
		}

		ImGui.TextDisabled("3D Objects");
		DrawChildren(scene.LotRoot, 0, false);
		ImGui.Separator();
		ImGui.TextDisabled("UI Canvas");
		DrawChildren(scene.LotUIRoot, 0, false);

		// The lot's mechanical links (§3.6) list under the tree, before the fenced-off base-content
		// section — they are creator content, unlike the base content below them.
		DrawLinksSection(builder);

		// Base content is drawn below, in its own fenced-off section, never inline with the creator's
		// own nodes. Drawn before the marquee so its rows take part in marquee hit-testing.
		DrawSecretSection(scene);

		DrawMarquee(builder);

		// Right-click on empty space → root menu (Unity parity).
		if (ImGui.BeginPopupContextWindow("hierarchy_bg_ctx", ImGui.PopupMouseButtonRight | ImGui.PopupNoOpenOverItems))
		{
			ContextMenuUI.DrawRootMenu(builder, scene.LotRoot);
			ImGui.EndPopup();
		}
		ImGui.End();
	}

	/// <summary>
	/// The lot's mechanical links (§3.6), listed under the tree. Welds and hinges are records in
	/// <see cref="LotConstraints"/> — lot-level state, not scene nodes — so they are drawn here as
	/// a view over that one table (the same way the base-content section is a view over the
	/// archive's secret paths) instead of as duplicated placeholder nodes with their own copy of
	/// the data to drift out of sync. Click selects the first part, whose Inspector section edits
	/// the link; right-click offers Select parts / Toggle / Remove.
	/// </summary>
	private void DrawLinksSection(Builder builder)
	{
		IReadOnlyList<LotConstraintRecord> links = LotConstraints.All;
		if (links.Count == 0) return;

		ImGui.Separator();
		ImGui.TextDisabled("Links (" + links.Count + ")");
		for (int i = 0; i < links.Count; i++) DrawLinkRow(builder, links[i]);
	}

	private void DrawLinkRow(Builder builder, LotConstraintRecord record)
	{
		string label = LinkLabel(builder, record) + "##link" + record.Id;
		Vector2 size = new Vector2(ImGui.GetContentRegionAvail().X * RowTitleFraction, 0f);
		if (ImGui.Selectable(label, false, 0, size))
		{
			Node partA = builder.Scene.GetByHandle(record.A);
			if (partA != null) builder.Selection.Select(partA, false);
		}

		if (ImGui.BeginPopupContextItem())
		{
			if (ImGui.MenuItem("Select parts", "", false, true)) SelectLinkParts(builder, record);
			if (ImGui.MenuItem(record.Enabled ? "Disable link" : "Enable link", "", false, true))
				ToggleLink(builder, record);
			if (ImGui.MenuItem("Remove link", "", false, true)) RemoveLink(builder, record);
			ImGui.EndPopup();
		}
	}

	private static void SelectLinkParts(Builder builder, LotConstraintRecord record)
	{
		Node partA = builder.Scene.GetByHandle(record.A);
		if (partA != null) builder.Selection.Select(partA, false);
		Node partB = record.B == LotConstraintRecord.WorldHandle ? null : builder.Scene.GetByHandle(record.B);
		if (partB != null) builder.Selection.Select(partB, true);
	}

	private static void ToggleLink(Builder builder, LotConstraintRecord record)
	{
		bool after = !record.Enabled;
		builder.History.Push(new DelegateCommand(after ? "Enable Link" : "Disable Link",
			b => { record.Enabled = after; if (b.Scene != null) b.Scene.RebuildConstraints(); },
			b => { record.Enabled = !after; if (b.Scene != null) b.Scene.RebuildConstraints(); }));
	}

	private static void RemoveLink(Builder builder, LotConstraintRecord record)
	{
		builder.History.Push(new DelegateCommand("Remove Link",
			b => { LotConstraints.Remove(record.Id); if (b.Scene != null) b.Scene.RebuildConstraints(); },
			b => { LotConstraints.Add(record); if (b.Scene != null) b.Scene.RebuildConstraints(); }));
	}

	private static string LinkLabel(Builder builder, LotConstraintRecord record)
	{
		string kind = record.Kind == LotConstraintKind.Weld ? "Weld" : "Hinge";
		string a = LinkName(builder, record.A);
		string b = record.B == LotConstraintRecord.WorldHandle ? "world" : LinkName(builder, record.B);
		return kind + ": " + a + " → " + b + (record.Enabled ? "" : "  (off)");
	}

	private static string LinkName(Builder builder, int handle)
	{
		Node node = builder.Scene.GetByHandle(handle);
		return node != null ? node.Name.ToString() : "missing";
	}

	/// <summary>
	/// The fenced-off base-content section (milestone 4.1). Base content is never listed inline with
	/// a creator's own nodes — it appears here instead, below a separate heading and caution, and
	/// only once the reveal has been confirmed. Keeping the two lists physically apart is what stops
	/// a shared, proprietary file from being edited by muscle memory alongside the creator's own work.
	///
	/// Rows are drawn with the same <see cref="DrawNode"/> as the main tree, so selecting, renaming
	/// (where allowed), dragging and the context menu all behave identically once revealed.
	/// </summary>
	private void DrawSecretSection(BuilderScene scene)
	{
		if (!SecretContent.Revealed) return;

		_secretRoots.Clear();
		CollectSecretRoots(scene.LotRoot, _secretRoots);
		CollectSecretRoots(scene.LotUIRoot, _secretRoots);
		// No base content in this lot: draw nothing at all rather than an empty heading.
		if (_secretRoots.Count == 0) return;

		ImGui.Spacing();
		ImGui.Separator();
		ImGui.Spacing();
		ImGui.TextColored(SecretContent.CautionColor, SecretContent.SectionHeading);
		ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
		ImGui.TextDisabled(SecretContent.SectionNote);
		ImGui.PopTextWrapPos();
		ImGui.Spacing();

		for (int i = 0; i < _secretRoots.Count; i++) DrawNode(_secretRoots[i], 0, true);
	}

	/// <summary>
	/// Collects the topmost base-content nodes — a node is a root when it is base content and no
	/// ancestor is, so a whole base subtree is rendered once, under its own top node, rather than being
	/// flattened. Recursion stops at a match for the same reason.
	///
	/// Public (and free of ImGui) so a self-test can prove the section finds the base script in a real
	/// tree — the failure this method exists to avoid is a section that draws nothing.
	/// </summary>
	public static void CollectSecretRoots(Node parent, List<Node> into)
	{
		int count = parent.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			Node child = parent.GetChild(i);
			if (IsInternalChild(child)) continue;
			if (SecretContent.IsSecretContent(child))
			{
				into.Add(child);
				continue;
			}
			CollectSecretRoots(child, into);
		}
	}


	private void DrawChildren(Node parent, int depth, bool inSecretSection)
	{
		List<Node> buf = _scratch[depth & 23];
		buf.Clear();
		int count = parent.GetChildCount();
		for (int i = 0; i < count; i++) buf.Add(parent.GetChild(i));
		for (int i = 0; i < buf.Count; i++) DrawNode(buf[i], depth, inSecretSection);
	}

	/// <summary>
	/// One hierarchy row. <paramref name="inSecretSection"/> is true only while the base-content section
	/// is being drawn: base content is excluded from the main tree, so without the flag its own section
	/// would draw nothing at all (the guard below would reject exactly the rows it is meant to show).
	/// </summary>
	private void DrawNode(Node node, int depth, bool inSecretSection)
	{
		if (IsInternalChild(node)) return; // mesh/outline/collision/mover: not a hierarchy entry
		if (SecretContent.IsSecretContent(node) && !inSecretSection) return; // drawn only in its own section

		bool hasChildren = HasVisibleChildren(node);
		bool selected = _builder.Selection.IsSelected(node);

		ImGui.PushID((int)node.GetInstanceId());

		if (hasChildren)
		{
			// Expandable row. TreePop is strictly paired with the actual TreeNodeEx return —
			// gating it on our own child-count guess desyncs the binding's tree stack, trips
			// its end-of-frame guard and freezes all builder input (the "Ground still open" crash).
			//
			// TreeNodeEx takes no size, so the row cannot be narrowed directly; instead the row is
			// flagged AllowItemOverlap and a real item is laid over its right half below, which is
			// what keeps the marquee usable from that side (see DrawRowMarqueeZone).
			int flags = ImGui.TreeNodeOpenOnArrow | ImGui.TreeNodeAllowItemOverlap;
			if (selected) flags |= ImGui.TreeNodeSelected;

			bool treeOpen = ImGui.TreeNodeEx(DisplayName(node), flags);
			Vector2 rowMin = ImGui.GetItemRectMin();
			RecordRow(node);
			HandleClicks(node);
			DrawDragDrop(node);
			if (ImGui.BeginPopupContextItem("node_ctx"))
			{
				if (!selected) _builder.Selection.Select(node, false);
				ContextMenuUI.DrawForNode(_builder, node);
				ImGui.EndPopup();
			}
			DrawRowMarqueeZone(rowMin);
			if (treeOpen)
			{
				DrawChildren(node, depth + 1, inSecretSection);
				ImGui.TreePop();
			}
		}
		else
		{
			// Childless rows are plain selectables: nothing is pushed onto the tree stack, so a
			// missing pop is structurally impossible — and clicking selects instead of toggling
			// an empty expandable node.
			//
			// Sized to the left half explicitly, because Selectable auto-spans the available width when
			// its size is 0 — left spanning, the row item would cover its whole line, and a press beside
			// the title would grab the row (starting a drag) instead of starting a marquee. The right
			// half is left as genuine empty space, which the marquee already accepts.
			float titleWidth = ImGui.GetContentRegionAvail().X * RowTitleFraction;
			ImGui.Selectable(DisplayName(node), selected, 0, new Vector2(titleWidth, 0f));
			RecordRow(node);
			HandleClicks(node);
			DrawDragDrop(node);
			if (ImGui.BeginPopupContextItem("node_ctx"))
			{
				if (!selected) _builder.Selection.Select(node, false);
				ContextMenuUI.DrawForNode(_builder, node);
				ImGui.EndPopup();
			}
		}

		ImGui.PopID();
	}

	/// <summary>
	/// Lays a non-interactive band over a tree row's right half and records it for the marquee.
	///
	/// Needed only for tree rows: a leaf row is narrowed by its own size, but <see cref="ImGui.TreeNodeEx"/>
	/// takes no size, so an <see cref="ImGui.InvisibleButton"/> is drawn over the right half of the row it
	/// was flagged AllowItemOverlap for. The button carries no behaviour of its own — it exists so a press
	/// there belongs to neither the row (which would select, drag or toggle it) nor the window (which
	/// would move it), leaving the marquee free to claim the gesture.
	/// </summary>
	private void DrawRowMarqueeZone(Vector2 rowMin)
	{
		float rightEdge = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
		float zoneX = rowMin.X + (rightEdge - rowMin.X) * RowTitleFraction;
		float zoneWidth = rightEdge - zoneX;
		if (zoneWidth <= 0f) return;

		float lineHeight = ImGui.GetTextLineHeight();
		ImGui.SameLine(0f, 0f);
		ImGui.SetCursorScreenPos(new Vector2(zoneX, rowMin.Y));
		ImGui.InvisibleButton("##marquee_zone", new Vector2(zoneWidth, lineHeight));

		_marqueeZones.Add(new Rect2(new Vector2(zoneX, rowMin.Y), new Vector2(zoneWidth, lineHeight)));
	}

	/// <summary>
	/// Drag-and-drop for a hierarchy row (milestone 2.4): the row is a drag source carrying its
	/// instance id and a drop target resolving to reorder/reparent. Must be called right after the
	/// row item, while it is still "the last item".
	/// </summary>
	private void DrawDragDrop(Node node)
	{
		if (IsInternalChild(node)) return;

		if (ImGui.BeginDragDropSource(ImGui.DragDropSourceAllowNullID))
		{
			_dragActive = true;
			_dragSourceNode = node;
			ImGui.SetDragDropPayload(DragPayload, node.GetInstanceId().ToString());
			ImGui.Text(DisplayName(node));
			ImGui.EndDragDropSource();
		}

		if (ImGui.BeginDragDropTarget())
		{
			// Only our own row drag is in flight, so the hovered target can show what a release would
			// do: an outline around the row for "into", a line between rows for "before/after".
			if (_dragActive && _dragSourceNode != null)
			{
				Vector2 min = ImGui.GetItemRectMin();
				Vector2 max = ImGui.GetItemRectMax();
				bool acceptsChildren = !(node is LotUIElement) && !(node is LotScriptNode);
				HierarchyDropPlacement placement = HierarchyDrop.ResolvePlacement(min.Y, max.Y - min.Y, ImGui.GetMousePos().Y, acceptsChildren);
				DrawDropFeedback(min, max, _dragSourceNode, node, placement);
			}

			string payload = ImGui.AcceptDragDropPayload(DragPayload);
			if (!string.IsNullOrEmpty(payload)) CompleteDrop(payload, node);
			ImGui.EndDragDropTarget();
		}
	}

	/// <summary>
	/// Draws where the drop would land, using the same resolution the drop itself will use — so an
	/// invalid target (own subtree, script row, cross-boundary) shows nothing at all.
	/// </summary>
	private static void DrawDropFeedback(Vector2 min, Vector2 max, Node source, Node target, HierarchyDropPlacement placement)
	{
		Node parent;
		int index;
		HierarchyDropPlacement effective;
		if (!HierarchyDrop.TryResolveTarget(source, target, placement, out parent, out index, out effective)) return;

		if (effective == HierarchyDropPlacement.Into)
		{
			// Highlight the row the node is about to become a child of.
			ImGui.DrawRectFilled(min, max, DropIntoFill);
			ImGui.DrawRect(min, max, DropAccent, 0f, 1f);
			return;
		}

		// Insertion line on the edge the node will be inserted at.
		float y = effective == HierarchyDropPlacement.Before ? min.Y : max.Y;
		ImGui.DrawLine(new Vector2(min.X, y), new Vector2(max.X, y), DropAccent, 2f);
	}

	/// <summary>Applies a completed drop as one undoable move (reorder or reparent).</summary>
	private void CompleteDrop(string payload, Node target)
	{
		if (!ulong.TryParse(payload, out ulong sourceId)) return;
		Node source = HierarchyDrop.FindByInstanceId(_builder.Scene.LotRoot, sourceId)
			?? HierarchyDrop.FindByInstanceId(_builder.Scene.LotUIRoot, sourceId);
		if (source == null) return;

		Vector2 min = ImGui.GetItemRectMin();
		Vector2 max = ImGui.GetItemRectMax();
		bool acceptsChildren = !(target is LotUIElement) && !(target is LotScriptNode);
		HierarchyDropPlacement placement = HierarchyDrop.ResolvePlacement(min.Y, max.Y - min.Y, ImGui.GetMousePos().Y, acceptsChildren);

		if (!HierarchyDrop.TryResolveTarget(source, target, placement, out Node parent, out int index)) return;

		Node oldParent = source.GetParent();
		if (oldParent == null) return;
		int oldIndex = source.GetIndex();
		if (oldParent == parent && oldIndex == index) return;

		string label = oldParent == parent ? "Reorder" : "Reparent";
		_builder.History.Push(new MoveNodeCommand(label, source, oldParent, oldIndex, parent, index));
	}

	private static string DisplayName(Node node)
	{
		LotScriptNode script = node as LotScriptNode;
		if (script == null) return node.Name.ToString();
		// Base content is marked once it is revealed, so a creator always knows when the row in front of
		// them is a shared file rather than their own.
		return SecretContent.IsBaseContent(script) ? SecretContent.RowMarker + script.DisplayName : script.DisplayName;
	}

	/// <summary>
	/// Whether a node's row should show an expand arrow. Nodes excluded from the main tree (internal
	/// children, and base content, which lives in its own section) do not count as children, or a parent
	/// whose only child is excluded would offer an arrow that expands to nothing.
	/// </summary>
	private static bool HasVisibleChildren(Node node)
	{
		int count = node.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			Node child = node.GetChild(i);
			if (IsInternalChild(child)) continue;
			if (SecretContent.IsSecretContent(child)) continue;
			return true;
		}
		return false;
	}

	/// <summary>
	/// Captures a row's on-screen rectangle for the marquee hit test.
	///
	/// The row item covers the title only (that is what may be dragged), but the marquee hit rect is
	/// widened to the panel's right edge: dragging a selection box across that row line should still
	/// pick the row up, even though the empty space beside the title is not part of the row's item.
	/// </summary>
	private void RecordRow(Node node)
	{
		Vector2 min = ImGui.GetItemRectMin();
		Vector2 max = ImGui.GetItemRectMax();
		max.X = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
		_rows.Add(new RowRect { Node = node, Min = min, Max = max });
	}

	/// <summary>True for implementation-detail children (mesh, outline, collision body, drag mover).</summary>
	private static bool IsInternalChild(Node node)
	{
		return node.HasMeta(LotObject.InternalChildMeta);
	}

	/// <summary>
	/// Marquee selection: press on a row's right-hand band — or on empty space — and drag a rectangle;
	/// every row whose on-screen rect it crosses is selected on release (Ctrl/Shift adds to the existing
	/// selection).
	///
	/// The press test is explicit geometry rather than "nothing is hovered", because a tree row's right
	/// half carries a filler item (<see cref="DrawRowMarqueeZone"/>) that exists purely to keep the press
	/// away from the row and the window — so hovering it must still count as a marquee press.
	/// </summary>
	private void DrawMarquee(Builder builder)
	{
		Vector2 mouse = ImGui.GetMousePos();

		if (!_marqueeActive
			&& ImGui.IsWindowHovered()
			&& mouse.Y >= _contentTopY
			&& IsMarqueePress(mouse)
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

	/// <summary>
	/// True when a press at <paramref name="point"/> belongs to the marquee rather than to a row: it
	/// landed in a row's right-hand band, or on empty space (nothing in the panel is hovered there, so
	/// there is no row to grab).
	/// </summary>
	private bool IsMarqueePress(Vector2 point)
	{
		for (int i = 0; i < _marqueeZones.Count; i++)
		{
			if (_marqueeZones[i].HasPoint(point)) return true;
		}
		return !ImGui.IsAnyItemHovered();
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