using Godot;

/// <summary>
/// Named sky/lighting presets for the creation environment (milestone 2.5). The value indexes
/// <see cref="LotEnvironmentSettings.PresetNames"/>, so a preset and its label cannot drift apart.
/// </summary>
public enum SkyPreset
{
	Day = 0,
	Sunset = 1,
	Night = 2,
	Overcast = 3
}

/// <summary>
/// The subset of the creation environment that varies over the day: the sky/ground gradient colours
/// plus the sun's colour and energy.
///
/// This type exists because <see cref="ProceduralSkyMaterial"/> does <b>not</b> animate on its own.
/// Its four colours are static properties — only the sun disc is drawn from the scene's
/// <c>DirectionalLight3D</c> — so without a curve like this the clock moves the sun through an
/// unchanging sky. See <see cref="LotEnvironmentSettings.SkyPaletteFromTime"/>.
/// </summary>
public struct LotSkyPalette
{
	public Color SkyTop;
	public Color SkyHorizon;
	public Color GroundHorizon;
	public Color GroundBottom;
	public Color SunColor;
	public float SunEnergy;

	/// <summary>
	/// Component-wise blend between two palettes. At t = 0 this returns <paramref name="from"/>
	/// <b>exactly</b> (see <see cref="Blend(in Color, in Color, float)"/>), which is what lets a
	/// keyframe evaluated at its own hour round-trip bit-for-bit — the "noon is the Day sky" contract.
	/// </summary>
	public static LotSkyPalette Blend(in LotSkyPalette from, in LotSkyPalette to, float t)
	{
		return new LotSkyPalette
		{
			SkyTop = Blend(from.SkyTop, to.SkyTop, t),
			SkyHorizon = Blend(from.SkyHorizon, to.SkyHorizon, t),
			GroundHorizon = Blend(from.GroundHorizon, to.GroundHorizon, t),
			GroundBottom = Blend(from.GroundBottom, to.GroundBottom, t),
			SunColor = Blend(from.SunColor, to.SunColor, t),
			SunEnergy = from.SunEnergy + (to.SunEnergy - from.SunEnergy) * t
		};
	}

	/// <summary>Explicit arithmetic rather than a library lerp helper: at t = 0 it must return
	/// <paramref name="from"/> exactly, with no rounding introduced on the way through.</summary>
	private static Color Blend(in Color from, in Color to, float t)
	{
		return new Color(
			from.R + (to.R - from.R) * t,
			from.G + (to.G - from.G) * t,
			from.B + (to.B - from.B) * t,
			from.A + (to.A - from.A) * t);
	}
}

/// <summary>
/// The creation environment's lighting state (milestone 2.5): sun angle + colour, procedural sky
/// colours and fog. This replaces the fixed procedural sky that used to be hardcoded in
/// <see cref="BuilderScene.BuildViewport"/>.
///
/// Session-only by design. Nothing here is serialized into a lot yet — environment persistence is
/// blocked on the .lot format (§8.1) — and it is deliberately NOT on the editor undo stack: it is a
/// view setting like the camera, not an edit to the lot's objects.
///
/// <see cref="BuilderScene.ApplyEnvironment"/> is the single writer of these values into Godot's
/// Environment / ProceduralSkyMaterial / DirectionalLight3D, so what any one field does is readable
/// in exactly one place.
/// </summary>
public struct LotEnvironmentSettings
{
	/// <summary>Sun elevation (altitude above the horizon) in degrees: 90 is directly overhead, 0 is
	/// on the horizon and negative is below it — the astronomical sense of "elevation".
	/// <see cref="SunRotationDegrees"/> converts it to the light's rotation, because a
	/// DirectionalLight3D shines along its -Z axis (see that method for the sign).</summary>
	public float SunElevationDeg;

	/// <summary>Sun azimuth in degrees, applied directly as the light's Y rotation (unlike
	/// <see cref="SunElevationDeg"/>, which is negated — see <see cref="SunRotationDegrees"/>).</summary>
	public float SunAzimuthDeg;

	public Color SunColor;
	public float SunEnergy;

	public Color SkyTopColor;
	public Color SkyHorizonColor;
	public Color GroundHorizonColor;
	public Color GroundBottomColor;

