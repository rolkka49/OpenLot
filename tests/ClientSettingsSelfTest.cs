using Godot;

/// <summary>
/// Verification for milestone 5.4's camera and graphics settings: the defaults match the values the
/// fields were hardcoded to before the milestone (so an untouched install is unchanged), the clamps
/// hold, and both blocks round-trip through the real config encoder on a scratch file — never the
/// real settings. Follows the project's self-test convention: no framework, PASS/FAIL lines.
/// </summary>
public static class ClientSettingsSelfTest
{
	private const string ScratchPath = "user://client_settings_selftest.cfg";
	private const float Tolerance = 1e-4f;

	private static int _failures;
	private static int _checks;

	public static int Run()
	{
		_failures = 0;
		_checks = 0;

		TestCameraDefaults();
		TestCameraClamps();
		TestCameraRoundTrip();
		TestGraphicsDefaults();
		TestGraphicsRoundTrip();

		GD.Print("[ClientSettingsSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
		return _failures;
	}

	private static void Check(string name, bool condition)
	{
		_checks++;
		if (condition) GD.Print("[ClientSettingsSelfTest] PASS  " + name);
		else { _failures++; GD.PrintErr("[ClientSettingsSelfTest] FAIL  " + name); }
	}

	private static bool Near(float a, float b)
	{
		return Mathf.Abs(a - b) <= Tolerance;
	}

	private static void TestCameraDefaults()
	{
		CameraSettings camera = new CameraSettings();
		Check("camera defaults: FOV is Godot's 75",
			Near(camera.Fov, CameraSettings.DefaultFov) && Near(camera.Fov, 75f));
		Check("camera defaults: sensitivity is the pre-5.4 0.15",
			Near(camera.LookSensitivity, 0.15f));
		Check("camera defaults: invert-Y is off", !camera.InvertY);
		Check("camera defaults: zoom step and blend match §3.14's fields",
			Near(camera.ZoomStep, 1.0f) && Near(camera.ZoomBlendStart, 3.5f));
	}

	private static void TestCameraClamps()
	{
		CameraSettings camera = new CameraSettings();
		camera.Fov = 500f;
		camera.LookSensitivity = 0f;
		camera.ZoomStep = 99f;
		camera.ZoomBlendStart = -5f;
		camera.ClampToLimits();
		Check("camera clamps: FOV is capped at the maximum", Near(camera.Fov, CameraSettings.MaxFov));
		Check("camera clamps: sensitivity is floored at the minimum", Near(camera.LookSensitivity, CameraSettings.MinSensitivity));
		Check("camera clamps: zoom step is capped", Near(camera.ZoomStep, CameraSettings.MaxZoomStep));
		Check("camera clamps: blend start is floored at zero", Near(camera.ZoomBlendStart, 0f));
	}

	private static void TestCameraRoundTrip()
	{
		CameraSettings written = new CameraSettings();
		written.Fov = 92f;
		written.LookSensitivity = 0.4f;
		written.InvertY = true;
		written.ZoomStep = 2f;
		written.ZoomBlendStart = 5f;
		// Seed another block first, so the save's re-read-first contract is exercised.
		ConfigFile seed = new ConfigFile();
		seed.SetValue("wallpaper", "source_path", "user://keep.png");
		seed.Save(ScratchPath);
		written.Save(ScratchPath);

		CameraSettings read = new CameraSettings();
		read.Load(ScratchPath);
		Check("camera round-trip: FOV survives", Near(read.Fov, 92f));
		Check("camera round-trip: sensitivity survives", Near(read.LookSensitivity, 0.4f));
		Check("camera round-trip: invert-Y survives", read.InvertY);
		Check("camera round-trip: zoom step survives", Near(read.ZoomStep, 2f));
		Check("camera round-trip: blend start survives", Near(read.ZoomBlendStart, 5f));

		ConfigFile config = new ConfigFile();
		config.Load(ScratchPath);
		Check("camera round-trip: the wallpaper block survives",
			config.GetValue("wallpaper", "source_path", "").AsString() == "user://keep.png");

		CameraSettings missing = new CameraSettings();
		missing.Fov = 100f;
		missing.Load("user://client_settings_selftest_missing.cfg");
		Check("camera: a missing file restores the defaults", Near(missing.Fov, CameraSettings.DefaultFov));
	}

	private static void TestGraphicsDefaults()
	{
		GraphicsSettings graphics = new GraphicsSettings();
		Check("graphics defaults: render scale is 1.0 (native)",
			Near(graphics.RenderScale, 1.0f));
		Check("graphics defaults: shadows are on", graphics.Shadows);
		Check("graphics defaults: MSAA is off", graphics.Msaa == 0);
		Check("graphics defaults: the render method is the engine's own", graphics.RenderMethod == "");
		Check("graphics: render-method labels are distinct",
			GraphicsSettings.RenderMethodLabel("forward_plus") != GraphicsSettings.RenderMethodLabel(""));
	}

	private static void TestGraphicsRoundTrip()
	{
		GraphicsSettings written = new GraphicsSettings();
		written.RenderScale = 0.75f;
		written.Shadows = false;
		written.Msaa = 2;
		written.RenderMethod = "gl_compatibility";
		written.Save(ScratchPath);

		GraphicsSettings read = new GraphicsSettings();
		read.Load(ScratchPath);
		Check("graphics round-trip: render scale survives", Near(read.RenderScale, 0.75f));
		Check("graphics round-trip: the shadow toggle survives", !read.Shadows);
		Check("graphics round-trip: MSAA survives", read.Msaa == 2);
		Check("graphics round-trip: the render method survives", read.RenderMethod == "gl_compatibility");

		// An unknown render method (a hand-edited value) falls back to the engine default.
		GraphicsSettings bogus = new GraphicsSettings();
		bogus.RenderMethod = "ray_tracing_please";
		bogus.ClampToLimits();
		Check("graphics: an unknown render method falls back to the engine default", bogus.RenderMethod == "");

		// Out-of-range numbers are clamped on load.
		ConfigFile config = new ConfigFile();
		config.SetValue("graphics", "render_scale", 4.0f);
		config.SetValue("graphics", "msaa", 99);
		config.Save(ScratchPath);
		GraphicsSettings clamped = new GraphicsSettings();
		clamped.Load(ScratchPath);
		Check("graphics: render scale is clamped on load", Near(clamped.RenderScale, GraphicsSettings.MaxRenderScale));
		Check("graphics: MSAA is clamped on load", clamped.Msaa == GraphicsSettings.MaxMsaa);
	}
}
