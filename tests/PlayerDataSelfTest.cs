using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// Verification for the per-player data API (milestone 3.12): the player-id shape, the store's
/// semantics and caps, the JSON round trip and version gate, the archive entry through the real
/// writer/VFS, the bound verbs and their refusal paths — plus a staged probe that writes through a
/// script inside a session, leaves it, and does a real save → reload round trip (the acceptance:
/// a value survives a lot reload). The synchronous part runs with the other delayed suites from
/// BuilderScene._Process; only the probe touches the live scene, and it is headless-only.
/// </summary>
public static class PlayerDataSelfTest
{
	private const string ScratchLot = "user://playerdata_selftest.lot";
	private const string ProbeScriptName = "__pdselftest_probe.lua";
	private const string ProbeScriptDir = "user://Scripts";
	private const long ProbeMidWaitMsec = 900;
	private const long ProbeExitWaitMsec = 500;

	private static int _checks;
	private static int _failures;

	public static int Run(BuilderScene scene)
	{
		_checks = 0;
		_failures = 0;
		CleanupScratch();

		TestIdShape();
		TestStoreSemantics();
		TestCaps();
		TestJson();
		TestArchiveRoundTrip();
		TestBoundVerbs(scene);

		GD.Print("[PlayerDataSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
		StartProbe(scene);
		return _failures;
	}

	private static void CleanupScratch()
	{
		RemoveIfPresent(ScratchLot);
		RemoveIfPresent(ProbeScriptDir + "/" + ProbeScriptName);
	}

	private static void RemoveIfPresent(string path)
	{
		if (Godot.FileAccess.FileExists(path)) DirAccess.RemoveAbsolute(path);
	}

	/// <summary>Removes the probe part a lot reload recreated, found by its script node rather than
	/// by name — the pre-save node reference is stale once the lot has been replaced, and a name
	/// match alone proved too brittle. The script carries the fixture's identity.</summary>
	private static void RemoveFixtureNodes(BuilderScene scene)
	{
		if (scene == null || scene.LotRoot == null) return;
		List<Node> scripts = new List<Node>();
		CollectProbeScripts(scene.LotRoot, scripts);
		for (int i = 0; i < scripts.Count; i++)
		{
			Node owner = scripts[i].GetParent();
			scene.DestroyEntity(owner != null ? owner : scripts[i]);
		}
	}

	private static void CollectProbeScripts(Node node, List<Node> into)
	{
		int count = node.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			Node child = node.GetChild(i);
			// A node already queued for free is on its way out — counting it would make the
			// settle check report an orphan that is one frame from gone.
			if (child.IsQueuedForDeletion()) continue;
			if (child is LotScriptNode script && script.DisplayName == ProbeScriptName) into.Add(child);
			CollectProbeScripts(child, into);
		}
	}

	private static bool HasProbeScript(Node node)
	{
		List<Node> scripts = new List<Node>();
		CollectProbeScripts(node, scripts);
		return scripts.Count > 0;
	}

	private static void Check(string name, bool condition)
	{
		_checks++;
		if (condition)
		{
			GD.Print("[PlayerDataSelfTest] PASS  " + name);
		}
		else
		{
			_failures++;
			GD.PrintErr("[PlayerDataSelfTest] FAIL  " + name);
		}
	}

	/// <summary>A deterministic 22-character test id: 16 bytes with a distinguishing first byte,
	/// encoded canonically — the same shape a real Account ID or guest id has.</summary>
	private static string TestId(byte seed)
	{
		byte[] bytes = new byte[16];
		bytes[0] = seed;
		return AccountEncoding.Encode(bytes);
	}

	// --- the id shape ----------------------------------------------------------------------------

	private static void TestIdShape()
	{
		string accountId = AccountKey.Create().AccountId;
		Check("id: a real §3.12a Account ID is a valid player id", PlayerDataStore.LooksLikePlayerId(accountId));
		Check("id: a guest-shaped id is valid", PlayerDataStore.LooksLikePlayerId(TestId(7)));
		Check("id: a 21-character id is refused", !PlayerDataStore.LooksLikePlayerId(TestId(7).Substring(0, 21)));
		Check("id: a 23-character id is refused", !PlayerDataStore.LooksLikePlayerId(TestId(7) + "A"));
		Check("id: non-alphabet characters are refused", !PlayerDataStore.LooksLikePlayerId("!!!!!!!!!!!!!!!!!!!!!!"));
		Check("id: empty and null are refused",
			!PlayerDataStore.LooksLikePlayerId("") && !PlayerDataStore.LooksLikePlayerId(null));

		// A non-canonical spelling: same 16 bytes, but the final character carries non-zero padding
		// bits, so it must NOT be a second valid spelling of the same id.
		string nonCanonical = TestId(7).Substring(0, 21) + "B";
		Check("id: a non-canonical final character is refused", !PlayerDataStore.LooksLikePlayerId(nonCanonical));
	}