	/// <summary>Fog is non-volumetric only: volumetric fog adds a second full-screen pass, which the
	/// project's bloat/perf rules do not justify for a creation-environment view setting.</summary>
	public bool FogEnabled;
	public Color FogColor;
	public float FogDensity;

	/// <summary>When set, the sun direction is derived from <see cref="TimeOfDayHours"/> instead of
	/// the manual <see cref="SunElevationDeg"/> / <see cref="SunAzimuthDeg"/> pair.</summary>
	public bool FollowTimeOfDay;

	/// <summary>Hour of day in [0, 24] used when <see cref="FollowTimeOfDay"/> is set.</summary>
	public float TimeOfDayHours;

	/// <summary>Display names, indexed by <see cref="SkyPreset"/>. Static so the menu's item loop
	/// allocates nothing per frame.</summary>
	public static readonly string[] PresetNames = { "Day", "Sunset", "Night", "Overcast" };

	/// <summary>The builder's original fixed sky, so a fresh lot looks exactly as it did before
	/// milestone 2.5 added these controls.</summary>
	public static LotEnvironmentSettings Default()
	{
		return ForPreset(SkyPreset.Day);
	}

	/// <summary>
	/// A complete settings snapshot for a preset. Every field is filled, so applying a preset never
	/// leaves a value behind from whatever the creator was editing before.
	/// </summary>
	public static LotEnvironmentSettings ForPreset(SkyPreset preset)
	{
		// The shared baseline is the Day look — the original hardcoded procedural sky. Its sun
		// elevation of 48 degrees is the original light rotation of X = -48, once SunRotationDegrees
		// negates it. The angle stays manual (rather than following TimeOfDayHours) so an untouched
		// builder keeps the exact lighting it shipped with until the creator asks for the clock.
		LotEnvironmentSettings settings = new LotEnvironmentSettings
		{
			FollowTimeOfDay = false,
			TimeOfDayHours = 12f,
			SunElevationDeg = 48f,
			SunAzimuthDeg = -28f,
			SunColor = new Color(1f, 1f, 1f),
			SunEnergy = 1.2f,
			SkyTopColor = new Color(0.36f, 0.56f, 0.85f),
			SkyHorizonColor = new Color(0.74f, 0.81f, 0.88f),
			GroundHorizonColor = new Color(0.74f, 0.72f, 0.68f),
			GroundBottomColor = new Color(0.40f, 0.37f, 0.33f),
			FogEnabled = false,
			FogColor = new Color(0.55f, 0.63f, 0.72f),
			FogDensity = 0.01f
		};

		switch (preset)
		{
			case SkyPreset.Sunset:
				settings.SunElevationDeg = 6f;
				settings.SunAzimuthDeg = -95f;
				settings.SunColor = new Color(1f, 0.62f, 0.34f);
				settings.SunEnergy = 1.05f;
				settings.SkyTopColor = new Color(0.17f, 0.22f, 0.42f);
				settings.SkyHorizonColor = new Color(0.96f, 0.55f, 0.30f);
				settings.GroundHorizonColor = new Color(0.55f, 0.33f, 0.26f);
				settings.GroundBottomColor = new Color(0.18f, 0.13f, 0.12f);
				break;
			case SkyPreset.Night:
				// A dim, blue "moon" above the horizon rather than a sun below it: a light below the
				// horizon plus a night-dark sky would leave the editor scene effectively black.
				settings.SunElevationDeg = 20f;
				settings.SunAzimuthDeg = 40f;
				settings.SunColor = new Color(0.45f, 0.52f, 0.78f);
				settings.SunEnergy = 0.25f;
				settings.SkyTopColor = new Color(0.015f, 0.025f, 0.07f);
				settings.SkyHorizonColor = new Color(0.05f, 0.07f, 0.15f);
				settings.GroundHorizonColor = new Color(0.03f, 0.035f, 0.06f);
				settings.GroundBottomColor = new Color(0.008f, 0.01f, 0.02f);
				break;
			case SkyPreset.Overcast:
				settings.SunElevationDeg = 62f;
				settings.SunAzimuthDeg = -18f;
				settings.SunColor = new Color(0.86f, 0.87f, 0.89f);
				settings.SunEnergy = 0.7f;
				settings.SkyTopColor = new Color(0.55f, 0.57f, 0.60f);
				settings.SkyHorizonColor = new Color(0.73f, 0.74f, 0.76f);
				settings.GroundHorizonColor = new Color(0.60f, 0.60f, 0.61f);
				settings.GroundBottomColor = new Color(0.44f, 0.44f, 0.45f);
				// Overcast is the one preset that carries fog: it is what sells the flat grey sky.
				settings.FogEnabled = true;
				settings.FogColor = new Color(0.72f, 0.74f, 0.77f);
				settings.FogDensity = 0.03f;
				break;
		}
		return settings;
	}

