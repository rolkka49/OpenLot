using System.Collections.Generic;

/// <summary>
/// Display data for the fonts this client ships (milestone 5.4's font clause). Pure data, no
/// ImGui and no file access, so it is verifiable headless — and so the settings list can group and
/// label fonts without parsing filenames.
///
/// A font's <b>id</b> is its file's stem, lower-cased (<c>Jupiteroid-Regular.ttf</c> =
/// <c>jupiteroid-regular</c>). Anything the registry finds that is not in this table is a
/// drop-in from <c>user://fonts/</c>: it falls back to a name derived from the file stem and the
/// <see cref="CustomCategory"/>, which is what keeps "add your own font" working with no code
/// change.
///
/// Every entry here is CC0-1.0 by GGBotNet — see <c>assets/fonts/LICENSE.md</c>, which
/// <c>FontServiceSelfTest</c> cross-checks so a font can never ship unrecorded.
/// </summary>
public static class FontCatalog
{
	public const string CustomCategory = "Custom";

	/// <summary>Category order the settings list groups by (also the filter drop-down order).</summary>
	public static readonly string[] CategoryOrder =
	{
		"Clean", "Mono", "Pixel", "Serif", "Display", CustomCategory
	};

	public struct FontInfo
	{
		public string DisplayName;
		public string Category;
	}

	// id -> (display name, category). Kept small and explicit on purpose: a nicer label than the
	// filename, and a category a person chose rather than one guessed from the name.
	private static readonly Dictionary<string, FontInfo> _bundled = new Dictionary<string, FontInfo>
	{
		{ "jupiteroid-regular", new FontInfo { DisplayName = "Jupiteroid", Category = "Clean" } },
		{ "jupiteroid-light", new FontInfo { DisplayName = "Jupiteroid Light", Category = "Clean" } },
		{ "typelightsans", new FontInfo { DisplayName = "Type Light Sans", Category = "Clean" } },
		{ "secoline", new FontInfo { DisplayName = "Secoline", Category = "Clean" } },
		{ "liber-struct", new FontInfo { DisplayName = "Liber Struct", Category = "Clean" } },

		{ "3x3-mono", new FontInfo { DisplayName = "3x3 Mono", Category = "Mono" } },
		{ "braillecc0", new FontInfo { DisplayName = "Braille CC0", Category = "Mono" } },
		{ "zerove-regular", new FontInfo { DisplayName = "Zerove", Category = "Mono" } },

		{ "publicpixel", new FontInfo { DisplayName = "Public Pixel", Category = "Pixel" } },
		{ "pixelzone", new FontInfo { DisplayName = "Pixelzone", Category = "Pixel" } },
		{ "pixelatedelegance", new FontInfo { DisplayName = "Pixelated Elegance", Category = "Pixel" } },
		{ "digittech14-regular", new FontInfo { DisplayName = "Digit Tech 14", Category = "Pixel" } },
		{ "y224", new FontInfo { DisplayName = "Y224", Category = "Pixel" } },
		{ "vyazpixel", new FontInfo { DisplayName = "Vyaz Pixel", Category = "Pixel" } },

		{ "oserif-regular", new FontInfo { DisplayName = "OSerif", Category = "Serif" } },
		{ "novem", new FontInfo { DisplayName = "Novem", Category = "Serif" } },
		{ "vaticanus", new FontInfo { DisplayName = "Vaticanus", Category = "Serif" } },
		{ "bitrimus", new FontInfo { DisplayName = "Bitrimus", Category = "Serif" } },

		{ "matrixtype-regular", new FontInfo { DisplayName = "MatrixType", Category = "Display" } },
		{ "rasterforge", new FontInfo { DisplayName = "Raster Forge", Category = "Display" } },
		{ "hexcore", new FontInfo { DisplayName = "Hexcore", Category = "Display" } },
		{ "deltablock-regular", new FontInfo { DisplayName = "Delta Block", Category = "Display" } },
		{ "darkbyte", new FontInfo { DisplayName = "DarkByte", Category = "Display" } },
	};

	/// <summary>Display name for an id, falling back to a tidied file stem for drop-ins.</summary>
	public static string DisplayName(string id)
	{
		if (string.IsNullOrEmpty(id)) return "";
		return _bundled.TryGetValue(id.ToLowerInvariant(), out FontInfo info) ? info.DisplayName : Tidy(id);
	}

	/// <summary>Category for an id; anything unrecognized is a user drop-in.</summary>
	public static string Category(string id)
	{
		if (string.IsNullOrEmpty(id)) return CustomCategory;
		return _bundled.TryGetValue(id.ToLowerInvariant(), out FontInfo info) ? info.Category : CustomCategory;
	}

	/// <summary>True when the id is one of the fonts this repo ships.</summary>
	public static bool IsBundled(string id)
	{
		return !string.IsNullOrEmpty(id) && _bundled.ContainsKey(id.ToLowerInvariant());
	}

	/// <summary>How many fonts the repo ships (the settings list shows this as "bundled").</summary>
	public static int BundledCount { get { return _bundled.Count; } }

	/// <summary>Sort key for a category; unknown categories sort last rather than throwing.</summary>
	public static int CategoryRank(string category)
	{
		int index = System.Array.IndexOf(CategoryOrder, category);
		return index < 0 ? CategoryOrder.Length : index;
	}

	// "some_font-name" -> "Some Font Name": what a drop-in file is called when we have no better
	// label for it. Deliberately crude — the creator knows what they dropped in.
	private static string Tidy(string id)
	{
		string[] parts = id.Replace('_', '-').Split('-');
		for (int i = 0; i < parts.Length; i++)
		{
			if (parts[i].Length == 0) continue;
			parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1);
		}
		return string.Join(" ", parts);
	}
}
