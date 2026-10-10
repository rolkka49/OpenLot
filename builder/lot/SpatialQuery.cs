using System.Collections.Generic;
using Godot;

/// <summary>
/// The spatial query primitive (milestone 3.13, primitives layer): sphere/box overlap and a swept
/// sphere, all against the lot physics space through one implementation, plus the radial explosion
/// helper built on the same query. Every hit resolves through
/// <see cref="BuilderScene.ResolveBodyEntity"/> — the same rules touch events use — is deduped to
/// one entry per entity, sorted by handle (deterministic) and capped, so a query can never hand a
/// script an unbounded list (one Lua crossing per query, none per hit).
///
/// Broad-phase: the physics server's shape query IS the broad phase (the Jolt BVH, filtered by the
/// collision mask) — and the mask is the editor pick mask, so every part in every group,
/// non-collidable included, is visible to a query, exactly as §3.11's raycast defines for
/// gameplay-facing queries. Session-gated at the API surface: outside a running session there is no
/// live world to query.
///
/// The editor-facing query-volume object (roadmap §3.13) runs this same query per frame — see
/// <see cref="QueryVolumeSession"/>, which turns the frame-to-frame difference into `entered` /
/// `exited` events.
/// </summary>
public static class SpatialQuery
{
	/// <summary>Hard cap on hits returned per query (truncation is reported to the caller).</summary>
	public const int MaxHits = 64;

	public struct Hit
	{
		public int Handle;
		public Vector3 Position;
	}

	// Reused shape resources: a query may run every frame, so it must not allocate a shape per call
	// (the perf rule); the properties are rewritten per query and the game is single-threaded.
	private static readonly SphereShape3D SharedSphere = new SphereShape3D();
	private static readonly BoxShape3D SharedBox = new BoxShape3D();

	public static bool OverlapSphere(BuilderScene scene, Vector3 center, float radius, List<Hit> hits, out bool truncated)
	{
		SharedSphere.Radius = radius;
		hits.Clear();
		return Collect(scene, SharedSphere, center, Vector3.Zero, hits, out truncated);
	}

	/// <summary>
	/// Box overlap with a full transform — the placed query volume's form (milestone 3.13), so a
	/// rotated box region samples its real orientation. The Lua verbs keep the axis-aligned
	/// centre/size form below, which stays exactly as it was.
	/// </summary>
	public static bool OverlapBox(BuilderScene scene, Transform3D transform, Vector3 size, List<Hit> hits, out bool truncated)
	{
		SharedBox.Size = size;
		hits.Clear();
		return CollectTransform(scene, SharedBox, transform, Vector3.Zero, hits, out truncated);
	}

	public static bool OverlapBox(BuilderScene scene, Vector3 center, Vector3 size, List<Hit> hits, out bool truncated)
	{
		SharedBox.Size = size;
		hits.Clear();
		return Collect(scene, SharedBox, center, Vector3.Zero, hits, out truncated);
	}

	/// <summary>
	/// A sphere swept along <paramref name="direction"/> for <paramref name="maxDistance"/> — the
	/// melee "what did I swing through" query. Implemented as a SAMPLED sweep: the shape is
	/// evaluated at points along the path (spacing at most half the radius, at most 16 gaps) and
	/// the hits merge, which gives the full "everything along the way" list with predictable
	/// semantics — Jolt's shape cast reports only the nearest collision, which is not what a
	/// hitbox wants. Coverage is continuous while maxDistance ≤ 32 × radius (the samples overlap);
	/// a longer sweep spaces them out and is stated as a limitation. Results are the colliders
	/// (their origins), not exact entry points — a recorded later refinement.
	/// </summary>
	public static bool SweepSphere(BuilderScene scene, Vector3 origin, Vector3 direction, float radius, float maxDistance,
		List<Hit> hits, out bool truncated)
	{
		hits.Clear();
		truncated = false;
		SharedSphere.Radius = radius;
		Vector3 unit = direction.Normalized();
		float spacing = Mathf.Max(radius * 0.5f, 1e-3f);
		int samples = Mathf.Clamp(Mathf.CeilToInt(maxDistance / spacing), 1, 16);
		float sampleStep = maxDistance / samples;
		bool any = false;
		for (int i = 0; i <= samples; i++)
		{
			Vector3 center = origin + unit * (i * sampleStep);
			if (!Collect(scene, SharedSphere, center, Vector3.Zero, hits, out bool sampleTruncated)) return any;
			any = true;
			truncated |= sampleTruncated;
		}
		return any;
	}

