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
}
