using System;
using System.Linq;
using Godot;
using Godot.Collections;

/// <summary>
/// Verification for milestone 4.1's `.lot` archive format: the version gate, the JSON round-trip of
/// the manifest / scene payload / environment settings, the <see cref="LotVfs"/> read path, and the
/// writer→reader cycle through a real zip file on disk.
///
/// Only pure data and file round-trips are covered — no scene is rebuilt here, because loading a lot
/// into the world is milestone 4.2's business (this milestone only makes saving possible). The
/// archive's on-disk binary layout is Godot's zip and is not re-tested; what is pinned is that the
/// bytes we write are exactly the bytes we read back.
///
/// Follows the project's self-test convention (no test framework: pass/fail lines, run headless
/// under #if DEBUG from <see cref="BuilderScene._Ready"/>).
/// </summary>
public static class LotArchiveSelfTest
{
	private static int _failures;
	private static int _checks;

	/// <summary>Scratch lot path. Lives under user:// so it needs a writable data dir, which a headless
	/// run has. Removed at the end of the suite so a test never leaves a stray file behind.</summary>
	private const string ScratchLotPath = "user://__lot_archive_selftest.lot";
	private const string ScratchTextLotPath = "user://__lot_archive_selftest_text.lot";

	public static int Run()
	{
		_failures = 0;
		_checks = 0;

		TestVersionGate();
		TestManifestRoundTrip();
		TestNodeRecordRoundTrip();
		TestEnvironmentRoundTrip();
		TestDocumentWithoutEnvironment();
		TestSceneWalk();
		TestSecretPaths();
		TestSecretContentGate();
		TestSecretSectionCollection();
		TestCollectionPass();
		TestSavePipeline();
		TestKindTagRoundTrip();
		TestImportFromBuffer();
		TestSaveLoadRoundTrip();
		TestLotFileNaming();
		TestAutosaveAndRecovery();
		TestVfsStreamsFromArchive();
		TestFullArchiveRoundTrip();
		TestVersionRejection();
		TestMissingFileRejection();

		CleanupScratchFiles();

		GD.Print("[LotArchiveSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
		return _failures;
	}

	/// <summary>Removes the scratch archives a run creates, so the suite leaves no stray .lot behind.</summary>
	private static void CleanupScratchFiles()
	{
		RemoveIfPresent(ScratchLotPath);
		RemoveIfPresent(ScratchTextLotPath);
	}

	private static void RemoveIfPresent(string path)
	{
		if (Godot.FileAccess.FileExists(path)) DirAccess.RemoveAbsolute(path);
	}

	private static void Check(string label, bool ok)
	{
		_checks++;
		if (ok) return;
		_failures++;
		GD.Print("[LotArchiveSelfTest] FAIL  " + label);
	}

	/// <summary>The version gate must call the current version OK and reject both directions.</summary>
	private static void TestVersionGate()
	{
		int current = LotArchive.CurrentFormatVersion;
		Check("version: the current format version is accepted",
			LotArchive.LotManifest.CheckVersion(current) == LotArchive.VersionStatus.Ok);
		Check("version: a newer format is rejected as TooNew",
			LotArchive.LotManifest.CheckVersion(current + 1) == LotArchive.VersionStatus.TooNew);
		Check("version: an older format is rejected as TooOld",
			LotArchive.LotManifest.CheckVersion(current - 1) == LotArchive.VersionStatus.TooOld);
		Check("version: the too-new reason names the file's version",
			LotArchive.LotManifest.DescribeVersion(current + 1).Contains((current + 1).ToString()));
		Check("version: the too-old reason names the file's version",
			LotArchive.LotManifest.DescribeVersion(current - 1).Contains((current - 1).ToString()));
	}

	/// <summary>Manifest fields must survive a JSON round-trip, and a missing key must fall back.</summary>
	private static void TestManifestRoundTrip()
	{
		LotArchive.LotManifest m = new LotArchive.LotManifest
		{
			FormatVersion = LotArchive.CurrentFormatVersion,
			Name = "Test Lot",
			Author = "Rolkka",
			CreatedUnix = 1700000000L,
			ModifiedUnix = 1700000500L,
			MaxPlayers = 12,
			EntryScript = "scripts/main.lua"
		};

		LotArchive.LotManifest back = RoundTrip(m);
		Check("manifest: format version survives", back.FormatVersion == m.FormatVersion);
		Check("manifest: name survives", back.Name == "Test Lot");
		Check("manifest: author survives", back.Author == "Rolkka");
		Check("manifest: created timestamp survives", back.CreatedUnix == 1700000000L);
		Check("manifest: modified timestamp survives", back.ModifiedUnix == 1700000500L);
		Check("manifest: maxPlayers survives", back.MaxPlayers == 12);
		Check("manifest: entry script survives", back.EntryScript == "scripts/main.lua");

		// An empty dictionary exercises the fallback path; a dictionary with the key present would
		// (correctly) take the supplied value, so it would not test the fallback at all.
		LotArchive.LotManifest partial = LotArchive.LotManifest.FromJson(new Dictionary());
		Check("manifest: a missing version falls back to the current format",
			partial.FormatVersion == LotArchive.CurrentFormatVersion);
		Check("manifest: a missing maxPlayers falls back to the default", partial.MaxPlayers == 8);
		Check("manifest: a missing name falls back", partial.Name == "Untitled Lot");
	}

	// --- JSON round-trip helpers ----------------------------------------------------------------

	/// <summary>Every round-trip below goes through the real <see cref="Json"/> encoder/decoder, not a
	/// shortcut, so a field that only survives a hand-rolled copy would still fail here.</summary>
	private static LotArchive.LotManifest RoundTrip(LotArchive.LotManifest m)
	{
		return LotArchive.LotManifest.FromJson(Json.ParseString(Json.Stringify(m.ToJson())));
	}

	private static LotArchive.LotDocument RoundTrip(LotArchive.LotDocument d)
	{
		return LotArchive.LotDocument.FromJson(Json.ParseString(Json.Stringify(d.ToJson())));
	}

	private static bool Near(float a, float b)
	{
		return Mathf.Abs(a - b) <= 1e-5f;
	}

	private static bool Near(Vector3 a, Vector3 b)
	{
		return Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Z, b.Z);
	}

	private static bool Near(Vector2 a, Vector2 b)
	{
		return Near(a.X, b.X) && Near(a.Y, b.Y);
	}

	/// <summary>Colour compare with a small tolerance: the JSON form is text, so exact float equality
	/// would be testing the printer's rounding rather than the format's fidelity.</summary>
	private static bool Near(Color a, Color b)
	{
		return Near(a.R, b.R) && Near(a.G, b.G) && Near(a.B, b.B) && Near(a.A, b.A);
	}

	/// <summary>A node record must carry its transform, colour, parent index and property bag.</summary>
	private static void TestNodeRecordRoundTrip()
	{
		LotArchive.LotNodeRecord rec = new LotArchive.LotNodeRecord
		{
			Kind = "cube",
			Name = "Box",
			Position = new Vector3(1f, 2f, 3f),
			RotationDegrees = new Vector3(0f, 90f, 0f),
			Scale = new Vector3(2f, 1f, 0.5f),
			Color = new Color(0.2f, 0.4f, 0.6f),
			ParentIndex = 2,
			Properties = new Dictionary
			{
				{ "anchored", true },
				{ "canCollide", false },
				{ "texture", "img_abc123" }
			}
		};

		LotArchive.LotNodeRecord back = LotArchive.LotNodeRecord.FromJson(Json.ParseString(Json.Stringify(rec.ToJson())));
		Check("node: kind survives", back.Kind == "cube");
		Check("node: name survives", back.Name == "Box");
		Check("node: position survives", Near(back.Position, rec.Position));
		Check("node: rotation survives", Near(back.RotationDegrees, rec.RotationDegrees));
		Check("node: scale survives", Near(back.Scale, rec.Scale));
		Check("node: colour survives", Near(back.Color, rec.Color));
		Check("node: parent index survives", back.ParentIndex == 2);
		Check("node: a bool property survives",
			back.Properties.ContainsKey("anchored") && back.Properties["anchored"].AsBool());
		Check("node: a false bool property survives",
			back.Properties.ContainsKey("canCollide") && !back.Properties["canCollide"].AsBool());
		Check("node: a string property survives",
			back.Properties.ContainsKey("texture") && back.Properties["texture"].AsString() == "img_abc123");

		LotArchive.LotNodeRecord sparse = LotArchive.LotNodeRecord.FromJson(new Dictionary { { "name", "Thing" } });
		Check("node: a missing kind falls back to group", sparse.Kind == "group");
		Check("node: a missing parent index falls back to -1", sparse.ParentIndex == -1);
		Check("node: a missing scale falls back to one", sparse.Scale == Vector3.One);
	}

