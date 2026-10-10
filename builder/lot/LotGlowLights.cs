using System.Collections.Generic;
using Godot;

/// <summary>
/// The lot's budgeted cast-light pool (milestone 2.9): the second half of luminescence.
///
/// Every glowing part emits from its own material (free, and unbounded in count), but a real
/// OmniLight3D per glowing part would be an unbounded per-part cost — the renderer pays for every
/// visible light — so cast light comes from a fixed pool instead: at most
/// <see cref="MaxActiveLights"/> lights, handed on each refresh to the glowing parts nearest the
/// active camera, and following those parts every frame. A glowing part without a slot stays
/// self-lit and simply casts no light; nothing is silently turned off.
///
/// The pool is parented to the node passed as <paramref name="lightHost"/> — the builder hands it
/// the viewport, not LotRoot, because replacing a lot frees every child of LotRoot (a pool light
/// parented there would die with the old world). Lights are internal-marked, so the hierarchy, the
/// archive walk and the session snapshot never see them: they are derived state and are never
/// persisted.
///
/// Cost is bounded and deliberate: a scan for candidates every <see cref="ScanIntervalSeconds"/>
/// (O(parts), on a timer), a nearest-N selection per scan (O(MaxActiveLights × candidates)), and
/// per frame only the ≤ MaxActiveLights assigned lights are updated. A lot with a thousand glowing
/// parts costs the same per frame as one with eight.
/// </summary>
public sealed class LotGlowLights
{
	/// <summary>
	/// The documented light budget: the pool never owns more than this many OmniLight3Ds, and a
	/// refresh lights at most this many of the nearest glowing parts. 8 covers a room full of lamps
	/// while keeping the light count bounded (the §3.6 chain warning picked the same number for its
	/// own budget); raise it here if the §14.1 harness ever says the budget can afford more.
	/// </summary>
	public const int MaxActiveLights = 8;

	/// <summary>
	/// How often the candidate set and the nearest-N selection are re-derived. Between scans the
	/// assigned lights just follow their parts, so this keeps a lot with many glowing parts cheap.
	/// </summary>
	public const double ScanIntervalSeconds = 0.5;

	private readonly Node _lightHost;
	private readonly Node _scanRoot;
	private readonly List<OmniLight3D> _pool = new List<OmniLight3D>();
	private readonly List<LotObject> _assigned = new List<LotObject>();
	private readonly List<LotObject> _candidates = new List<LotObject>();
	private readonly List<Vector3> _candidatePositions = new List<Vector3>();
	private readonly int[] _selection = new int[MaxActiveLights];
	private double _scanTimer = ScanIntervalSeconds; // scan on the first update
	private bool _needsRescan;

	public LotGlowLights(Node lightHost, Node scanRoot)
	{
		_lightHost = lightHost;
		_scanRoot = scanRoot;
	}

	/// <summary>How many parts the pool is currently lighting.</summary>
	public int ActiveLightCount { get { return _assigned.Count; } }

	/// <summary>How many light nodes the pool has created (it grows lazily to the number of parts
	/// actually lit, never beyond <see cref="MaxActiveLights"/>).</summary>
	public int PoolSize { get { return _pool.Count; } }

	/// <summary>Forces the next <see cref="Update"/> to re-scan immediately — used after a lot is
	/// replaced, whose parts the previous candidate list no longer describes.</summary>
	public void Invalidate()
	{
		_scanTimer = ScanIntervalSeconds;
		_needsRescan = true;
	}

	/// <summary>
	/// Per-frame tick. <paramref name="viewCamera"/> supplies the selection's viewpoint (whichever
	/// camera is current — the freecam in build mode, the player camera in a session); with no
	/// camera nothing is re-selected, but already-assigned lights still follow their parts.
	/// </summary>
	public void Update(double delta, Camera3D viewCamera)
	{
		if (_needsRescan) _scanTimer = ScanIntervalSeconds;
		_scanTimer += delta;
		if (viewCamera != null && _scanTimer >= ScanIntervalSeconds)
		{
			_scanTimer = 0d;
			RefreshNow(viewCamera.GlobalPosition);
		}
		FollowAssignedParts();
	}

