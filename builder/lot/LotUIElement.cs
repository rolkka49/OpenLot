using System.Collections.Generic;
using Godot;

public enum LotUIKind
{
	Frame,
	Button,
	Text,
	Scrollbar,
	/// <summary>A tick box with a label (milestone 5.3). Its state is <see cref="LotUIElement.Checked"/>.</summary>
	Checkbox,
	/// <summary>A closed box showing the selected option, opening a list on activation
	/// (milestone 5.3). Its state is <see cref="LotUIElement.Options"/> + <see cref="LotUIElement.SelectedIndex"/>.</summary>
	Dropdown,
	/// <summary>A clipping, scrolling region whose contents are its child elements (milestone 5.3).</summary>
	ScrollContainer
}

/// <summary>Horizontal alignment of a lot UI element's text (milestone 5.3).</summary>
public enum LotUITextAlign
{
	Left,
	Center,
	Right
}

/// <summary>
/// A 2D UI element belonging to the lot, ported from Unity's uGUI spawnables onto OpenLot's
/// ImGui UI layer. Elements are plain data nodes under LotUIRoot; the viewport draws them every
/// frame as a screen-space overlay clipped to the 3D view. Coordinates are pixels from the
/// viewport's top-left corner, Y down — matching screen-space ImGui draw calls.
///
/// A <see cref="LotUIKind.ScrollContainer"/> may hold child elements (milestone 5.3): a child's
/// Position is relative to its container's top-left and rides the container's scroll, while a
/// root-level element stays picture-frame-relative (design doc D3).
/// </summary>
public partial class LotUIElement : Node
{
	/// <summary>Most options a Dropdown may hold — a bound, so a script cannot grow one forever.</summary>
	public const int MaxOptions = 32;

	/// <summary>Longest single Dropdown option, in characters.</summary>
	public const int MaxOptionLength = 64;

	public LotUIKind Kind;
	public string Label = "";
	public Vector2 Position;
	public Vector2 Size;
	public Color Color;
	public bool Visible = true;

	// --- Checkbox (milestone 5.3) ---
	public bool Checked;

	// --- Dropdown (milestone 5.3) ---
	/// <summary>The option list, in display order. Empty for every other kind.</summary>
	public readonly List<string> Options = new List<string>();
	/// <summary>Index of the selected option, or -1 when nothing is selected / there are no options.</summary>
	public int SelectedIndex = -1;

	// --- Shared text layout (milestone 5.3) ---
	public LotUITextAlign TextAlign = LotUITextAlign.Left;

	public static LotUIElement Create(LotUIKind kind, Vector2 pos, Vector2 size, string label)
	{
		LotUIElement el = new LotUIElement();
		el.Kind = kind;
		el.Position = pos;
		el.Size = size;
		el.Label = label ?? "";
		el.Color = LotUiRenderer.DefaultColor(kind);
		// A button's caption has always been drawn centred; that is the default the alignment
		// property starts from, so an untouched button renders exactly as it did before the
		// property existed (everything else starts left, its old behaviour too).
		el.TextAlign = kind == LotUIKind.Button ? LotUITextAlign.Center : LotUITextAlign.Left;
		return el;
	}

	/// <summary>Flips the checkbox and returns the new state.</summary>
	public bool ToggleChecked()
	{
		Checked = !Checked;
		return Checked;
	}

	/// <summary>The selected option text, or "" when nothing is selected.</summary>
	public string SelectedOption()
	{
		if (SelectedIndex < 0 || SelectedIndex >= Options.Count) return "";
		return Options[SelectedIndex];
	}

	/// <summary>Selects by index, clamped into range; -1 (or an empty list) clears the selection.</summary>
	public void SetSelectedByIndex(int index)
	{
		if (Options.Count == 0 || index < 0) { SelectedIndex = -1; return; }
		SelectedIndex = index >= Options.Count ? Options.Count - 1 : index;
	}

	/// <summary>Selects by option text. Returns false (and changes nothing) when the text is not an
	/// option, so a load cannot silently select something else.</summary>
	public bool SetSelectedByName(string name)
	{
		if (string.IsNullOrEmpty(name)) { SelectedIndex = -1; return false; }
		for (int i = 0; i < Options.Count; i++)
		{
			if (Options[i] == name) { SelectedIndex = i; return true; }
		}
		return false;
	}