	/// <summary>
	/// The radial impulse an explosion applies to a point: linear falloff from full power at the
	/// centre to zero at the rim, along the outward direction. No direction exists exactly at the
	/// centre, so that point receives nothing (stated, not accidental). Pure — the self-test pins it.
	/// </summary>
	public static Vector3 ExplosionImpulse(Vector3 center, Vector3 target, float radius, float power)
	{
		if (radius <= 0f || power <= 0f) return Vector3.Zero;
		Vector3 delta = target - center;
		float distance = delta.Length();
		if (distance >= radius || distance < 1e-4f) return Vector3.Zero;
		float falloff = 1f - distance / radius;
		return delta / distance * (power * falloff);
	}

	/// <summary>
	/// Applies an explosion to every simulated body the radius query finds: each is woken (a
	/// sleeping body would swallow the push) and receives its falloff-scaled radial impulse at its
	/// centre. Static/anchored parts are found by the query but skipped — they have nowhere to
	/// carry a push. Returns how many bodies were pushed.
	/// </summary>
	public static int Explode(BuilderScene scene, Vector3 center, float radius, float power)
	{
		List<Hit> hits = new List<Hit>();
		if (!OverlapSphere(scene, center, radius, hits, out bool _)) return 0;
		int pushed = 0;
		for (int i = 0; i < hits.Count; i++)
		{
			LotObject part = scene.GetByHandle(hits[i].Handle) as LotObject;
			if (part == null) continue;
			RigidBody3D body = part.CollisionBody as RigidBody3D;
			if (body == null || !GodotObject.IsInstanceValid(body)) continue;
			Vector3 impulse = ExplosionImpulse(center, hits[i].Position, radius, power);
			if (impulse.LengthSquared() < 1e-8f) continue;
			body.Sleeping = false;
			body.ApplyCentralImpulse(impulse);
			pushed++;
		}
		return pushed;
	}

	private static bool Collect(BuilderScene scene, Shape3D shape, Vector3 center, Vector3 motion,
		List<Hit> hits, out bool truncated)
	{
		return CollectTransform(scene, shape, new Transform3D(Basis.Identity, center), motion, hits, out truncated);
	}

	/// <summary>
	/// Runs one shape query and fills <paramref name="hits"/>: entities only (a collider that
	/// resolves to no lot object is skipped), deduped by handle, sorted handle-ascending and capped
	/// at <see cref="MaxHits"/>. False when there is no physics space (no live world).
	/// </summary>
	private static bool CollectTransform(BuilderScene scene, Shape3D shape, Transform3D transform, Vector3 motion,
		List<Hit> hits, out bool truncated)
	{
		truncated = false;
		if (scene == null || scene.LotRoot == null) return false;
		World3D world = scene.LotRoot.GetWorld3D();
		PhysicsDirectSpaceState3D space = world != null ? world.DirectSpaceState : null;
		if (space == null) return false;

		PhysicsShapeQueryParameters3D query = new PhysicsShapeQueryParameters3D();
		query.Shape = shape;
		query.Transform = transform;
		query.Motion = motion;
		query.CollisionMask = LotObject.EditorPickMask;
		query.CollideWithAreas = false;
		query.CollideWithBodies = true;

		// One extra result is requested so "exactly full" and "overflowing" are distinguishable.
		Godot.Collections.Array<Godot.Collections.Dictionary> results = space.IntersectShape(query, MaxHits + 1);
		for (int i = 0; i < results.Count; i++)
		{
			Godot.Collections.Dictionary result = results[i];
			Node collider = result["collider"].As<Node>();
			if (collider == null) continue;
			int handle = scene.ResolveBodyEntity(collider);
			if (handle < 0) continue;
			bool seen = false;
			for (int j = 0; j < hits.Count; j++)
			{
				if (hits[j].Handle == handle) { seen = true; break; }
			}
			if (seen) continue;
			Vector3 position = collider is Node3D node3D ? node3D.GlobalPosition : Vector3.Zero;
			hits.Add(new Hit { Handle = handle, Position = position });
		}

		hits.Sort((Hit a, Hit b) => a.Handle.CompareTo(b.Handle));
		if (hits.Count > MaxHits)
		{
			hits.RemoveRange(MaxHits, hits.Count - MaxHits);
			truncated = true;
		}
		return true;
	}
}
