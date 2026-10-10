using System.Collections.Generic;
using Godot;

/// <summary>
/// The play-time half of a placed query volume (milestone 3.13): while a session runs, each volume
/// samples the shared <see cref="SpatialQuery"/> once per frame and the difference against the
/// previous frame becomes `entered` / `exited` deliveries through the §3.7 registry — the
/// vocabulary reserved for exactly this — so volumes ride the existing event spine (coalescing,
/// the per-frame cap, watchdog units and two-ended cleanup all included) instead of a second
/// subscription mechanism.
///
/// Built on session entry and torn down on exit: volumes are gameplay, like sensors. The build-mode
/// previews hide for the session and come back on exit. The baseline is deliberately EMPTY, so the
/// contents present when a session starts fire `entered` on the first evaluation — stated, so a
/// script can rely on "everything in the region has entered".
/// </summary>
public sealed class QueryVolumeSession
{
	private readonly BuilderScene _scene;
	private readonly List<LotObject> _volumes = new List<LotObject>();
	private readonly Dictionary<int, HashSet<int>> _inside = new Dictionary<int, HashSet<int>>();
	// Reused per tick (the per-frame hot-path rule): the query result list, the current-content
	// set and the two change lists allocate only when a set actually grows.
	private readonly List<SpatialQuery.Hit> _hits = new List<SpatialQuery.Hit>();
	private readonly HashSet<int> _current = new HashSet<int>();
	private readonly List<int> _entered = new List<int>();
	private readonly List<int> _exited = new List<int>();

	public QueryVolumeSession(BuilderScene scene)
	{
		_scene = scene;
	}

	/// <summary>How many volumes this session watches (0 for a lot with none).</summary>
	public int VolumeCount { get { return _volumes.Count; } }

	public void Build()
	{
		_volumes.Clear();
		_inside.Clear();
		CollectVolumes(_scene.LotRoot);
		for (int i = 0; i < _volumes.Count; i++)
		{
			_volumes[i].SetQueryVolumeVisualVisible(false);
			// An editor-placed node carries a handle only once a registration path touches it
			// (§3.2 option A), and the runtime's walk resolves script-bearing entities only — so
			// without this, a volume placed from the toolbox and left unscripted would evaluate
			// nothing. The region gets its identity here, which is what makes placement alone
			// enough (a script on the volume is the common case, not a requirement).
			int handle = _scene.EnsureEntityHandle(_volumes[i]);
			if (handle >= 0) _inside[handle] = new HashSet<int>();
		}
	}

	/// <summary>One evaluation for every live volume. Volumes destroyed mid-session drop out; a
	/// just-destroyed node (still in the tree for the rest of its frame) is skipped, the §3.6
	/// lesson about deletion windows.</summary>
	public void Tick()
	{
		for (int i = _volumes.Count - 1; i >= 0; i--)
		{
			LotObject volume = _volumes[i];
			if (!GodotObject.IsInstanceValid(volume))
			{
				_volumes.RemoveAt(i);
				continue;
			}
			if (volume.IsQueuedForDeletion()) continue;
			int handle = BuilderScene.HandleOf(volume);
			if (handle < 0) continue;

			_hits.Clear();
			bool queried = volume.QueryShape == QueryVolumeShape.Box
				? SpatialQuery.OverlapBox(_scene, new Transform3D(volume.GlobalBasis, volume.GlobalPosition), volume.Size, _hits, out bool _)
				: SpatialQuery.OverlapSphere(_scene, volume.GlobalPosition, volume.QueryRadius, _hits, out bool _);
			if (!queried) continue;

			// The volume's own pick proxy is found by the query (its layer is in the pick mask):
			// it is the subject, not a guest.
			_current.Clear();
			for (int h = 0; h < _hits.Count; h++)
			{
				if (_hits[h].Handle != handle) _current.Add(_hits[h].Handle);
			}

			if (!_inside.TryGetValue(handle, out HashSet<int> previous))
			{
				previous = new HashSet<int>();
				_inside[handle] = previous;
			}
			Diff(previous, _current, _entered, _exited);
			for (int e = 0; e < _entered.Count; e++) _scene.EnqueueEntityContact(handle, _entered[e], "entered");
			for (int x = 0; x < _exited.Count; x++) _scene.EnqueueEntityContact(handle, _exited[x], "exited");
		}
	}

	public void Teardown()
	{
		for (int i = 0; i < _volumes.Count; i++)
		{
			LotObject volume = _volumes[i];
			if (GodotObject.IsInstanceValid(volume)) volume.SetQueryVolumeVisualVisible(true);
		}
		_volumes.Clear();
		_inside.Clear();
	}

	/// <summary>
	/// The pure set difference behind the evaluation: what is in <paramref name="current"/> but not
	/// in <paramref name="previous"/> enters; what left exits. Both lists come out handle-ascending
	/// (deterministic delivery order), and <paramref name="previous"/> is updated to the current
	/// contents. Zero allocation once the buffers have grown — it runs per volume per frame.
	/// </summary>
	public static void Diff(HashSet<int> previous, HashSet<int> current, List<int> entered, List<int> exited)
	{
		entered.Clear();
		exited.Clear();
		foreach (int handle in current)
		{
			if (!previous.Contains(handle)) entered.Add(handle);
		}
		foreach (int handle in previous)
		{
			if (!current.Contains(handle)) exited.Add(handle);
		}
		entered.Sort();
		exited.Sort();
		previous.Clear();
		foreach (int handle in current) previous.Add(handle);
	}

	private void CollectVolumes(Node node)
	{
		int count = node.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			Node child = node.GetChild(i);
			if (child.HasMeta(LotObject.InternalChildMeta)) continue;
			LotObject part = child as LotObject;
			if (part != null && part.Kind == LotObjectKind.QueryVolume && !part.IsQueuedForDeletion())
				_volumes.Add(part);
			CollectVolumes(child);
		}
	}
}