	/// <summary>
	/// Replaces the option list from the '|'-separated wire/archive form, clamping to the caps.
	/// Keeps the current selection when its text survives, otherwise clears it — so editing a
	/// dropdown's options in the Inspector cannot leave a stale index pointing at a different
	/// option. Returns true when the list changed.
	/// </summary>
	public bool SetOptionsFromText(string text)
	{
		string keep = SelectedOption();
		List<string> parsed = ParseOptions(text);
		bool same = parsed.Count == Options.Count;
		if (same)
		{
			for (int i = 0; i < parsed.Count; i++)
			{
				if (parsed[i] != Options[i]) { same = false; break; }
			}
		}
		if (same) return false;

		Options.Clear();
		Options.AddRange(parsed);
		if (SelectedIndex >= Options.Count) SelectedIndex = -1;
		if (keep.Length > 0)
		{
			// The selection's text survived: keep pointing at it. If it vanished, clear the
			// selection rather than let the index silently point at a DIFFERENT option.
			if (SetSelectedByName(keep)) return true;
			SelectedIndex = -1;
		}
		return true;
	}

	/// <summary>The option list as the '|'-separated text stored in lot.json / passed from Lua.</summary>
	public string OptionsToText()
	{
		return string.Join("|", Options);
	}

	/// <summary>
	/// Parses the '|'-separated option text into a list: entries are trimmed, blanks dropped, and
	/// the count/length caps applied. Pure and shared by the descriptor setter, the Load path and
	/// the Lua setter, so all three agree on what an option list is.
	/// </summary>
	public static List<string> ParseOptions(string text)
	{
		List<string> result = new List<string>();
		if (string.IsNullOrEmpty(text)) return result;
		string[] parts = text.Split('|');
		for (int i = 0; i < parts.Length; i++)
		{
			if (result.Count >= MaxOptions) break;
			string option = parts[i].Trim();
			if (option.Length == 0) continue;
			if (option.Length > MaxOptionLength) option = option.Substring(0, MaxOptionLength);
			result.Add(option);
		}
		return result;
	}
}

/// <summary>
/// Screen-space overlay renderer + default geometry for lot UI elements (the OpenLot UI layer).
/// Draw calls go into the current ImGui window's draw list, so drawing right after the 3D image
/// item keeps the overlay clipped to the viewport window.
///
/// Two entry points (milestone 5.3, design doc D1): <see cref="DrawAll"/> is the editor overlay —
/// non-interactive, exactly as before, so the UI gizmo's raw-mouse drag is untouched;
/// <see cref="DrawSession"/> adds the interactive pass a player session needs, resolving presses
/// through real ImGui item state and reporting activations to the caller.
/// </summary>
public static class LotUiRenderer
{
	/// <summary>Text alignment names for the Choice property (milestone 5.3).</summary>
	public static readonly string[] TextAlignNames = { "Left", "Center", "Right" };

	/// <summary>The display name of a text alignment, for the Choice property's getter.</summary>
	public static string AlignName(LotUITextAlign align)
	{
		int index = (int)align;
		return index >= 0 && index < TextAlignNames.Length ? TextAlignNames[index] : TextAlignNames[0];
	}

	/// <summary>Parses a text alignment name; an unknown or empty name falls back to Left, so a
	/// load can never leave the enum at a value the renderer does not handle.</summary>
	public static LotUITextAlign AlignFromName(string name)
	{
		for (int i = 0; i < TextAlignNames.Length; i++)
		{
			if (TextAlignNames[i] == name) return (LotUITextAlign)i;
		}
		return LotUITextAlign.Left;
	}

	private static readonly Color SelectedOutline = new Color(0.18f, 0.52f, 0.89f, 1f);
	private static readonly Color BorderColor = new Color(0f, 0f, 0f, 0.35f);
	private static readonly Color ThumbColor = new Color(0.38f, 0.38f, 0.43f, 1f);
	private static readonly Color ButtonTextColor = new Color(1f, 1f, 1f, 1f);
	private static readonly Color TickColor = new Color(0.55f, 0.82f, 1f, 1f);
	private static readonly Color ArrowColor = new Color(0.85f, 0.85f, 0.9f, 1f);

	public static Vector2 DefaultSize(LotUIKind kind)
	{
		switch (kind)
		{
			case LotUIKind.Frame: return new Vector2(200f, 200f);
			case LotUIKind.Button: return new Vector2(160f, 40f);
			case LotUIKind.Text: return new Vector2(150f, 30f);
			case LotUIKind.Checkbox: return new Vector2(160f, 24f);
			case LotUIKind.Dropdown: return new Vector2(180f, 28f);
			case LotUIKind.ScrollContainer: return new Vector2(240f, 180f);
			default: return new Vector2(160f, 20f);
		}
	}