	// --- store semantics -------------------------------------------------------------------------

	private static void TestStoreSemantics()
	{
		PlayerDataStore store = new PlayerDataStore();
		string playerA = TestId(1);
		string playerB = TestId(2);

		Check("store: number set/get round-trips",
			store.Set(playerA, "coins", PlayerDataValue.OfNumber(50), out string _) &&
			store.TryGet(playerA, "coins", out PlayerDataValue coins) &&
			coins.Kind == PlayerDataValue.ValueKind.Number && coins.Number == 50);
		Check("store: string set/get round-trips",
			store.Set(playerA, "name", PlayerDataValue.OfString("Ada"), out string _) &&
			store.TryGet(playerA, "name", out PlayerDataValue name) &&
			name.Kind == PlayerDataValue.ValueKind.String && name.Text == "Ada");
		Check("store: boolean set/get round-trips",
			store.Set(playerA, "seen", PlayerDataValue.OfBoolean(true), out string _) &&
			store.TryGet(playerA, "seen", out PlayerDataValue seen) &&
			seen.Kind == PlayerDataValue.ValueKind.Boolean && seen.Flag);

		int beforeBytes = store.StoredBytes;
		Check("store: an overwrite changes the value and its kind",
			store.Set(playerA, "coins", PlayerDataValue.OfString("many"), out string _) &&
			store.TryGet(playerA, "coins", out PlayerDataValue replaced) &&
			replaced.Kind == PlayerDataValue.ValueKind.String && replaced.Text == "many" &&
			store.StoredBytes != beforeBytes);

		Check("store: players are isolated",
			store.Set(playerB, "coins", PlayerDataValue.OfNumber(1), out string _) &&
			store.TryGet(playerB, "coins", out PlayerDataValue bCoins) && bCoins.Number == 1 &&
			store.TryGet(playerA, "coins", out PlayerDataValue aCoins) && aCoins.Text == "many");
		Check("store: a missing key reads false", !store.TryGet(playerA, "nope", out PlayerDataValue _));
		Check("store: an unknown player reads false", !store.TryGet(TestId(9), "coins", out PlayerDataValue _));

		Check("store: removal reports true once and false afterwards",
			store.Remove(playerA, "coins") && !store.Remove(playerA, "coins") &&
			!store.TryGet(playerA, "coins", out PlayerDataValue _));

		Check("store: an invalid player id is refused with the shape message",
			!store.Set("short", "k", PlayerDataValue.OfNumber(1), out string idError) && idError.Contains("22"));
		Check("store: an empty key is refused",
			!store.Set(playerA, "", PlayerDataValue.OfNumber(1), out string keyError) && keyError.Contains("key"));
		Check("store: NaN is refused",
			!store.Set(playerA, "n", PlayerDataValue.OfNumber(double.NaN), out string nanError) && nanError.Contains("NaN"));
		Check("store: infinity is refused",
			!store.Set(playerA, "i", PlayerDataValue.OfNumber(double.PositiveInfinity), out string infError) && infError.Contains("NaN"));
	}

	// --- caps ------------------------------------------------------------------------------------

