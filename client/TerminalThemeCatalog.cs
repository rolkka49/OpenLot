using System.Collections.Generic;
using Godot;

/// <summary>
/// The <c>theme</c> command's vocabulary: the preset names it accepts and the color grammar it
/// parses. Pure rules only — the palette itself lives on the terminal (its colors are the ones
/// <c>LogLine</c> bakes into each line), so this type stays verifiable without a Terminal node,
/// which cannot be instantiated in a headless suite.
///
/// §5.4 lifts the palette into one shared theme service (terminal + builder panels + dialogs, one
/// global theme so the surfaces can never drift apart) and adds the custom palette, fonts and
/// window shapes; the command contract here — list, apply a preset, set one role color — is
/// deliberately the shape that service will keep.
/// </summary>
public static class TerminalThemeCatalog
{
	public const string DefaultPreset = "default";
	public const string GreenMatrixPreset = "green-matrix";
	public const string AmberRetroPreset = "amber-retro";
	public const string CyberpunkPreset = "cyberpunk";
	public const string SynthwavePreset = "synthwave";
	public const string KawaiiPreset = "kawaii";
	public const string NavyPreset = "navy";
	public const string ForestPreset = "forest";
	public const string MonoPreset = "mono";

	/// <summary>The saved user preset's canonical name (never in <see cref="PresetNames"/> — it has no
	/// fixed palette, so the settings window offers it separately with a name field).</summary>
	public const string CustomPreset = "custom";

	private static readonly string[] _presets =
	{
		DefaultPreset, GreenMatrixPreset, AmberRetroPreset, CyberpunkPreset,
		SynthwavePreset, KawaiiPreset, NavyPreset, ForestPreset, MonoPreset,
	};

	/// <summary>The preset names <c>theme</c> lists and the ghost completion can suggest.</summary>
	public static IReadOnlyList<string> PresetNames { get { return _presets; } }

	/// <summary>
	/// Resolves a typed preset word to its canonical name, case-insensitively and accepting the
	/// shorthands a creator is likely to reach for first (which the ghost vocabulary cannot offer,
	/// because it completes whole options rather than synonyms).
	/// </summary>
	public static bool TryParsePreset(string text, out string preset)
	{
		preset = "";
		if (string.IsNullOrWhiteSpace(text)) return false;

		string key = text.Trim().ToLowerInvariant().Replace(' ', '-').Replace('_', '-');
		switch (key)
		{
			case "default":
			case "reset":
			case "neutral":
				preset = DefaultPreset;
				return true;
			case "green":
			case "matrix":
			case "green-matrix":
				preset = GreenMatrixPreset;
				return true;
			case "amber":
			case "retro":
			case "amber-retro":
				preset = AmberRetroPreset;
				return true;
			case "cyber":
			case "cyberpunk":
			case "blade":
			case "blade-runner":
			case "bladerunner":
			case "neon":
				preset = CyberpunkPreset;
				return true;
			case "synth":
			case "synthwave":
			case "vapor":
			case "vaporwave":
			case "outrun":
				preset = SynthwavePreset;
				return true;
			case "kawaii":
			case "pink":
			case "sakura":
				preset = KawaiiPreset;
				return true;
			case "navy":
			case "blue":
				preset = NavyPreset;
				return true;
			case "forest":
			case "wood":
				preset = ForestPreset;
				return true;
			case "mono":
			case "monochrome":
			case "greyscale":
			case "grayscale":
				preset = MonoPreset;
				return true;
			case "custom":
			case "mine":
				preset = CustomPreset;
				return true;
			default:
				return false;
		}
	}

	// The named colors `theme foreground|background` accepts. A fixed set keeps the command's
	// behaviour (and its refusal message) predictable; hex covers everything else.
	private static readonly Dictionary<string, Color> _namedColors = new Dictionary<string, Color>
	{
		{ "white", new Color(1f, 1f, 1f) },
		{ "black", new Color(0f, 0f, 0f) },
		{ "grey", new Color(0.5f, 0.5f, 0.5f) },
		{ "gray", new Color(0.5f, 0.5f, 0.5f) },
		{ "red", new Color(1f, 0.25f, 0.25f) },
		{ "green", new Color(0.25f, 0.9f, 0.35f) },
		{ "amber", new Color(1f, 0.72f, 0.25f) },
		{ "blue", new Color(0.35f, 0.6f, 1f) },
		{ "cyan", new Color(0.3f, 0.9f, 0.9f) },
		{ "magenta", new Color(0.9f, 0.4f, 0.9f) },
		{ "yellow", new Color(1f, 0.9f, 0.3f) },
	};

	/// <summary>
	/// Parses a color argument: a hex value (<c>#rgb</c>, <c>#rgba</c>, <c>#rrggbb</c>,
	/// <c>#rrggbbaa</c>) or one of the named colors above. The shape is validated here so an
	/// unusable value is refused with our own message instead of reaching Godot's parser, which
	/// silently returns something for input it cannot read.
	/// </summary>
	public static bool TryParseColor(string text, out Color color)
	{
		color = new Color(1f, 1f, 1f);
		if (string.IsNullOrWhiteSpace(text)) return false;

		string value = text.Trim();
		if (value.StartsWith("#")) return TryParseHex(value, out color);
		return _namedColors.TryGetValue(value.ToLowerInvariant(), out color);
	}

	private static bool TryParseHex(string value, out Color color)
	{
		color = new Color(1f, 1f, 1f);
		string digits = value.Substring(1);

		// Godot's own HTML parser accepts exactly these lengths (#rgb/#rgba/#rrggbb/#rrggbbaa).
		if (digits.Length != 3 && digits.Length != 4 && digits.Length != 6 && digits.Length != 8) return false;
		for (int i = 0; i < digits.Length; i++)
		{
			if (!IsHexDigit(digits[i])) return false;
		}

		color = new Color(value);
		return true;
	}

	private static bool IsHexDigit(char c)
	{
		return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
	}
}
