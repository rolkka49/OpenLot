using Godot;

/// <summary>
/// Shared helper for the self-tests that need a real image file on disk (the image pipeline in
/// <see cref="WallpaperSelfTest"/> and the part Texture property in
/// <see cref="LotPropertySelfTest"/>). Writing a real PNG is what exercises the actual import path
/// — format normalisation, GPU texture creation and reference counting — rather than a stand-in.
/// Test-only; nothing in the shipped code paths calls it.
/// </summary>
internal static class TestImages
{
	/// <summary>Writes a small solid-colour PNG to <paramref name="path"/>. Returns "" on failure.</summary>
	internal static string WritePng(string path, Color color)
	{
		Image image = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8);
		if (image == null) return "";
		image.Fill(color);
		Error result = image.SavePng(path);
		image.Dispose();
		return result == Error.Ok ? path : "";
	}

	/// <summary>
	/// Writes a small solid-colour JPEG. Used to reproduce a mislabelled file — real content in a
	/// file whose NAME claims a different format, which is what breaks extension-based loaders.
	/// </summary>
	internal static string WriteJpeg(string path, Color color)
	{
		Image image = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8);
		if (image == null) return "";
		image.Fill(color);
		Error result = image.SaveJpg(path, 0.9f);
		image.Dispose();
		return result == Error.Ok ? path : "";
	}

	/// <summary>
	/// Writes a small solid-colour lossless WebP. Many downloaded images are WebP regardless of the
	/// name they are saved under, so this is the other format the mislabelled-file test covers.
	/// </summary>
	internal static string WriteWebp(string path, Color color)
	{
		Image image = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8);
		if (image == null) return "";
		image.Fill(color);
		Error result = image.SaveWebp(path, false, 1.0f);
		image.Dispose();
		return result == Error.Ok ? path : "";
	}
}
