using Godot;

/// <summary>How the player camera answers "where do I sit, what do I look at" (milestone 3.10).</summary>
public enum LotCameraMode
{
	/// <summary>Today's default: orbiting behind the character.</summary>
	ThirdPerson,
	/// <summary>Parked at the character's head; the character's own mesh is hidden.</summary>
	FirstPerson,
	/// <summary>Parked at a creator-set spot, looking at a creator-set target; follows nobody.</summary>
	Fixed,
	/// <summary>Driven by a running FlyCamera shot; returns to the previous mode when it ends.</summary>
	Scripted
}

/// <summary>
/// Pure camera math for milestone 3.10 (design doc D3/D5): mode-name parsing, the first-person eye
/// offset and the shot's linear interpolation. No scene state — everything here is hand-verifiable
/// in the self-test.
/// </summary>
public static class LotCameraMath
{
	/// <summary>
	/// How far above the orbit's focus point the first-person eye sits (the focus is the
	/// character's chest at FocusHeight 1.0; the eye lands at 1.6 above the character centre, the
	/// capsule being 2.0 tall).
	/// </summary>
	public const float FirstPersonEyeAboveFocus = 0.6f;

	public static bool TryParseMode(string name, out LotCameraMode mode)
	{
		switch (name)
		{
			case "thirdperson": mode = LotCameraMode.ThirdPerson; return true;
			case "firstperson": mode = LotCameraMode.FirstPerson; return true;
			case "fixed": mode = LotCameraMode.Fixed; return true;
			case "scripted": mode = LotCameraMode.Scripted; return true;
			default: mode = LotCameraMode.ThirdPerson; return false;
		}
	}

	public static string ModeName(LotCameraMode mode)
	{
		switch (mode)
		{
			case LotCameraMode.FirstPerson: return "firstperson";
			case LotCameraMode.Fixed: return "fixed";
			case LotCameraMode.Scripted: return "scripted";
			default: return "thirdperson";
		}
	}

	/// <summary>The eye position for first-person: the character centre raised by the focus height
	/// plus the eye offset.</summary>
	public static Vector3 FirstPersonPosition(Vector3 characterCenter, float focusHeight)
	{
		return characterCenter + Vector3.Up * (focusHeight + FirstPersonEyeAboveFocus);
	}

	// --- player zoom & the first-person blend (milestone 3.14) -----------------------------------

	/// <summary>
	/// Where the zoom sits in the first-person blend band: 0 at or above <paramref name="blendStart"/>,
	/// 1 at or past <paramref name="blendEnd"/> (the innermost distance), linear between. Pure —
	/// the self-test pins both ends and the middle.
	/// </summary>
	public static float BlendFactor(float distance, float blendStart, float blendEnd)
	{
		if (distance >= blendStart) return 0f;
		if (distance <= blendEnd) return 1f;
		return (blendStart - distance) / (blendStart - blendEnd);
	}

	/// <summary>The classic smoothstep ease on 0..1 — zero slope at both ends, so the blend leaves
	/// the orbit and arrives at the eye without a visible kink.</summary>
	public static float Smoothstep(float t)
	{
		t = Mathf.Clamp(t, 0f, 1f);
		return t * t * (3f - 2f * t);
	}

	/// <summary>
	/// Exponential approach for the zoom glide: <paramref name="current"/> eases toward
	/// <paramref name="target"/> at <paramref name="rate"/> (1/seconds), snapping to exactly the
	/// target once within a ten-thousandth — the "lands on the previous orbit" contract the probe
	/// checks. A non-positive rate or delta leaves the value untouched.
	/// </summary>
	public static float Damp(float current, float target, float rate, float delta)
	{
		if (rate <= 0f || delta <= 0f) return current;
		if (Mathf.Abs(current - target) <= 1e-4f) return target;
		return target + (current - target) * Mathf.Exp(-rate * delta);
	}

	// --- world-up aim (the straight-down LookAt fix) ----------------------------------------------

	/// <summary>
	/// The rotation for a camera aiming along <paramref name="forward"/> with world up — the same
	/// basis Godot's <c>Basis.LookingAt</c> builds (camera convention: the local -Z axis aims at
	/// the target), computed here so the colinear case resolves silently. A view exactly straight
	/// down or straight up makes the engine log "Target and up vectors are colinear" on every
	/// frame and substitute <c>Vector3::get_any_perpendicular</c>; this replicates that fallback
	/// (see <see cref="AnyPerpendicular"/>) so the resulting pose is the engine-identical one,
	/// minus the per-frame warning. Every non-colinear aim is identical to <c>LookAt</c> — the
	/// self-test pins both halves against the engine's own output.
	/// </summary>
	public static Basis LookBasis(Vector3 forward, Vector3 up)
	{
		Vector3 z = -forward.Normalized();
		Vector3 x = up.Cross(z);
		if (x.IsZeroApprox()) x = AnyPerpendicular(up);
		x = x.Normalized();
		return new Basis(x, z.Cross(x), z);
	}

	/// <summary>
	/// A deterministic perpendicular to <paramref name="v"/> — a faithful port of Godot's
	/// <c>Vector3::get_any_perpendicular</c> (core/math/vector3.h): the cross with RIGHT or UP,
	/// whichever differs more from the vector's smallest-magnitude axis. Exactly the fallback the
	/// engine's own <c>Basis::looking_at</c> uses when the target and up vectors are colinear —
	/// the self-test pins the result against the engine's native path for both colinear
	/// directions, so the pose that replaces Godot's warning is the engine-identical one. Only
	/// ever called with world up by <see cref="LookBasis"/>, but ported generally so the logic
	/// stays traceable to the engine's.
	/// </summary>
	public static Vector3 AnyPerpendicular(Vector3 v)
	{
		// Ported verbatim (core/math/vector3.h); the zero check is the engine's ERR_FAIL path.
		if (v.IsZeroApprox()) return Vector3.Zero;
		return v.Cross(Mathf.Abs(v.X) <= Mathf.Abs(v.Y) && Mathf.Abs(v.X) <= Mathf.Abs(v.Z)
			? Vector3.Right
			: Vector3.Up).Normalized();
	}
}
