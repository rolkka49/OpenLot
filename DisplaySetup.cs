using Godot;

/// <summary>
/// Project-wide display setup. OpenLot's entire UI (the terminal and every builder panel) is Dear
/// ImGui, and the addon draws that overlay as canvas items attached to the ROOT viewport, so the
/// overlay inherits that viewport's "final transform". Under any Godot content-scale stretch
/// (<c>display/window/stretch/mode</c> != disabled) that transform is a scale — measured 1.6667x at
/// a 1920x1400 window against the 1152x648 base — and the addon lays ImGui out at the logical
/// <c>get_visible_rect()</c> size before the engine upscales it to real pixels, so windows and text
/// grow and blur as the window is widened or heightened. <see cref="NormalizeStretch"/> pins the
/// mode to Disabled at runtime, so the overlay stays a fixed size and renders 1:1/crisp regardless
/// of the project setting (a stale editor instance rewriting project.godot cannot re-enable the
/// scaling by accident).
/// </summary>
public static class DisplaySetup
{
	/// <summary>
	/// Forces the root window's content-scale mode to Disabled. Idempotent, so it is safe to call
	/// from every scene entry point; aspect/size are ignored once the mode is Disabled, so they are
	/// left untouched.
	/// </summary>
	public static void NormalizeStretch()
	{
		SceneTree tree = Engine.GetMainLoop() as SceneTree;
		if (tree == null || tree.Root == null) return;
		if (tree.Root.ContentScaleMode != Window.ContentScaleModeEnum.Disabled)
			tree.Root.ContentScaleMode = Window.ContentScaleModeEnum.Disabled;
	}
}
