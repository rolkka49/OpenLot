using System.Collections.Generic;
using Godot;

/// <summary>
/// The in-session path from a lot UI press to the script that subscribed to it (milestone 5.3).
///
/// The overlay renderer reports the elements the player activated this frame — resolved through
/// ImGui item state, design doc D1 — and this router turns each into one queued <c>clicked</c>
/// delivery on the element's handle. The delivery runs later, from <see cref="ScriptRuntime.Tick"/>,
/// under the existing event rules (coalescing, the per-frame cap, one watchdog unit per handler,
/// two-ended cleanup), and carries the attribution the sensor path uses — so <c>net.fromPlayer</c>
/// is true inside the handler and its <c>net.server</c> calls inherit it.
///
/// This is the shared in-session input seam Phase 2 planned: the in-lot command UI (§6.3) and the
/// part-side <c>clicked</c> event are its later consumers, not a second mechanism.
/// </summary>
public sealed class SessionInputRouter
{
	private readonly List<LotUIElement> _pending = new List<LotUIElement>();
	private readonly BuilderScene _scene;

	public SessionInputRouter(BuilderScene scene)
	{
		_scene = scene;
	}

	/// <summary>Number of activations waiting to be flushed (diagnostics).</summary>
	public int PendingCount { get { return _pending.Count; } }

	/// <summary>Records one element the player activated this frame, for the next flush.</summary>
	public void Report(LotUIElement element)
	{
		if (element != null) _pending.Add(element);
	}

	/// <summary>
	/// Sends one queued <c>clicked</c> per activated element and clears the list. Called once per
	/// frame after the overlay has drawn; a no-op when nothing was activated. The scene's enqueue is
	/// itself session-gated, so this is safe to call every frame.
	/// </summary>
	public void Flush()
	{
		if (_pending.Count == 0) return;
		for (int i = 0; i < _pending.Count; i++)
		{
			int handle = BuilderScene.HandleOf(_pending[i]);
			if (handle >= 0) _scene.EnqueueUiClick(handle);
		}
		_pending.Clear();
	}

	// --- pure rules (self-test) ------------------------------------------------------------------

	/// <summary>
	/// The topmost element under a picture-frame point, or null: later siblings win (they draw on
	/// top) and a scroll container's contents win over the container itself, so the answer is the
	/// element the player sees. <paramref name="origin"/> is the screen position of the frame's
	/// top-left corner. Pure geometry — the live session resolves the press through ImGui item
	/// state; this is the ordering rule that resolution has to agree with.
	///
	/// A container's scroll offset is deliberately not modelled here (it lives in ImGui's child
	/// state); the rule is stated at scroll zero, which is what the ordering self-test checks.
	/// </summary>
	public static LotUIElement TopmostAt(Node root, Vector2 origin, Vector2 point)
	{
		if (root == null) return null;
		for (int i = root.GetChildCount() - 1; i >= 0; i--)
		{
			LotUIElement element = root.GetChild(i) as LotUIElement;
			if (element == null || !element.Visible) continue;
			Vector2 min = origin + element.Position;
			if (!Contains(min, element.Size, point)) continue;
			LotUIElement inner = TopmostAt(element, min, point);
			return inner ?? element;
		}
		return null;
	}

	private static bool Contains(Vector2 min, Vector2 size, Vector2 point)
	{
		return point.X >= min.X && point.Y >= min.Y && point.X <= min.X + size.X && point.Y <= min.Y + size.Y;
	}
}
