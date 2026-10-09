using Godot;

/// <summary>
/// The sharp hinge-axis tools (milestone 3.6, orientation pass): exact presets and 45° steps for a
/// hinge axis that is otherwise a free world-space direction. All static and pure so the self-test
/// can pin the arithmetic without a scene; the buttons in the Toolbox (placement step) and the
/// Inspector are thin shells over these.
///
/// "Vertical" is world up. "Horizontal" flattens the current axis onto the ground plane, keeping
/// its heading, and falls back to the caller's hint (the placement default) when the axis is
/// vertical — a vertical axis has no heading to flatten. "Yaw 45°" turns the axis about the world
/// vertical (a vertical axis is invariant). "Tilt 45°" swings the axis in the vertical plane it
/// already defines, so repeated presses walk it through horizontal → diagonal → vertical → ... .
/// </summary>
public static class HingeAxisMath
{
	public const float StepDegrees = 45f;

	/// <summary>The exact vertical axis, so the button and the test agree by construction.</summary>
	public static Vector3 VerticalAxis { get { return Vector3.Up; } }

	/// <summary>Flattens <paramref name="axis"/> onto the ground plane, keeping its heading.
	/// <paramref name="fallback"/> (flattened too) is used when the axis is vertical.</summary>
	public static Vector3 Horizontal(Vector3 axis, Vector3 fallback)
	{
		Vector3 flat = new Vector3(axis.X, 0f, axis.Z);
		if (flat.LengthSquared() > 1e-6f) return flat.Normalized();
		Vector3 flatFallback = new Vector3(fallback.X, 0f, fallback.Z);
		if (flatFallback.LengthSquared() > 1e-6f) return flatFallback.Normalized();
		return Vector3.Right;
	}

	/// <summary>Rotates the axis 45° about the world vertical. A vertical axis is invariant — the
	/// button stays honest instead of producing a degenerate direction.</summary>
	public static Vector3 Yaw(Vector3 axis)
	{
		if (Mathf.Abs(axis.Dot(Vector3.Up)) > 0.999f) return axis;
		return axis.Rotated(Vector3.Up, Mathf.DegToRad(StepDegrees)).Normalized();
	}

	/// <summary>Rotates the axis 45° in the vertical plane it defines, about the horizontal
	/// perpendicular to it (world X for a vertical axis, which has no such perpendicular).</summary>
	public static Vector3 Tilt(Vector3 axis)
	{
		Vector3 horizontal = new Vector3(axis.X, 0f, axis.Z);
		Vector3 pivot = horizontal.LengthSquared() > 1e-6f
			? Vector3.Up.Cross(horizontal.Normalized()).Normalized()
			: Vector3.Right;
		return axis.Rotated(pivot, Mathf.DegToRad(StepDegrees)).Normalized();
	}

	/// <summary>Normalizes an axis for storage. False for a zero/degenerate direction, which is
	/// never a valid hinge — callers report that instead of storing a guess.</summary>
	public static bool TryNormalize(Vector3 axis, out Vector3 normalized)
	{
		normalized = Vector3.Up;
		if (axis.LengthSquared() < 1e-6f) return false;
		normalized = axis.Normalized();
		return true;
	}
}
