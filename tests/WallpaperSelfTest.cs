using Godot;

/// <summary>
/// Verification for the milestone 2.3 image pipeline (the shared <see cref="LotTextureCache"/> and
/// the <see cref="WallpaperService"/> that consumes it). Follows the project's self-test
/// convention: no test framework exists (Openlot.csproj is Godot.NET.Sdk + NLua), so this runs
/// from BuilderScene._Ready() under #if DEBUG and prints pass/fail lines.
///
/// Scope note: only the deterministic parts are covered — the cover-fit arithmetic, the asset
/// reference counting/disposal, and the wallpaper's set/swap/clear bookkeeping. What an image
/// actually looks like on screen is a rendering result and is checked by eye in the running client.
/// </summary>
public static class WallpaperSelfTest
{
	private const float Tolerance = 1e-3f;
	private static int _failures;
	private static int _checks;

	public static int Run()
	{
		_failures = 0;
		_checks = 0;

		TestCoverFit();
		TestAssetLifetime();
		TestBadPathRegistersNothing();
		TestMislabelledExtension();
		TestRealFile();
		TestWallpaperSwapAndClear();
		TestPersistence();

		GD.Print("[WallpaperSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
		return _failures;
	}

	private static void TestCoverFit()
	{
		// Landscape image in a square viewport: height is the binding axis, so the image is scaled
		// to 1000/1080 and the width overflows equally on both sides.
		WallpaperService.ComputeCoverFit(new Vector2(1920f, 1080f), new Vector2(1000f, 1000f),
			out Vector2 drawn, out Vector2 offset);
		CheckNear("cover: landscape height fills the viewport", drawn.Y, 1000f);
		CheckNear("cover: landscape width overflows", drawn.X, 1920f * (1000f / 1080f));
		CheckNear("cover: landscape is centred horizontally", offset.X, (1000f - drawn.X) * 0.5f);
		CheckNear("cover: landscape has no vertical offset", offset.Y, 0f);

		// Portrait image in a square viewport: width is the binding axis now.
		WallpaperService.ComputeCoverFit(new Vector2(1000f, 2000f), new Vector2(1000f, 1000f),
			out drawn, out offset);
		CheckNear("cover: portrait width fills the viewport", drawn.X, 1000f);
		CheckNear("cover: portrait height overflows", drawn.Y, 2000f);
		CheckNear("cover: portrait is centred vertically", offset.Y, -500f);

		// Exact match: no scaling at all.
		WallpaperService.ComputeCoverFit(new Vector2(800f, 600f), new Vector2(800f, 600f),
			out drawn, out offset);
		Check("cover: exact match keeps the image size", drawn == new Vector2(800f, 600f));
		Check("cover: exact match has no offset", offset == Vector2.Zero);

		// Degenerate inputs must not divide by zero.
		WallpaperService.ComputeCoverFit(Vector2.Zero, new Vector2(1000f, 1000f),
			out drawn, out offset);
		Check("cover: empty image yields nothing", drawn == Vector2.Zero && offset == Vector2.Zero);
		WallpaperService.ComputeCoverFit(new Vector2(100f, 100f), Vector2.Zero,
			out drawn, out offset);
		Check("cover: empty viewport yields nothing", drawn == Vector2.Zero && offset == Vector2.Zero);
	}

	private static void TestAssetLifetime()
	{
		string path = WriteTestImage("user://wallpaper_selftest_a.png");
		if (path.Length == 0)
		{
			Check("asset: test image could be written", false);
			return;
		}

		string assetId = LotTextureCache.ImportFromFile(path, out string importError);
		Check("asset: import returns an id (error: '" + importError + "')", assetId.Length > 0);
		Check("asset: the id resolves to a texture", LotTextureCache.Get(assetId) != null);
		Check("asset: import holds exactly one reference", LotTextureCache.Acquire(assetId) == 2);
		Check("asset: one release leaves it held", !LotTextureCache.Release(assetId));

		// A second import of the same file must be the same asset, not a duplicate entry.
		string again = LotTextureCache.ImportFromFile(path, out string _);
		Check("asset: re-importing the same file returns the same id", again == assetId);
		Check("asset: re-import adds one reference", LotTextureCache.Acquire(assetId) == 3);
		LotTextureCache.Release(assetId);
		LotTextureCache.Release(assetId);

		Check("asset: the last release frees the entry", LotTextureCache.Release(assetId));
		Check("asset: a freed id no longer resolves", !LotTextureCache.Contains(assetId));
		Check("asset: a freed id returns no texture", LotTextureCache.Get(assetId) == null);
	}

	private static void TestBadPathRegistersNothing()
	{
		int before = LotTextureCache.Count;
		string assetId = LotTextureCache.ImportFromFile("user://definitely_not_an_image_xyz.png", out string error);
		Check("asset: an unreadable path returns no id", assetId.Length == 0);
		Check("asset: an unreadable path explains itself", error.Length > 0);
		Check("asset: an unreadable path registers nothing", LotTextureCache.Count == before);

		// A file that exists but is not an image must be rejected by the content sniff without
		// invoking an engine loader (which is what used to flood the console with PNG errors).
		string notAnImage = "user://wallpaper_selftest_notanimage.png";
		Godot.FileAccess file = Godot.FileAccess.Open(notAnImage, Godot.FileAccess.ModeFlags.Write);
		if (file != null)
		{
			file.StoreString("this is definitely not a PNG, whatever the extension claims");
			file.Dispose();
		}
		string rejected = LotTextureCache.ImportFromFile(notAnImage, out string rejectError);
		Check("asset: a mislabelled non-image is rejected", rejected.Length == 0);
		Check("asset: the rejection names the problem", rejectError.Contains("not a recognised image"));
		Check("asset: a rejected file registers nothing", LotTextureCache.Count == before);
	}

	/// <summary>
	/// The real-world failure this loader was changed for: a JPEG (or WebP) saved with a ".png"
	/// name — common with downloaded images. Godot's extension-guessed loader called the PNG driver,
	/// which refused the file and printed engine errors; sniffing the content loads it instead.
	/// </summary>
	private static void TestMislabelledExtension()
	{
		CheckMislabelled("jpeg", TestImages.WriteJpeg("user://wallpaper_selftest_jpeg_as_png.png", new Color(0.1f, 0.7f, 0.3f, 1f)));
		CheckMislabelled("webp", TestImages.WriteWebp("user://wallpaper_selftest_webp_as_png.png", new Color(0.7f, 0.2f, 0.4f, 1f)));
	}

	/// <summary>Imports a mislabelled file and checks it loads, resolves and releases.</summary>
	private static void CheckMislabelled(string label, string path)
	{
		if (path.Length == 0)
		{
			Check("mislabelled (" + label + "): test image could be written", false);
			return;
		}

		string assetId = LotTextureCache.ImportFromFile(path, out string error);
		Check("mislabelled (" + label + "): real content under a .png name still loads (error: '" + error + "')",
			assetId.Length > 0);
		Check("mislabelled (" + label + "): the loaded asset resolves to a texture", LotTextureCache.Get(assetId) != null);
		Check("mislabelled (" + label + "): the asset is fully released", LotTextureCache.Release(assetId));
	}

	/// <summary>
	/// Opt-in check against a real file the developer names, so the loader can be verified against
	/// the images that actually live on this machine — including ones in directories with
	/// non-ASCII characters in their path, which is exactly where the reported failure came from.
	/// Set <c>OPENLOT_TEST_IMAGE</c> to an image path to run it (same opt-in convention as
	/// OPENLOT_HEAVY_TESTS); it is skipped otherwise and never fails the suite when unset.
	/// </summary>
	private static void TestRealFile()
	{
		string path = System.Environment.GetEnvironmentVariable("OPENLOT_TEST_IMAGE");
		if (string.IsNullOrEmpty(path)) return;

		string assetId = LotTextureCache.ImportFromFile(path, out string error);
		Check("real file: '" + path + "' loads (error: '" + error + "')", assetId.Length > 0);
		Check("real file: the texture is non-null", LotTextureCache.Get(assetId) != null);
		Check("real file: the texture is released again", LotTextureCache.Release(assetId));
	}

	private static void TestWallpaperSwapAndClear()
	{
		// This suite drives the ONE live WallpaperService, and it used to start with an
		// unconditional Clear() — which wiped the user's wallpaper every time the builder scene
		// loaded, because the suite runs from BuilderScene._Ready. Snapshot the live choice, pin its
		// asset so the test's Clear() cannot free it, and put it back afterwards. That is what makes
		// the suite non-destructive, and the check at the end keeps it that way.
		string saved = WallpaperService.AssetId;
		if (saved.Length > 0) LotTextureCache.Acquire(saved);

		WallpaperService.Clear();
		Check("wallpaper: starts with no wallpaper", !WallpaperService.HasWallpaper);

		string first = WriteTestImage("user://wallpaper_selftest_b.png");
		string second = WriteTestImage("user://wallpaper_selftest_c.png");
		if (first.Length == 0 || second.Length == 0)
		{
			Check("wallpaper: test images could be written", false);
			RestoreSavedWallpaper(saved);
			return;
		}

		Check("wallpaper: setting from a file succeeds", WallpaperService.SetFromFile(first, out string errorA));
		Check("wallpaper: a successful set reports no error", errorA.Length == 0);
		string firstId = WallpaperService.AssetId;
		Check("wallpaper: the asset id is live", LotTextureCache.Contains(firstId));

		// A bad path must leave the working wallpaper alone.
		Check("wallpaper: a bad path fails", !WallpaperService.SetFromFile("user://nope_xyz.png", out string errorB));
		Check("wallpaper: a failed set explains itself", errorB.Length > 0);
		Check("wallpaper: a failed set keeps the previous image", WallpaperService.AssetId == firstId);

		// Switching releases the outgoing asset.
		Check("wallpaper: switching succeeds", WallpaperService.SetFromFile(second, out string errorC));
		Check("wallpaper: switching reports no error", errorC.Length == 0);
		string secondId = WallpaperService.AssetId;
		Check("wallpaper: the new asset is live", LotTextureCache.Contains(secondId));
		Check("wallpaper: the previous asset was released", !LotTextureCache.Contains(firstId));

		WallpaperService.Clear();
		Check("wallpaper: clear drops the wallpaper", !WallpaperService.HasWallpaper);
		Check("wallpaper: clear releases the texture", !LotTextureCache.Contains(secondId));

		// A second clear must be a harmless no-op.
		WallpaperService.Clear();
		Check("wallpaper: clearing twice is harmless", !WallpaperService.HasWallpaper);

		// Picking the same image twice must not bank a second reference (it would strand the image
		// in the cache after a clear).
		WallpaperService.SetFromFile(first, out string _);
		WallpaperService.SetFromFile(first, out string _);
		Check("wallpaper: re-picking the same image keeps one asset", WallpaperService.AssetId == firstId);
		WallpaperService.Clear();
		Check("wallpaper: re-picking the same image does not leak it", !LotTextureCache.Contains(firstId));

		RestoreSavedWallpaper(saved);
		Check("wallpaper: the suite left the pre-existing wallpaper applied", WallpaperService.AssetId == saved);
	}

	/// <summary>Puts back the wallpaper that was set before the suite ran, and drops the pin.</summary>
	private static void RestoreSavedWallpaper(string saved)
	{
		if (saved.Length == 0) return;
		WallpaperService.Set(saved, out string _);
		LotTextureCache.Release(saved);
	}

	/// <summary>
	/// The wallpaper is remembered on disk so it survives both a scene change and a restart. The
	/// stored contract is checked directly here (by reading the file) rather than through
	/// <see cref="WallpaperService.LoadPersistedSettings"/>, because that call is a one-shot init
	/// step and cannot be re-run inside one process — the restore half is proven by running the app
	/// twice, which is how it was verified by hand.
	///
	/// The section and key are spelled out rather than shared with the service on purpose: if those
	/// names change, any already-saved wallpaper would stop being found, so the test should fail.
	/// </summary>
	private static void TestPersistence()
	{
		// Persistence only writes once the settings have been read, so make sure that has happened
		// (idempotent) — for a direct builder-scene run this suite is the first thing to touch it.
		WallpaperService.LoadPersistedSettings();

		string saved = WallpaperService.AssetId;
		if (saved.Length > 0) LotTextureCache.Acquire(saved);

		string path = WriteTestImage("user://wallpaper_selftest_persist.png");
		if (path.Length == 0)
		{
			Check("persistence: test image could be written", false);
			RestoreSavedWallpaper(saved);
			return;
		}

		Check("persistence: setting a wallpaper succeeds", WallpaperService.SetFromFile(path, out string _));
		Check("persistence: the choice is written to disk", ReadStoredWallpaper() == path);
		Check("persistence: the stored path is the image's own file", Godot.FileAccess.FileExists(ReadStoredWallpaper()));

		WallpaperService.Clear();
		Check("persistence: clearing removes the stored choice", ReadStoredWallpaper().Length == 0);

		WallpaperService.Clear();
		Check("persistence: clearing twice keeps it removed", ReadStoredWallpaper().Length == 0);

		RestoreSavedWallpaper(saved);
		string expected = saved.Length > 0 ? LotTextureCache.GetSourcePath(saved) : "";
		Check("persistence: the pre-existing wallpaper is stored again", ReadStoredWallpaper() == expected);
	}

	/// <summary>Reads the remembered path straight out of the settings file.</summary>
	private static string ReadStoredWallpaper()
	{
		const string settingsPath = "user://openlot_settings.cfg";
		if (!Godot.FileAccess.FileExists(settingsPath)) return "";

		ConfigFile config = new ConfigFile();
		if (config.Load(settingsPath) != Error.Ok) return "";
		return config.GetValue("wallpaper", "source_path", "").AsString();
	}

	/// <summary>Writes a tiny PNG so the import path is exercised for real. Returns "" on failure.</summary>
	private static string WriteTestImage(string path)
	{
		return TestImages.WritePng(path, new Color(0.2f, 0.4f, 0.6f, 1f));
	}

	private static void Check(string name, bool condition)
	{
		_checks++;
		if (condition)
		{
			GD.Print("[WallpaperSelfTest] PASS  " + name);
		}
		else
		{
			_failures++;
			GD.PrintErr("[WallpaperSelfTest] FAIL  " + name);
		}
	}

	private static void CheckNear(string name, float actual, float expected)
	{
		Check(name + " (got " + actual.ToString("0.######") + ", want " + expected.ToString("0.######") + ")",
			Mathf.Abs(actual - expected) <= Tolerance);
	}
}