	private static void TestCaps()
	{
		PlayerDataStore store = new PlayerDataStore();
		string playerA = TestId(1);

		Check("caps: a key past the limit is refused",
			!store.Set(playerA, new string('k', PlayerDataStore.MaxKeyLength + 1), PlayerDataValue.OfNumber(1), out string keyError) &&
			keyError.Contains("longer"));
		Check("caps: a string at the limit is accepted",
			store.Set(playerA, "big", PlayerDataValue.OfString(new string('x', PlayerDataStore.MaxStringLength)), out string _));
		Check("caps: a string past the limit is refused",
			!store.Set(playerA, "bigger", PlayerDataValue.OfString(new string('x', PlayerDataStore.MaxStringLength + 1)), out string bigError) &&
			bigError.Contains("longer"));

		// Players: fill to the cap, watch the next refusal, then prove a removal frees a slot.
		int accepted = 0;
		string lastError = "";
		for (int i = 0; i < PlayerDataStore.MaxPlayers + 1; i++)
		{
			byte[] bytes = new byte[16];
			bytes[0] = (byte)(i & 0xFF);
			bytes[1] = (byte)((i >> 8) & 0xFF);
			if (store.Set(AccountEncoding.Encode(bytes), "k", PlayerDataValue.OfNumber(i), out string error)) accepted++;
			else lastError = error;
		}
		Check("caps: exactly " + PlayerDataStore.MaxPlayers + " players are accepted (got " + accepted + ")", accepted == PlayerDataStore.MaxPlayers);
		Check("caps: the next player is refused readably", lastError.Contains("maximum") && store.PlayerCount == PlayerDataStore.MaxPlayers);
		byte[] freshBytes = new byte[16];
		freshBytes[2] = 1;
		string freshPlayer = AccountEncoding.Encode(freshBytes);
		Check("caps: removing a player's last key frees a slot",
			store.Remove(TestId(0), "k") && store.Set(freshPlayer, "k", PlayerDataValue.OfNumber(1), out string _));

		// The total byte budget: fill with big strings until refused, then prove removal frees it.
		PlayerDataStore budget = new PlayerDataStore();
		string bigValue = new string('x', PlayerDataStore.MaxStringLength);
		int acceptedBig = 0;
		string budgetError = "";
		for (int i = 0; i < 200; i++)
		{
			if (budget.Set(playerA, "k" + i, PlayerDataValue.OfString(bigValue), out string error)) acceptedBig++;
			else { budgetError = error; break; }
		}
		Check("budget: entries are accepted until the total budget refuses (got " + acceptedBig + ")",
			acceptedBig > 0 && budgetError.Contains("budget"));
		Check("budget: the store never exceeded its cap", budget.StoredBytes <= PlayerDataStore.MaxTotalBytes);
		int held = budget.StoredBytes;
		Check("budget: an overwrite frees what the old value held",
			budget.Set(playerA, "k0", PlayerDataValue.OfNumber(0), out string _) && budget.StoredBytes < held);
		Check("budget: a removal frees budget for a new entry",
			budget.Remove(playerA, "k1") && budget.Set(playerA, "fresh", PlayerDataValue.OfNumber(1), out string _));
	}

	// --- the JSON round trip and the version gate ------------------------------------------------

	private static void TestJson()
	{
		PlayerDataStore store = new PlayerDataStore();
		string playerA = TestId(1);
		string playerB = TestId(2);
		store.Set(playerA, "coins", PlayerDataValue.OfNumber(50), out string _);
		store.Set(playerA, "name", PlayerDataValue.OfString("Ada"), out string _);
		store.Set(playerA, "seen", PlayerDataValue.OfBoolean(true), out string _);
		store.Set(playerA, "empty", PlayerDataValue.OfString(""), out string _);
		store.Set(playerA, "unicode", PlayerDataValue.OfString("ėmą ünïcode"), out string _);
		store.Set(playerB, "score", PlayerDataValue.OfNumber(1), out string _);

		string text = Json.Stringify(store.ToJson());
		bool loaded = PlayerDataStore.FromJson(text, out PlayerDataStore back, out int skipped, out string error);
		Check("json: round-trips (error: '" + error + "')", loaded && skipped == 0);
		if (loaded)
		{
			Check("json: the player count round-trips", back.PlayerCount == 2);
			Check("json: a number round-trips", back.TryGet(playerA, "coins", out PlayerDataValue coins) && coins.Number == 50);
			Check("json: a string round-trips", back.TryGet(playerA, "name", out PlayerDataValue name) && name.Text == "Ada");
			Check("json: a boolean round-trips", back.TryGet(playerA, "seen", out PlayerDataValue seen) && seen.Flag);
			Check("json: an empty string round-trips", back.TryGet(playerA, "empty", out PlayerDataValue empty) && empty.Text == "");
			Check("json: unicode round-trips", back.TryGet(playerA, "unicode", out PlayerDataValue uni) && uni.Text == "ėmą ünïcode");
			Check("json: the byte accounting is rebuilt", back.StoredBytes == store.StoredBytes);
		}

		Check("json: a newer version is refused readably",
			!PlayerDataStore.FromJson("{\"version\":2,\"players\":{}}", out PlayerDataStore _, out int _, out string versionError) &&
			versionError.Contains("version 2"));
		Check("json: non-object text is refused",
			!PlayerDataStore.FromJson("[1,2,3]", out PlayerDataStore _, out int _, out string shapeError) &&
			shapeError.Contains("JSON object"));
		Check("json: empty text is an empty store",
			PlayerDataStore.FromJson("", out PlayerDataStore emptyStore, out int _, out string _) && emptyStore.PlayerCount == 0);

		// Unknown value kinds and malformed ids are skipped, not fatal (the lenient-load rule).
		string playerId = TestId(3);
		string mixed = "{\"version\":1,\"players\":{\"" + playerId + "\":{\"a\":42,\"b\":null,\"c\":[1],\"d\":{\"x\":1},\"e\":\"ok\"},\"zzz\":{\"k\":1}}}";
		bool mixedOk = PlayerDataStore.FromJson(mixed, out PlayerDataStore mixedStore, out int mixedSkipped, out string mixedError);
		Check("json: a mixed document loads, skipping what it cannot read (error: '" + mixedError + "')",
			mixedOk && mixedSkipped == 4 &&
			mixedStore.TryGet(playerId, "a", out PlayerDataValue a) && a.Number == 42 &&
			mixedStore.TryGet(playerId, "e", out PlayerDataValue e) && e.Text == "ok");
	}