	/// <summary>The §2.5 environment block must round-trip field-for-field (that is what unblocks the
	/// milestone 2.5 persistence item), and every field has to actually be carried.</summary>
	private static void TestEnvironmentRoundTrip()
	{
		LotEnvironmentSettings env = LotEnvironmentSettings.ForPreset(SkyPreset.Overcast);
		env.FollowTimeOfDay = true;
		env.TimeOfDayHours = 18.5f;
		env.FogDensity = 0.042f;

		LotArchive.LotDocument doc = new LotArchive.LotDocument { Environment = env };
		LotArchive.LotDocument back = RoundTrip(doc);

		Check("env: the block survives", back.Environment.HasValue);
		if (!back.Environment.HasValue) return;
		LotEnvironmentSettings got = back.Environment.Value;

		Check("env: follows the clock", got.FollowTimeOfDay);
		Check("env: time of day survives", Near(got.TimeOfDayHours, 18.5f));
		Check("env: the manual sun elevation survives", Near(got.SunElevationDeg, env.SunElevationDeg));
		Check("env: the manual sun azimuth survives", Near(got.SunAzimuthDeg, env.SunAzimuthDeg));
		Check("env: sun colour survives", Near(got.SunColor, env.SunColor));
		Check("env: sun energy survives", Near(got.SunEnergy, env.SunEnergy));
		Check("env: sky top survives", Near(got.SkyTopColor, env.SkyTopColor));
		Check("env: sky horizon survives", Near(got.SkyHorizonColor, env.SkyHorizonColor));
		Check("env: ground horizon survives", Near(got.GroundHorizonColor, env.GroundHorizonColor));
		Check("env: ground bottom survives", Near(got.GroundBottomColor, env.GroundBottomColor));
		Check("env: fog toggle survives", got.FogEnabled);
		Check("env: fog colour survives", Near(got.FogColor, env.FogColor));
		Check("env: fog density survives", Near(got.FogDensity, 0.042f));
	}

	/// <summary>A document that never set an environment must stay environment-less, so persistence
	/// cannot silently stamp a look onto a lot that never chose one.</summary>
	private static void TestDocumentWithoutEnvironment()
	{
		LotArchive.LotDocument doc = new LotArchive.LotDocument();
		LotArchive.LotDocument back = RoundTrip(doc);
		Check("doc: no environment stays absent", !back.Environment.HasValue);
		Check("doc: no objects stays empty", back.Objects.Count == 0);
		Check("doc: no UI stays empty", back.UiElements.Count == 0);
	}

	/// <summary>
	/// The scene-walk (4.1 Step A): a live lot tree must turn into records that survive the real JSON
	/// encoder, with internal implementation children and script placeholders excluded. This is what
	/// makes "save the lot" possible — the format round-trip below already proves the bytes.
	/// </summary>
	private static void TestSceneWalk()
	{
		// Fixture world: the two containers a lot has. Nodes are all added before the capture, so the
		// walk sees one settled tree.
		Node3D lotRoot = new Node3D();
		lotRoot.Name = "TestLotRoot";
		Node uiRoot = new Node();
		uiRoot.Name = "TestUiRoot";

		LotObject ground = LotObject.Create(LotObjectKind.Plane, "Ground", new Color(0.45f, 0.44f, 0.42f));
		ground.Position = new Vector3(0f, -0.5f, 0f);
		ground.Scale = new Vector3(10f, 1f, 10f);
		lotRoot.AddChild(ground);

		LotObject pillar = LotObject.Create(LotObjectKind.Cylinder, "Pillar", new Color(0.2f, 0.4f, 0.6f));
		pillar.Position = new Vector3(2f, 1f, 0f);
		pillar.RotationDegrees = new Vector3(0f, 45f, 0f);
		pillar.SetAnchored(false);
		pillar.SetCanCollide(false);
		lotRoot.AddChild(pillar);

		LotObject character = LotObject.Create(LotObjectKind.Capsule, "Character", new Color(1f, 1f, 1f));
		character.Position = new Vector3(4f, 1f, 4f);
		lotRoot.AddChild(character);

		// A script placeholder IS recorded (as a script record) so it can be restored onto the entity it
		// is attached to; its *text* is still collected separately from the archive.
		LotScriptNode scriptNode = new LotScriptNode();
		scriptNode.ScriptPath = "user://Scripts/Test.lua";
		scriptNode.DisplayName = "Test.lua";
		scriptNode.Name = "Test_lua";
		lotRoot.AddChild(scriptNode);

		LotUIElement button = LotUIElement.Create(LotUIKind.Button, new Vector2(20f, 40f), new Vector2(160f, 40f), "Go");
		uiRoot.AddChild(button);

		LotArchive.LotDocument captured = new LotArchive.LotDocument();
		LotSceneWalk.CaptureChildren(lotRoot, captured.Objects);
		LotSceneWalk.CaptureChildren(uiRoot, captured.UiElements);

		Check("walk: three parts and one script are captured", captured.Objects.Count == 4);
		Check("walk: internal collision/outline children exist but are not captured",
			ground.GetChildCount() >= 2 && captured.Objects.Count == 4);
		Check("walk: the script placeholder is recorded as a script, not a part",
			captured.Objects.Count == 4 && captured.Objects[3].Kind == LotArchive.ScriptRecordKind);
		Check("walk: the script record carries only the file name",
			captured.Objects.Count == 4 && captured.Objects[3].Properties.Count == 1
				&& captured.Objects[3].Properties[LotArchive.ScriptNameProperty].AsString() == "Test.lua");
		Check("walk: one UI element is captured", captured.UiElements.Count == 1);
		Check("walk: kinds are stable lowercase tags",
			captured.Objects.Count == 4 && captured.Objects[0].Kind == "plane"
				&& captured.Objects[1].Kind == "cylinder" && captured.Objects[2].Kind == "capsule");
		Check("walk: names are captured", captured.Objects.Count == 4 && captured.Objects[1].Name == "Pillar");
		Check("walk: position and rotation are captured",
			captured.Objects.Count == 4 && Near(captured.Objects[1].Position, new Vector3(2f, 1f, 0f))
				&& Near(captured.Objects[1].RotationDegrees, new Vector3(0f, 45f, 0f)));
		Check("walk: scale is captured", captured.Objects.Count == 4 && Near(captured.Objects[0].Scale, new Vector3(10f, 1f, 10f)));
		Check("walk: colour is captured", captured.Objects.Count == 4 && Near(captured.Objects[1].Color, new Color(0.2f, 0.4f, 0.6f)));
		Check("walk: the declared anchored property is captured as a bool",
			captured.Objects.Count == 4 && captured.Objects[1].Properties.ContainsKey("anchored")
				&& !captured.Objects[1].Properties["anchored"].AsBool());
		Check("walk: the declared canCollide property is captured as a bool",
			captured.Objects.Count == 4 && captured.Objects[1].Properties.ContainsKey("canCollide")
				&& !captured.Objects[1].Properties["canCollide"].AsBool());
		Check("walk: root-level records point at the container",
			captured.Objects.Count == 4 && captured.Objects[0].ParentIndex == -1 && captured.Objects[2].ParentIndex == -1);
		Check("walk: the UI element kind is captured", captured.UiElements.Count == 1 && captured.UiElements[0].Kind == "Button");
		Check("walk: UI pixel position is captured",
			captured.UiElements.Count == 1 && Near(captured.UiElements[0].Position, new Vector3(20f, 40f, 0f)));
		Check("walk: the UI label rides in the property bag",
			captured.UiElements.Count == 1 && captured.UiElements[0].Properties["label"].AsString() == "Go");

		// Then the captured lot through the real encoder: a walked lot must survive exactly as a
		// hand-built one does, or saving would silently lose fields.
		LotArchive.LotDocument back = RoundTrip(captured);
		Check("walk: a captured lot survives a JSON round-trip",
			back.Objects.Count == 4 && back.UiElements.Count == 1);
		Check("walk: a captured transform survives the round-trip",
			back.Objects.Count == 4 && Near(back.Objects[1].Position, new Vector3(2f, 1f, 0f)));
		Check("walk: a captured property survives the round-trip",
			back.Objects.Count == 4 && back.Objects[1].Properties.ContainsKey("canCollide")
				&& !back.Objects[1].Properties["canCollide"].AsBool());

		// Free the fixture: it is outside the tree, so plain Free (not QueueFree) is the correct call.
		lotRoot.Free();
		uiRoot.Free();
	}


