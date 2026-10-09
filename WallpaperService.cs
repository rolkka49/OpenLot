using Godot;

/// <summary>
/// Desktop wallpaper for OpenLot's ImGui layer: the flat area that sits behind the dockspace and
/// every panel. This is 2D UI chrome, deliberately NOT the lot's 3D sky — the lot environment
/// (time of day, fog, sky presets) is milestone 2.5's business and lives in the BuilderScene.
///
/// Drawing model: a full-viewport, input-less ImGui window submitted BEFORE the dockspace host
/// window, so it renders underneath every panel. While a wallpaper is active, the dockspace host
/// window and the empty dock node are pushed transparent for that one call so nothing opaque is
/// painted over the image. With no wallpaper set this method is exactly
/// <see cref="ImGui.DockspaceOverMainViewport"/> and the layout path is unchanged.
///
/// Images resolve through <see cref="LotTextureCache"/>, the same path the part Texture property
/// uses, so there is one loader and one disposal rule.
///
/// The choice is remembered on disk (<c>user://openlot_settings.cfg</c>) and restored by
/// <see cref="LoadPersistedSettings"/>, so it survives both scene changes (Terminal ↔ Builder) and
/// a restart. That file is the seed of §4.4's client settings; it is written by re-reading first so
/// it can grow keys without one writer dropping another's.
/// </summary>
public static class WallpaperService
{
	// Leading "##" keeps the window out of any visible title, and makes the id stable.
	private const string WindowId = "##openlot_wallpaper";
	private static readonly Color FullyTransparent = new Color(0f, 0f, 0f, 0f);

	/// <summary>Where the choice is remembered. Small on purpose: §4.4 grows this into the real
	/// client settings surface, and this file is the one it will adopt rather than replace.</summary>
	private const string SettingsPath = "user://openlot_settings.cfg";
	private const string Section = "wallpaper";
	private const string SourcePathKey = "source_path";

	private static string _assetId = "";
	/// <summary>True once <see cref="LoadPersistedSettings"/> has run, so it is a one-shot init step.</summary>
	private static bool _settingsLoaded;

	/// <summary>Asset id of the active wallpaper, or "" when the default background is showing.</summary>
	public static string AssetId { get { return _assetId; } }

	public static bool HasWallpaper { get { return _assetId.Length > 0; } }

	/// <summary>
	/// Loads an image file and makes it the wallpaper. Returns false (with a message) when the
	/// file cannot be read, leaving the current wallpaper untouched.
	/// </summary>
	public static bool SetFromFile(string path, out string error)
	{
		// Reuses an already-loaded image when the same file is picked again.
		string assetId = LotTextureCache.ImportFromFile(path, out error);
		if (assetId.Length == 0) return false;
		// ImportFromFile already holds one reference for us, so swap directly.
		Swap(assetId);
		error = "";
		return true;
	}

	/// <summary>Makes a previously registered asset the wallpaper. False when the id is unknown.</summary>
	public static bool Set(string assetId, out string error)
	{
		if (LotTextureCache.Acquire(assetId) == 0)
		{
			error = "no such image asset: " + assetId;
			return false;
		}
		Swap(assetId);
		error = "";
		return true;
	}

	/// <summary>Returns to the default background, drops the texture reference and forgets the
	/// remembered choice, so the default is still showing after a restart.</summary>
	public static void Clear()
	{
		if (_assetId.Length == 0) return;
		string previous = _assetId;
		_assetId = "";
		LotTextureCache.Release(previous);
		PersistSetting("");
	}