	// --- the archive entry, through the real writer and VFS --------------------------------------

	private static void TestArchiveRoundTrip()
	{
		PlayerDataStore store = new PlayerDataStore();
		string playerId = TestId(4);
		store.Set(playerId, "coins", PlayerDataValue.OfNumber(99), out string _);
		store.Set(playerId, "name", PlayerDataValue.OfString("backpack"), out string _);

		Check("archive: the writer accepts a lot with player data",
			LotArchiveWriter.Write(ScratchLot, new LotArchive.LotManifest(), new LotArchive.LotDocument(),
				null, null, store.ToJson(), out string writeError));

		LotVfs vfs = new LotVfs();
		try
		{
			if (!vfs.Open(ScratchLot, out string openError))
			{
				Check("archive: the scratch lot opens (error: '" + openError + "')", false);
			}
			else
			{
				Check("archive: the playerdata entry is present", vfs.HasEntry(LotArchive.PlayerDataEntry));
				string text = vfs.ReadEntryText(LotArchive.PlayerDataEntry);
				bool ok = PlayerDataStore.FromJson(text, out PlayerDataStore back, out int _, out string parseError);
				Check("archive: the entry parses back (error: '" + parseError + "')", ok &&
					back.TryGet(playerId, "coins", out PlayerDataValue coins) && coins.Number == 99 &&
					back.TryGet(playerId, "name", out PlayerDataValue name) && name.Text == "backpack");
			}
		}
		finally
		{
			vfs.Dispose();
		}

		Check("archive: a lot written without player data is accepted",
			LotArchiveWriter.Write(ScratchLot, new LotArchive.LotManifest(), new LotArchive.LotDocument(),
				null, null, null, out string _));
		LotVfs second = new LotVfs();
		try
		{
			Check("archive: the entry is omitted for an empty store",
				second.Open(ScratchLot, out string _) && !second.HasEntry(LotArchive.PlayerDataEntry));
		}
		finally
		{
			second.Dispose();
		}
	}

	// --- the bound verbs, against the live scene's API and store ---------------------------------

