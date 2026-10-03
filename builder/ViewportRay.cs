using Godot;

/// <summary>
/// Screen-space cursor -> world ray mapping for the lot viewport. Shared by the gizmo (which needs
/// the ray to pick its handles) and the part drag controller (which needs it to pick and drag
/// objects), so both agree exactly on where the cursor points.
/// </summary>
public static class ViewportRay
{
	/// <summary>
	/// Converts an ImGui mouse position into a world-space ray through the SubViewport camera.
	/// The render target is sized to match the displayed image, so the scale is normally 1:1;
	/// normalizing keeps the frame in which the viewport was just resized correct.
	/// </summary>
	public static void FromScreen(Camera3D camera, Vector2 mouse, Vector2 imageOrigin, Vector2 imageSize, Vector2I viewportSize, out Vector3 origin, out Vector3 direction)
	{
		Vector2 viewportPoint = new Vector2(
			(mouse.X - imageOrigin.X) * (viewportSize.X / imageSize.X),
			(mouse.Y - imageOrigin.Y) * (viewportSize.Y / imageSize.Y));
		origin = camera.ProjectRayOrigin(viewportPoint);
		direction = camera.ProjectRayNormal(viewportPoint);
	}
}