	/// <summary>
	/// Re-derives the luminous candidates, picks the nearest <see cref="MaxActiveLights"/>, and
	/// positions the pool. Called by <see cref="Update"/> on its scan interval; public so the
	/// self-test can drive it deterministically against fixtures.
	/// </summary>
	public void RefreshNow(Vector3 viewPosition)
	{
		_needsRescan = false;
		CollectCandidates();
		int chosen = LotMaterialMath.SelectNearestSlots(_candidatePositions, viewPosition,
			MaxActiveLights, _selection);
		_assigned.Clear();
		for (int i = 0; i < chosen; i++)
		{
			LotObject part = _candidates[_selection[i]];
			if (part != null && GodotObject.IsInstanceValid(part)) _assigned.Add(part);
		}
		EnsurePoolLights(_assigned.Count);
		FollowAssignedParts();
	}

	private void CollectCandidates()
	{
		_candidates.Clear();
		_candidatePositions.Clear();
		if (_scanRoot == null || !GodotObject.IsInstanceValid(_scanRoot)) return;
		CollectRecursive(_scanRoot);
	}

	private void CollectRecursive(Node node)
	{
		int count = node.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			Node child = node.GetChild(i);
			// Internal children (outline, collision body, label, a session's joints) are never
			// candidates and carry no lot parts under them.
			if (child.HasMeta(LotObject.InternalChildMeta)) continue;
			LotObject part = child as LotObject;
			if (part != null && part.Kind != LotObjectKind.Decal
				&& part.Luminescence > 0f && !part.IsQueuedForDeletion())
			{
				_candidates.Add(part);
				_candidatePositions.Add(part.GlobalPosition);
			}
			CollectRecursive(child);
		}
	}

	private void EnsurePoolLights(int count)
	{
		while (_pool.Count < count && _pool.Count < MaxActiveLights)
		{
			OmniLight3D light = new OmniLight3D();
			light.Name = "GlowLight" + _pool.Count;
			// Lamp shadows would multiply the cost this budget exists to bound, and the sun is
			// already the scene's shadow caster: lamp light is shadowless by decision.
			light.ShadowEnabled = false;
			light.SetMeta(LotObject.InternalChildMeta, true);
			light.Visible = false;
			_pool.Add(light);
			_lightHost.AddChild(light);
		}
	}

	private void FollowAssignedParts()
	{
		for (int i = 0; i < _pool.Count; i++)
		{
			OmniLight3D light = _pool[i];
			if (!GodotObject.IsInstanceValid(light)) continue;

			LotObject part = i < _assigned.Count ? _assigned[i] : null;
			if (part != null && !GodotObject.IsInstanceValid(part))
			{
				// A session script destroyed the part mid-frame. Hide the light and ask for a
				// rescan; the slot mapping is left untouched so the remaining lights cannot
				// shift under their parts.
				_needsRescan = true;
				part = null;
			}
			if (part == null)
			{
				light.Visible = false;
				continue;
			}

			light.Visible = true;
			light.GlobalPosition = part.GlobalPosition;
			Color tint = part.Color;
			light.LightColor = new Color(tint.R, tint.G, tint.B);
			light.LightEnergy = LotMaterialMath.LightEnergyFor(part.Luminescence);
			light.OmniRange = LotMaterialMath.LightRangeFor(MaxExtentOf(part));
		}
	}

	/// <summary>Largest visual dimension of the part, in world units — the size input to the
	/// light-range rule. Derived from the live mesh and node scale (the same source the mass rule
	/// uses), so a resized or rescaled lamp re-derives its range on the next follow.</summary>
	private static float MaxExtentOf(LotObject part)
	{
		MeshInstance3D meshInstance = part.MeshInstance;
		if (meshInstance == null || !GodotObject.IsInstanceValid(meshInstance)) return 0f;
		Mesh mesh = meshInstance.Mesh;
		if (mesh == null) return 0f;
		Vector3 size = mesh.GetAabb().Size * part.Scale.Abs();
		return Mathf.Max(size.X, Mathf.Max(size.Y, size.Z));
	}
}
