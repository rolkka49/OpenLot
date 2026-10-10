using System.Collections.Generic;
using Godot;

/// <summary>
/// The pure math behind a part's surface material (milestone 2.9): the transparency→alpha mapping,
/// the emission and cast-light numbers a luminescence value turns into, the clamps that keep both
/// properties in range, and the nearest-N slot selection the glow pool runs.
///
/// Nothing here touches nodes: the self-test drives this file directly, and <see cref="LotObject"/>
/// (the material) and <see cref="LotGlowLights"/> (the pool) are the only callers that bind it to a
/// live world. The light numbers are deliberately few, named constants — §11.4's rule is that
/// anything affecting feel stays exposed data rather than a buried literal, so the tunable numbers
/// live in one place here instead of inside the pool.
/// </summary>
public static class LotMaterialMath
{
	/// <summary>Transparency range: 0 fully opaque (the default — the pre-milestone material), 1
	/// invisible. Roblox-style direction on purpose: 0 = solid.</summary>
	public const float MinTransparency = 0f;
	public const float MaxTransparency = 1f;
	public const float DefaultTransparency = 0f;

	/// <summary>Luminescence range: 0 is a normal lit part; above 0 the part glows. No upper bound
	/// is invented — the value scales emission linearly, and the Inspector accepts any number — but
	/// 0..4 is the practical range.</summary>
	public const float MinLuminescence = 0f;
	public const float DefaultLuminescence = 0f;

	/// <summary>Cast-light energy per unit of luminescence. 1 = Godot's own "one light" energy, so
	/// a part at 1 reads like an ordinary lamp; this is the knob to turn if the eye check says lamps
	/// are too bright or too dim.</summary>
	public const float LightEnergyPerLuminescence = 1f;

	/// <summary>Cast-light range: a base plus a slope over the part's largest dimension (a bigger
	/// lamp reaches further), clamped so a tiny part still lights its own surface and a
	/// building-sized slab cannot light the whole lot.</summary>
	public const float LightRangeBase = 2f;
	public const float LightRangePerExtent = 3f;
	public const float MinLightRange = 3f;
	public const float MaxLightRange = 24f;

	/// <summary>Clamps a transparency into [<see cref="MinTransparency"/>, <see cref="MaxTransparency"/>].</summary>
	public static float ClampTransparency(float transparency)
	{
		return Mathf.Clamp(transparency, MinTransparency, MaxTransparency);
	}

	/// <summary>Clamps a luminescence at <see cref="MinLuminescence"/> (values above are kept — a
	/// very bright lamp is the creator's call, not an error).</summary>
	public static float ClampLuminescence(float luminescence)
	{
		return Mathf.Max(luminescence, MinLuminescence);
	}

	/// <summary>The material's albedo alpha for a transparency: 0 → 1 (opaque), 1 → 0 (invisible).
	/// The single mapping behind both the part's material and anything that needs the derived value.</summary>
	public static float AlphaForTransparency(float transparency)
	{
		return 1f - ClampTransparency(transparency);
	}

	/// <summary>Emission strength for a luminescence; linear (the property value IS the
	/// multiplier), so a script and the Inspector slider agree on what the number means.</summary>
	public static float EmissionEnergyFor(float luminescence)
	{
		return ClampLuminescence(luminescence);
	}

	/// <summary>Cast-light energy a luminescence value maps to. See
	/// <see cref="LightEnergyPerLuminescence"/>.</summary>
	public static float LightEnergyFor(float luminescence)
	{
		return ClampLuminescence(luminescence) * LightEnergyPerLuminescence;
	}

	/// <summary>
	/// Cast-light range for a part whose largest visual dimension is <paramref name="maxExtent"/>:
	/// <c>base + slope * extent</c>, clamped into [<see cref="MinLightRange"/>,
	/// <see cref="MaxLightRange"/>]. A negative or zero extent floors at the minimum range.
	/// </summary>
	public static float LightRangeFor(float maxExtent)
	{
		return Mathf.Clamp(LightRangeBase + LightRangePerExtent * Mathf.Max(maxExtent, 0f),
			MinLightRange, MaxLightRange);
	}

	/// <summary>
	/// Picks the <paramref name="budget"/> nearest candidates to <paramref name="view"/> and writes
	/// their indices into <paramref name="outIndices"/>, nearest first. Returns how many indices
	/// were written (0 for a null/empty list or a non-positive budget). Deterministic: equal squared
	/// distances resolve to the lower candidate index, and callers build the list in tree order, so
	/// two equidistant lamps always resolve the same way across sessions.
	///
	/// Allocation-free: the caller owns both buffers (the pool reuses them across refreshes).
	/// </summary>
	public static int SelectNearestSlots(IReadOnlyList<Vector3> positions, Vector3 view, int budget, int[] outIndices)
	{
		if (positions == null || outIndices == null || budget <= 0) return 0;
		int count = positions.Count;
		int limit = Mathf.Min(budget, Mathf.Min(count, outIndices.Length));
		int written = 0;
		for (int pick = 0; pick < limit; pick++)
		{
			int best = -1;
			float bestDistance = float.MaxValue;
			for (int i = 0; i < count; i++)
			{
				if (AlreadyPicked(outIndices, written, i)) continue;
				float distance = positions[i].DistanceSquaredTo(view);
				if (distance < bestDistance)
				{
					bestDistance = distance;
					best = i;
				}
			}
			if (best < 0) break;
			outIndices[written++] = best;
		}
		return written;
	}

	private static bool AlreadyPicked(int[] picked, int count, int index)
	{
		for (int i = 0; i < count; i++)
		{
			if (picked[i] == index) return true;
		}
		return false;
	}
}