	private static void TestBoundVerbs(BuilderScene scene)
	{
		string me = scene.LocalPlayerId;
		Check("verbs: the local player id is a valid shape (" + me + ")", PlayerDataStore.LooksLikePlayerId(me));
		Check("verbs: GetLocalPlayer agrees with the scene", scene.Api.GetLocalPlayer() == me);

		// The display name is presentation (milestone 3.12a's fix): it may be "" on a machine with
		// no account, so pin the wiring, not a value — the verb must agree with the scene the same
		// way the id does.
		Check("verbs: GetLocalPlayerName agrees with the scene",
			scene.Api.GetLocalPlayerName() == scene.LocalPlayerName);

		Check("verbs: SetPlayerValue stores a number", scene.Api.SetPlayerValue(me, "coins", 50d));
		Check("verbs: GetPlayerValue reads it back", scene.Api.GetPlayerValue(me, "coins") is double coins && coins == 50d);
		Check("verbs: a string value round-trips", scene.Api.SetPlayerValue(me, "name", "Ada") &&
			scene.Api.GetPlayerValue(me, "name") is string name && name == "Ada");
		Check("verbs: a boolean value round-trips", scene.Api.SetPlayerValue(me, "seen", true) &&
			scene.Api.GetPlayerValue(me, "seen") is bool seen && seen);
		Check("verbs: a missing key reads null", scene.Api.GetPlayerValue(me, "missing") == null);
		Check("verbs: a removed value is gone", scene.Api.RemovePlayerValue(me, "coins") && !scene.Api.RemovePlayerValue(me, "coins"));

		Check("verbs: a non-primitive value is refused", !scene.Api.SetPlayerValue(me, "bad", new Godot.Collections.Dictionary()));
		Check("verbs: NaN is refused through the verb", !scene.Api.SetPlayerValue(me, "nan", double.NaN));
		Check("verbs: a peer id is refused with the §10.1 pointer", !scene.Api.SetPlayerValue(1, "coins", 5));
		Check("verbs: a bad id string is refused", !scene.Api.SetPlayerValue("short", "coins", 5) &&
			scene.Api.GetPlayerValue("short", "coins") == null);
		Check("verbs: an oversize string is refused",
			!scene.Api.SetPlayerValue(me, "big", new string('x', PlayerDataStore.MaxStringLength + 1)));
	}

	// --- the staged probe: a script writes in a session; save → reload keeps it -----------------

	private static BuilderScene _probeScene;
	private static LotObject _probePart;
	private static ulong _probeDeadlineMsec;
	private static int _probeStage; // 0 = not started, 1 = in session, 2 = left, 3 = cleanup settle, 4 = finished
	private static int _probeChecks;
	private static int _probeFailures;

	/// <summary>Whether the staged probe completed; the scene warns when a run ends too early.</summary>
	public static bool ProbeDone { get { return _probeStage >= 4; } }

	private const string ProbeScript =
		"function start()\n" +
		"    local me = Lot.GetLocalPlayer()\n" +
		"    Lot.Log('PDS me ' .. me)\n" +
		"    Lot.SetPlayerValue(me, 'coins', 50)\n" +
		"    Lot.SetPlayerValue(me, 'name', 'Ada')\n" +
		"    Lot.SetPlayerValue(me, 'seen', true)\n" +
		"    Lot.Log('PDS coins ' .. tostring(Lot.GetPlayerValue(me, 'coins') == 50))\n" +
		"    Lot.Log('PDS peer ' .. tostring(Lot.SetPlayerValue(1, 'coins', 5)))\n" +
		"    Lot.Log('PDS badid ' .. tostring(Lot.SetPlayerValue('short', 'coins', 5)))\n" +
		"end\n";

	private static void StartProbe(BuilderScene scene)
	{
		if (DisplayServer.GetName() != "headless")
		{
			_probeStage = 4;
			return;
		}
		_probeScene = scene;
		_probeChecks = 0;
		_probeFailures = 0;

		// A fixture script on a scratch part: the write happens through the real bound verbs inside
		// a real session, with the real sandbox and watchdog in the way.
		if (!DirAccess.DirExistsAbsolute(ProbeScriptDir)) DirAccess.MakeDirRecursiveAbsolute(ProbeScriptDir);
		FileAccess file = FileAccess.Open(ProbeScriptDir + "/" + ProbeScriptName, FileAccess.ModeFlags.Write);
		if (file != null)
		{
			file.StoreString(ProbeScript);
			file.Dispose();
		}

		_probePart = LotObject.Create(LotObjectKind.Cube, "PlayerDataProbePart", new Color(0.4f, 0.4f, 0.9f));
		_probePart.Position = new Vector3(180f, 1f, 180f);
		scene.LotRoot.AddChild(_probePart);
		scene.RegisterHandle(_probePart);
		LotScriptNode script = new LotScriptNode();
		script.ScriptPath = ProbeScriptDir + "/" + ProbeScriptName;
		script.DisplayName = ProbeScriptName;
		script.Name = "PDS_probe_lua";
		_probePart.AddChild(script);

		_probeDeadlineMsec = Time.GetTicksMsec() + ProbeMidWaitMsec;
		_probeStage = 1;
		GD.Print("[PlayerDataSelfTest] session probe started");
		scene.EnterTestMode(false);
	}

	private static void ProbeCheck(string name, bool condition)
	{
		_probeChecks++;
		if (condition)
		{
			GD.Print("[PlayerDataSelfTest] PASS  (session probe) " + name);
		}
		else
		{
			_probeFailures++;
			GD.PrintErr("[PlayerDataSelfTest] FAIL  (session probe) " + name);
		}
	}