	public static Color DefaultColor(LotUIKind kind)
	{
		switch (kind)
		{
			case LotUIKind.Frame: return new Color(0.18f, 0.18f, 0.22f, 0.85f);
			case LotUIKind.Button: return new Color(0.25f, 0.25f, 0.28f, 1f);
			case LotUIKind.Text: return new Color(1f, 1f, 1f, 1f);
			case LotUIKind.Checkbox: return new Color(0.16f, 0.16f, 0.19f, 0.9f);
			case LotUIKind.Dropdown: return new Color(0.22f, 0.22f, 0.26f, 1f);
			case LotUIKind.ScrollContainer: return new Color(0.12f, 0.12f, 0.15f, 0.7f);
			default: return new Color(0.12f, 0.12f, 0.15f, 1f);
		}
	}

	/// <summary>True for the kinds that take a press during a session (milestone 5.3).</summary>
	public static bool IsInteractive(LotUIKind kind)
	{
		return kind == LotUIKind.Button || kind == LotUIKind.Checkbox || kind == LotUIKind.Dropdown;
	}

	/// <summary>
	/// Editor overlay (build mode): draws every visible element non-interactively. Must be called
	/// inside the Viewport window scope, right after the 3D image item.
	/// <paramref name="bounds"/> is the picture frame size; elements outside it are skipped defensively
	/// (the editor clamps every element into the frame, so this only guards legacy/loaded data).
	/// </summary>
	public static void DrawAll(Node root, Vector2 origin, Vector2 bounds, SelectionManager selection)
	{
		DrawChildren(root, origin, origin, bounds, selection, false, null);
	}

	/// <summary>
	/// Session overlay (milestone 5.3): the same geometry, plus an <see cref="ImGui.InvisibleButton"/>
	/// over each interactive element so a press is resolved through ImGui item state. Every element
	/// the player activated this frame is appended to <paramref name="activations"/> for the caller
	/// to route to the script that subscribed to it.
	/// </summary>
	public static void DrawSession(Node root, Vector2 origin, Vector2 bounds, SelectionManager selection,
		List<LotUIElement> activations)
	{
		DrawChildren(root, origin, origin, bounds, selection, true, activations);
	}

