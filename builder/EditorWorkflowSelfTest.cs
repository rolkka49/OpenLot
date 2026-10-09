using System.Collections.Generic;
using Godot;

/// <summary>
/// V1 verification for the editor workflow (milestone 2.4): the undo/redo stack, the node-lifetime
/// commands, the transform/property/script commands, and the pure hierarchy drag-drop rules. Runs
/// from BuilderScene._Process after a few physics frames (so Builder.Instance exists), headless
/// under #if DEBUG, in the same style as GizmoSelfTest / NetSelfTest: it prints pass/fail lines and
/// returns its failure count.
///
/// It works on a throwaway scratch container and a PRIVATE EditorHistory wherever possible, so
/// running it leaves the live lot and the real undo stack untouched (the two end-to-end checks that
/// must use the real Builder.History clear it again afterwards).
/// </summary>
public static class EditorWorkflowSelfTest
{
	private static int _checks;
	private static int _failures;

	public static int Run(BuilderScene scene)
	{
		_checks = 0;
		_failures = 0;

		Builder builder = Builder.Instance;
		if (builder == null || scene == null)
		{
			GD.PushError("[EditorWorkflowSelfTest] Builder.Instance is null; suite skipped");
			return 1;
		}

		Node3D scratch = new Node3D();
		scratch.Name = "EditorWorkflowScratch";
		scene.AddChild(scratch);

		TestHistory(builder, scratch);
		TestNodeLifetime(builder, scratch);
		TestMove(builder, scratch);
		TestTransform(builder, scratch);
		TestProperty(builder, scratch);
		TestScriptEdit(builder, scene, scratch);
		TestDropRules(scene, scratch);

		scratch.QueueFree();
		GD.Print("[EditorWorkflowSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
		return _failures;
	}

	// --- Assertions (same shape as the other suites) ---

	private static void Check(string name, bool condition)
	{
		_checks++;
		if (condition)
		{
			GD.Print("[EditorWorkflowSelfTest] PASS  " + name);
		}
		else
		{
			_failures++;
			GD.PrintErr("[EditorWorkflowSelfTest] FAIL  " + name);
		}
	}

	private static void CheckVec(string name, Vector3 actual, Vector3 expected)
	{
		Check(name + " (got " + actual + ", want " + expected + ")", actual.IsEqualApprox(expected));
	}

	private static Node3D MakeGroup(Node parent, string name)
	{
		Node3D group = new Node3D();
		group.Name = name;
		parent.AddChild(group);
		return group;
	}

	// --- The stack itself ---

	private static void TestHistory(Builder builder, Node3D scratch)
	{
		EditorHistory h = new EditorHistory(builder);
		int applied = 0;
		Check("a fresh history starts clean", !h.IsDirty);

		h.Push(new DelegateCommand("A", b => applied += 1, b => applied -= 1));
		Check("push applies the command immediately", applied == 1);
		Check("push makes undo available", h.CanUndo);
		Check("push leaves redo unavailable", !h.CanRedo);
		Check("undo names the pending command", h.UndoLabel == "A");
		Check("an entry without a save point marks the history dirty", h.IsDirty);

		h.Undo();
		Check("undo reverts the command", applied == 0);
		Check("undo makes redo available", h.CanRedo);
		Check("redo names the pending command", h.RedoLabel == "A");

		h.Redo();
		Check("redo re-applies the command", applied == 1);

		h.Push(new DelegateCommand("B", b => applied += 10, b => applied -= 10));
		h.Undo();
		h.Push(new DelegateCommand("C", b => applied += 100, b => applied -= 100));
		Check("a new push cuts the redo branch", !h.CanRedo);
		Check("the cut branch is really gone", h.Count == 2);
		Check("the new command applied after the cut", applied == 101);

		h.Clear();
		Check("clear empties the stack", h.Count == 0 && !h.CanUndo && !h.CanRedo);

		EditorHistory small = new EditorHistory(builder);
		small.Capacity = 2;
		for (int i = 0; i < 5; i++) small.Push(new DelegateCommand("F" + i, b => { }, b => { }));
		Check("capacity trims the oldest entries", small.Count == 2);
		Check("trimming keeps undo available", small.CanUndo);

		EditorHistory save = new EditorHistory(builder);
		save.MarkSavePoint();
		save.Push(new DelegateCommand("D", b => { }, b => { }));
		Check("a new entry marks the history dirty", save.IsDirty);
		save.Undo();
		Check("undoing back to the save point is clean", !save.IsDirty);
		save.Redo();
		save.MarkSavePoint();
		Check("marking a save point clears dirty", !save.IsDirty);
	}

	// --- Node-lifetime commands ---

	private static void TestNodeLifetime(Builder builder, Node3D scratch)
	{
		EditorHistory h = new EditorHistory(builder);

		Node3D created = MakeGroup(scratch, "Created");
		scratch.RemoveChild(created); // CreateNodeCommand takes a detached node
		h.Push(new CreateNodeCommand("Create", created, scratch, 0));
		Check("create attaches the node", created.GetParent() == scratch);

		h.Undo();
		Check("undo detaches the created node", created.GetParent() == null);
		Check("the created node stays alive for redo", GodotObject.IsInstanceValid(created));

		h.Redo();
		Check("redo re-attaches the SAME node", created.GetParent() == scratch);

		Node3D second = MakeGroup(scratch, "Second");
		Node3D third = MakeGroup(scratch, "Third");
		Check("scratch has three children before delete", scratch.GetChildCount() == 3);

		h.Push(new DeleteNodeCommand(second, scratch, second.GetIndex()));
		Check("delete detaches the node", second.GetParent() == null);
		Check("delete keeps the node alive for undo", GodotObject.IsInstanceValid(second));
		Check("scratch has two children after delete", scratch.GetChildCount() == 2);

		h.Undo();
		Check("undo restores the node", second.GetParent() == scratch);
		Check("undo restores its original index", second.GetIndex() == 1 && scratch.GetChild(1) == second);

		h.Redo();
		Check("redo deletes it again", second.GetParent() == null);

		// Dropping the history while the deletion is in effect frees the detached node.
		h.Clear();
		Check("discarding a delete entry frees the detached node", second.IsQueuedForDeletion());

		created.QueueFree();
		third.QueueFree();
	}

	// --- Reorder / reparent ---

	private static void TestMove(Builder builder, Node3D scratch)
	{
		EditorHistory h = new EditorHistory(builder);
		Node3D parentA = MakeGroup(scratch, "ParentA");
		Node3D parentB = MakeGroup(scratch, "ParentB");
		Node3D child = MakeGroup(parentA, "Child");
		Node3D sibling = MakeGroup(parentA, "Sibling");

		Check("child starts under ParentA", child.GetParent() == parentA);
		Check("child starts at index 0", child.GetIndex() == 0);

		h.Push(new MoveNodeCommand("Reorder", child, parentA, 0, parentA, 1));
		Check("reorder moves the child", child.GetIndex() == 1);
		Check("reorder keeps the parent", child.GetParent() == parentA);
		Check("reorder shifts the sibling up", sibling.GetIndex() == 0);

		h.Undo();
		Check("undo restores the original index", child.GetIndex() == 0);
		h.Redo();
		Check("redo reorders again", child.GetIndex() == 1);

		Node3D moved = MakeGroup(parentA, "Moved");
		moved.Position = new Vector3(3f, 0f, 0f);
		Vector3 worldBefore = moved.GlobalPosition;
		h.Push(new MoveNodeCommand("Reparent", moved, parentA, moved.GetIndex(), parentB, 0));
		Check("reparent changes the parent", moved.GetParent() == parentB);
		CheckVec("reparent preserves the world position", moved.GlobalPosition, worldBefore);

		h.Undo();
		Check("undo reparents back", moved.GetParent() == parentA);
		CheckVec("undo reparent preserves the world position too", moved.GlobalPosition, worldBefore);

		Node3D empty = MakeGroup(scratch, "Empty");
		EditTreeOps.MoveToIndex(empty, 7);
		Check("moving to an index in an empty parent is a no-op", empty.GetParent() == scratch);

		parentA.QueueFree();
		parentB.QueueFree();
	}

	// --- Transform ---

	private static void TestTransform(Builder builder, Node3D scratch)
	{
		EditorHistory h = new EditorHistory(builder);
		Node3D node = MakeGroup(scratch, "TransformTarget");
		Transform3D before = node.Transform;
		node.Position = new Vector3(1f, 2f, 3f);
		Transform3D after = node.Transform;

		Check("transform command reports a change", TransformCommand.Changed(before, after));
		Check("identical transforms report no change", !TransformCommand.Changed(after, after));

		h.Push(new TransformCommand("Move", node, before, after));
		CheckVec("push applies the after transform", node.Position, new Vector3(1f, 2f, 3f));
		h.Undo();
		CheckVec("undo restores the before transform", node.Position, Vector3.Zero);
		h.Redo();
		CheckVec("redo reapplies the after transform", node.Position, new Vector3(1f, 2f, 3f));

		node.QueueFree();
	}

	// --- Properties (end-to-end through the real Builder.History) ---

	private static void TestProperty(Builder builder, Node3D scratch)
	{
		LotPropertyDescriptor anchored = new LotPropertyDescriptor
		{
			Id = "selftest_anchored",
			DisplayName = "Anchored",
			Kind = LotPropertyKind.Bool,
			Category = "Test",
			AppliesTo = n => n is LotObject,
			GetBool = n => ((LotObject)n).Anchored,
			SetBool = (n, v) => ((LotObject)n).SetAnchored(v)
		};

		LotObject part = LotObject.Create(LotObjectKind.Cube, "UndoTestPart", new Color(1f, 1f, 1f));
		scratch.AddChild(part);
		Check("part starts anchored", part.Anchored);

		builder.History.Clear();
		builder.Properties.WriteBool(part, anchored, false);
		Check("property write applies the value", !part.Anchored);
		Check("property write adds one history entry", builder.History.Count == 1);

		builder.History.Undo();
		Check("property undo restores the old value", part.Anchored);
		builder.History.Redo();
		Check("property redo reapplies the value", !part.Anchored);

		builder.Properties.WriteBool(part, anchored, false);
		Check("a redundant property write adds no entry", builder.History.Count == 1);

		builder.History.Clear();
		Check("clearing the shared history empties it", builder.History.Count == 0);
		part.QueueFree();
	}

	// --- Code edits + the "only if the script is selected" rule ---

	private static void TestScriptEdit(Builder builder, BuilderScene scene, Node3D scratch)
	{
		const string path = "user://Scripts/__editor_selftest.lua";
		builder.Scripts.SaveScript(path, "v1");
		Check("test script file exists", Godot.FileAccess.FileExists(path));

		builder.History.Clear();
		builder.History.Push(new ScriptEditCommand(path, "v1", "v2"));
		Check("script edit writes the new buffer", ReadFile(path) == "v2");
		builder.History.Undo();
		Check("script undo restores the old buffer", ReadFile(path) == "v1");
		builder.History.Redo();
		Check("script redo reapplies the new buffer", ReadFile(path) == "v2");
		builder.History.Clear();

		LotScriptNode scriptNode = new LotScriptNode();
		scriptNode.ScriptPath = path;
		scriptNode.DisplayName = "__editor_selftest.lua";
		scriptNode.Name = "__editor_selftest_lua";
		scene.LotRoot.AddChild(scriptNode);

		Check("FindScriptNode locates the placeholder", CodeEditorWindow.FindScriptNode(scene.LotRoot, path) == scriptNode);

		builder.Selection.ClearSelection();
		Check("an unselected script is not recorded", !CodeEditorWindow.ShouldRecordScriptEdit(builder, path));
		builder.Selection.Select(scriptNode, false);
		Check("a selected script is recorded", CodeEditorWindow.ShouldRecordScriptEdit(builder, path));
		builder.Selection.ClearSelection();
		Check("deselecting stops recording", !CodeEditorWindow.ShouldRecordScriptEdit(builder, path));

		scene.LotRoot.RemoveChild(scriptNode);
		scriptNode.QueueFree();
		Godot.DirAccess.RemoveAbsolute(path);
	}

	private static string ReadFile(string path)
	{
		Godot.FileAccess file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
		if (file == null) return null;
		string text = file.GetAsText();
		file.Dispose();
		return text;
	}

	// --- Hierarchy drag-drop rules (pure) ---

	private static void TestDropRules(BuilderScene scene, Node3D scratch)
	{
		Check("top band inserts before", HierarchyDrop.ResolvePlacement(100f, 20f, 101f, true) == HierarchyDropPlacement.Before);
		Check("middle band drops into a parent-capable row", HierarchyDrop.ResolvePlacement(100f, 20f, 110f, true) == HierarchyDropPlacement.Into);
		Check("bottom band inserts after", HierarchyDrop.ResolvePlacement(100f, 20f, 119f, true) == HierarchyDropPlacement.After);
		Check("middle band becomes after for a leaf row", HierarchyDrop.ResolvePlacement(100f, 20f, 110f, false) == HierarchyDropPlacement.After);
		Check("a zero-height row falls back", HierarchyDrop.ResolvePlacement(100f, 0f, 110f, true) == HierarchyDropPlacement.Into);

		Node3D parent = MakeGroup(scratch, "DropParent");
		Node3D child = MakeGroup(parent, "DropChild");
		Node3D other = MakeGroup(scratch, "DropOther");

		Node resolvedParent;
		int index;

		bool intoOk = HierarchyDrop.TryResolveTarget(other, parent, HierarchyDropPlacement.Into, out resolvedParent, out index);
		Check("dropping into a group resolves", intoOk && resolvedParent == parent);
		Check("into-placement appends at the end", index == parent.GetChildCount());

		bool beforeOk = HierarchyDrop.TryResolveTarget(other, child, HierarchyDropPlacement.Before, out resolvedParent, out index);
		Check("before-placement targets the sibling parent", beforeOk && resolvedParent == parent);
		Check("before-placement uses the target index", index == 0);

		Check("dropping a node into itself is rejected", !HierarchyDrop.TryResolveTarget(parent, parent, HierarchyDropPlacement.Into, out resolvedParent, out index));
		Check("dropping a parent into its own child is rejected", !HierarchyDrop.TryResolveTarget(parent, child, HierarchyDropPlacement.Into, out resolvedParent, out index));
		Check("crossing the 3D/UI boundary is rejected", !HierarchyDrop.TryResolveTarget(other, MakeUi(scratch, "DropUi"), HierarchyDropPlacement.After, out resolvedParent, out index));

		// The drag feedback must show what will really happen, so the effective placement matters.
		LotUIElement uiSource = MakeUi(scratch, "DropUiSource");
		LotUIElement uiTarget = MakeUi(scratch, "DropUiTarget");
		Node effectiveParent;
		int effectiveIndex;
		HierarchyDropPlacement effective;
		bool uiInto = HierarchyDrop.TryResolveTarget(uiSource, uiTarget, HierarchyDropPlacement.Into, out effectiveParent, out effectiveIndex, out effective);
		Check("into becomes after for a flat UI row", uiInto && effective == HierarchyDropPlacement.After);
		Check("the UI drop lands among siblings", effectiveParent == scratch && effectiveIndex == uiTarget.GetIndex() + 1);

		LotScriptNode script = new LotScriptNode();
		script.Name = "DropScript";
		scratch.AddChild(script);
		Check("dropping onto a script placeholder is rejected", !HierarchyDrop.TryResolveTarget(other, script, HierarchyDropPlacement.After, out resolvedParent, out index));
		Check("dragging a script placeholder is rejected", !HierarchyDrop.TryResolveTarget(script, other, HierarchyDropPlacement.After, out resolvedParent, out index));

		Check("IsAncestor finds a parent", HierarchyDrop.IsAncestor(parent, child));
		Check("IsAncestor is false for a sibling", !HierarchyDrop.IsAncestor(other, child));
		Check("FindByInstanceId finds a nested node", HierarchyDrop.FindByInstanceId(scratch, child.GetInstanceId()) == child);

		parent.QueueFree();
		other.QueueFree();
		script.QueueFree();
	}

	private static LotUIElement MakeUi(Node parent, string name)
	{
		LotUIElement element = LotUIElement.Create(LotUIKind.Frame, Vector2.Zero, new Vector2(10f, 10f), "");
		element.Name = name;
		parent.AddChild(element);
		return element;
	}

}