	/// <summary>
	/// Draws the wallpaper (when one is set) and then the dockspace. Call this instead of
	/// <see cref="ImGui.DockspaceOverMainViewport"/> as the first thing in a layout handler.
	/// </summary>
	public static void DrawBackgroundAndDockspace()
	{
		if (_assetId.Length == 0)
		{
			// No wallpaper: byte-for-byte the original behaviour, no style changes at all.
			ImGui.DockspaceOverMainViewport();
			return;
		}

		DrawBackground();

		// The dockspace host window and an empty central dock node would otherwise paint their
		// own opaque fill over the image. Overriding the two colours for just this call keeps the
		// change local to the wallpaper case — docked panels keep their normal background.
		ImGui.PushStyleColor(ImGui.ColWindowBg, FullyTransparent);
		ImGui.PushStyleColor(ImGui.ColDockingEmptyBg, FullyTransparent);
		ImGui.DockspaceOverMainViewport();
		ImGui.PopStyleColor(2);
	}

	/// <summary>
	/// Opens a file browser to pick the wallpaper, and applies the choice when the user confirms.
	/// <paramref name="host"/> is any node in the live scene (the dialog needs a tree parent).
	/// </summary>
	public static void RequestFileDialog(Node host)
	{
		ImageFilePicker.Open(host, "Choose a wallpaper image", OnFileSelected);
	}

	private static void DrawBackground()
	{
		Texture2D texture = LotTextureCache.Get(_assetId);
		if (texture == null)
		{
			// The asset was released behind our back: draw nothing. The dockspace transparency
			// push still yields the plain background rather than an opaque panel colour.
			return;
		}

		long imguiId = LotTextureCache.GetImGuiId(_assetId);
		if (imguiId < 0) return;

		Vector2 display = GetDisplaySize();
		if (display.X <= 0f || display.Y <= 0f) return;

		ImGui.SetNextWindowPos(0f, 0f, ImGui.CondAlways);
		ImGui.SetNextWindowSize(display.X, display.Y, ImGui.CondAlways);
		// Zero padding/rounding/border: this window is a canvas for the image, not a panel.
		ImGui.PushStyleVar(ImGui.StyleVarWindowPadding, Vector2.Zero);
		ImGui.PushStyleVar(ImGui.StyleVarWindowBorderSize, 0f);
		ImGui.PushStyleVar(ImGui.StyleVarWindowRounding, 0f);

		int flags = ImGui.WindowNoTitleBar | ImGui.WindowNoResize | ImGui.WindowNoMove
			| ImGui.WindowNoScrollbar | ImGui.WindowNoScrollWithMouse | ImGui.WindowNoCollapse
			| ImGui.WindowNoSavedSettings | ImGui.WindowNoBackground | ImGui.WindowNoFocusOnAppearing
			// Never focusable and never brought forward: that is what pins it behind every panel.
			| ImGui.WindowNoBringToFrontOnFocus | ImGui.WindowNoInputs | ImGui.WindowNoDocking;

		bool open = ImGui.Begin(WindowId, flags);
		if (!open)
		{
			ImGui.End();
			ImGui.PopStyleVar(3);
			return;
		}

		Vector2 imageSize = texture.GetSize();
		if (imageSize.X > 0f && imageSize.Y > 0f)
		{
			ComputeCoverFit(imageSize, display, out Vector2 drawnSize, out Vector2 offset);
			ImGui.SetCursorPos(offset);
			ImGui.Image(imguiId, drawnSize);
		}

		ImGui.End();
		ImGui.PopStyleVar(3);
	}

	/// <summary>
	/// Cover fit: scale the image so it covers the viewport on both axes, then centre it. The
	/// overflow is clipped by the hosting window, so there is no aspect distortion and no
	/// letterboxing. Pure arithmetic, so the self-test covers it directly.
	/// </summary>
	public static void ComputeCoverFit(Vector2 imageSize, Vector2 viewportSize, out Vector2 drawnSize, out Vector2 offset)
	{
		if (imageSize.X <= 0f || imageSize.Y <= 0f || viewportSize.X <= 0f || viewportSize.Y <= 0f)
		{
			drawnSize = Vector2.Zero;
			offset = Vector2.Zero;
			return;
		}

		float scale = Mathf.Max(viewportSize.X / imageSize.X, viewportSize.Y / imageSize.Y);
		drawnSize = imageSize * scale;
		// The offset is negative on the overflowing axis: the image starts left of / above the
		// viewport origin and is clipped equally on both sides.
		offset = (viewportSize - drawnSize) * 0.5f;
	}