	/// <summary>The secret folders are the same format as the normal ones; only the path prefix marks
	/// them, which is what keeps the v1 record unchanged.</summary>
	private static void TestSecretPaths()
	{
		Check("secret: the code folder is recognised",
			LotArchive.IsSecretPath(LotArchive.SecretScriptsPrefix + "Character.lua"));
		Check("secret: the asset folder is recognised",
			LotArchive.IsSecretPath(LotArchive.SecretAssetsPrefix + "tex-abc.png"));
		Check("secret: a normal script is not secret",
			!LotArchive.IsSecretPath(LotArchive.ScriptsPrefix + "MyScript.lua"));
		Check("secret: a normal asset is not secret",
			!LotArchive.IsSecretPath(LotArchive.AssetsPrefix + "tex-abc.png"));
		Check("secret: the manifest and payload are not secret",
			!LotArchive.IsSecretPath(LotArchive.ManifestEntry) && !LotArchive.IsSecretPath(LotArchive.LotEntry));
		Check("secret: an empty path is not secret", !LotArchive.IsSecretPath("") && !LotArchive.IsSecretPath(null));
		Check("secret: the exact folder names are the agreed ones",
			LotArchive.SecretScriptsPrefix == "Secret_Code/" && LotArchive.SecretAssetsPrefix == "Secret_Asset/");
	}
	/// <summary>
	/// The base-content section must actually find the base nodes in a real tree — the failure mode this
	/// guards is a section that draws its heading but no rows, because the same "exclude base content"
	/// rule that keeps it out of the main tree also rejected it from its own section.
	/// </summary>
	private static void TestSecretSectionCollection()
	{
		Node3D lotRoot = new Node3D();
		lotRoot.Name = "SectionFixture";

		LotObject ground = LotObject.Create(LotObjectKind.Cube, "Ground", new Color(1f, 1f, 1f));
		lotRoot.AddChild(ground);

		LotScriptNode entry = new LotScriptNode();
		entry.DisplayName = BuilderScene.DefaultLotScriptName;
		entry.Name = "Character_lua";
		lotRoot.AddChild(entry);

		LotScriptNode creator = new LotScriptNode();
		creator.DisplayName = "MyScript.lua";
		creator.Name = "MyScript_lua";
		lotRoot.AddChild(creator);

		System.Collections.Generic.List<Node> roots = new System.Collections.Generic.List<Node>();
		HierarchyPanel.CollectSecretRoots(lotRoot, roots);

		Check("secret section: the base script is collected", roots.Count == 1 && roots[0] == entry);
		Check("secret section: a creator script is not collected", !roots.Contains(creator));
		Check("secret section: an ordinary part is not collected", !roots.Contains(ground));

		// The section draws nothing only when the lot genuinely has no base content — never because the
		// walk failed to descend.
		Node3D noBase = new Node3D();
		LotObject ball = LotObject.Create(LotObjectKind.Sphere, "Ball", new Color(1f, 1f, 1f));
		noBase.AddChild(ball);
		System.Collections.Generic.List<Node> none = new System.Collections.Generic.List<Node>();
		HierarchyPanel.CollectSecretRoots(noBase, none);
		Check("secret section: a lot with no base content collects nothing", none.Count == 0);

		lotRoot.Free();
		noBase.Free();
	}


	/// <summary>
	/// The editor-side secret-content gate (the 4.1 follow-up): base content starts hidden, base content
	/// is what the separate section holds (and that classification never changes with the reveal state),
	/// and reveal/hide only controls whether the section is drawn. The section's actual on-screen layout
	/// is a rendering outcome checked by eye in the running client, the same split the project uses for
	/// the wallpaper.
	/// </summary>
	private static void TestSecretContentGate()
	{
		// The default must be hidden: that is the safe state and the milestone's requirement.
		SecretContent.Hide();
		Check("secret gate: base content starts hidden", !SecretContent.Revealed);

		LotScriptNode baseScript = new LotScriptNode();
		baseScript.DisplayName = BuilderScene.DefaultLotScriptName;
		baseScript.Name = "Character_lua";

		LotScriptNode creatorScript = new LotScriptNode();
		creatorScript.DisplayName = "MyScript.lua";
		creatorScript.Name = "MyScript_lua";

		// A script with no DisplayName falls back to the node name, which cannot contain a dot — so it
		// does not accidentally count as the shipped script. Documents why DisplayName is authoritative.
		LotScriptNode unnamed = new LotScriptNode();
		unnamed.DisplayName = "";
		unnamed.Name = "Character_lua";

		LotObject part = LotObject.Create(LotObjectKind.Cube, "Box", new Color(1f, 1f, 1f));

		Check("secret gate: the shipped script counts as base content", SecretContent.IsBaseContent(baseScript));
		Check("secret gate: a creator script does not", !SecretContent.IsBaseContent(creatorScript));
		Check("secret gate: a script with no display name is not mistaken for base content",
			!SecretContent.IsBaseContent(unnamed));

		// The separation contract: base content always belongs to the secret section, and nothing else
		// ever does. This must hold while hidden AND while revealed, because a classification that
		// flipped with the reveal state is exactly how base content would end up inline again.
		Check("secret gate: a hidden base script belongs to the secret section",
			SecretContent.IsSecretContent(baseScript));
		Check("secret gate: a creator script never belongs to the secret section",
			!SecretContent.IsSecretContent(creatorScript));
		Check("secret gate: a 3D part never belongs to the secret section", !SecretContent.IsSecretContent(part));
		Check("secret gate: a null node never belongs to the secret section", !SecretContent.IsSecretContent(null));

		SecretContent.Reveal();
		Check("secret gate: revealing opens the section", SecretContent.Revealed);
		Check("secret gate: revealing does not change what counts as base content",
			SecretContent.IsSecretContent(baseScript) && SecretContent.IsBaseContent(baseScript));
		Check("secret gate: revealing still excludes a creator script from the section",
			!SecretContent.IsSecretContent(creatorScript));

		SecretContent.Hide();
		Check("secret gate: hiding closes the section", !SecretContent.Revealed);
		Check("secret gate: hiding does not change what counts as base content",
			SecretContent.IsSecretContent(baseScript));

		// The caution is the whole point of the toggle, so its wording is pinned rather than left free
		// to drift into a neutral label.
		Check("secret gate: the warning calls the content proprietary",
			SecretContent.RevealWarning.Contains("proprietary"));
		Check("secret gate: the warning tells the creator to be sure",
			SecretContent.RevealWarning.Contains("know what you are doing"));
		Check("secret gate: the section heading names the content",
			SecretContent.SectionHeading.Contains("Secret Content"));
		Check("secret gate: the section note warns about breakage",
			SecretContent.SectionNote.Contains("break"));
		Check("secret gate: the row marker is not empty", SecretContent.RowMarker.Trim().Length > 0);

		part.Free();
		baseScript.Free();
		creatorScript.Free();
		unnamed.Free();
	}



