using Godot;

/// <summary>
/// Verification for milestone 2.5's environment model: the preset table reproduces the builder's
/// original fixed sky for Day, the presets are coherent, and the time-of-day curve puts the sun
/// where the clock says it should be.
///
/// Only pure data and pure math are covered. The on-screen result — sky colours, shadow direction,
/// fog — is a rendering outcome and is checked by eye in the running client, the same split the
/// roadmap uses for the 2D wallpaper.
///
/// Follows the project's self-test convention (no test framework: pass/fail lines, run headless
/// under #if DEBUG from <see cref="BuilderScene._Ready"/>).
/// </summary>
public static class EnvironmentSelfTest
{
	private static int _failures;
	private static int _checks;

	public static int Run()
	{
		_failures = 0;
		_checks = 0;

		TestDefaults();
		TestPresets();
		TestTimeOfDayCurve();
		TestSkyPaletteOverTime();
		TestEffective();

		GD.Print("[EnvironmentSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
		return _failures;
	}

	/// <summary>The "no visual change" contract: the default settings must reproduce the values the
	/// viewport hardcoded before this milestone, or an untouched builder would look different.</summary>
	private static void TestDefaults()
	{
		LotEnvironmentSettings def = LotEnvironmentSettings.Default();
		Check("default: sun elevation matches the original fixed sun",
			def.SunElevationDeg == 48f && def.SunAzimuthDeg == -28f);
		Check("default: the applied light rotation is the original one (X = -48, Y = -28)",
			LotEnvironmentSettings.SunRotationDegrees(def) == new Vector3(-48f, -28f, 0f));
		Check("default: sun energy matches the original", def.SunEnergy == 1.2f);
		Check("default: sun colour is white", def.SunColor == new Color(1f, 1f, 1f));
		Check("default: sky top matches the original procedural sky",
			def.SkyTopColor == new Color(0.36f, 0.56f, 0.85f));
		Check("default: sky horizon matches the original",
			def.SkyHorizonColor == new Color(0.74f, 0.81f, 0.88f));
		Check("default: ground horizon matches the original",
			def.GroundHorizonColor == new Color(0.74f, 0.72f, 0.68f));
		Check("default: ground bottom matches the original",
			def.GroundBottomColor == new Color(0.40f, 0.37f, 0.33f));
		Check("default: fog starts off (the original had none)", !def.FogEnabled);
		Check("default: the clock does not drive the sun until asked", !def.FollowTimeOfDay);
	}

	private static void TestPresets()
	{
		Check("presets: Day equals the default",
			SameSettings(LotEnvironmentSettings.ForPreset(SkyPreset.Day), LotEnvironmentSettings.Default()));
		Check("presets: one name per preset",
			LotEnvironmentSettings.PresetNames.Length == (int)SkyPreset.Overcast + 1);

		LotEnvironmentSettings day = LotEnvironmentSettings.ForPreset(SkyPreset.Day);
		LotEnvironmentSettings sunset = LotEnvironmentSettings.ForPreset(SkyPreset.Sunset);
		LotEnvironmentSettings night = LotEnvironmentSettings.ForPreset(SkyPreset.Night);
		LotEnvironmentSettings overcast = LotEnvironmentSettings.ForPreset(SkyPreset.Overcast);

		Check("presets: night is dimmer than day", night.SunEnergy < day.SunEnergy);
		Check("presets: night has no fog of its own", !night.FogEnabled);
		Check("presets: sunset is warmer than day (less blue)", sunset.SunColor.B < day.SunColor.B);
		Check("presets: sunset sits lower than day", sunset.SunElevationDeg < day.SunElevationDeg);
		Check("presets: overcast turns fog on", overcast.FogEnabled);
		Check("presets: overcast is dimmer than day", overcast.SunEnergy < day.SunEnergy);
		Check("presets: every preset keeps the sun above the horizon",
			sunset.SunElevationDeg > 0f && night.SunElevationDeg > 0f && overcast.SunElevationDeg > 0f);
		Check("presets: every preset is distinct from Day",
			!SameSettings(sunset, day) && !SameSettings(night, day) && !SameSettings(overcast, day));
	}

	/// <summary>The clock: sunrise/sunset on the horizon, noon at the peak, midnight below it, and a
	/// westward sweep through the day.</summary>
	private static void TestTimeOfDayCurve()
	{
		LotEnvironmentSettings.SunAnglesFromTime(12f, out float noonElev, out _);
		Check("clock: noon is the highest sun", noonElev >= 69.9f);

		LotEnvironmentSettings.SunAnglesFromTime(0f, out float midnightElev, out _);
		Check("clock: midnight is below the horizon", midnightElev < 0f);

		LotEnvironmentSettings.SunAnglesFromTime(6f, out float dawnElev, out _);
		Check("clock: 06:00 sits on the horizon", Mathf.Abs(dawnElev) < 0.01f);

		LotEnvironmentSettings.SunAnglesFromTime(18f, out float duskElev, out _);
		Check("clock: 18:00 sits on the horizon", Mathf.Abs(duskElev) < 0.01f);

		LotEnvironmentSettings.SunAnglesFromTime(6f, out _, out float az6);
		LotEnvironmentSettings.SunAnglesFromTime(12f, out _, out float az12);
		LotEnvironmentSettings.SunAnglesFromTime(18f, out _, out float az18);
		Check("clock: the sun sweeps westward through the day", az6 < az12 && az12 < az18);

		// Resolution: the clock overrides the manual pair only while it is switched on.
		LotEnvironmentSettings follow = LotEnvironmentSettings.Default();
		follow.FollowTimeOfDay = true;
		follow.TimeOfDayHours = 12f;
		LotEnvironmentSettings.ResolveSunAngles(follow, out float followElev, out _);
		Check("clock: switching it on overrides the manual elevation",
			Mathf.Abs(followElev - noonElev) < 0.001f);

		LotEnvironmentSettings.ResolveSunAngles(LotEnvironmentSettings.Default(), out float manualElev,
			out float manualAz);
		Check("clock: switching it off returns the manual angles",
			manualElev == 48f && manualAz == -28f);

		// The sign convention: elevation is altitude, so the light's X rotation is its negation. Noon
		// must therefore point the light further down (a more negative X) than dawn does.
		follow.FollowTimeOfDay = false;
		follow.SunElevationDeg = 70f;
		follow.SunAzimuthDeg = 0f;
		float straightDown = LotEnvironmentSettings.SunRotationDegrees(follow).X;
		follow.SunElevationDeg = 0f;
		float atHorizon = LotEnvironmentSettings.SunRotationDegrees(follow).X;
		Check("clock: a higher elevation points the light further down",
			straightDown < atHorizon);
	}

	/// <summary>
	/// The follow-up fix: Godot's ProceduralSkyMaterial colours are static, so the clock needs its own
	/// sky curve. These checks pin the contract that curve has to keep — noon identical to the Day
	/// preset, night dark but still editable, and dusk warmer for longer than dawn.
	/// </summary>
	private static void TestSkyPaletteOverTime()
	{
		LotSkyPalette noon = LotEnvironmentSettings.SkyPaletteFromTime(12f);
		LotSkyPalette midnight = LotEnvironmentSettings.SkyPaletteFromTime(0f);

		Check("clock sky: noon is exactly the Day preset's sky",
			SamePalette(noon, LotEnvironmentSettings.PaletteOf(LotEnvironmentSettings.Default())));
		Check("clock sky: midnight is darker than noon", Dimmer(midnight, noon));
		Check("clock sky: midnight is dark but not black (the lot stays editable)",
			midnight.SkyTop.R + midnight.SkyTop.G + midnight.SkyTop.B >= 0.05f
			&& midnight.SkyHorizon.R + midnight.SkyHorizon.G + midnight.SkyHorizon.B >= 0.15f);

		LotSkyPalette dawn = LotEnvironmentSettings.SkyPaletteFromTime(6.5f);
		LotSkyPalette sunset = LotEnvironmentSettings.SkyPaletteFromTime(18f);
		LotSkyPalette dusk = LotEnvironmentSettings.SkyPaletteFromTime(19.5f);
		Check("clock sky: sunset is warmer than dawn", Warmth(sunset) > Warmth(dawn));
		Check("clock sky: dusk is still warm after sunset, and past dawn's warmth",
			Warmth(dusk) > Warmth(dawn) && Warmth(dusk) < Warmth(sunset));

		// Interpolation: 10:00 is halfway between the 08:00 and 12:00 stops, so its top colour is
		// exactly the average of the two.
		LotSkyPalette morning = LotEnvironmentSettings.SkyPaletteFromTime(8f);
		LotSkyPalette ten = LotEnvironmentSettings.SkyPaletteFromTime(10f);
		Check("clock sky: a midpoint blends the surrounding stops",
			Mathf.Abs(ten.SkyTop.R - (morning.SkyTop.R + noon.SkyTop.R) * 0.5f) < 1e-5f
			&& Mathf.Abs(ten.SkyTop.B - (morning.SkyTop.B + noon.SkyTop.B) * 0.5f) < 1e-5f);

		// The table wraps: 21:00 through 04:30 is the flat night plateau, so the small hours are
		// stable and the segment past midnight needs no special case.
		Check("clock sky: the night plateau carries across midnight",
			SamePalette(LotEnvironmentSettings.SkyPaletteFromTime(23f), midnight)
			&& SamePalette(LotEnvironmentSettings.SkyPaletteFromTime(1f), midnight)
			&& SamePalette(LotEnvironmentSettings.SkyPaletteFromTime(4.5f), midnight));
	}

	/// <summary>The rendered snapshot: manual mode passes straight through, the clock substitutes the
	/// sky colours, and the stored settings are never modified.</summary>
	private static void TestEffective()
	{
		LotEnvironmentSettings manual = LotEnvironmentSettings.Default();
		Check("effective: manual mode passes straight through",
			SameSettings(LotEnvironmentSettings.Effective(manual), manual));

		LotEnvironmentSettings clock = LotEnvironmentSettings.Default();
		clock.FollowTimeOfDay = true;
		clock.TimeOfDayHours = 18f;
		LotEnvironmentSettings effective = LotEnvironmentSettings.Effective(clock);

		Check("effective: the clock replaces the stored sky and sun colours",
			!SameSettings(effective, clock)
			&& SamePalette(LotEnvironmentSettings.PaletteOf(effective),
				LotEnvironmentSettings.SkyPaletteFromTime(18f)));
		Check("effective: the clock leaves fog alone",
			effective.FogEnabled == clock.FogEnabled
			&& effective.FogColor == clock.FogColor
			&& effective.FogDensity == clock.FogDensity);
		Check("effective: the stored settings keep the manual colours",
			clock.SkyTopColor == new Color(0.36f, 0.56f, 0.85f)
			&& clock.SunColor == new Color(1f, 1f, 1f));
	}

	/// <summary>How warm a palette reads: the horizon's red-blue separation. The horizon is what
	/// carries the dawn/dusk colour, so this is what "warmer" means here.</summary>
	private static float Warmth(in LotSkyPalette palette)
	{
		return palette.SkyHorizon.R - palette.SkyHorizon.B;
	}

	/// <summary>True when every sky colour in one palette is darker than the matching colour in the
	/// other (the ground is left out: it is a different surface, not part of the sky gradient).</summary>
	private static bool Dimmer(in LotSkyPalette dim, in LotSkyPalette bright)
	{
		return dim.SkyTop.R < bright.SkyTop.R && dim.SkyTop.G < bright.SkyTop.G && dim.SkyTop.B < bright.SkyTop.B
			&& dim.SkyHorizon.R < bright.SkyHorizon.R && dim.SkyHorizon.G < bright.SkyHorizon.G
			&& dim.SkyHorizon.B < bright.SkyHorizon.B;
	}

	private static bool SamePalette(in LotSkyPalette a, in LotSkyPalette b)
	{
		return a.SkyTop == b.SkyTop
			&& a.SkyHorizon == b.SkyHorizon
			&& a.GroundHorizon == b.GroundHorizon
			&& a.GroundBottom == b.GroundBottom
			&& a.SunColor == b.SunColor
			&& a.SunEnergy == b.SunEnergy;
	}

	/// <summary>Value equality over every field, so a preset comparison cannot pass on a field the
	/// test forgot to look at.</summary>
	private static bool SameSettings(in LotEnvironmentSettings a, in LotEnvironmentSettings b)
	{
		return a.FollowTimeOfDay == b.FollowTimeOfDay
			&& a.TimeOfDayHours == b.TimeOfDayHours
			&& a.SunElevationDeg == b.SunElevationDeg
			&& a.SunAzimuthDeg == b.SunAzimuthDeg
			&& a.SunColor == b.SunColor
			&& a.SunEnergy == b.SunEnergy
			&& a.SkyTopColor == b.SkyTopColor
			&& a.SkyHorizonColor == b.SkyHorizonColor
			&& a.GroundHorizonColor == b.GroundHorizonColor
			&& a.GroundBottomColor == b.GroundBottomColor
			&& a.FogEnabled == b.FogEnabled
			&& a.FogColor == b.FogColor
			&& a.FogDensity == b.FogDensity;
	}

	private static void Check(string label, bool passed)
	{
		_checks++;
		if (passed) return;
		_failures++;
		GD.PrintErr("[EnvironmentSelfTest] FAIL: " + label);
	}
}
