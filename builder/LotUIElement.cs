using Godot;

public enum LotUIKind
{
	Frame,
	Button,
	Text,
	Scrollbar
}

/// <summary>
/// A 2D UI element belonging to the lot, ported from Unity's uGUI spawnables onto OpenLot's
/// ImGui UI layer. Elements are plain data nodes under LotUIRoot; the viewport draws them every
/// frame as a screen-space overlay clipped to the 3D view. Coordinates are pixels from the
/// viewport's top-left corner, Y down — matching screen-space ImGui draw calls.
/// </summary>
public partial class LotUIElement : Node
{
	public LotUIKind Kind;
	public string Label = "";
	public Vector2 Position;
	public Vector2 Size;
	public Color Color;
	public bool Visible = true;

	public static LotUIElement Create(LotUIKind kind, Vector2 pos, Vector2 size, string label)
	{
		LotUIElement el = new LotUIElement();
		el.Kind = kind;
		el.Position = pos;
		el.Size = size;
		el.Label = label ?? "";
		el.Color = LotUiRenderer.DefaultColor(kind);
		return el;
	}
}

/// <summary>
/// Screen-space overlay renderer + default geometry for lot UI elements (the OpenLot UI layer).
/// Draw calls go into the current ImGui window's draw list, so drawing right after the 3D image
/// item keeps the overlay clipped to the viewport window.
/// </summary>
public static class LotUiRenderer
{
	private static readonly Color SelectedOutline = new Color(0.18f, 0.52f, 0.89f, 1f);
	private static readonly Color BorderColor = new Color(0f, 0f, 0f, 0.35f);
	private static readonly Color ThumbColor = new Color(0.38f, 0.38f, 0.43f, 1f);
	private static readonly Color ButtonTextColor = new Color(1f, 1f, 1f, 1f);

	// Reused child buffer so the per-frame overlay walk allocates nothing.
	private static readonly System.Collections.Generic.List<Node> _childrenBuffer = new System.Collections.Generic.List<Node>();

	public static Vector2 DefaultSize(LotUIKind kind)
	{
		switch (kind)
		{
			case LotUIKind.Frame: return new Vector2(200f, 200f);
			case LotUIKind.Button: return new Vector2(160f, 40f);
			case LotUIKind.Text: return new Vector2(150f, 30f);
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
			default: return new Color(0.12f, 0.12f, 0.15f, 1f);
		}
	}

	/// <summary>
	/// Must be called inside the Viewport window scope, right after the 3D image item.
	/// <paramref name="bounds"/> is the picture frame size; elements outside it are skipped defensively
	/// (the editor clamps every element into the frame, so this only guards legacy/loaded data).
	/// </summary>
	public static void DrawAll(Node root, Vector2 origin, Vector2 bounds, SelectionManager selection)
	{
		_childrenBuffer.Clear();
		int count = root.GetChildCount();
		for (int i = 0; i < count; i++) _childrenBuffer.Add(root.GetChild(i));

		for (int i = 0; i < _childrenBuffer.Count; i++)
		{
			LotUIElement element = _childrenBuffer[i] as LotUIElement;
			if (element == null || !element.Visible) continue;
			if (!OverlapsFrame(element, bounds)) continue;
			DrawElement(element, origin, selection.IsSelected(element));
		}
	}

	/// <summary>True when any part of the element lies inside the picture frame.</summary>
	private static bool OverlapsFrame(LotUIElement element, Vector2 bounds)
	{
		return element.Position.X < bounds.X && element.Position.Y < bounds.Y
			&& element.Position.X + element.Size.X > 0f && element.Position.Y + element.Size.Y > 0f;
	}

	private static void DrawElement(LotUIElement element, Vector2 origin, bool selected)
	{
		Vector2 min = origin + element.Position;
		Vector2 max = min + element.Size;

		switch (element.Kind)
		{
			case LotUIKind.Frame:
			case LotUIKind.Button:
				ImGui.DrawRectFilled(min, max, element.Color);
				ImGui.DrawRect(min, max, BorderColor, 0f, 1f);
				if (element.Kind == LotUIKind.Button && element.Label.Length > 0)
				{
					// No text-metrics query in the wrapper, so centering is approximate.
					float lineH = ImGui.GetTextLineHeight();
					float approxWidth = element.Label.Length * lineH * 0.5f;
					float x = min.X + System.MathF.Max(2f, (element.Size.X - approxWidth) * 0.5f);
					float y = min.Y + System.MathF.Max(2f, (element.Size.Y - lineH) * 0.5f);
					ImGui.DrawText(new Vector2(x, y), ButtonTextColor, element.Label);
				}
				break;

			case LotUIKind.Text:
				ImGui.DrawText(min, element.Color, element.Label);
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
		}

		if (selected)
			ImGui.DrawRect(min - new Vector2(2f, 2f), max + new Vector2(2f, 2f), SelectedOutline, 0f, 2f);
	}
}