	/// <summary>Writes a small text file, returning whether it landed.</summary>
	private static bool WriteTextFile(string path, string text)
	{
		Godot.FileAccess file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write);
		if (file == null) return false;
		file.StoreString(text);
		file.Dispose();
		return true;
	}

	/// <summary>
	/// The collection pass (4.1 Step B): real script files and real image bytes must be picked up,
	/// routed to the right folder (base content to the secret ones), and reported when their source has
	/// gone instead of being silently dropped.
	/// </summary>
	private static void TestCollectionPass()
	{
		string baseImagePath = TestImages.WritePng("user://lotarchiveselftest_base.png", new Color(0.9f, 0.3f, 0.1f, 1f));
		string userImagePath = TestImages.WritePng("user://lotarchiveselftest_user.png", new Color(0.1f, 0.3f, 0.9f, 1f));
		if (baseImagePath.Length == 0 || userImagePath.Length == 0)
		{
			Check("collect: test images could be written", false);
			return;
		}

		Node3D lotRoot = new Node3D();
		lotRoot.Name = "CollectLotRoot";
		Node uiRoot = new Node();
		uiRoot.Name = "CollectUiRoot";

		// Real image files, imported through the one image pipeline the Texture property uses.
		string baseTextureId = LotTextureCache.ImportFromFile(baseImagePath, out string _);
		string userTextureId = LotTextureCache.ImportFromFile(userImagePath, out string _);

		LotObject character = LotObject.Create(LotObjectKind.Capsule, "Character", new Color(1f, 1f, 1f));
		character.SetTexture(baseTextureId);
		lotRoot.AddChild(character);

		LotObject wall = LotObject.Create(LotObjectKind.Cube, "Wall", new Color(0.8f, 0.8f, 0.8f));
		wall.SetTexture(userTextureId);
		lotRoot.AddChild(wall);

		// A base script (routed to the secret folder) and a creator script, both on real files.
		string baseScriptPath = "user://lotarchiveselftest_base.lua";
		string userScriptPath = "user://lotarchiveselftest_user.lua";
		Check("collect: the script fixtures could be written",
			WriteTextFile(baseScriptPath, "-- base\n") && WriteTextFile(userScriptPath, "-- user\n"));

		LotScriptNode baseScript = new LotScriptNode();
		baseScript.ScriptPath = baseScriptPath;
		baseScript.DisplayName = BuilderScene.DefaultLotScriptName;
		baseScript.Name = "Character_lua";
		lotRoot.AddChild(baseScript);

		LotScriptNode userScript = new LotScriptNode();
		userScript.ScriptPath = userScriptPath;
		userScript.DisplayName = "MyScript.lua";
		userScript.Name = "MyScript_lua";
		lotRoot.AddChild(userScript);

		// A script whose file is gone must be reported, not turned into an empty entry.
		LotScriptNode goneScript = new LotScriptNode();
		goneScript.ScriptPath = "user://lotarchiveselftest_gone.lua";
		goneScript.DisplayName = "Gone.lua";
		goneScript.Name = "Gone_lua";
		lotRoot.AddChild(goneScript);

		LotArchive.LotDocument document = new LotArchive.LotDocument();
		LotSceneWalk.CaptureChildren(lotRoot, document.Objects);
		LotSceneWalk.CaptureChildren(uiRoot, document.UiElements);

		System.Collections.Generic.Dictionary<string, string> scripts = new System.Collections.Generic.Dictionary<string, string>();
		System.Collections.Generic.Dictionary<string, byte[]> assets = new System.Collections.Generic.Dictionary<string, byte[]>();
		System.Collections.Generic.List<string> missing = new System.Collections.Generic.List<string>();
		LotArchiveCollect.Scripts(lotRoot, scripts, missing);
		LotArchiveCollect.Assets(document, assets, missing);

		CollectRoutingChecks(document, scripts, assets, missing, baseImagePath, baseTextureId, userTextureId);

		// The lot-root script (a direct child of the lot root) is what a host boots (§8.1).
		Check("collect: the lot-root script becomes the entry script",
			LotArchiveCollect.ResolveEntryScript(lotRoot) == LotArchiveCollect.ScriptEntryPath(BuilderScene.DefaultLotScriptName, true));

		// Tear the fixture down: drop the cache references the parts hold, then remove the files.
		character.ReleaseTexture();
		wall.ReleaseTexture();
		LotTextureCache.Release(baseTextureId);
		LotTextureCache.Release(userTextureId);
		lotRoot.Free();
		uiRoot.Free();
		RemoveIfPresent(baseImagePath);
		RemoveIfPresent(userImagePath);
		RemoveIfPresent(baseScriptPath);
		RemoveIfPresent(userScriptPath);
	}
	/// <summary>The routing / missing-content / archive assertions for <see cref="TestCollectionPass"/>.</summary>
	private static void CollectRoutingChecks(LotArchive.LotDocument document,
		System.Collections.Generic.Dictionary<string, string> scripts,
		System.Collections.Generic.Dictionary<string, byte[]> assets,
		System.Collections.Generic.List<string> missing, string baseImagePath,
		string baseTextureId, string userTextureId)
	{
		Check("collect: base content is routed to the secret code folder",
			scripts.ContainsKey(LotArchive.SecretScriptsPrefix + BuilderScene.DefaultLotScriptName));
		Check("collect: a creator script is routed to the normal folder",
			scripts.ContainsKey(LotArchive.ScriptsPrefix + "MyScript.lua"));
		Check("collect: the base script's text is captured",
			scripts.ContainsKey(LotArchive.SecretScriptsPrefix + BuilderScene.DefaultLotScriptName)
				&& scripts[LotArchive.SecretScriptsPrefix + BuilderScene.DefaultLotScriptName] == "-- base\n");
		Check("collect: an unreadable script is reported missing and not fabricated",
			missing.Contains(LotArchive.ScriptsPrefix + "Gone.lua")
				&& !scripts.ContainsKey(LotArchive.ScriptsPrefix + "Gone.lua"));

		string secretAsset = LotArchiveCollect.AssetEntryPath(baseTextureId, true);
		string normalAsset = LotArchiveCollect.AssetEntryPath(userTextureId, false);
		Check("collect: the character texture is routed to the secret asset folder",
			assets.ContainsKey(secretAsset) && LotArchive.IsSecretPath(secretAsset));
		Check("collect: the normal texture is routed to the normal asset folder",
			assets.ContainsKey(normalAsset) && !LotArchive.IsSecretPath(normalAsset));
		Check("collect: the captured bytes are the file's real bytes",
			assets.ContainsKey(secretAsset) && assets[secretAsset].Length == Godot.FileAccess.GetFileAsBytes(baseImagePath).Length);
		Check("collect: the asset entry keeps the source extension", secretAsset.EndsWith(".png"));
		Check("collect: the base character record is detected as base content",
			document.Objects.Count > 0 && LotArchiveCollect.IsBaseAssetRecord(document.Objects[0]));

		// A record referencing an id with no source file must be reported, never silently skipped.
		LotArchive.LotDocument orphan = new LotArchive.LotDocument();
		orphan.Objects.Add(new LotArchive.LotNodeRecord { Kind = "cube", Name = "Orphan" });
		orphan.Objects[0].Properties["texture"] = "tex-no-such-source";
		System.Collections.Generic.Dictionary<string, byte[]> orphanAssets = new System.Collections.Generic.Dictionary<string, byte[]>();
		System.Collections.Generic.List<string> orphanMissing = new System.Collections.Generic.List<string>();
		LotArchiveCollect.Assets(orphan, orphanAssets, orphanMissing);
		Check("collect: an asset with no source file is reported missing",
			orphanMissing.Contains("tex-no-such-source") && orphanAssets.Count == 0);

		// The whole collected payload must survive a real archive round-trip, secrets included.
		LotArchive.LotManifest manifest = new LotArchive.LotManifest
		{
			EntryScript = LotArchiveCollect.ScriptEntryPath(BuilderScene.DefaultLotScriptName, true)
		};
		bool wrote = LotArchiveWriter.Write(ScratchTextLotPath, manifest, document, scripts, assets, out string writeError);
		Check("collect: a collected lot writes", wrote);
		if (writeError.Length > 0) GD.Print("[LotArchiveSelfTest]     write error: " + writeError);

		LotVfs vfs = new LotVfs();
		try
		{
			Check("collect: the archive opens", vfs.Open(ScratchTextLotPath, out _));
			Check("collect: the secret script entry is listed",
				vfs.Entries.Contains(LotArchive.SecretScriptsPrefix + BuilderScene.DefaultLotScriptName));
			Check("collect: the secret asset entry is listed", vfs.Entries.Contains(secretAsset));
			byte[] streamed = vfs.ReadEntry(secretAsset);
			Check("collect: the secret asset bytes stream back",
				streamed != null && streamed.Length == Godot.FileAccess.GetFileAsBytes(baseImagePath).Length);
			Check("collect: the secret script text streams back",
				vfs.ReadEntryText(LotArchive.SecretScriptsPrefix + BuilderScene.DefaultLotScriptName) == "-- base\n");
			Check("collect: only the secret entries report as secret", vfs.Entries.Count(LotArchive.IsSecretPath) == 2);
			Check("collect: directory entries are not listed as readable files",
				!vfs.Entries.Contains(LotArchive.ScriptsPrefix) && !vfs.Entries.Contains("assets/"));
		}
		finally
		{
			vfs.Dispose();
		}
	}

	/// <summary>
	/// The save pipeline end to end (4.1 Step C): a live lot tree written through <see cref="LotSave"/>
	/// and read back as a real archive. This is the integration check that the walk, the collection pass
	/// and the writer agree, which is what autosave depends on.
	/// </summary>
	private static void TestSavePipeline()
	{
		Node3D lotRoot = new Node3D();
		Node uiRoot = new Node();

		LotObject ground = LotObject.Create(LotObjectKind.Plane, "Ground", new Color(0.45f, 0.44f, 0.42f));
		lotRoot.AddChild(ground);

		LotObject crate = LotObject.Create(LotObjectKind.Cube, "Crate", new Color(0.7f, 0.4f, 0.2f));
		crate.Position = new Vector3(1f, 2f, 3f);
		crate.SetAnchored(false);
		lotRoot.AddChild(crate);

		LotUIElement label = LotUIElement.Create(LotUIKind.Text, new Vector2(10f, 10f), new Vector2(120f, 30f), "Hi");
		uiRoot.AddChild(label);

		string entryPath = "user://lotarchiveselftest_entry.lua";
		Check("save: the entry script fixture could be written", WriteTextFile(entryPath, "-- entry\n"));
		LotScriptNode entry = new LotScriptNode();
		entry.ScriptPath = entryPath;
		entry.DisplayName = BuilderScene.DefaultLotScriptName;
		entry.Name = "Entry_lua";
		lotRoot.AddChild(entry);

		LotArchive.LotManifest manifest = LotSave.BuildManifest(lotRoot, "Pipeline Lot", 0L);
		Check("save: the built manifest takes the lot name", manifest.Name == "Pipeline Lot");
		Check("save: the built manifest points at the entry script",
			manifest.EntryScript == LotArchiveCollect.ScriptEntryPath(BuilderScene.DefaultLotScriptName, true));
		Check("save: the built manifest stamps a modified time", manifest.ModifiedUnix > 0L);
		Check("save: a supplied creation time is preserved by the manifest builder",
			LotSave.BuildManifest(lotRoot, "Pipeline Lot", 1700000000L).CreatedUnix == 1700000000L);

		LotEnvironmentSettings environment = LotEnvironmentSettings.ForPreset(SkyPreset.Night);
		// The §3.5 collision matrix is session-wide lot state; give it a non-default value so the
		// pipeline's embedding of it is actually exercised. It is reset once the checks below run.
		LotCollisionGroups.ResetAllCollisions();
		LotCollisionGroups.SetCollidesByName("Character", "Decoration", false);
		System.Collections.Generic.List<string> missing = new System.Collections.Generic.List<string>();
		bool written = LotSave.Write(lotRoot, uiRoot, environment, ScratchLotPath, manifest, missing, out string error);
		Check("save: the pipeline writes the lot", written);
		if (error.Length > 0) GD.Print("[LotArchiveSelfTest]     write error: " + error);
		Check("save: nothing was reported missing", missing.Count == 0);

		Check("save: the written lot reads back",
			LotArchiveReader.Read(ScratchLotPath, out LotArchive.LotManifest readManifest,
				out LotArchive.LotDocument readDocument, out string readError));
		if (readError.Length > 0) GD.Print("[LotArchiveSelfTest]     read error: " + readError);
		Check("save: the manifest name survives the pipeline", readManifest != null && readManifest.Name == "Pipeline Lot");
		Check("save: both parts and the entry script survive the pipeline",
			readDocument != null && readDocument.Objects.Count == 3);
		Check("save: the UI element survives the pipeline", readDocument != null && readDocument.UiElements.Count == 1);
		Check("save: a part property survives the pipeline",
			readDocument != null && readDocument.Objects.Count == 3
				&& readDocument.Objects[1].Properties.ContainsKey("anchored")
				&& !readDocument.Objects[1].Properties["anchored"].AsBool());
		Check("save: the environment survives the pipeline",
			readDocument != null && readDocument.Environment.HasValue
				&& readDocument.Environment.Value.SunEnergy == environment.SunEnergy);
		Check("save: the collision matrix is embedded in the pipeline",
			readDocument != null && readDocument.Collision != null
				&& readDocument.Collision.Count == LotCollisionGroups.Count);
		// Decode the read-back matrix and confirm the changed pair came through; then reset so no
		// later check sees a modified shared table.
		if (readDocument != null && readDocument.Collision != null)
			LotCollisionGroups.FromJson(readDocument.Collision);
		Check("save: the loaded matrix reproduces the changed pair",
			!LotCollisionGroups.GetCollidesByName("Character", "Decoration"));
		LotCollisionGroups.ResetAllCollisions();

		LotVfs vfs = new LotVfs();
		try
		{
			Check("save: the entry script is packed inside the archive",
				vfs.Open(ScratchLotPath, out _) && vfs.HasEntry(manifest.EntryScript));
			Check("save: the packed entry script text matches the file",
				vfs.ReadEntryText(manifest.EntryScript) == "-- entry\n");
		}
		finally
		{
			vfs.Dispose();
		}

		lotRoot.Free();
		uiRoot.Free();
		RemoveIfPresent(entryPath);
	}
	/// <summary>
	/// Autosave's guard rails and the crash-marker contract (4.1 Step C). The positive write path is
	/// covered by <see cref="TestSavePipeline"/>, because Tick and a manual save share one pipeline.
	/// </summary>
	/// <summary>The kind-tag vocabulary must round-trip: the capture writes a tag and the load reads it
	/// back, so a tag either parses to the kind it was written for or the lot changes shape on load.</summary>
	private static void TestKindTagRoundTrip()
	{
		LotObjectKind[] kinds =
		{
			LotObjectKind.Cube, LotObjectKind.Sphere, LotObjectKind.Cylinder,
			LotObjectKind.Plane, LotObjectKind.Capsule, LotObjectKind.Decal
		};

		bool allRoundTrip = true;
		for (int i = 0; i < kinds.Length; i++)
		{
			string tag = LotSceneWalk.LotKindTag(kinds[i]);
			if (!LotSceneWalk.TryParseLotKind(tag, out LotObjectKind parsed) || parsed != kinds[i]) allRoundTrip = false;
		}
		Check("kinds: every object kind survives a tag round-trip", allRoundTrip);
		Check("kinds: tags are lowercase", LotSceneWalk.LotKindTag(LotObjectKind.Capsule) == "capsule");
		Check("kinds: an unknown tag is refused rather than guessed",
			!LotSceneWalk.TryParseLotKind("pyramid", out _));
		Check("kinds: an empty tag is refused", !LotSceneWalk.TryParseLotKind("", out _));
		// The script tag is not an object kind: it must NOT parse as one, or a script would be restored
		// as a part.
		Check("kinds: the script record kind is not an object kind",
			!LotSceneWalk.TryParseLotKind(LotArchive.ScriptRecordKind, out _));
	}

	/// <summary>Registering archived bytes under a supplied id is what lets a loaded record's texture
	/// reference resolve; the id must be the caller's, not one derived from a path.</summary>
	private static void TestImportFromBuffer()
	{
		string imagePath = TestImages.WritePng("user://lotarchiveselftest_buffer.png", new Color(0.2f, 0.7f, 0.3f, 1f));
		if (imagePath.Length == 0)
		{
			Check("buffer: the test image could be written", false);
			return;
		}

		byte[] bytes = Godot.FileAccess.GetFileAsBytes(imagePath);
		Check("buffer: the test image bytes are readable", bytes != null && bytes.Length > 0);

		const string suppliedId = "tex-roundtrip-supplied";
		Check("buffer: importing under a supplied id succeeds",
			LotTextureCache.ImportFromBuffer(suppliedId, bytes, "assets/" + suppliedId + ".png", out string error));
		Check("buffer: nothing is reported on a clean import", error.Length == 0);
		Check("buffer: the supplied id is the id registered", LotTextureCache.Contains(suppliedId));
		Check("buffer: the id resolves to a texture", LotTextureCache.Get(suppliedId) != null);
		Check("buffer: the decoded pixels are the image's own",
			HasTopLeftPixel(LotTextureCache.Get(suppliedId), new Color(0.2f, 0.7f, 0.3f, 1f)));

		// A second reference to the same id must reuse the entry rather than decode again.
		Check("buffer: a repeat import reuses the entry",
			LotTextureCache.ImportFromBuffer(suppliedId, bytes, "again", out _));
		Check("buffer: the repeat import held one more reference", !LotTextureCache.Release(suppliedId));
		Check("buffer: releasing both frees the entry", LotTextureCache.Release(suppliedId));
		Check("buffer: the freed id is gone", !LotTextureCache.Contains(suppliedId));

		Check("buffer: content that is not an image is refused",
			!LotTextureCache.ImportFromBuffer("tex-not-an-image", new byte[] { 1, 2, 3, 4 }, "junk", out string badError));
		Check("buffer: the refusal explains itself", badError.Length > 0);
		Check("buffer: a refused import registers nothing", !LotTextureCache.Contains("tex-not-an-image"));
		Check("buffer: an empty id is refused", !LotTextureCache.ImportFromBuffer("", bytes, "x", out _));

		RemoveIfPresent(imagePath);
	}

	/// <summary>True when a texture is the 8x8 test image with the expected top-left pixel.</summary>
	private static bool HasTopLeftPixel(Texture2D texture, Color expected)
	{
		if (texture == null) return false;
		Image image = texture.GetImage();
		if (image == null || image.IsEmpty()) return false;
		if (image.GetWidth() != 8 || image.GetHeight() != 8) return false;
		Color actual = image.GetPixel(0, 0);
		return Same8Bit(actual.R, expected.R) && Same8Bit(actual.G, expected.G)
			&& Same8Bit(actual.B, expected.B) && Same8Bit(actual.A, expected.A);
	}

	/// <summary>
	/// Compares two colour channels at 8-bit precision, allowing one step. A decoded pixel is quantised to
	/// 8 bits and re-deriving a byte from a float can land either side of a .5 boundary (0.1f is a hair
	/// above 0.1, so it rounds up while the stored byte rounded down) — so this tests that the pipeline
	/// carried the same image, without pretending an 8-bit round-trip is bit-exact.
	/// </summary>
	private static bool Same8Bit(float a, float b)
	{
		return Mathf.Abs(Mathf.RoundToInt(a * 255f) - Mathf.RoundToInt(b * 255f)) <= 1;
	}

	/// <summary>Children a creator sees: internal implementation children (outline hull, collision body)
	/// are not part of a node's authored content and must not be counted as such.</summary>
	private static int AuthoredChildCount(Node node)
	{
		if (node == null) return 0;
		int count = 0;
		for (int i = 0; i < node.GetChildCount(); i++)
		{
			if (!node.GetChild(i).HasMeta(LotObject.InternalChildMeta)) count++;
		}
		return count;
	}


	/// <summary>
	/// The §4.2 round trip: a live lot saved to an archive and loaded into fresh containers must come back
	/// with its objects, transforms, properties, UI, image bytes, and its scripts <b>still attached to the
	/// entities they belong to</b>. That last part is the reason scripts are recorded rather than inferred
	/// from the archive's script entries.
	/// </summary>
	private static void TestSaveLoadRoundTrip()
	{
		DirAccess.MakeDirRecursiveAbsolute(ScriptManager.ScriptsDir);
		string imagePath = TestImages.WritePng("user://lotarchiveselftest_rt.png", new Color(0.9f, 0.4f, 0.1f, 1f));
		string rootScriptPath = ScriptManager.ScriptsDir + "/__rt_root.lua";
		string attachedScriptPath = ScriptManager.ScriptsDir + "/__rt_attached.lua";
		if (imagePath.Length == 0 || !WriteTextFile(rootScriptPath, "-- root\n") || !WriteTextFile(attachedScriptPath, "-- attached\n"))
		{
			Check("roundtrip: the fixture files could be written", false);
			return;
		}

		// --- the source lot ---
		Node3D sourceRoot = new Node3D();
		Node sourceUi = new Node();

		LotObject ground = LotObject.Create(LotObjectKind.Plane, "Ground", new Color(0.45f, 0.44f, 0.42f));
		ground.Scale = new Vector3(10f, 1f, 10f);
		sourceRoot.AddChild(ground);

		string assetId = LotTextureCache.ImportFromFile(imagePath, out string _);
		LotObject crate = LotObject.Create(LotObjectKind.Cube, "Crate", new Color(0.7f, 0.4f, 0.2f));
		crate.Position = new Vector3(1f, 2f, 3f);
		crate.RotationDegrees = new Vector3(0f, 45f, 0f);
		crate.SetAnchored(false);
		crate.SetCanCollide(false);
		crate.SetTexture(assetId);
		sourceRoot.AddChild(crate);

		LotScriptNode rootScript = new LotScriptNode();
		rootScript.ScriptPath = rootScriptPath;
		rootScript.DisplayName = "__rt_root.lua";
		rootScript.Name = "__rt_root_lua";
		sourceRoot.AddChild(rootScript);

		// A script attached to an *entity*, not the lot root: exactly what a root-only restore would lose.
		LotScriptNode attachedScript = new LotScriptNode();
		attachedScript.ScriptPath = attachedScriptPath;
		attachedScript.DisplayName = "__rt_attached.lua";
		attachedScript.Name = "__rt_attached_lua";
		crate.AddChild(attachedScript);

		LotUIElement button = LotUIElement.Create(LotUIKind.Button, new Vector2(20f, 40f), new Vector2(160f, 40f), "Go");
		button.Visible = false;
		sourceUi.AddChild(button);

		// --- save ---
		LotArchive.LotManifest manifest = LotSave.BuildManifest(sourceRoot, "Round Trip Lot", 1700000000L);
		System.Collections.Generic.List<string> missing = new System.Collections.Generic.List<string>();
		Check("roundtrip: the lot saves",
			LotSave.Write(sourceRoot, sourceUi, LotEnvironmentSettings.ForPreset(SkyPreset.Sunset),
				ScratchLotPath, manifest, missing, out string writeError));
		if (writeError.Length > 0) GD.Print("[LotArchiveSelfTest]     write error: " + writeError);
		Check("roundtrip: nothing was missing at save time", missing.Count == 0);

		// Drop every reference the save side held, so the load genuinely decodes the *archived* bytes
		// rather than finding the same image already in the cache under the same id.
		crate.ReleaseTexture();
		LotTextureCache.Release(assetId);
		sourceRoot.Free();
		sourceUi.Free();
		Check("roundtrip: the source image was fully released", !LotTextureCache.Contains(assetId));

		RunRoundTripLoad(assetId);

		RemoveIfPresent(imagePath);
		RemoveIfPresent(rootScriptPath);
		RemoveIfPresent(attachedScriptPath);
	}

	/// <summary>The load half of <see cref="TestSaveLoadRoundTrip"/>: decode, restore into fresh
	/// containers, and check both the report and the rebuilt content.</summary>
	private static void RunRoundTripLoad(string assetId)
	{
		Check("roundtrip: the saved lot reads back",
			LotArchiveReader.Read(ScratchLotPath, out LotArchive.LotManifest readManifest,
				out LotArchive.LotDocument document, out string readError));
		if (readError.Length > 0) GD.Print("[LotArchiveSelfTest]     read error: " + readError);

		Node3D destRoot = new Node3D();
		Node destUi = new Node();
		LotVfs vfs = new LotVfs();
		LotSceneLoad.LoadReport report = new LotSceneLoad.LoadReport();
		try
		{
			Check("roundtrip: the archive opens for the load", vfs.Open(ScratchLotPath, out _));
			Check("roundtrip: the restore runs", LotSceneLoad.Restore(destRoot, destUi, document, vfs, report));
		}
		finally
		{
			vfs.Dispose();
		}

		Check("roundtrip: nothing was reported unrestorable", report.Warnings.Count == 0);
		Check("roundtrip: both parts came back", report.PartsCreated == 2);
		Check("roundtrip: the UI element came back", report.UiCreated == 1);
		Check("roundtrip: both scripts came back", report.ScriptsCreated == 2);
		Check("roundtrip: the image came back", report.AssetsLoaded == 1);
		Check("roundtrip: the manifest kept the lot name", readManifest != null && readManifest.Name == "Round Trip Lot");
		Check("roundtrip: the manifest kept the creation stamp",
			readManifest != null && readManifest.CreatedUnix == 1700000000L);

		AssertRoundTripContent(destRoot, destUi, assetId);

		destRoot.Free();
		destUi.Free();
		LotTextureCache.Release(assetId);
	}

	/// <summary>The structure / transform / property / UI assertions for the round trip.</summary>
	private static void AssertRoundTripContent(Node3D destRoot, Node destUi, string assetId)
	{
		Node crateNode = FindChildNamed(destRoot, "Crate");
		Check("roundtrip: the object layer has three children", destRoot.GetChildCount() == 3);
		Check("roundtrip: the crate has exactly its attached script as a child",
			AuthoredChildCount(crateNode) == 1 && FindChildOfType<LotScriptNode>(crateNode) != null);
		Check("roundtrip: the lot-root script is still a root child",
			FindChildNamed(destRoot, "__rt_root_lua") is LotScriptNode);

		LotObject backCrate = crateNode as LotObject;
		Check("roundtrip: the part kind survived", backCrate != null && backCrate.Kind == LotObjectKind.Cube);
		Check("roundtrip: the position survived", backCrate != null && Near(backCrate.Position, new Vector3(1f, 2f, 3f)));
		Check("roundtrip: the rotation survived", backCrate != null && Near(backCrate.RotationDegrees, new Vector3(0f, 45f, 0f)));
		Check("roundtrip: the colour survived", backCrate != null && Near(backCrate.Color, new Color(0.7f, 0.4f, 0.2f)));
		Check("roundtrip: 'anchored' survived", backCrate != null && !backCrate.Anchored);
		Check("roundtrip: 'canCollide' survived", backCrate != null && !backCrate.CanCollide);
		LotObject backGround = FindChildNamed(destRoot, "Ground") as LotObject;
		Check("roundtrip: the scale survived", backGround != null && Near(backGround.Scale, new Vector3(10f, 1f, 10f)));

		Check("roundtrip: the part still references its image id", backCrate != null && backCrate.TextureId == assetId);
		Check("roundtrip: the image was re-registered from the archive", LotTextureCache.Contains(assetId));
		Check("roundtrip: the recovered image is the same image",
			HasTopLeftPixel(LotTextureCache.Get(assetId), new Color(0.9f, 0.4f, 0.1f, 1f)));

		LotUIElement backButton = destUi.GetChildCount() > 0 ? destUi.GetChild(0) as LotUIElement : null;
		Check("roundtrip: the UI element kept its kind", backButton != null && backButton.Kind == LotUIKind.Button);
		Check("roundtrip: the UI label survived", backButton != null && backButton.Label == "Go");
		Check("roundtrip: the UI rect survived",
			backButton != null && Near(backButton.Position, new Vector2(20f, 40f)) && Near(backButton.Size, new Vector2(160f, 40f)));
		Check("roundtrip: the UI visibility survived", backButton != null && !backButton.Visible);
	}

	/// <summary>
	/// Godot's FileDialog does not append the filter's extension for us, so the picker guarantees it. The
	/// suffix matters: every later step keys off a <c>.lot</c> path, and a save that landed as
	/// <c>MyLot</c> would not be offered by the Open browser's filter.
	/// </summary>
	private static void TestLotFileNaming()
	{
		Check("lot path: a bare name gains the extension",
			LotFilePicker.EnsureExtension("MyLot") == "MyLot.lot");
		Check("lot path: an existing extension is kept",
			LotFilePicker.EnsureExtension("MyLot.lot") == "MyLot.lot");
		Check("lot path: the extension match is case-insensitive",
			LotFilePicker.EnsureExtension("MyLot.LOT") == "MyLot.LOT");
		Check("lot path: a directory is preserved",
			LotFilePicker.EnsureExtension("user://l/Mine") == "user://l/Mine.lot");
		Check("lot path: a dotted name is not mistaken for an extension",
			LotFilePicker.EnsureExtension("My.Lot.v2") == "My.Lot.v2.lot");
		Check("lot path: an empty path is left alone", LotFilePicker.EnsureExtension("") == "");
	}

	/// <summary>The first direct child of the given type, or null.</summary>
	private static T FindChildOfType<T>(Node parent) where T : class
	{
		if (parent == null) return null;
		int count = parent.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			T match = parent.GetChild(i) as T;
			if (match != null) return match;
		}
		return null;
	}

	/// <summary>The first direct child with the given node name, or null.</summary>
	private static Node FindChildNamed(Node parent, string name)
	{
		if (parent == null) return null;
		int count = parent.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			Node child = parent.GetChild(i);
			if (child.Name.ToString() == name) return child;
		}
		return null;
	}

	/// <summary>
	/// Autosave's guard rails and the crash-marker contract (4.1 Step C). The positive write path is
	/// covered by <see cref="TestSavePipeline"/>, because Tick and a manual save share one pipeline.
	/// </summary>
	private static void TestAutosaveAndRecovery()
	{
		// Start from a known state: the marker and any leftover snapshot are cleared first, so each
		// transition below is deterministic rather than depending on what a previous run left behind.
		LotAutosave.MarkSessionEnd();
		LotAutosave.DiscardSnapshot();
		Check("autosave: a clean state offers no recovery", !LotAutosave.HasRecoverableAutosave());

		LotAutosave.MarkSessionStart();
		Check("autosave: the marker is written when a session starts",
			Godot.FileAccess.FileExists(LotAutosave.CrashMarkerPath));
		Check("autosave: a marker with no snapshot offers no recovery", !LotAutosave.HasRecoverableAutosave());

		// A crashed session leaves both files behind; only then is a prompt warranted. The snapshot is
		// written through the REAL pipeline (not a placeholder), because the recovery prompt now hands this
		// very file to the loader — so it has to be a genuinely loadable lot, not just a file that exists.
		LotArchive.LotDocument snapshotDoc = new LotArchive.LotDocument();
		snapshotDoc.Objects.Add(new LotArchive.LotNodeRecord { Kind = "cube", Name = "Recovered" });
		Check("autosave: the snapshot fixture could be written",
			LotArchiveWriter.Write(LotAutosave.AutosavePath, new LotArchive.LotManifest { Name = "Recovered Lot" },
				snapshotDoc, null, null, out string snapshotError));
		if (snapshotError.Length > 0) GD.Print("[LotArchiveSelfTest]     snapshot write error: " + snapshotError);
		Check("autosave: a marker plus a snapshot offers recovery", LotAutosave.HasRecoverableAutosave());

		// The snapshot must read back as a lot — that is the contract the Recover button relies on.
		Check("autosave: the snapshot is a loadable lot",
			LotArchiveReader.Read(LotAutosave.AutosavePath, out LotArchive.LotManifest snapManifest,
				out LotArchive.LotDocument snapRead, out string snapReadError));
		if (snapReadError.Length > 0) GD.Print("[LotArchiveSelfTest]     snapshot read error: " + snapReadError);
		Check("autosave: the recovered snapshot keeps its content",
			snapManifest != null && snapManifest.Name == "Recovered Lot"
				&& snapRead != null && snapRead.Objects.Count == 1 && snapRead.Objects[0].Name == "Recovered");

		LotAutosave.DiscardSnapshot();
		Check("autosave: discarding removes the snapshot", !Godot.FileAccess.FileExists(LotAutosave.AutosavePath));
		Check("autosave: discarding leaves the session marker alone",
			Godot.FileAccess.FileExists(LotAutosave.CrashMarkerPath));
		Check("autosave: no recovery is offered after discarding", !LotAutosave.HasRecoverableAutosave());

		// A clean exit is exactly what distinguishes it from a crash.
		LotAutosave.MarkSessionEnd();
		Check("autosave: a clean exit removes the marker", !Godot.FileAccess.FileExists(LotAutosave.CrashMarkerPath));

		// The two guards that must stop a snapshot. A null scene would fail loudly if either guard let
		// the call through, so "no file written" is the assertion.
		LotAutosave.Tick(LotAutosave.IntervalSeconds * 10.0, null, "Session Lot", true, true);
		Check("autosave: an active player session is never snapshotted",
			!Godot.FileAccess.FileExists(LotAutosave.AutosavePath));
		LotAutosave.Tick(LotAutosave.IntervalSeconds * 10.0, null, "Clean Lot", false, false);
		Check("autosave: a clean lot is never snapshotted", !Godot.FileAccess.FileExists(LotAutosave.AutosavePath));

		bool saved = LotAutosave.SaveNow(null, "No Scene", out string saveError);
		Check("autosave: saving with no scene fails with a reason", !saved && saveError.Length > 0);
		Check("autosave: the snapshot path is the documented one", LotAutosave.AutosavePath == "user://autosave.lot");
		Check("autosave: the interval is positive", LotAutosave.IntervalSeconds > 0.0);

		// Leave the filesystem as the running app expects it: a live session, no stray snapshot. The
		// marker is restored because removing it is what signals "crash" to the next boot.
		LotAutosave.DiscardSnapshot();
		LotAutosave.MarkSessionStart();
	}




	/// <summary>The VFS must stream entries out of the archive with no extracted copy on disk.</summary>
	private static void TestVfsStreamsFromArchive()
	{
		System.Collections.Generic.Dictionary<string, string> scripts =
			new System.Collections.Generic.Dictionary<string, string> { { LotArchive.ScriptsPrefix + "Character.lua", "-- character\n" } };
		System.Collections.Generic.Dictionary<string, byte[]> assets =
			new System.Collections.Generic.Dictionary<string, byte[]> { { LotArchive.AssetsPrefix + "note.bin", new byte[] { 1, 2, 3, 4, 5 } } };

		bool wrote = LotArchiveWriter.Write(ScratchTextLotPath, new LotArchive.LotManifest(), new LotArchive.LotDocument(),
			scripts, assets, out string writeError);
		Check("vfs: a lot with scripts and assets writes", wrote);
		if (!wrote) { GD.Print("[LotArchiveSelfTest]     write error: " + writeError); return; }
		Check("vfs: no error is reported on a clean write", writeError.Length == 0);

		LotVfs vfs = new LotVfs();
		try
		{
			Check("vfs: opening an existing archive succeeds", vfs.Open(ScratchTextLotPath, out _));
			Check("vfs: the archive reports as open", vfs.IsOpen);
			Check("vfs: the archive path is recorded", vfs.Path == ScratchTextLotPath);

			Check("vfs: the manifest entry is listed", vfs.Entries.Contains(LotArchive.ManifestEntry));
			Check("vfs: the payload entry is listed", vfs.Entries.Contains(LotArchive.LotEntry));
			Check("vfs: the script entry is listed", vfs.Entries.Contains(LotArchive.ScriptsPrefix + "Character.lua"));
			Check("vfs: the asset entry is listed", vfs.Entries.Contains(LotArchive.AssetsPrefix + "note.bin"));

			Check("vfs: an absent entry is reported absent", !vfs.HasEntry(LotArchive.AssetsPrefix + "nope.png"));
			Check("vfs: reading an absent entry returns null", vfs.ReadEntry(LotArchive.AssetsPrefix + "nope.png") == null);
			Check("vfs: reading absent text returns null", vfs.ReadEntryText("missing.json") == null);

			Check("vfs: script text streams back byte-exact",
				vfs.ReadEntryText(LotArchive.ScriptsPrefix + "Character.lua") == "-- character\n");

			byte[] binary = vfs.ReadEntry(LotArchive.AssetsPrefix + "note.bin");
			Check("vfs: asset bytes stream back", binary != null && binary.Length == 5);
			Check("vfs: asset bytes match what was written",
				binary != null && binary[0] == 1 && binary[1] == 2 && binary[2] == 3 && binary[3] == 4 && binary[4] == 5);
		}
		finally
		{
			vfs.Dispose();
		}

		Check("vfs: the close left the archive closed", !vfs.IsOpen);

		// Streaming rather than extracting is the whole point of the VFS: the archive is one file and
		// nothing appears beside it. (If a future change started extracting, this catches the stray
		// directory the reader would have left behind.)
		string sidecar = ScratchTextLotPath.Substring(0, ScratchTextLotPath.Length - 4);
		Check("vfs: no extracted folder was left beside the archive", !DirAccess.DirExistsAbsolute(sidecar));
	}

	/// <summary>The writer→reader cycle through a real file, with every payload kind present.</summary>
	private static void TestFullArchiveRoundTrip()
	{
		LotArchive.LotManifest manifest = new LotArchive.LotManifest
		{
			Name = "Round Trip Lot",
			Author = "Rolkka",
			MaxPlayers = 16,
			EntryScript = LotArchive.ScriptsPrefix + "main.lua"
		};

		LotArchive.LotDocument doc = new LotArchive.LotDocument { Environment = LotEnvironmentSettings.ForPreset(SkyPreset.Sunset) };
		doc.Objects.Add(new LotArchive.LotNodeRecord
		{
			Kind = "cube", Name = "Ground",
			Position = new Vector3(0f, -0.5f, 0f), Scale = new Vector3(10f, 1f, 10f),
			Properties = new Dictionary { { "anchored", true } }
		});
		doc.Objects.Add(new LotArchive.LotNodeRecord
		{
			Kind = "cylinder", Name = "Pillar",
			Position = new Vector3(2f, 1f, 0f), RotationDegrees = new Vector3(0f, 45f, 0f),
			Properties = new Dictionary { { "canCollide", false }, { "texture", "img_xyz" } }
		});
		doc.UiElements.Add(new LotArchive.LotNodeRecord
		{
			Kind = "Button", Name = "PlayBtn",
			Position = new Vector3(20f, 40f, 0f), Color = new Color(0.25f, 0.25f, 0.28f)
		});

		System.Collections.Generic.Dictionary<string, string> scripts =
			new System.Collections.Generic.Dictionary<string, string> { { LotArchive.ScriptsPrefix + "main.lua", "-- main\nfunction start() end\n" } };
		System.Collections.Generic.Dictionary<string, byte[]> assets =
			new System.Collections.Generic.Dictionary<string, byte[]> { { LotArchive.AssetsPrefix + "pixel.png", new byte[] { 137, 80, 78, 71 } } };

		Check("archive: writing a full lot succeeds",
			LotArchiveWriter.Write(ScratchLotPath, manifest, doc, scripts, assets, out string writeError));
		if (writeError.Length > 0) GD.Print("[LotArchiveSelfTest]     write error: " + writeError);

		Check("archive: reading the lot back succeeds",
			LotArchiveReader.Read(ScratchLotPath, out LotArchive.LotManifest readManifest, out LotArchive.LotDocument readDoc, out string readError));
		if (readError.Length > 0) GD.Print("[LotArchiveSelfTest]     read error: " + readError);

		Check("archive: the manifest survives the cycle",
			readManifest != null && readManifest.Name == "Round Trip Lot" && readManifest.MaxPlayers == 16);
		Check("archive: the entry script survives the cycle",
			readManifest != null && readManifest.EntryScript == LotArchive.ScriptsPrefix + "main.lua");
		Check("archive: both objects survive the cycle", readDoc != null && readDoc.Objects.Count == 2);
		Check("archive: the UI element survives the cycle", readDoc != null && readDoc.UiElements.Count == 1);
		Check("archive: object order survives the cycle",
			readDoc != null && readDoc.Objects.Count == 2 && readDoc.Objects[1].Name == "Pillar");
		Check("archive: an object property survives the cycle",
			readDoc != null && readDoc.Objects.Count == 2 && readDoc.Objects[1].Properties["texture"].AsString() == "img_xyz");
		Check("archive: the environment survives the cycle",
			readDoc != null && readDoc.Environment.HasValue && readDoc.Environment.Value.SunElevationDeg == 6f);
	}

	/// <summary>A lot stamped with a future format version must be refused with a readable reason and
	/// no payload, never half-loaded.</summary>
	private static void TestVersionRejection()
	{
		LotArchive.LotManifest future = new LotArchive.LotManifest { FormatVersion = LotArchive.CurrentFormatVersion + 1 };
		Check("reject: writing a future-stamped manifest succeeds",
			LotArchiveWriter.Write(ScratchLotPath, future, new LotArchive.LotDocument(), null, null, out _));

		bool ok = LotArchiveReader.Read(ScratchLotPath, out LotArchive.LotManifest refused, out LotArchive.LotDocument refusedDoc, out string error);
		Check("reject: a future version is not loaded", !ok);
		Check("reject: no manifest is returned for a rejected lot", refused == null);
		Check("reject: no payload is returned for a rejected lot", refusedDoc == null);
		Check("reject: the reason is readable", error.Contains("newer"));
		Check("reject: the reason names the unsupported version",
			error.Contains((LotArchive.CurrentFormatVersion + 1).ToString()));
	}

	/// <summary>A path with no lot must be refused with a readable reason, not an engine error.</summary>
	private static void TestMissingFileRejection()
	{
		bool ok = LotArchiveReader.Read("user://__definitely_not_a_lot.lot", out _, out _, out string error);
		Check("missing: a nonexistent lot is not loaded", !ok);
		Check("missing: the reason mentions the path", error.Contains("__definitely_not_a_lot.lot"));
	}
}