	/// <summary>
	/// Draws the direct children of <paramref name="parent"/> whose whole rect lies at least partly
	/// in the clip rect. <paramref name="origin"/> is the screen position the children's Position is
	/// measured from (the picture frame for root-level elements, a container's top-left minus its
	/// scroll for its contents).
	/// </summary>
	private static void DrawChildren(Node parent, Vector2 origin, Vector2 clipMin, Vector2 clipSize,
		SelectionManager selection, bool interactive, List<LotUIElement> activations)
	{
		int count = parent.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			LotUIElement element = parent.GetChild(i) as LotUIElement;
			if (element == null || !element.Visible) continue;
			Vector2 min = origin + element.Position;
			if (!Intersects(min, element.Size, clipMin, clipSize)) continue;
			DrawElement(element, min, selection, interactive, activations);
		}
	}

	/// <summary>True when any part of the element (at <paramref name="min"/>) lies in the clip rect.</summary>
	private static bool Intersects(Vector2 min, Vector2 size, Vector2 clipMin, Vector2 clipSize)
	{
		return min.X < clipMin.X + clipSize.X && min.Y < clipMin.Y + clipSize.Y
			&& min.X + size.X > clipMin.X && min.Y + size.Y > clipMin.Y;
	}

	private static void DrawElement(LotUIElement element, Vector2 min, SelectionManager selection,
		bool interactive, List<LotUIElement> activations)
	{
		Vector2 max = min + element.Size;
		bool selected = selection != null && selection.IsSelected(element);

		switch (element.Kind)
		{
			case LotUIKind.Frame:
			case LotUIKind.Button:
				ImGui.DrawRectFilled(min, max, element.Color);
				ImGui.DrawRect(min, max, BorderColor, 0f, 1f);
				if (element.Label.Length > 0) DrawAlignedText(element, min, ButtonTextColor);
				break;

			case LotUIKind.Text:
				DrawAlignedText(element, min, element.Color);
				break;

			case LotUIKind.Checkbox:
				DrawCheckbox(element, min, max, interactive, activations);
				break;

			case LotUIKind.Dropdown:
				DrawDropdown(element, min, max, interactive, activations);
				break;

			case LotUIKind.Scrollbar:
				ImGui.DrawRectFilled(min, max, element.Color);
				bool vertical = element.Size.Y > element.Size.X;
				Vector2 thumbMin;
				Vector2 thumbMax;
				if (vertical)
				{
					thumbMin = new Vector2(max.X - 8f, min.Y + 2f);
					thumbMax = new Vector2(max.X - 2f, min.Y + element.Size.Y * 0.3f);
				}
				else
				{
					thumbMin = new Vector2(min.X + 2f, min.Y + 2f);
					thumbMax = new Vector2(min.X + 2f + element.Size.X * 0.3f, max.Y - 2f);
				}
				ImGui.DrawRectFilled(thumbMin, thumbMax, ThumbColor);
				break;

			case LotUIKind.ScrollContainer:
				ImGui.DrawRectFilled(min, max, element.Color);
				ImGui.DrawRect(min, max, BorderColor, 0f, 1f);
				DrawContainer(element, min, selection, interactive, activations);
				break;
		}

		if (selected)
			ImGui.DrawRect(min - new Vector2(2f, 2f), max + new Vector2(2f, 2f), SelectedOutline, 0f, 2f);
	}

	/// <summary>
	/// A checkbox: a tick box on the left and its label after it. In a session the whole element is
	/// one press target, and a press flips the state before the activation is reported — so the
	/// handler that runs later for the element already reads the new value (design doc D6).
	/// </summary>
	private static void DrawCheckbox(LotUIElement element, Vector2 min, Vector2 max, bool interactive,
		List<LotUIElement> activations)
	{
		bool pressed = false;
		if (interactive)
		{
			ImGui.SetCursorScreenPos(min);
			pressed = ImGui.InvisibleButton("##ui" + element.GetInstanceId(), element.Size);
			if (pressed)
			{
				element.ToggleChecked();
				if (activations != null) activations.Add(element);
			}
		}

		ImGui.DrawRectFilled(min, max, element.Color);
		ImGui.DrawRect(min, max, BorderColor, 0f, 1f);

		float box = Mathf.Min(element.Size.Y - 6f, 18f);
		if (box < 4f) box = Mathf.Max(4f, element.Size.Y - 2f);
		Vector2 boxMin = new Vector2(min.X + 4f, min.Y + (element.Size.Y - box) * 0.5f);
		Vector2 boxMax = boxMin + new Vector2(box, box);
		ImGui.DrawRect(boxMin, boxMax, BorderColor, 0f, 1f);
		if (element.Checked)
			ImGui.DrawRectFilled(boxMin + new Vector2(3f, 3f), boxMax - new Vector2(3f, 3f), TickColor);

		if (element.Label.Length > 0)
		{
			float lineH = ImGui.GetTextLineHeight();
			float y = min.Y + Mathf.Max(2f, (element.Size.Y - lineH) * 0.5f);
			ImGui.DrawText(new Vector2(boxMax.X + 6f, y), ButtonTextColor, element.Label);
		}
	}

	/// <summary>
	/// A dropdown: a closed box showing the selected option, with an arrow. In a session, a press
	/// opens an ImGui popup of the options; choosing one sets the selection and reports the
	/// activation, so the handler reads the newly selected value.
	/// </summary>
	private static void DrawDropdown(LotUIElement element, Vector2 min, Vector2 max, bool interactive,
		List<LotUIElement> activations)
	{
		string preview = element.SelectedOption();

		if (interactive)
		{
			string buttonId = "##ui" + element.GetInstanceId();
			ImGui.SetCursorScreenPos(min);
			if (ImGui.InvisibleButton(buttonId, element.Size)) ImGui.OpenPopup(buttonId);

			ImGui.DrawRectFilled(min, max, element.Color);
			ImGui.DrawRect(min, max, BorderColor, 0f, 1f);
			if (preview.Length > 0)
			{
				float lineH = ImGui.GetTextLineHeight();
				ImGui.DrawText(new Vector2(min.X + 6f, min.Y + Mathf.Max(2f, (element.Size.Y - lineH) * 0.5f)),
					ButtonTextColor, preview);
			}
			DrawArrow(min, max);

			if (ImGui.BeginPopup(buttonId))
			{
				for (int i = 0; i < element.Options.Count; i++)
				{
					if (ImGui.Selectable(element.Options[i], i == element.SelectedIndex))
					{
						element.SelectedIndex = i;
						if (activations != null) activations.Add(element);
						ImGui.CloseCurrentPopup();
					}
				}
				ImGui.EndPopup();
			}
			return;
		}

		// Build mode: the same box, no interaction (the popup would fight the UI gizmo's drag).
		ImGui.DrawRectFilled(min, max, element.Color);
		ImGui.DrawRect(min, max, BorderColor, 0f, 1f);
		if (preview.Length > 0)
		{
			float lineH = ImGui.GetTextLineHeight();
			ImGui.DrawText(new Vector2(min.X + 6f, min.Y + Mathf.Max(2f, (element.Size.Y - lineH) * 0.5f)),
				ButtonTextColor, preview);
		}
		DrawArrow(min, max);
	}

	/// <summary>Small downward triangle at the right of a dropdown box.</summary>
	private static void DrawArrow(Vector2 min, Vector2 max)
	{
		float cy = (min.Y + max.Y) * 0.5f;
		float cx = max.X - 12f;
		ImGui.DrawTriangleFilled(new Vector2(cx - 4f, cy - 2f), new Vector2(cx + 4f, cy - 2f),
			new Vector2(cx, cy + 3f), ArrowColor);
	}

	/// <summary>
	/// A scroll container: draws its own box, then its contents inside an ImGui child window so they
	/// clip to the container rect. In a session the child scrolls (wheel + scrollbar); in build mode
	/// scrolling is frozen so a drag cannot disturb the element layout (design doc D1/D3).
	/// </summary>
	private static void DrawContainer(LotUIElement element, Vector2 min, SelectionManager selection,
		bool interactive, List<LotUIElement> activations)
	{
		int flags = interactive
			? ImGui.WindowNoTitleBar | ImGui.WindowNoResize | ImGui.WindowNoMove | ImGui.WindowNoBackground
			: ImGui.WindowNoTitleBar | ImGui.WindowNoResize | ImGui.WindowNoMove | ImGui.WindowNoBackground
				| ImGui.WindowNoScrollbar | ImGui.WindowNoScrollWithMouse;

		ImGui.SetCursorScreenPos(min);
		bool open = ImGui.BeginChild("##uiscroll" + element.GetInstanceId(), element.Size.X, element.Size.Y,
			false, flags);
		if (open)
		{
			float scroll = ImGui.GetScrollY();
			Vector2 childOrigin = min - new Vector2(0f, scroll);
			DrawChildren(element, childOrigin, min, element.Size, selection, interactive, activations);

			// Establish the scrollable extent: ImGui derives a child's content size from the cursor,
			// not from the draw calls we added above.
			ImGui.SetCursorPos(new Vector2(0f, ContentHeight(element)));
			ImGui.Dummy(new Vector2(1f, 1f));
		}
		// Unconditional pair, exactly like Begin()/End().
		ImGui.EndChild();
	}

	/// <summary>
	/// The scrollable height of a container's contents: the bottom edge of its lowest direct child,
	/// plus the same padding the editor uses. Zero for an empty container. Pure, so the self-test
	/// pins it without an ImGui frame.
	/// </summary>
	public static float ContentHeight(LotUIElement container)
	{
		float bottom = 0f;
		if (container == null) return bottom;
		int count = container.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			LotUIElement child = container.GetChild(i) as LotUIElement;
			if (child == null || !child.Visible) continue;
			float childBottom = child.Position.Y + child.Size.Y;
			if (childBottom > bottom) bottom = childBottom;
		}
		return bottom;
	}

	/// <summary>
	/// Draws an element's text according to its <see cref="LotUIElement.TextAlign"/>. Width is
	/// approximated (the wrapper has no text-metrics query), the same rule the button label always
	/// used.
	/// </summary>
	private static void DrawAlignedText(LotUIElement element, Vector2 min, Color color)
	{
		float lineH = ImGui.GetTextLineHeight();
		float approxWidth = element.Label.Length * lineH * 0.5f;
		float y = min.Y + Mathf.Max(2f, (element.Size.Y - lineH) * 0.5f);
		float x;
		if (element.TextAlign == LotUITextAlign.Center)
			x = min.X + Mathf.Max(2f, (element.Size.X - approxWidth) * 0.5f);
		else if (element.TextAlign == LotUITextAlign.Right)
			x = min.X + Mathf.Max(2f, element.Size.X - approxWidth - 4f);
		else
			x = min.X + 2f;
		ImGui.DrawText(new Vector2(x, y), color, element.Label);
	}
}
