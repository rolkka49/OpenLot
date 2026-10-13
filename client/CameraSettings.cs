using Godot;

/// <summary>
/// The camera settings (§5.4): field of view, look sensitivity, invert-Y and §3.14's player zoom
/// speed/blend range. One global set shared by the freecam and the player camera — the two read the
/// same value today (both were hardcoded to 0.15), so a single FOV/sensitivity is what "one value
/// tunes both" means; per-camera values would be a later extension if feel testing (§11.4) asks.
///
/// A plain instance with an explicit settings path (the <see cref="FontRegistry"/> pattern), so a
/// suite drives a scratch file and can never touch the real settings;
/// <see cref="CameraSettingsService"/> owns the one the client uses. The defaults are exactly the
/// values the fields were hardcoded to before this milestone, so an untouched install behaves
/// identically.
/// </summary>
public class CameraSettings
{
	/// <summary>Shared client settings file — the same one the wallpaper/theme/font blocks live in.</summary>
	public const string DefaultSettingsPath = "user://openlot_settings.cfg";

	private const string Section = "camera";

	public const float DefaultFov = 75f;                 // Godot's own Camera3D default
	public const float DefaultSensitivity = 0.15f;       // degrees per pixel (the pre-5.4 value)
	public const float DefaultZoomStep = 1.0f;           // §3.14's OrbitalCamera.ZoomStep
	public const float DefaultZoomBlendStart = 3.5f;     // §3.14's OrbitalCamera.BlendStart

	// Ranges chosen to stay usable at both ends: a fisheye and a telescope are the extremes.
	public const float MinFov = 40f;
	public const float MaxFov = 120f;
	public const float MinSensitivity = 0.02f;
	public const float MaxSensitivity = 1.0f;
	public const float MinZoomStep = 0.1f;
	public const float MaxZoomStep = 5.0f;
	public const float MinZoomBlendStart = 0f;
	public const float MaxZoomBlendStart = 10f;

	public float Fov = DefaultFov;
	public float LookSensitivity = DefaultSensitivity;
	public bool InvertY;
	public float ZoomStep = DefaultZoomStep;
	public float ZoomBlendStart = DefaultZoomBlendStart;

	public void ResetToDefaults()
	{
		Fov = DefaultFov;
		LookSensitivity = DefaultSensitivity;
		InvertY = false;
		ZoomStep = DefaultZoomStep;
		ZoomBlendStart = DefaultZoomBlendStart;
	}

	/// <summary>Clamps every value into its range — run on load and before applying, so a
	/// hand-edited or out-of-range value can never put the camera somewhere unusable.</summary>
	public void ClampToLimits()
	{
		Fov = Mathf.Clamp(Fov, MinFov, MaxFov);
		LookSensitivity = Mathf.Clamp(LookSensitivity, MinSensitivity, MaxSensitivity);
		ZoomStep = Mathf.Clamp(ZoomStep, MinZoomStep, MaxZoomStep);
		ZoomBlendStart = Mathf.Clamp(ZoomBlendStart, MinZoomBlendStart, MaxZoomBlendStart);
	}

	// --- persistence (explicit path, so a suite never touches the real settings) ---

	public void Load(string path)
	{
		ResetToDefaults();
		if (string.IsNullOrEmpty(path) || !Godot.FileAccess.FileExists(path)) return;

		ConfigFile config = new ConfigFile();
		if (config.Load(path) != Error.Ok) return;
		if (!config.HasSection(Section)) return;

		Fov = config.GetValue(Section, "fov", DefaultFov).AsSingle();
		LookSensitivity = config.GetValue(Section, "sensitivity", DefaultSensitivity).AsSingle();
		InvertY = config.GetValue(Section, "invert_y", false).AsBool();
		ZoomStep = config.GetValue(Section, "zoom_step", DefaultZoomStep).AsSingle();
		ZoomBlendStart = config.GetValue(Section, "zoom_blend_start", DefaultZoomBlendStart).AsSingle();
		ClampToLimits();
	}

	public void Save(string path)
	{
		if (string.IsNullOrEmpty(path)) return;

		ConfigFile config = new ConfigFile();
		// Re-read first: the wallpaper, alias, font, theme and graphics blocks share this file.
		if (Godot.FileAccess.FileExists(path)) config.Load(path);
		if (config.HasSection(Section)) config.EraseSection(Section);

		config.SetValue(Section, "fov", Fov);
		config.SetValue(Section, "sensitivity", LookSensitivity);
		config.SetValue(Section, "invert_y", InvertY);
		config.SetValue(Section, "zoom_step", ZoomStep);
		config.SetValue(Section, "zoom_blend_start", ZoomBlendStart);

		Error result = config.Save(path);
		if (result != Error.Ok) GD.PushWarning("[Camera] could not save the camera settings (" + result + ")");
	}
}

/// <summary>
/// The client's live camera settings. A static holder around one <see cref="CameraSettings"/> —
/// the same shape as <see cref="ThemeService"/> — because both scenes (the terminal's settings
/// window and the builder) read it and the builder applies it to its two cameras.
///
/// <see cref="Changed"/> is how an edit reaches the live cameras without the settings surface
/// depending on the builder: <see cref="BuilderScene"/> subscribes while it is alive (and
/// unsubscribes on exit, so the subscription cannot outlive it).
/// </summary>
public static class CameraSettingsService
{
	public static event System.Action Changed;

	/// <summary>Settable seam: the settings file the values are remembered in.</summary>
	public static string SettingsPath { get; set; } = CameraSettings.DefaultSettingsPath;

	public static CameraSettings Current { get; private set; } = new CameraSettings();

	/// <summary>True once <see cref="LoadPersistedSettings"/> has run (diagnostics / tests).</summary>
	public static bool Ready { get; private set; }

	/// <summary>Idempotent, so both scenes can call it (the wallpaper/theme/fonts do the same).</summary>
	public static void LoadPersistedSettings()
	{
		if (Ready) return;
		Ready = true;
		Current.Load(SettingsPath);
	}

	/// <summary>Writes the current values (one write per edit gesture) and tells the live cameras.</summary>
	public static void ApplyAndSave()
	{
		Current.ClampToLimits();
		Save();
		Changed?.Invoke();
	}

	/// <summary>Tells the live cameras to re-read the values without writing the file — used while a
	/// slider is being dragged, so the preview is live but the file is written once, on release (the
	/// commit-on-release rule §2.4's undo coalescing follows too).</summary>
	public static void NotifyChanged()
	{
		Changed?.Invoke();
	}

	/// <summary>Writes the current values without re-applying (boot, and diagnostics).</summary>
	public static void Save()
	{
		Current.Save(SettingsPath);
	}
}