	/// <summary>
	/// Time-of-day keyframes for the sky (the milestone 2.5 follow-up fix). Godot's
	/// <see cref="ProceduralSkyMaterial"/> colours are static and never react to the sun, so the clock
	/// would otherwise move the sun disc through an unchanging sky. These stops give the sky its own
	/// curve, keyed to the same sunrise/sunset as <see cref="SunAnglesFromTime"/> (06:00 / 18:00);
	/// <see cref="DayPalette"/> is the noon stop and IS the Day preset's sky, so noon is identical to
	/// the fixed sky this milestone replaced.
	///
	/// Dusk is deliberately warmer and longer than dawn: dawn has one warm stop (06:30), dusk has two
	/// (18:00 and 19:30), so the warm afterglow lingers instead of the two ends being mirror images.
	///
	/// Declared before the two tables below on purpose — C# runs static field initialisers in textual
	/// order and those tables reference these palettes.
	/// </summary>
	private static readonly LotSkyPalette NightPalette = new LotSkyPalette
	{
		SkyTop = new Color(0.02f, 0.03f, 0.08f),
		SkyHorizon = new Color(0.06f, 0.08f, 0.16f),
		GroundHorizon = new Color(0.04f, 0.045f, 0.07f),
		GroundBottom = new Color(0.015f, 0.018f, 0.03f),
		SunColor = new Color(0.45f, 0.52f, 0.78f),
		SunEnergy = 0.25f
	};

	private static readonly LotSkyPalette PreDawnPalette = new LotSkyPalette
	{
		SkyTop = new Color(0.08f, 0.12f, 0.28f),
		SkyHorizon = new Color(0.35f, 0.34f, 0.45f),
		GroundHorizon = new Color(0.16f, 0.15f, 0.18f),
		GroundBottom = new Color(0.06f, 0.06f, 0.08f),
		SunColor = new Color(0.70f, 0.66f, 0.75f),
		SunEnergy = 0.35f
	};

	/// <summary>The single warm stop on the sunrise side; dusk gets two (see the summary above).</summary>
	private static readonly LotSkyPalette DawnPalette = new LotSkyPalette
	{
		SkyTop = new Color(0.20f, 0.32f, 0.60f),
		SkyHorizon = new Color(0.85f, 0.62f, 0.48f),
		GroundHorizon = new Color(0.45f, 0.38f, 0.36f),
		GroundBottom = new Color(0.22f, 0.20f, 0.20f),
		SunColor = new Color(1f, 0.80f, 0.62f),
		SunEnergy = 0.85f
	};

	private static readonly LotSkyPalette MorningPalette = new LotSkyPalette
	{
		SkyTop = new Color(0.33f, 0.52f, 0.82f),
		SkyHorizon = new Color(0.72f, 0.80f, 0.88f),
		GroundHorizon = new Color(0.70f, 0.69f, 0.66f),
		GroundBottom = new Color(0.38f, 0.36f, 0.33f),
		SunColor = new Color(1f, 0.97f, 0.92f),
		SunEnergy = 1.15f
	};

	/// <summary>The noon stop, derived from the Day preset so the two can never drift apart.</summary>
	private static readonly LotSkyPalette DayPalette = PaletteOf(ForPreset(SkyPreset.Day));

	private static readonly LotSkyPalette AfternoonPalette = new LotSkyPalette
	{
		SkyTop = new Color(0.34f, 0.53f, 0.84f),
		SkyHorizon = new Color(0.80f, 0.78f, 0.78f),
		GroundHorizon = new Color(0.72f, 0.68f, 0.62f),
		GroundBottom = new Color(0.40f, 0.36f, 0.31f),
		SunColor = new Color(1f, 0.92f, 0.80f),
		SunEnergy = 1.15f
	};

