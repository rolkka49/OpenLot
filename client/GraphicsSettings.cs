using Godot;

/// <summary>
/// The graphics settings (§5.4): resolution scale, a shadow toggle, MSAA and the render method. A
/// plain instance with an explicit settings path (the <see cref="FontRegistry"/> pattern);
/// <see cref="GraphicsSettingsService"/> owns the one the client uses.
///
/// Three of the four apply live to the builder's lot viewport. The render METHOD cannot change at
/// runtime in Godot — it is fixed when the rendering device initialises — so it is stored as a
/// preference and applied by relaunching with <c>--rendering-method</c> (see
/// <see cref="GraphicsSettingsService.RestartWithRenderMethod"/>); the settings window says so
/// plainly rather than offering a switch that quietly does nothing.
/// </summary>
public class GraphicsSettings
{
	/// <summary>Shared client settings file — the same one the wallpaper/theme/font blocks live in.</summary>
	public const string DefaultSettingsPath = "user://openlot_settings.cfg";

	private const string Section = "graphics";

	public const float DefaultRenderScale = 1.0f;
	public const bool DefaultShadows = true;
	public const int DefaultMsaa = 0;                       // Viewport.Msaa.Disabled

	public const float MinRenderScale = 0.5f;
	public const float MaxRenderScale = 1.0f;
	public const int MinMsaa = 0;
	public const int MaxMsaa = 3;                           // Disabled / 2x / 4x / 8x

	/// <summary>The render-method values the dropdown offers; "" leaves the engine's own choice.</summary>
	public static readonly string[] RenderMethods = { "", "forward_plus", "mobile", "gl_compatibility" };

	public float RenderScale = DefaultRenderScale;
	public bool Shadows = DefaultShadows;
	public int Msaa = DefaultMsaa;

	/// <summary>"" (engine default) or one of <see cref="RenderMethods"/>. Restart-required.</summary>
	public string RenderMethod = "";

	public static string RenderMethodLabel(string method)
	{
		switch (method)
		{
			case "forward_plus": return "Forward+ (desktop)";
			case "mobile": return "Mobile";
			case "gl_compatibility": return "Compatibility (GLES3)";
			default: return "Engine default";
		}
	}

	/// <summary>The render method this process actually booted with (read-only, for the settings note).</summary>
	public static string RunningRenderMethod()
	{
		return ProjectSettings.GetSetting("rendering/renderer/rendering_method", "").AsString();
	}

	public void ResetToDefaults()
	{
		RenderScale = DefaultRenderScale;
		Shadows = DefaultShadows;
		Msaa = DefaultMsaa;
		RenderMethod = "";
	}

	public void ClampToLimits()
	{
		RenderScale = Mathf.Clamp(RenderScale, MinRenderScale, MaxRenderScale);
		Msaa = Mathf.Clamp(Msaa, MinMsaa, MaxMsaa);
		if (RenderMethod != "" && System.Array.IndexOf(RenderMethods, RenderMethod) < 0) RenderMethod = "";
	}

	// --- persistence (explicit path, so a suite never touches the real settings) ---

	public void Load(string path)
	{
		ResetToDefaults();
		if (string.IsNullOrEmpty(path) || !Godot.FileAccess.FileExists(path)) return;

		ConfigFile config = new ConfigFile();
		if (config.Load(path) != Error.Ok) return;
		if (!config.HasSection(Section)) return;

		RenderScale = config.GetValue(Section, "render_scale", DefaultRenderScale).AsSingle();
		Shadows = config.GetValue(Section, "shadows", DefaultShadows).AsBool();
		Msaa = config.GetValue(Section, "msaa", DefaultMsaa).AsInt32();
		RenderMethod = config.GetValue(Section, "render_method", "").AsString();
		ClampToLimits();
	}

	public void Save(string path)
	{
		if (string.IsNullOrEmpty(path)) return;

		ConfigFile config = new ConfigFile();
		if (Godot.FileAccess.FileExists(path)) config.Load(path);
		if (config.HasSection(Section)) config.EraseSection(Section);

		config.SetValue(Section, "render_scale", RenderScale);
		config.SetValue(Section, "shadows", Shadows);
		config.SetValue(Section, "msaa", Msaa);
		config.SetValue(Section, "render_method", RenderMethod);

		Error result = config.Save(path);
		if (result != Error.Ok) GD.PushWarning("[Graphics] could not save the graphics settings (" + result + ")");
	}
}

/// <summary>
/// The live graphics settings. Static holder around one <see cref="GraphicsSettings"/>; the settings
/// window edits it, and <see cref="BuilderScene"/> subscribes to <see cref="Changed"/> to push the
/// live values (scale, shadows, MSAA) onto its lot viewport — unsubscribing on exit, so the
/// subscription cannot outlive the scene.
/// </summary>
public static class GraphicsSettingsService
{
	public static event System.Action Changed;

	public static string SettingsPath { get; set; } = GraphicsSettings.DefaultSettingsPath;

	public static GraphicsSettings Current { get; private set; } = new GraphicsSettings();

	public static bool Ready { get; private set; }

	public static void LoadPersistedSettings()
	{
		if (Ready) return;
		Ready = true;
		Current.Load(SettingsPath);
	}

	/// <summary>Clamps, writes and tells the live viewport to re-apply.</summary>
	public static void ApplyAndSave()
	{
		Current.ClampToLimits();
		Save();
		Changed?.Invoke();
	}

	/// <summary>Tells the live viewport to re-read the values without writing the file — a slider drag
	/// previews live and commits the file on release (see <see cref="CameraSettingsService.NotifyChanged"/>).</summary>
	public static void NotifyChanged()
	{
		Changed?.Invoke();
	}

	public static void Save()
	{
		Current.Save(SettingsPath);
	}

	/// <summary>
	/// Relaunches the client with the stored render method (Godot cannot switch it at runtime). The
	/// caller quits afterwards; a failure returns a message naming the exact command-line flag, so a
	/// manual restart is always available.
	/// </summary>
	public static bool RestartWithRenderMethod(out string error)
	{
		string method = Current.RenderMethod;
		if (method.Length == 0)
		{
			error = "Choose a render method first.";
			return false;
		}

		string executable = OS.GetExecutablePath();
		if (executable.Length == 0)
		{
			error = "Could not locate the OpenLot executable; restart it with --rendering-method " + method + ".";
			return false;
		}

		System.Collections.Generic.List<string> args = new System.Collections.Generic.List<string>();
		args.Add("--rendering-method");
		args.Add(method);
		if (OS.HasFeature("editor"))
		{
			// Running inside the editor: point the new instance at this project, or it would open
			// the editor's own project manager instead of the game.
			args.Add("--path");
			args.Add(ProjectSettings.GlobalizePath("res://"));
		}

		int result = OS.CreateProcess(executable, args.ToArray(), false);
		if (result != (int)Error.Ok)
		{
			error = "Could not start a new instance (" + result + "); restart OpenLot with --rendering-method " + method + ".";
			return false;
		}

		error = "";
		return true;
	}
}
