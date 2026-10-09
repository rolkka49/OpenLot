using Godot;

/// <summary>
/// The creation environment's lighting editor (milestone 2.5), drawn inline inside the builder's
/// View menu — no separate panel (§2.5 chose the View menu as the one home for view settings, next
/// to the wallpaper entries).
///
/// The state itself lives on <see cref="BuilderScene"/> (see <see cref="LotEnvironmentSettings"/>:
/// session-only, not undoable). This class only draws the widgets and forwards changes through
/// <see cref="BuilderScene.ApplyEnvironment"/>, which is the single writer of the values.
///
/// Presets are the primary control; the sliders below them are the manual override. Touching any
/// manual control drops the preset to "Custom", so the preset row can never claim to describe
/// values the settings no longer match.
/// </summary>
public class EnvironmentControls
{
	/// <summary>Index into <see cref="LotEnvironmentSettings.PresetNames"/>, or -1 for Custom. UI
	/// state only — it is not part of the settings, which is why it lives here.</summary>
	private int _presetIndex = (int)SkyPreset.Day;

	public void Draw(Builder builder)
	{
		BuilderScene scene = builder.Scene;
		if (scene == null) return;

		LotEnvironmentSettings settings = scene.Environment;

		bool changed = DrawPreset(ref settings);
		ImGui.Separator();
		bool manual = DrawSun(ref settings);
		ImGui.Separator();
		manual |= DrawSky(ref settings);
		ImGui.Separator();
		manual |= DrawFog(ref settings);

		if (manual)
		{
			_presetIndex = -1;
			changed = true;
		}
		if (changed) scene.ApplyEnvironment(settings);
	}

	/// <summary>Preset picker. Applied as a whole snapshot so no value survives from the previous
	/// look (see <see cref="LotEnvironmentSettings.ForPreset"/>).</summary>
	private bool DrawPreset(ref LotEnvironmentSettings settings)
	{
		bool changed = false;
		string current = _presetIndex >= 0 ? LotEnvironmentSettings.PresetNames[_presetIndex] : "Custom";
		if (ImGui.BeginMenu("Sky preset: " + current))
		{
			for (int i = 0; i < LotEnvironmentSettings.PresetNames.Length; i++)
			{
				if (ImGui.MenuItem(LotEnvironmentSettings.PresetNames[i], "", _presetIndex == i))
				{
					_presetIndex = i;
					settings = LotEnvironmentSettings.ForPreset((SkyPreset)i);
					changed = true;
				}
			}
			ImGui.EndMenu();
		}
		return changed;
	}

	private bool DrawSun(ref LotEnvironmentSettings settings)
	{
		bool changed = false;
		ImGui.TextDisabled("Sun");

		bool follow = ImGui.Checkbox("Use time of day", settings.FollowTimeOfDay);
		if (follow != settings.FollowTimeOfDay) { settings.FollowTimeOfDay = follow; changed = true; }

		// The clock and the manual controls are mutually exclusive: whichever is not driving the sun
		// is greyed out, so it is never ambiguous which one the viewport is showing. With the clock on
		// it owns the sun's colour and energy as well as its angle, because those are part of the
		// keyframed sky (see LotEnvironmentSettings.SkyPaletteFromTime).
		ImGui.BeginDisabled(!settings.FollowTimeOfDay);
		float hours = ImGui.SliderFloat("Time of day", settings.TimeOfDayHours, 0f, 24f, "%.2f h",
			ImGui.SliderAlwaysClamp);
		if (hours != settings.TimeOfDayHours) { settings.TimeOfDayHours = hours; changed = true; }
		ImGui.EndDisabled();

		ImGui.BeginDisabled(settings.FollowTimeOfDay);
		float elevation = ImGui.SliderFloat("Elevation", settings.SunElevationDeg, -90f, 90f, "%.1f deg",
			ImGui.SliderAlwaysClamp);
		if (elevation != settings.SunElevationDeg) { settings.SunElevationDeg = elevation; changed = true; }
		float azimuth = ImGui.SliderFloat("Azimuth", settings.SunAzimuthDeg, -180f, 180f, "%.1f deg",
			ImGui.SliderAlwaysClamp);
		if (azimuth != settings.SunAzimuthDeg) { settings.SunAzimuthDeg = azimuth; changed = true; }

		Color color = ImGui.ColorEdit3("Sun colour", settings.SunColor);
		if (color != settings.SunColor) { settings.SunColor = color; changed = true; }

		float energy = ImGui.SliderFloat("Sun energy", settings.SunEnergy, 0f, 3f, "%.2f",
			ImGui.SliderAlwaysClamp);
		if (energy != settings.SunEnergy) { settings.SunEnergy = energy; changed = true; }
		ImGui.EndDisabled();

		return changed;
	}

	/// <summary>The four procedural-sky gradient colours. The sky drives ambient light too
	/// (<c>AmbientLightSource = Sky</c>), so these change shading as well as the backdrop.
	///
	/// While the clock is on, the sky comes from the time-of-day curve instead, so these widgets are
	/// greyed out: Godot's ProceduralSkyMaterial colours never react to the sun on their own, which is
	/// exactly why the curve exists.</summary>
	private bool DrawSky(ref LotEnvironmentSettings settings)
	{
		bool changed = false;
		ImGui.TextDisabled("Sky");

		bool following = settings.FollowTimeOfDay;
		if (following)
		{
			ImGui.TextDisabled("Sky colours follow the time of day.");
			ImGui.BeginDisabled(true);
		}

		Color top = ImGui.ColorEdit3("Sky top", settings.SkyTopColor);
		if (top != settings.SkyTopColor) { settings.SkyTopColor = top; changed = true; }
		Color horizon = ImGui.ColorEdit3("Sky horizon", settings.SkyHorizonColor);
		if (horizon != settings.SkyHorizonColor) { settings.SkyHorizonColor = horizon; changed = true; }
		Color groundHorizon = ImGui.ColorEdit3("Ground horizon", settings.GroundHorizonColor);
		if (groundHorizon != settings.GroundHorizonColor) { settings.GroundHorizonColor = groundHorizon; changed = true; }
		Color groundBottom = ImGui.ColorEdit3("Ground bottom", settings.GroundBottomColor);
		if (groundBottom != settings.GroundBottomColor) { settings.GroundBottomColor = groundBottom; changed = true; }

		if (following) ImGui.EndDisabled();

		return changed;
	}

	private bool DrawFog(ref LotEnvironmentSettings settings)
	{
		bool changed = false;
		ImGui.TextDisabled("Fog");

		bool enabled = ImGui.Checkbox("Fog enabled", settings.FogEnabled);
		if (enabled != settings.FogEnabled) { settings.FogEnabled = enabled; changed = true; }

		ImGui.BeginDisabled(!settings.FogEnabled);
		Color color = ImGui.ColorEdit3("Fog colour", settings.FogColor);
		if (color != settings.FogColor) { settings.FogColor = color; changed = true; }
		float density = ImGui.SliderFloat("Fog density", settings.FogDensity, 0f, 0.1f, "%.4f",
			ImGui.SliderAlwaysClamp);
		if (density != settings.FogDensity) { settings.FogDensity = density; changed = true; }
		ImGui.EndDisabled();

		return changed;
	}
}
