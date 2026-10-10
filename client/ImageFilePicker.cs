using Godot;

/// <summary>
/// The one image file browser in the project, shared by the wallpaper and the part Texture
/// property so both open the same dialog with the same filter and the same failure behaviour.
///
/// Godot's own FileDialog (a Control-based window) is used strictly as an internal editor/debug
/// convenience for choosing a file. It is not gameplay or creator-facing UI, so it does not
/// introduce a second UI paradigm on a player-facing surface (clinerules 1.2). It is marked here
/// so a later switch to the in-house UI layer is a single-file change.
/// </summary>
public static class ImageFilePicker
{
	private const string ImageFilter = "*.png,*.jpg,*.jpeg,*.webp,*.bmp,*.tga";
	private static readonly Vector2I DialogSize = new Vector2I(900, 560);

	/// <summary>
	/// Opens the browser and invokes <paramref name="onPicked"/> with the chosen path when the user
	/// confirms. The dialog frees itself in every exit path (confirm or cancel).
	/// </summary>
	public static void Open(Node host, string title, System.Action<string> onPicked)
	{
		if (host == null || !GodotObject.IsInstanceValid(host) || onPicked == null) return;

		FileDialog dialog = new FileDialog();
		dialog.Title = title;
		dialog.FileMode = FileDialog.FileModeEnum.OpenFile;
		dialog.Access = FileDialog.AccessEnum.Filesystem;
		dialog.AddFilter(ImageFilter, "Images");

		dialog.FileSelected += path =>
		{
			onPicked(path);
			Free(dialog);
		};
		dialog.Canceled += () => Free(dialog);

		host.AddChild(dialog);
		dialog.PopupCentered(DialogSize);
	}

	private static void Free(FileDialog dialog)
	{
		if (GodotObject.IsInstanceValid(dialog)) dialog.QueueFree();
	}
}
