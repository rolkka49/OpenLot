using Godot;

/// <summary>
/// The one `.lot` file browser in the project, used by the File menu's Save As and Open. Like
/// <see cref="ImageFilePicker"/> it uses Godot's own FileDialog strictly as an internal editor/debug
/// convenience for choosing a file — not gameplay or creator-facing UI — so it does not introduce a
/// second UI paradigm on a player-facing surface (clinerules 1.2). Marked here so a later switch to the
/// in-house UI layer is a single-file change.
/// </summary>
public static class LotFilePicker
{
	private const string LotFilter = "*.lot";
	private const string LotFilterName = "OpenLot lot";
	private static readonly Vector2I DialogSize = new Vector2I(900, 560);

	/// <summary>Opens the browser to choose an existing lot, invoking <paramref name="onPicked"/> with
	/// the chosen path on confirm. The dialog frees itself on every exit path.</summary>
	public static void OpenForLoad(Node host, string title, System.Action<string> onPicked)
	{
		if (!CanOpen(host, onPicked)) return;

		FileDialog dialog = new FileDialog();
		dialog.Title = title;
		dialog.FileMode = FileDialog.FileModeEnum.OpenFile;
		dialog.Access = FileDialog.AccessEnum.Filesystem;
		dialog.AddFilter(LotFilter, LotFilterName);

		dialog.FileSelected += path =>
		{
			onPicked(path);
			Free(dialog);
		};
		dialog.Canceled += () => Free(dialog);

		host.AddChild(dialog);
		dialog.PopupCentered(DialogSize);
	}

	/// <summary>Opens the browser to choose where to write a lot, pre-filling <paramref name="suggestedName"/>.
	/// Godot does not append the extension for us, so <paramref name="onPicked"/> receives the path with
	/// the <c>.lot</c> extension guaranteed.</summary>
	public static void OpenForSave(Node host, string title, string suggestedName, System.Action<string> onPicked)
	{
		if (!CanOpen(host, onPicked)) return;

		FileDialog dialog = new FileDialog();
		dialog.Title = title;
		dialog.FileMode = FileDialog.FileModeEnum.SaveFile;
		dialog.Access = FileDialog.AccessEnum.Filesystem;
		dialog.AddFilter(LotFilter, LotFilterName);
		dialog.CurrentFile = EnsureExtension(suggestedName);

		dialog.FileSelected += path =>
		{
			onPicked(EnsureExtension(path));
			Free(dialog);
		};
		dialog.Canceled += () => Free(dialog);

		host.AddChild(dialog);
		dialog.PopupCentered(DialogSize);
	}

	/// <summary>A path ending in <c>.lot</c>, adding the extension only when it is absent.</summary>
	public static string EnsureExtension(string path)
	{
		if (string.IsNullOrEmpty(path)) return path;
		if (path.EndsWith(".lot", System.StringComparison.OrdinalIgnoreCase)) return path;
		return path + ".lot";
	}

	private static bool CanOpen(Node host, System.Action<string> onPicked)
	{
		return host != null && GodotObject.IsInstanceValid(host) && onPicked != null;
	}

	private static void Free(FileDialog dialog)
	{
		if (GodotObject.IsInstanceValid(dialog)) dialog.QueueFree();
	}
}