	private static readonly LotSkyPalette SunsetPalette = new LotSkyPalette
	{
		SkyTop = new Color(0.20f, 0.24f, 0.48f),
		SkyHorizon = new Color(0.98f, 0.50f, 0.24f),
		GroundHorizon = new Color(0.52f, 0.30f, 0.22f),
		GroundBottom = new Color(0.18f, 0.13f, 0.12f),
		SunColor = new Color(1f, 0.55f, 0.28f),
		SunEnergy = 1.0f
	};

	/// <summary>The afterglow past sunset: still warm, which is what makes dusk outlast dawn.</summary>
	private static readonly LotSkyPalette DuskPalette = new LotSkyPalette
	{
		SkyTop = new Color(0.10f, 0.12f, 0.28f),
		SkyHorizon = new Color(0.72f, 0.36f, 0.26f),
		GroundHorizon = new Color(0.30f, 0.20f, 0.18f),
		GroundBottom = new Color(0.10f, 0.09f, 0.10f),
		SunColor = new Color(0.85f, 0.52f, 0.40f),
		SunEnergy = 0.5f
	};

	/// <summary>
	/// The day's sky curve: hour → palette. The 21:00 — 04:30 span is a flat night plateau (both
	/// endpoints are <see cref="NightPalette"/>) so the small hours are stable and the lot stays
	/// readable; 12:00 is <see cref="DayPalette"/>, the exact pre-milestone sky.
	/// </summary>
	private static readonly SkyKeyframe[] SkyKeyframes =
	{
		new SkyKeyframe(0f, NightPalette),
		new SkyKeyframe(4.5f, NightPalette),
		new SkyKeyframe(5.5f, PreDawnPalette),
		new SkyKeyframe(6.5f, DawnPalette),
		new SkyKeyframe(8f, MorningPalette),
		new SkyKeyframe(12f, DayPalette),
		new SkyKeyframe(16.5f, AfternoonPalette),
		new SkyKeyframe(18f, SunsetPalette),
		new SkyKeyframe(19.5f, DuskPalette),
		new SkyKeyframe(21f, NightPalette)
	};

	/// <summary>
	/// <see cref="SkyKeyframes"/> with a copy of the first frame appended at hour 24, so looking up an
	/// hour never needs a wrap special case: the 21:00 — midnight segment interpolates like any other.
	/// </summary>
	private static readonly SkyKeyframe[] Timeline = BuildTimeline();

	private static SkyKeyframe[] BuildTimeline()
	{
		SkyKeyframe[] timeline = new SkyKeyframe[SkyKeyframes.Length + 1];
		System.Array.Copy(SkyKeyframes, timeline, SkyKeyframes.Length);
		timeline[SkyKeyframes.Length] = new SkyKeyframe(24f, SkyKeyframes[0].Palette);
		return timeline;
	}

	/// <summary>One stop on the day's sky curve: the hour it applies from, and the look it sets.</summary>
	private struct SkyKeyframe
	{
		public float Hour;
		public LotSkyPalette Palette;

		public SkyKeyframe(float hour, in LotSkyPalette palette)
		{
			Hour = hour;
			Palette = palette;
		}
	}

	/// <summary>
	/// The sun angles actually applied: the manual pair, or the time-of-day curve when
	/// <see cref="FollowTimeOfDay"/> is set. Kept separate from the stored manual angles so toggling
	/// the clock off returns to the angle the creator last set by hand.
	/// </summary>
	public static void ResolveSunAngles(in LotEnvironmentSettings settings, out float elevationDeg, out float azimuthDeg)
	{
		if (!settings.FollowTimeOfDay)
		{
			elevationDeg = settings.SunElevationDeg;
			azimuthDeg = settings.SunAzimuthDeg;
			return;
		}
		SunAnglesFromTime(settings.TimeOfDayHours, out elevationDeg, out azimuthDeg);
	}

