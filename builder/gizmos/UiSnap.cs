using System.Collections.Generic;
using Godot;

/// <summary>
/// Snap targets and alignment-guide math for the UI editor. Candidate coordinates come from the
/// picture frame (edges + centre) and from every other visible element (edges + centre), so an
/// element can be aligned to the frame or to its neighbours on either axis.
///
/// The math is pure: callers own the reusable lists and decide what to do with the result, which
/// keeps the per-frame drag path allocation-free (no new lists, no LINQ).
/// </summary>
public static class UiSnap
{
	/// <summary>How close (in pixels) a line must be before it snaps.</summary>
	public const float ThresholdPixels = 6f;

	/// <summary>
	/// How close a line must stay to a guide coordinate to keep the guide drawn after clamping.
	/// Guards against showing a guide for an alignment that the frame clamp then broke.
	/// </summary>
	public const float GuideEpsilon = 0.5f;

	/// <summary>
	/// Fills the per-axis candidate coordinates: the frame's left/centre/right and top/centre/bottom,
	/// plus the same three lines for every visible element except <paramref name="ignore"/> (the one
	/// being edited — an element must never snap to itself).
	/// </summary>
	public static void CollectTargets(Node root, Node ignore, Vector2 bounds, List<float> xs, List<float> ys)
	{
		xs.Clear();
		ys.Clear();

		xs.Add(0f);
		xs.Add(bounds.X * 0.5f);
		xs.Add(bounds.X);
		ys.Add(0f);
		ys.Add(bounds.Y * 0.5f);
		ys.Add(bounds.Y);

		if (root == null) return;
		int count = root.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			LotUIElement element = root.GetChild(i) as LotUIElement;
			if (element == null || element == ignore || !element.Visible) continue;
			AddRectLines(element.Position, element.Size, xs, ys);
		}
	}

	/// <summary>Appends a rect's two edges and centre on both axes.</summary>
	public static void AddRectLines(Vector2 position, Vector2 size, List<float> xs, List<float> ys)
	{
		xs.Add(position.X);
		xs.Add(position.X + size.X * 0.5f);
		xs.Add(position.X + size.X);
		ys.Add(position.Y);
		ys.Add(position.Y + size.Y * 0.5f);
		ys.Add(position.Y + size.Y);
	}

	/// <summary>
	/// Finds the smallest shift that puts one of <paramref name="sourceLines"/> onto a target line.
	/// Returns false when nothing is within <paramref name="threshold"/>; <paramref name="guide"/> is
	/// the target coordinate to draw the alignment guide at.
	/// </summary>
	public static bool TrySnapLine(IReadOnlyList<float> sourceLines, IReadOnlyList<float> targetLines,
		float threshold, out float delta, out float guide)
	{
		delta = 0f;
		guide = 0f;
		float best = threshold;
		bool found = false;

		for (int s = 0; s < sourceLines.Count; s++)
		{
			for (int t = 0; t < targetLines.Count; t++)
			{
				float candidate = targetLines[t] - sourceLines[s];
				float distance = Mathf.Abs(candidate);
				if (distance <= best)
				{
					best = distance;
					delta = candidate;
					guide = targetLines[t];
					found = true;
				}
			}
		}
		return found;
	}

	/// <summary>
	/// Finds the smallest shift that puts a single dragged edge onto a target line (resize snapping,
	/// where only the edge under the handle may move).
	/// </summary>
	public static bool TrySnapEdge(float edge, IReadOnlyList<float> targetLines, float threshold, out float delta, out float guide)
	{
		delta = 0f;
		guide = 0f;
		float best = threshold;
		bool found = false;

		for (int t = 0; t < targetLines.Count; t++)
		{
			float candidate = targetLines[t] - edge;
			float distance = Mathf.Abs(candidate);
			if (distance <= best)
			{
				best = distance;
				delta = candidate;
				guide = targetLines[t];
				found = true;
			}
		}
		return found;
	}

	/// <summary>True when the guide coordinate is one of the rect's edges or its centre on the X axis.</summary>
	public static bool RectTouchesX(Vector2 position, Vector2 size, float x)
	{
		return Near(position.X, x) || Near(position.X + size.X * 0.5f, x) || Near(position.X + size.X, x);
	}

	/// <summary>True when the guide coordinate is one of the rect's edges or its centre on the Y axis.</summary>
	public static bool RectTouchesY(Vector2 position, Vector2 size, float y)
	{
		return Near(position.Y, y) || Near(position.Y + size.Y * 0.5f, y) || Near(position.Y + size.Y, y);
	}

	private static bool Near(float a, float b)
	{
		return Mathf.Abs(a - b) <= GuideEpsilon;
	}
}