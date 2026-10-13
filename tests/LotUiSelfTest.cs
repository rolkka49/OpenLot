using System.Collections.Generic;
using Godot;

/// <summary>
/// Verification for milestone 5.3 (lot UI customization): the new element kinds and their state
/// rules, the declared customization properties, the nesting/clip math behind scroll containers,
/// the archive round-trip of a nested tree, and the real bound Lua verbs.
///
/// Follows the project's self-test convention: no framework, prints PASS/FAIL lines, returns the
/// failure count. Runs from <c>BuilderScene._Ready</c> under #if DEBUG. It touches the live VM (to
/// drive the bound verbs) but not <see cref="Builder"/>, which does not exist yet at that point —
/// the same split <see cref="LotPropertySelfTest"/> uses. Everything it spawns it destroys again,
/// so the suites that follow see the lot unchanged.
/// </summary>
public static class LotUiSelfTest
{
	private static int _failures;
	private static int _checks;

	public static int Run()
	{
		_failures = 0;
		_checks = 0;

		TestKindDefaults();
		TestOptionsParsing();
		TestCheckboxAndSelection();
		TestTextAlign();
		TestDeclarations();
		TestDescriptorAccessors();
		TestNestingMath();
		TestArchiveNesting();
		TestLuaBindings();

		GD.Print("[LotUiSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
		return _failures;
	}

	private static void TestKindDefaults()
	{
		Vector2 checkbox = LotUiRenderer.DefaultSize(LotUIKind.Checkbox);
		Vector2 dropdown = LotUiRenderer.DefaultSize(LotUIKind.Dropdown);
		Vector2 container = LotUiRenderer.DefaultSize(LotUIKind.ScrollContainer);
		Check("kinds: every new kind has a usable default size",
			checkbox.X > 0f && checkbox.Y > 0f && dropdown.X > 0f && dropdown.Y > 0f
			&& container.X > 0f && container.Y > 0f);
		Check("kinds: the new defaults are distinct from the fallback",
			container.X != 160f || container.Y != 20f);

		Check("kinds: the new kinds have a visible default color",
			LotUiRenderer.DefaultColor(LotUIKind.Checkbox).A > 0f
			&& LotUiRenderer.DefaultColor(LotUIKind.Dropdown).A > 0f
			&& LotUiRenderer.DefaultColor(LotUIKind.ScrollContainer).A > 0f);

		Check("kinds: interactivity is Button/Checkbox/Dropdown only",
			LotUiRenderer.IsInteractive(LotUIKind.Button) && LotUiRenderer.IsInteractive(LotUIKind.Checkbox)
			&& LotUiRenderer.IsInteractive(LotUIKind.Dropdown)
			&& !LotUiRenderer.IsInteractive(LotUIKind.Frame) && !LotUiRenderer.IsInteractive(LotUIKind.Text)
			&& !LotUiRenderer.IsInteractive(LotUIKind.Scrollbar)
			&& !LotUiRenderer.IsInteractive(LotUIKind.ScrollContainer));

		// The default alignment preserves the pre-5.3 look: a button's caption was always centred,
		// everything else drew at its left edge.
		LotUIElement button = LotUIElement.Create(LotUIKind.Button, Vector2.Zero, new Vector2(80f, 30f), "B");
		LotUIElement text = LotUIElement.Create(LotUIKind.Text, Vector2.Zero, new Vector2(80f, 30f), "t");
		Check("kinds: a button's label is centred by default (the old look)",
			button.TextAlign == LotUITextAlign.Center);
		Check("kinds: text starts left-aligned (the old look)",
			text.TextAlign == LotUITextAlign.Left);
		button.Free();
		text.Free();
	}

	private static void TestOptionsParsing()
	{
		Check("options: a pipe-separated list splits",
			LotUIElement.ParseOptions("A|B|C").Count == 3);
		Check("options: whitespace is trimmed",
			LotUIElement.ParseOptions(" A | B ")[0] == "A" && LotUIElement.ParseOptions(" A | B ")[1] == "B");
		Check("options: blank entries are dropped", LotUIElement.ParseOptions("A||B").Count == 2);
		Check("options: empty text is an empty list", LotUIElement.ParseOptions("").Count == 0);
		Check("options: null text is an empty list", LotUIElement.ParseOptions(null).Count == 0);

		System.Text.StringBuilder many = new System.Text.StringBuilder();
		for (int i = 0; i < LotUIElement.MaxOptions + 10; i++)
		{
			if (i > 0) many.Append('|');
			many.Append("o").Append(i);
		}
		Check("options: the count is capped", LotUIElement.ParseOptions(many.ToString()).Count == LotUIElement.MaxOptions);

		System.Text.StringBuilder longOption = new System.Text.StringBuilder();
		for (int i = 0; i < LotUIElement.MaxOptionLength + 20; i++) longOption.Append('x');
		Check("options: a long option is truncated",
			LotUIElement.ParseOptions(longOption.ToString())[0].Length == LotUIElement.MaxOptionLength);
	}

	private static void TestCheckboxAndSelection()
	{
		LotUIElement checkbox = LotUIElement.Create(LotUIKind.Checkbox, Vector2.Zero, new Vector2(100f, 20f), "Sound");
		Check("checkbox: a new checkbox is unchecked", !checkbox.Checked);
		Check("checkbox: toggling flips and reports", checkbox.ToggleChecked() && checkbox.Checked);
		Free(checkbox);

		LotUIElement dd = LotUIElement.Create(LotUIKind.Dropdown, Vector2.Zero, new Vector2(100f, 20f), "");
		Check("dropdown: nothing is selected in an empty dropdown", dd.SelectedIndex == -1 && dd.SelectedOption() == "");

		dd.SetOptionsFromText("Low|Medium|High");
		dd.SelectedIndex = 0;
		Check("dropdown: the options round-trip", dd.OptionsToText() == "Low|Medium|High");
		Check("dropdown: the selection reports its text", dd.SelectedOption() == "Low");

		dd.SetOptionsFromText("Low|Medium");
		Check("dropdown: a surviving selection is kept", dd.SelectedOption() == "Low");

		dd.SetOptionsFromText("Alpha|Beta");
		Check("dropdown: a vanished selection is cleared", dd.SelectedIndex == -1);

		Check("dropdown: selecting an unknown option changes nothing",
			!dd.SetSelectedByName("Gamma") && dd.SelectedIndex == -1);
		Check("dropdown: selecting a real option works", dd.SetSelectedByName("Beta") && dd.SelectedIndex == 1);

		dd.SetSelectedByIndex(99);
		Check("dropdown: an out-of-range index clamps to the last option", dd.SelectedIndex == 1);
		dd.SetSelectedByIndex(-1);
		Check("dropdown: -1 clears the selection", dd.SelectedIndex == -1);

		dd.Options.Clear();
		Check("dropdown: with no options there is nothing to report",
			dd.SelectedOption() == "" && dd.SelectedIndex == -1);
		Free(dd);
	}

	private static void TestTextAlign()
	{
		Check("align: names round-trip",
			LotUiRenderer.AlignName(LotUITextAlign.Center) == "Center"
			&& LotUiRenderer.AlignFromName("Right") == LotUITextAlign.Right
			&& LotUiRenderer.AlignFromName("Left") == LotUITextAlign.Left);
		Check("align: an unknown name falls back to Left",
			LotUiRenderer.AlignFromName("Sideways") == LotUITextAlign.Left
			&& LotUiRenderer.AlignFromName(null) == LotUITextAlign.Left);
	}

	private static void TestDeclarations()
	{
		Check("registry: the UI customization rows are declared",
			LotPropertyRegistry.Find("label") != null && LotPropertyRegistry.Find("ui_text_align") != null
			&& LotPropertyRegistry.Find("ui_checked") != null && LotPropertyRegistry.Find("ui_options") != null
			&& LotPropertyRegistry.Find("ui_selected") != null);
		Check("registry: the UI rows live under the UI category",
			LotPropertyRegistry.Find("ui_checked").Category == "UI"
			&& LotPropertyRegistry.Find("ui_options").Category == "UI");

		LotPropertyDescriptor selected = LotPropertyRegistry.Find("ui_selected");
		Check("registry: the dropdown selection reads its options per node",
			selected.Kind == LotPropertyKind.Choice && selected.ListOptionsFor != null && selected.ListOptions == null);
	}

	private static void TestDescriptorAccessors()
	{
		LotUIElement checkbox = LotUIElement.Create(LotUIKind.Checkbox, Vector2.Zero, new Vector2(100f, 20f), "Old");
		LotPropertyDescriptor label = LotPropertyRegistry.Find("label");
		label.SetText(checkbox, "New");
		Check("accessors: the label descriptor writes the element", checkbox.Label == "New" && label.GetText(checkbox) == "New");

		LotPropertyDescriptor checkedDescriptor = LotPropertyRegistry.Find("ui_checked");
		checkedDescriptor.SetBool(checkbox, true);
		Check("accessors: the checked descriptor writes the element", checkbox.Checked && checkedDescriptor.GetBool(checkbox));

		LotPropertyDescriptor align = LotPropertyRegistry.Find("ui_text_align");
		align.SetChoice(checkbox, "Right");
		Check("accessors: the alignment descriptor writes the element", checkbox.TextAlign == LotUITextAlign.Right);
		Free(checkbox);

		LotUIElement dd = LotUIElement.Create(LotUIKind.Dropdown, Vector2.Zero, new Vector2(100f, 20f), "");
		LotPropertyDescriptor options = LotPropertyRegistry.Find("ui_options");
		options.SetText(dd, "One|Two");
		Check("accessors: the options descriptor writes the element", dd.OptionsToText() == "One|Two");

		LotPropertyDescriptor selected = LotPropertyRegistry.Find("ui_selected");
		string[] choices = selected.ListOptionsFor(dd);
		Check("accessors: the selection list is the element's own options", choices.Length == 2 && choices[1] == "Two");
		selected.SetChoice(dd, "Two");
		Check("accessors: selecting through the descriptor works", dd.SelectedOption() == "Two" && selected.GetChoice(dd) == "Two");
		Free(dd);
	}

	private static void TestNestingMath()
	{
		LotUIElement container = LotUIElement.Create(LotUIKind.ScrollContainer, Vector2.Zero, new Vector2(200f, 100f), "");
		LotUIElement first = LotUIElement.Create(LotUIKind.Text, new Vector2(10f, 10f), new Vector2(80f, 20f), "a");
		LotUIElement second = LotUIElement.Create(LotUIKind.Text, new Vector2(10f, 120f), new Vector2(80f, 20f), "b");
		container.AddChild(first);
		container.AddChild(second);

		Check("scroll: the content height is the lowest child's bottom edge",
			Mathf.Abs(LotUiRenderer.ContentHeight(container) - 140f) < 0.001f);

		second.Visible = false;
		Check("scroll: a hidden child does not extend the content",
			Mathf.Abs(LotUiRenderer.ContentHeight(container) - 30f) < 0.001f);
		second.Visible = true;

		// TopmostAt: later siblings draw on top, and a container's contents win over the container.
		Node root = new Node();
		LotUIElement under = LotUIElement.Create(LotUIKind.Frame, new Vector2(0f, 0f), new Vector2(100f, 100f), "");
		LotUIElement over = LotUIElement.Create(LotUIKind.Button, new Vector2(0f, 0f), new Vector2(100f, 100f), "b");
		root.AddChild(under);
		root.AddChild(over);
		Check("hit-test: the later sibling wins",
			SessionInputRouter.TopmostAt(root, Vector2.Zero, new Vector2(50f, 50f)) == over);
		Check("hit-test: a click outside every element finds nothing",
			SessionInputRouter.TopmostAt(root, Vector2.Zero, new Vector2(500f, 500f)) == null);

		Node nestedRoot = new Node();
		LotUIElement box = LotUIElement.Create(LotUIKind.ScrollContainer, new Vector2(0f, 0f), new Vector2(200f, 200f), "");
		LotUIElement inner = LotUIElement.Create(LotUIKind.Checkbox, new Vector2(10f, 10f), new Vector2(80f, 20f), "c");
		box.AddChild(inner);
		nestedRoot.AddChild(box);
		Check("hit-test: a container's child wins over the container",
			SessionInputRouter.TopmostAt(nestedRoot, Vector2.Zero, new Vector2(20f, 20f)) == inner);
		Check("hit-test: the container itself wins where it has no child",
			SessionInputRouter.TopmostAt(nestedRoot, Vector2.Zero, new Vector2(150f, 150f)) == box);

		// Hierarchy drop: a scroll container accepts children; any other UI row stays a leaf.
		Node dropRoot = new Node();
		LotUIElement dropContainer = LotUIElement.Create(LotUIKind.ScrollContainer, Vector2.Zero, new Vector2(100f, 100f), "");
		LotUIElement dropLeaf = LotUIElement.Create(LotUIKind.Frame, Vector2.Zero, new Vector2(50f, 50f), "");
		LotUIElement dropSource = LotUIElement.Create(LotUIKind.Frame, Vector2.Zero, new Vector2(50f, 50f), "");
		dropRoot.AddChild(dropContainer);
		dropRoot.AddChild(dropLeaf);
		dropRoot.AddChild(dropSource);
		Node dropParent;
		int dropIndex;
		HierarchyDropPlacement dropEffective;
		bool intoContainer = HierarchyDrop.TryResolveTarget(dropSource, dropContainer, HierarchyDropPlacement.Into,
			out dropParent, out dropIndex, out dropEffective);
		Check("drop: a scroll container accepts children",
			intoContainer && dropEffective == HierarchyDropPlacement.Into && dropParent == dropContainer);
		bool intoLeaf = HierarchyDrop.TryResolveTarget(dropSource, dropLeaf, HierarchyDropPlacement.Into,
			out dropParent, out dropIndex, out dropEffective);
		Check("drop: a plain UI row still behaves as a leaf",
			intoLeaf && dropEffective == HierarchyDropPlacement.After);

		root.Free();
		nestedRoot.Free();
		dropRoot.Free();
		container.Free();
	}

	private static void TestArchiveNesting()
	{
		Node root = new Node();
		LotUIElement container = LotUIElement.Create(LotUIKind.ScrollContainer, new Vector2(5f, 5f), new Vector2(200f, 100f), "");
		container.Name = "Box";
		root.AddChild(container);

		LotUIElement checkbox = LotUIElement.Create(LotUIKind.Checkbox, new Vector2(4f, 4f), new Vector2(80f, 20f), "Ticked");
		checkbox.Checked = true;
		checkbox.Name = "Tick";
		container.AddChild(checkbox);

		LotUIElement dd = LotUIElement.Create(LotUIKind.Dropdown, new Vector2(4f, 30f), new Vector2(80f, 20f), "");
		dd.SetOptionsFromText("A|B");
		dd.SetSelectedByName("B");
		dd.Name = "Pick";
		container.AddChild(dd);

		List<LotArchive.LotNodeRecord> records = new List<LotArchive.LotNodeRecord>();
		LotSceneWalk.CaptureChildren(root, records);

		Check("archive: a nested tree captures container then children", records.Count == 3);
		Check("archive: the container keeps the container kind", records[0].Kind == "ScrollContainer");
		Check("archive: a child records its container as parent", records[1].ParentIndex == 0 && records[2].ParentIndex == 0);
		Check("archive: a checkbox carries its checked state", records[1].Properties.ContainsKey("ui_checked") && records[1].Properties["ui_checked"].AsBool());
		Check("archive: a checkbox carries its label", records[1].Properties.ContainsKey("label") && records[1].Properties["label"].AsString() == "Ticked");
		Check("archive: a dropdown carries its options and selection",
			records[2].Properties["ui_options"].AsString() == "A|B" && records[2].Properties["ui_selected"].AsString() == "B");

		// And through the real encoder, so a nested UI tree with the new values survives the exact
		// bytes lot.json carries (the Vector3/Color-as-string pitfalls the archive already handles).
		LotArchive.LotDocument document = new LotArchive.LotDocument();
		document.UiElements.AddRange(records);
		LotArchive.LotDocument back = LotArchive.LotDocument.FromJson(Json.ParseString(Json.Stringify(document.ToJson())));
		Check("archive: a nested tree survives the JSON encoder",
			back.UiElements.Count == 3 && back.UiElements[1].ParentIndex == 0 && back.UiElements[2].ParentIndex == 0);
		Check("archive: the new UI values survive the JSON encoder",
			back.UiElements[1].Properties["ui_checked"].AsBool()
			&& back.UiElements[2].Properties["ui_selected"].AsString() == "B"
			&& back.UiElements[0].Kind == "ScrollContainer");

		root.Free();
	}

	private static void TestLuaBindings()
	{
		LuaManager lua = LuaManager.Instance;
		if (lua == null || !lua.IsRuntimeAvailable)
		{
			Check("lua: the lot UI verbs are bound and behave (runtime unavailable, skipped)", true);
			return;
		}

		const string script =
			"local cb = Lot.SpawnUICheckbox(100, 100, 120, 20, 'Sound', false)\n" +
			"assert(type(cb) == 'number' and cb >= 0, 'checkbox spawn failed')\n" +
			"assert(Lot.GetUIChecked(cb) == false, 'a new checkbox should be unchecked')\n" +
			"Lot.SetUIChecked(cb, true)\n" +
			"assert(Lot.GetUIChecked(cb) == true, 'SetUIChecked did not stick')\n" +
			"assert(Lot.GetUIText(cb) == 'Sound', 'the checkbox label did not stick')\n" +
			"Lot.SetUITextAlign(cb, 'Right')\n" +
			"assert(Lot.GetUITextAlign(cb) == 'Right', 'SetUITextAlign did not stick')\n" +
			"Lot.SetUIVisible(cb, false)\n" +
			"assert(Lot.GetUIVisible(cb) == false, 'SetUIVisible did not stick')\n" +
			"local dd = Lot.SpawnUIDropdown(100, 140, 160, 24, 'Low|Medium|High')\n" +
			"assert(type(dd) == 'number' and dd >= 0, 'dropdown spawn failed')\n" +
			"assert(Lot.GetUIDropdownOptions(dd) == 'Low|Medium|High', 'the options did not stick')\n" +
			"assert(Lot.GetUIDropdownSelection(dd) == 'Low', 'the first option should start selected')\n" +
			"Lot.SetUIDropdownSelection(dd, 'High')\n" +
			"assert(Lot.GetUIDropdownSelection(dd) == 'High', 'the selection did not stick')\n" +
			"Lot.SetUIDropdownOptions(dd, 'Only')\n" +
			"assert(Lot.GetUIDropdownSelection(dd) == '', 'a vanished selection should clear')\n" +
			"local sc = Lot.SpawnUIScrollContainer(100, 200, 200, 120)\n" +
			"assert(type(sc) == 'number' and sc >= 0, 'scroll container spawn failed')\n" +
			"Lot.DestroyObject(cb) Lot.DestroyObject(dd) Lot.DestroyObject(sc)";

		bool ran = lua.RunString(script, "lot_ui_selftest");
		Check("lua: the lot UI verbs are bound and behave" + (ran ? "" : " (" + lua.LastError + ")"), ran);
	}

	private static void Free(Node node)
	{
		if (node != null && GodotObject.IsInstanceValid(node)) node.Free();
	}

	private static void Check(string name, bool condition)
	{
		_checks++;
		if (condition)
		{
			GD.Print("[LotUiSelfTest] PASS  " + name);
		}
		else
		{
			_failures++;
			GD.Print("[LotUiSelfTest] FAIL  " + name);
		}
	}
}
