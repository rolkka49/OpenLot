using Godot;

/// <summary>
/// Verification for the screen-space selection outline (the replacement for the old world-space
/// inverted hull). Two halves, split the way the project splits every rendering-side feature:
///
/// <list type="bullet">
/// <item>The CONTRACT is checked here headlessly — a part no longer carries any "Outline" mesh
/// child (nothing about the highlight is scene geometry any more), the outline state machine is
/// unchanged, the objects with nothing to outline (decal, query volume) stay untouched, and the
/// live mask set follows the real selection.</item>
/// <item>The LOOK (a constant pixel-width ring on the silhouette) is a rendering result and is
/// checked by eye in the running client, like the wallpaper and the sky.</item>
/// </list>
///
/// Runs from BuilderScene._Process after a few physics frames (Builder.Instance must exist).
/// </summary>
public static class SelectionOutlineSelfTest
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
			GD.PushError("[SelectionOutlineSelfTest] Builder.Instance is null; suite skipped");
			return 1;
		}

		TestNoSceneGeometry();
		TestStateMachine();
		TestLiveMasks(builder, scene);

		GD.Print("[SelectionOutlineSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
		return _failures;
	}

	/// <summary>
	/// The headline contract: the highlight is not an object. A part's internal children are its
	/// mesh and its collision body — there is no Outline hull to collide with, raycast against, or
	/// grow in world space, which is exactly what the old inverted hull was.
	/// </summary>
	private static void TestNoSceneGeometry()
	{
		LotObject cube = LotObject.Create(LotObjectKind.Cube, "OutlineTestCube", new Color(1f, 1f, 1f));
		Check("outline: a part has no Outline child", cube.GetNodeOrNull<MeshInstance3D>("Outline") == null);

		int meshes = 0;
		int bodies = 0;
		for (int i = 0; i < cube.GetChildCount(); i++)
		{
			Node child = cube.GetChild(i);
			if (child is MeshInstance3D) meshes++;
			if (child is PhysicsBody3D) bodies++;
		}
		Check("outline: a part keeps exactly its mesh child", meshes == 1);
		Check("outline: a part keeps exactly its collision body", bodies == 1);
		Check("outline: a part still reports a silhouette", cube.HasOutlineSilhouette);

		// A decal's transform is derived from its host's face, and a query volume is a region: both
		// deliberately have no silhouette to outline (the same rule the old hull followed).
		LotObject decal = LotObject.Create(LotObjectKind.Decal, "OutlineTestDecal", new Color(1f, 1f, 1f));
		Check("outline: a decal has no silhouette", !decal.HasOutlineSilhouette);
		LotObject volume = LotObject.Create(LotObjectKind.QueryVolume, "OutlineTestVolume", new Color(1f, 1f, 1f));
		Check("outline: a query volume has no silhouette", !volume.HasOutlineSilhouette);

		cube.QueueFree();
		decal.QueueFree();
		volume.QueueFree();
	}

	/// <summary>The state machine the mask is built from (unchanged by the rewrite).</summary>
	private static void TestStateMachine()
	{
		LotObject cube = LotObject.Create(LotObjectKind.Cube, "OutlineStateCube", new Color(1f, 1f, 1f));
		Check("outline state: a fresh part starts unhighlighted", cube.OutlineState == LotOutlineState.None);

		cube.SetOutline(LotOutlineState.Selected);
		Check("outline state: selecting records Selected", cube.OutlineState == LotOutlineState.Selected);
		cube.SetOutline(LotOutlineState.Dragged);
		Check("outline state: dragging records Dragged", cube.OutlineState == LotOutlineState.Dragged);
		cube.SetOutline(LotOutlineState.None);
		Check("outline state: clearing records None", cube.OutlineState == LotOutlineState.None);

		LotObject decal = LotObject.Create(LotObjectKind.Decal, "OutlineStateDecal", new Color(1f, 1f, 1f));
		decal.SetOutline(LotOutlineState.Selected);
		Check("outline state: outlining a decal stays a safe no-op", decal.OutlineState == LotOutlineState.None);

		LotObject volume = LotObject.Create(LotObjectKind.QueryVolume, "OutlineStateVolume", new Color(1f, 1f, 1f));
		volume.SetOutline(LotOutlineState.Selected);
		Check("outline state: outlining a query volume stays a safe no-op", volume.OutlineState == LotOutlineState.None);

		// The two colours the mask materials and the ring shader carry.
		Check("outline colours: the selected tint matches the UI elements' selection blue",
			SelectionOutline.SelectedColor.R < 0.2f && SelectionOutline.SelectedColor.B > 0.8f);
		Check("outline colours: the dragged tint is lighter than the selected one",
			SelectionOutline.DraggedColor.G > SelectionOutline.SelectedColor.G);
		Check("outline width: the ring is a screen-space width in pixels",
			SelectionOutline.OutlineThicknessPixels > 0f && SelectionOutline.OutlineThicknessPixels < 8f);

		cube.QueueFree();
		decal.QueueFree();
		volume.QueueFree();
	}

	/// <summary>
	/// The live half: two temporary parts on the real lot, selected through the real selection
	/// manager, must produce two mask meshes (one per outlined object) and none once the selection
	/// is cleared. They are freed immediately (not queued) so no later suite sees them.
	/// </summary>
	private static void TestLiveMasks(Builder builder, BuilderScene scene)
	{
		SelectionOutline outline = builder.Outline;
		if (outline == null || scene.LotRoot == null)
		{
			Check("outline masks: the outline service and lot root exist", false);
			return;
		}

		LotObject first = LotObject.Create(LotObjectKind.Cube, "OutlineMaskA", new Color(1f, 1f, 1f));
		first.Position = new Vector3(-3f, 0f, 0f);
		scene.LotRoot.AddChild(first);
		LotObject second = LotObject.Create(LotObjectKind.Sphere, "OutlineMaskB", new Color(1f, 1f, 1f));
		second.Position = new Vector3(3f, 0f, 0f);
		scene.LotRoot.AddChild(second);

		builder.Selection.ClearSelection();
		builder.Selection.Select(first, false);
		builder.Selection.Select(second, true);
		outline.Refresh();

		Check("outline masks: two selected objects produce two masks", outline.MaskCount == 2);
		Check("outline masks: the pass reports itself active", outline.IsActive);
		Check("outline masks: both sources are recorded as Selected",
			first.OutlineState == LotOutlineState.Selected && second.OutlineState == LotOutlineState.Selected);

		// A decal selected alongside them must not add a mask (nothing to outline).
		LotObject decal = LotObject.Create(LotObjectKind.Decal, "OutlineMaskDecal", new Color(1f, 1f, 1f));
		first.AddChild(decal);
		builder.Selection.Select(decal, true);
		outline.Refresh();
		Check("outline masks: selecting a decal adds nothing", outline.MaskCount == 2);

		builder.Selection.ClearSelection();
		outline.Refresh();
		Check("outline masks: clearing the selection clears the pass", outline.MaskCount == 0);
		Check("outline masks: the pass reports itself inactive", !outline.IsActive);

		// Free immediately: a queued free would leave these on the lot for the rest of the frame,
		// where the session-driving suites behind this one would see them.
		first.Free();
		second.Free();
		outline.Refresh();
	}

	private static void Check(string name, bool condition)
	{
		_checks++;
		if (condition)
		{
			GD.Print("[SelectionOutlineSelfTest] PASS  " + name);
		}
		else
		{
			_failures++;
			GD.PrintErr("[SelectionOutlineSelfTest] FAIL  " + name);
		}
	}
}
