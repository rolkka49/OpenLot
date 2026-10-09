using Godot;

/// <summary>
/// Shared ImGui window flags for OpenLot's chrome — the terminal windows and the builder panels.
///
/// Docking is deliberately OFF. In this Dear ImGui build the docked-window "maximize" gesture is a
/// double-click on the dock node's tab bar, which hides the tab bar and leaves that panel filling
/// the whole dockspace with no visible tab to click back to normal. The addon's C# wrapper exposes
/// no dock-node flags and no IO config, so the gesture cannot be switched off directly; with no
/// dock node there is no tab bar to double-click in the first place, which removes it for good.
/// <see cref="ImGui.WindowNoCollapse"/> does the same for a floating window's title-bar double-click
/// (ImGui's other per-window double-click action), so no window can be blown up or folded away by
/// accident. Windows remain freely movable and resizable; only dock/rearrange is given up.
///
/// A panel that needs its *body* for a mouse gesture (the hierarchy's marquee) additionally begins
/// with <see cref="ImGui.WindowNoMove"/> and re-grants movement through
/// <see cref="TitleBarWindowDrag"/>, because ImGui's own "move from title bar only" option is another
/// IO setting the addon does not expose — so without that combination a drag in the panel body would
/// both move the window and swallow the gesture.
/// </summary>
public static class EditorChrome
{
	/// <summary>Flags every terminal window and builder panel is begun with.</summary>
	public const int PanelWindowFlags = ImGui.WindowNoDocking | ImGui.WindowNoCollapse;
}
