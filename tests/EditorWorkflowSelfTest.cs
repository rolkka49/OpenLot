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
public static partial class EditorWorkflowSelfTest
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
		TestGroupTransform(builder, scratch);
		TestProperty(builder, scratch);
		TestScriptEdit(builder, scene, scratch);
		TestDropRules(scene, scratch);
		TestEditorWindows(builder);
		TestCodeEditorClipboard();
		TestPasteDelivery(scene);

		scratch.QueueFree();
		GD.Print("[EditorWorkflowSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
		return _failures;
	}

	// --- The editor-window registry (post-3.11 friction review) ---

	private static void TestEditorWindows(Builder builder)
	{
		Check("windows: the registry lists Code Editor then Output (draw order)",
			builder.EditorWindows.Count == 2
			&& builder.EditorWindows[0].Title == "Code Editor"
			&& builder.EditorWindows[1].Title == "Output");

		IEditorWindow output = builder.EditorWindows[1];
		// The Output panel is open at startup: it is where build-mode diagnostics land, so a
		// creator should not have to find it in Add Window before a failure means anything.
		Check("windows: the Output panel starts open", output.IsOpen);
		output.Close(); // from here the Show/Close pair below starts from a known state
		output.Show();
		bool opened = output.IsOpen;
		output.Close();
		Check("windows: Show/Close drive IsOpen", opened && !output.IsOpen);
		output.Show(); // leave it as the editor starts, so later draws match a fresh launch
	}

	// --- Code editor clipboard bridge (pure rules) ---

	private static InputEventKey KeyPress(Key key, bool ctrl, bool meta = false, bool echo = false, bool pressed = true)
	{
		return new InputEventKey { Keycode = key, Pressed = pressed, CtrlPressed = ctrl, MetaPressed = meta, Echo = echo };
	}

	private static void TestCodeEditorClipboard()
	{
		Check("editor clipboard: Ctrl+V asks to paste",
			CodeEditorWindow.ResolveClipboardAction(KeyPress(Key.V, true)) == CodeEditorWindow.ClipboardAction.Paste);
		Check("editor clipboard: Ctrl+C asks to copy",
			CodeEditorWindow.ResolveClipboardAction(KeyPress(Key.C, true)) == CodeEditorWindow.ClipboardAction.Copy);
		Check("editor clipboard: Ctrl+X asks to cut",
			CodeEditorWindow.ResolveClipboardAction(KeyPress(Key.X, true)) == CodeEditorWindow.ClipboardAction.Cut);
		Check("editor clipboard: Ctrl+Shift+C is the console's copy-everything binding",
			CodeEditorWindow.ResolveClipboardAction(
				new InputEventKey { Keycode = Key.C, Pressed = true, CtrlPressed = true, ShiftPressed = true })
				== CodeEditorWindow.ClipboardAction.Copy);
		Check("editor clipboard: Cmd (Meta) counts like Ctrl",
			CodeEditorWindow.ResolveClipboardAction(KeyPress(Key.V, false, true)) == CodeEditorWindow.ClipboardAction.Paste);
		Check("editor clipboard: the unmodified key does nothing",
			CodeEditorWindow.ResolveClipboardAction(KeyPress(Key.V, false)) == CodeEditorWindow.ClipboardAction.None);
		Check("editor clipboard: another Ctrl shortcut is left alone",
			CodeEditorWindow.ResolveClipboardAction(KeyPress(Key.Z, true)) == CodeEditorWindow.ClipboardAction.None
			&& CodeEditorWindow.ResolveClipboardAction(KeyPress(Key.S, true)) == CodeEditorWindow.ClipboardAction.None);
		Check("editor clipboard: a key release does nothing",
			CodeEditorWindow.ResolveClipboardAction(KeyPress(Key.V, true, false, false, false)) == CodeEditorWindow.ClipboardAction.None);
		Check("editor clipboard: a key echo does nothing (holding Ctrl+V pastes once)",
			CodeEditorWindow.ResolveClipboardAction(KeyPress(Key.V, true, false, true)) == CodeEditorWindow.ClipboardAction.None);
		Check("editor clipboard: no event does nothing", CodeEditorWindow.ResolveClipboardAction(null) == CodeEditorWindow.ClipboardAction.None);

		Check("editor clipboard: CRLF and lone CR become newlines",
			CodeEditorWindow.NormalizeClipboardText("a\r\nb\rc") == "a\nb\nc");
		Check("editor clipboard: null/empty normalize to empty",
			CodeEditorWindow.NormalizeClipboardText(null) == "" && CodeEditorWindow.NormalizeClipboardText("") == "");

		List<CodeEditorWindow.Keystroke> steps = CodeEditorWindow.BuildPasteKeystrokes("a\nb");
		Check("editor clipboard: text becomes keystrokes with Enter for the newline",
			steps.Count == 3
			&& steps[0].Key == Key.None && steps[0].Unicode == 'a'
			&& steps[1].Key == Key.Enter
			&& steps[2].Unicode == 'b');
		Check("editor clipboard: control characters are not typed as characters",
			CodeEditorWindow.BuildPasteKeystrokes("a\tb").Count == 2
			&& CodeEditorWindow.BuildPasteKeystrokes("\t").Count == 0);
	}

	/// <summary>
	/// The one engine assumption the paste bridge rests on: a key event fed through
	/// <see cref="Input.ParseInputEvent"/> really is delivered to nodes' <c>_Input</c>. The ImGui
	/// addon is such a node, so this is what lets queued paste keystrokes reach the focused field —
	/// the same class of spike `InputSelfTest` runs for the action layer.
	/// </summary>
	private sealed partial class KeyDeliveryProbe : Node
	{
		public int KeyPresses;
		public int Characters;

		public override void _Input(InputEvent @event)
		{
			InputEventKey key = @event as InputEventKey;
			if (key == null || !key.Pressed) return;
			KeyPresses++;
			if (key.Unicode != 0) Characters++;
		}
	}

	private static void TestPasteDelivery(BuilderScene scene)
	{
		KeyDeliveryProbe probe = new KeyDeliveryProbe();
		probe.Name = "ClipboardDeliveryProbe";
		scene.AddChild(probe);
		// Godot enables input processing for an overridden _Input; the explicit call is here so the
		// probe can never be the silent reason this check passes trivially.
		probe.SetProcessInput(true);

		// Driver: the real bridge, from the clipboard entry point's inner half through Tick — the
		// same two calls Builder._Input and Builder._Process make.
		CodeEditorWindow editor = new CodeEditorWindow(Builder.Instance);
		editor.PasteText("hi\n");
		editor.Tick(0.0);

		Check("editor clipboard: a queued paste is delivered to nodes' _Input as typed keystrokes (" +
			probe.Characters + " characters, " + probe.KeyPresses + " key presses)",
			probe.Characters == 2 && probe.KeyPresses == 3);

		int before = probe.KeyPresses;
		editor.PasteText("");
		editor.Tick(0.0);
		Check("editor clipboard: an empty paste types nothing", probe.KeyPresses == before);

		probe.QueueFree();
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

		h.Push(new DeleteNodesCommand(new Node[] { second }));
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

		// A multi-selection delete is ONE entry that takes the whole set (the reported bug: only one
		// item went). Four more siblings, on top of the surviving "created" and "third".
		Node3D a = MakeGroup(scratch, "MultiA");
		Node3D b = MakeGroup(scratch, "MultiB");
		Node3D c = MakeGroup(scratch, "MultiC");
		Node3D d = MakeGroup(scratch, "MultiD");
		Check("six siblings to select", scratch.GetChildCount() == 6); // created, third, a, b, c, d
		h.Push(new DeleteNodesCommand(new Node[] { c, a }));
		Check("a two-node delete takes both", a.GetParent() == null && c.GetParent() == null);
		Check("a two-node delete leaves the others", b.GetParent() == scratch && d.GetParent() == scratch);
		Check("a multi-delete names the set in the history label",
			h.UndoLabel != null && h.UndoLabel.Contains("2"));

		h.Undo();
		Check("one undo restores the whole set", a.GetParent() == scratch && c.GetParent() == scratch);
		Check("one undo restores both indices (a at 2, c at 4)",
			a.GetIndex() == 2 && c.GetIndex() == 4
			&& scratch.GetChild(2) == a && scratch.GetChild(4) == c);

		h.Redo();
		Check("redo takes the set again", a.GetParent() == null && c.GetParent() == null && scratch.GetChildCount() == 4);

		// A node inside another candidate's subtree needs no entry of its own: detaching the ancestor
		// takes it, and its own recorded index would be meaningless. This one goes through the real
		// scene entry point (BuilderScene.DeleteNodes → the real history), so the real history is
		// cleared again afterwards.
		Node3D parentGroup = MakeGroup(scratch, "MultiParent");
		Node3D childGroup = MakeGroup(parentGroup, "MultiChild");
		builder.Scene.DeleteNodes(new List<Node> { parentGroup, childGroup });
		Check("deleting a parent and its child in one set is one entry taking the subtree",
			parentGroup.GetParent() == null && childGroup.GetParent() == parentGroup);
		builder.History.Undo();
		Check("one undo brings the pair back", parentGroup.GetParent() == scratch && childGroup.GetParent() == parentGroup);
		builder.History.Clear();

		h.Clear();
		a.QueueFree();
		b.QueueFree();
		c.QueueFree();
		d.QueueFree();
		parentGroup.QueueFree();
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

	/// <summary>
	/// The multi-object transform entry (the multi-select gizmo's undo): one command covering every
	/// member, so a group drag is a single Ctrl+Z that restores positions, rotations and the
	/// authored sizes of sized parts together.
	/// </summary>
	private static void TestGroupTransform(Builder builder, Node3D scratch)
	{
		EditorHistory h = new EditorHistory(builder);
		Node3D first = MakeGroup(scratch, "GroupTransformA");
		Node3D second = MakeGroup(scratch, "GroupTransformB");
		LotObject sized = LotObject.Create(LotObjectKind.Cube, "GroupTransformSized", new Color(1f, 1f, 1f));
		scratch.AddChild(sized);

		Transform3D beforeFirst = first.Transform;
		Transform3D beforeSecond = second.Transform;
		Transform3D beforeSized = sized.Transform;
		Vector3 beforeSize = sized.Size;

		first.Position = new Vector3(1f, 0f, 0f);
		second.Position = new Vector3(0f, 2f, 0f);
		sized.Position = new Vector3(0f, 0f, 3f);
		sized.SetSize(new Vector3(2f, 2f, 2f));

		TransformNodesCommand command = new TransformNodesCommand();
		command.Add(first, beforeFirst, first.Transform, null, Vector3.One, Vector3.One);
		command.Add(second, beforeSecond, second.Transform, null, Vector3.One, Vector3.One);
		command.Add(sized, beforeSized, sized.Transform, sized, beforeSize, sized.Size);
		command.FinishLabel("Move");
		Check("group transform: the entry counts its members", command.Count == 3);
		Check("group transform: the label names the operation and the count", command.Label == "Move 3 Objects");

		h.Push(command);
		h.Undo();
		CheckVec("group transform: undo restores the first member", first.Position, Vector3.Zero);
		CheckVec("group transform: undo restores the second member", second.Position, Vector3.Zero);
		CheckVec("group transform: undo restores the sized member's position", sized.Position, Vector3.Zero);
		CheckVec("group transform: undo restores the sized member's size", sized.Size, beforeSize);
		h.Redo();
		CheckVec("group transform: redo reapplies the first member", first.Position, new Vector3(1f, 0f, 0f));
		CheckVec("group transform: redo reapplies the sized member's position", sized.Position, new Vector3(0f, 0f, 3f));
		CheckVec("group transform: redo reapplies the sized member's size", sized.Size, new Vector3(2f, 2f, 2f));

		// A one-member entry keeps the plain label (a single-object drag routed through it).
		TransformNodesCommand single = new TransformNodesCommand();
		single.Add(first, beforeFirst, beforeFirst, null, Vector3.One, Vector3.One);
		single.FinishLabel("Scale");
		Check("group transform: a one-member entry is labelled without a count", single.Label == "Scale");

		first.QueueFree();
		second.QueueFree();
		sized.QueueFree();
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