	/// <summary>Advances the staged probe across real frames (called every frame from
	/// BuilderScene._Process): in-session → left the session → saved + reloaded → one settle frame
	/// (the cleanup's queue-free must land before the next suite enters Test mode).</summary>
	public static void TickProbe()
	{
		if (_probeStage == 0 || _probeStage >= 4) return;
		if (Time.GetTicksMsec() < _probeDeadlineMsec) return;

		if (_probeStage == 1)
		{
			_probeStage = 2;
			string me = _probeScene.LocalPlayerId;
			ProbeCheck("the script saw the local player id", HasLog("PDS me " + me));
			ProbeCheck("the script read back its number through the verbs", HasLog("PDS coins true"));
			ProbeCheck("a peer-id argument is refused in-session", HasLog("PDS peer false"));
			ProbeCheck("a bad id string is refused in-session", HasLog("PDS badid false"));
			ProbeCheck("the store holds the values after the script ran",
				_probeScene.PlayerData.TryGet(me, "coins", out PlayerDataValue coins) && coins.Number == 50 &&
				_probeScene.PlayerData.TryGet(me, "seen", out PlayerDataValue seen) && seen.Flag);

			_probeScene.ExitTestMode();
			_probeDeadlineMsec = Time.GetTicksMsec() + ProbeExitWaitMsec;
			return;
		}

		if (_probeStage == 3)
		{
			// Stage 3 (settle): the cleanup's queue-free has landed (the §3.6 deletion-window
			// lesson), so the orphan check is meaningful and "probe done" reaches the chain with
			// a clean tree — the next suite enters Test mode in the same _Process pass the check
			// would otherwise run in.
			ProbeCheck("cleanup: no probe script node is left for a later reload to warn about",
				!HasProbeScript(_probeScene.LotRoot));
			_probePart = null;
			_probeScene = null;
			_probeStage = 4;
			GD.Print("[PlayerDataSelfTest] session probe: " + _probeChecks + " checks, "
				+ _probeFailures + " failure(s).");
			return;
		}

		// Stage 2: out of the session. Durable ≠ authored (design doc D5): the values survive the
		// session boundary, and then a REAL save → reload round trip must bring them back — the
		// milestone's acceptance.
		_probeStage = 3;
		string playerId = _probeScene.LocalPlayerId;
		ProbeCheck("leaving Test mode did not roll the store back",
			_probeScene.PlayerData.TryGet(playerId, "name", out PlayerDataValue name) && name.Text == "Ada");

		System.Collections.Generic.List<string> missing = new System.Collections.Generic.List<string>();
		LotArchive.LotManifest manifest = LotSave.BuildManifest(_probeScene.LotRoot, "PlayerDataProbe", 0L);
		bool saved = LotSave.WriteScene(_probeScene, ScratchLot, manifest, missing, out string saveError);
		ProbeCheck("the lot saves with its player data (error: '" + saveError + "')", saved);
		if (saved)
		{
			bool loaded = LotLoad.Load(_probeScene, ScratchLot, out LotArchive.LotManifest _,
				out LotSceneLoad.LoadReport _, out string loadError);
			ProbeCheck("the lot reloads (error: '" + loadError + "')", loaded);
			if (loaded)
			{
				ProbeCheck("the value survived the lot reload (the acceptance)",
					_probeScene.PlayerData.TryGet(playerId, "coins", out PlayerDataValue coins) && coins.Number == 50 &&
					_probeScene.PlayerData.TryGet(playerId, "seen", out PlayerDataValue seen) && seen.Flag);
				ProbeCheck("the reloaded value is readable through the bound verb again",
					_probeScene.Api.GetPlayerValue(playerId, "name") is string reloadedName && reloadedName == "Ada");
			}
		}

		CleanupScratch();
		// The reload recreated the saved probe part and its script entry, and the fixture file is
		// now deleted — left in place, every later script reload would warn about it.
		RemoveFixtureNodes(_probeScene);
		// The queue-free lands at the end of this frame: the orphan check and the completion
		// signal wait one frame (stage 3 above).
		_probeDeadlineMsec = Time.GetTicksMsec() + 50;
	}

	private static bool HasLog(string exact)
	{
		int start = Math.Max(0, LotLog.Count - 200);
		for (int i = start; i < LotLog.Count; i++)
		{
			if (LotLog.At(i).Text == exact) return true;
		}
		return false;
	}
}
