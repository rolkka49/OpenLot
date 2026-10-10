using System.Collections.Generic;
using Godot;

/// <summary>
/// The build-mode hinge markers (milestone 3.6, orientation pass): while a part with hinges is
/// selected, every hinge on it is drawn with the same ball-and-rod the placement step uses — the
/// ball at the hinge point, the rod along the axis — so an orientation set with the sharp tools (or
/// a placement confirmed earlier) is visible without entering Test mode. That visibility is the
/// point: before this, nothing on screen showed a hinge's axis outside a session, which made the
/// orientation look unchangeable.
///
/// Purely visual and editor-only: pooled markers (reused, never allocated per frame), all
/// internal-marked so the hierarchy, the save walk, the session snapshot and the pick masks skip
/// them, hidden during a session (editor tooling is suspended there), while the placement tool is
/// armed (it draws its own marker) and when a lot replace freed the pool.
/// </summary>
public sealed class HingeVisualizer
{
	/// <summary>Cap on markers drawn at once — past this a part's hinges are the Hierarchy/Inspector
	/// list's business, not an overlay's.</summary>
	private const int MaxMarkers = 8;

	private readonly Builder _builder;
	private readonly List<Node3D> _markers = new List<Node3D>();
	private readonly List<MeshInstance3D> _rods = new List<MeshInstance3D>();

	public HingeVisualizer(Builder builder)
	{
		_builder = builder;
	}

	/// <summary>Redraws the markers for the current selection. Called once per physics frame; a
	/// no-op with nothing selected, in a session, with the placement tool armed, or no hinges.</summary>
	public void Tick()
	{
		BuilderScene scene = _builder.Scene;
		int used = 0;
		if (scene != null && !scene.InTestMode && !_builder.ConstraintPick.IsArmed && LotConstraints.Count > 0)
		{
			LotObject part = _builder.Selection.GetFirstValid() as LotObject;
			int handle = HandleOf(part);
			if (handle >= 0)
			{
				IReadOnlyList<LotConstraintRecord> all = LotConstraints.All;
				for (int i = 0; i < all.Count && used < MaxMarkers; i++)
				{
					LotConstraintRecord record = all[i];
					if (record.Kind != LotConstraintKind.Hinge || !record.Touches(handle)) continue;
					LotObject first = scene.GetByHandle(record.A) as LotObject;
					if (first == null || !first.IsInsideTree()) continue;
					EnsureMarker(scene, used);
					_markers[used].GlobalPosition = first.GlobalTransform * record.Pivot;
					ConstraintVisuals.AimAxis(_rods[used], record.Axis);
					_markers[used].Visible = true;
					used++;
				}
			}
		}

		for (int i = used; i < _markers.Count; i++)
		{
			Node3D marker = _markers[i];
			if (marker != null && GodotObject.IsInstanceValid(marker)) marker.Visible = false;
		}
	}

	/// <summary>Creates or recreates the pooled marker at <paramref name="index"/>. A lot replace
	/// frees every lot-root child, so a pooled marker can be dead and is rebuilt here.</summary>
	private void EnsureMarker(BuilderScene scene, int index)
	{
		while (_markers.Count <= index)
		{
			_markers.Add(null);
			_rods.Add(null);
		}
		if (_markers[index] != null && GodotObject.IsInstanceValid(_markers[index])) return;

		MeshInstance3D rod;
		_markers[index] = ConstraintVisuals.CreateMarker(scene.LotRoot, "HingeMarker", out rod);
		_rods[index] = rod;
	}

	private static int HandleOf(Node node)
	{
		if (node == null || !node.HasMeta(BuilderScene.HandleMeta)) return -1;
		return node.GetMeta(BuilderScene.HandleMeta).AsInt32();
	}
}