	private static void Swap(string assetId)
	{
		string previous = _assetId;
		_assetId = assetId;
		// Both entry points arrive holding exactly one reference on the new asset, so the outgoing
		// one is always released — including when the same image is picked again, where the
		// unconditional release is what stops a repeat pick from banking a second reference.
		if (previous.Length > 0) LotTextureCache.Release(previous);

		PersistSetting(LotTextureCache.GetSourcePath(assetId));
	}

	/// <summary>
	/// Restores the remembered wallpaper. Idempotent, and safe to call from any scene's _Ready: the
	/// first call does the work, later ones return immediately.
	///
	/// A wallpaper chosen during this session always wins over the stored one, so this can never
	/// overwrite a choice the user just made. If the stored image is gone (moved, deleted) the entry
	/// is forgotten with a warning instead of leaving the config pointing at nothing.
	/// </summary>
	public static void LoadPersistedSettings()
	{
		if (_settingsLoaded) return;
		_settingsLoaded = true;

		// A wallpaper chosen during this session always wins over the stored one, so this can never
		// overwrite a choice the user just made.
		if (_assetId.Length > 0) return;

		string path = ReadStoredPath();
		if (path.Length == 0) return;

		if (!Godot.FileAccess.FileExists(path))
		{
			GD.PushWarning("[Wallpaper] the remembered wallpaper image is gone; forgetting it: " + path);
			PersistSetting("");
			return;
		}

		if (!SetFromFile(path, out string error))
		{
			GD.PushWarning("[Wallpaper] the remembered wallpaper could not be restored: " + error);
			PersistSetting("");
			return;
		}

		GD.Print("[Wallpaper] restored: " + path);
	}

	/// <summary>The remembered wallpaper path, or "" when nothing is stored.</summary>
	private static string ReadStoredPath()
	{
		if (!Godot.FileAccess.FileExists(SettingsPath)) return "";

		ConfigFile config = new ConfigFile();
		if (config.Load(SettingsPath) != Error.Ok) return "";
		return config.GetValue(Section, SourcePathKey, "").AsString();
	}

	/// <summary>
	/// Writes (or clears) the remembered wallpaper path. Compares against what is already stored and
	/// returns early when it matches, so restoring at startup cannot rewrite the file it just read,
	/// and clearing when nothing is set does not create a config file at all.
	///
	/// Nothing is written until <see cref="LoadPersistedSettings"/> has run, so anything that
	/// touches the wallpaper before startup finishes (a debug self-test, for instance) cannot
	/// rewrite the stored preference.
	/// </summary>
	private static void PersistSetting(string sourcePath)
	{
		if (!_settingsLoaded) return;
		if (ReadStoredPath() == sourcePath) return;

		ConfigFile config = new ConfigFile();
		// Re-read first so keys a future settings writer added are not dropped by our save.
		if (Godot.FileAccess.FileExists(SettingsPath)) config.Load(SettingsPath);

		if (sourcePath.Length == 0)
		{
			// Godot's erase_section_key also drops the section once its last key goes, so clearing
			// leaves no empty stub behind and there is nothing further to tidy up here.
			if (!config.HasSectionKey(Section, SourcePathKey)) return;
			config.EraseSectionKey(Section, SourcePathKey);
		}
		else
		{
			config.SetValue(Section, SourcePathKey, sourcePath);
		}

		Error result = config.Save(SettingsPath);
		if (result != Error.Ok)
			GD.PushWarning("[Wallpaper] could not save the wallpaper setting (" + result + ")");
	}

	private static void OnFileSelected(string path)
	{
		if (!SetFromFile(path, out string error))
			GD.PushWarning("[Wallpaper] " + error);
	}

	private static Vector2 GetDisplaySize()
	{
		SceneTree tree = Engine.GetMainLoop() as SceneTree;
		if (tree == null || tree.Root == null) return Vector2.Zero;
		return tree.Root.GetVisibleRect().Size;
	}
}