	/// <summary>
	/// The DirectionalLight3D rotation that puts the sun at the resolved elevation and azimuth.
	/// The elevation is negated because the light shines along its -Z axis: rotating it by -90
	/// degrees about X points it straight down, which is the sun directly overhead. Kept as a pure
	/// function so the sign convention is verifiable without a live viewport.
	/// </summary>
	public static Vector3 SunRotationDegrees(in LotEnvironmentSettings settings)
	{
		ResolveSunAngles(settings, out float elevation, out float azimuth);
		return new Vector3(-elevation, azimuth, 0f);
	}

	/// <summary>
	/// A single sine over the day: the sun sits on the horizon at 06:00 and 18:00, peaks at noon and
	/// is below the horizon (negative elevation) overnight. The azimuth sweeps 15 degrees per hour,
	/// so the daylight half of the cycle covers a full 180 degrees east-to-west.
	///
	/// Both outputs are pure angles (elevation is altitude, not the light's rotation) — see
	/// <see cref="SunRotationDegrees"/> for the conversion.
	/// </summary>
	public static void SunAnglesFromTime(float hours, out float elevationDeg, out float azimuthDeg)
	{
		const float sunriseHour = 6f;
		const float noonPeakElevationDeg = 70f;

		elevationDeg = Mathf.Sin((hours - sunriseHour) / 12f * Mathf.Pi) * noonPeakElevationDeg;
		azimuthDeg = (hours - sunriseHour) / 24f * 360f;
	}

	/// <summary>
	/// The sky/light palette for a given hour, blended between the surrounding keyframes (the table
	/// wraps at midnight, so any hour is valid).
	///
	/// This is the piece Godot does not provide: <see cref="ProceduralSkyMaterial"/> draws its sun disc
	/// from the scene's <c>DirectionalLight3D</c> but keeps its own four colours fixed, so without this
	/// the sky would look the same at midnight as at noon.
	///
	/// Runs only when the clock's value changes, never per frame, so the linear scan over ten stops is
	/// the right shape here — no lookup structure is worth the extra machinery.
	/// </summary>
	public static LotSkyPalette SkyPaletteFromTime(float hours)
	{
		hours = Mathf.PosMod(hours, 24f);

		// Advance while the NEXT stop has already been reached. The timeline always ends at hour 24,
		// which is past any wrapped hour, so `index + 1` is always a valid frame.
		int index = 0;
		while (index + 1 < Timeline.Length && Timeline[index + 1].Hour <= hours) index++;

		SkyKeyframe from = Timeline[index];
		SkyKeyframe to = Timeline[index + 1];
		float t = (hours - from.Hour) / (to.Hour - from.Hour);
		return LotSkyPalette.Blend(from.Palette, to.Palette, t);
	}

	/// <summary>The stored colour fields as a palette, so the clock curve and the manual widgets are
	/// expressed in the same type (and tests can compare the two).</summary>
	public static LotSkyPalette PaletteOf(in LotEnvironmentSettings settings)
	{
		return new LotSkyPalette
		{
			SkyTop = settings.SkyTopColor,
			SkyHorizon = settings.SkyHorizonColor,
			GroundHorizon = settings.GroundHorizonColor,
			GroundBottom = settings.GroundBottomColor,
			SunColor = settings.SunColor,
			SunEnergy = settings.SunEnergy
		};
	}

	/// <summary>
	/// The settings as they are actually rendered: identical to <paramref name="settings"/> unless the
	/// clock is driving the sun, in which case the sky and sun colours come from
	/// <see cref="SkyPaletteFromTime"/> rather than the stored manual values.
	///
	/// The stored settings are never modified — this returns a copy — so unticking the clock restores
	/// exactly what the creator last set by hand. Sun <i>direction</i> is resolved separately by
	/// <see cref="SunRotationDegrees"/>; each function does one job.
	/// </summary>
	public static LotEnvironmentSettings Effective(in LotEnvironmentSettings settings)
	{
		if (!settings.FollowTimeOfDay) return settings;

		LotSkyPalette palette = SkyPaletteFromTime(settings.TimeOfDayHours);
		LotEnvironmentSettings effective = settings;
		effective.SkyTopColor = palette.SkyTop;
		effective.SkyHorizonColor = palette.SkyHorizon;
		effective.GroundHorizonColor = palette.GroundHorizon;
		effective.GroundBottomColor = palette.GroundBottom;
		effective.SunColor = palette.SunColor;
		effective.SunEnergy = palette.SunEnergy;
		return effective;
	}
}
