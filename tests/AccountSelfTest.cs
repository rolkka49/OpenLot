using System;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Godot;

/// <summary>
/// Verification for the account identity primitive (milestone 3.12a). The first check is the
/// spike the whole design rests on — a P-256 keypair rebuilt from its 32-byte seed alone — and it
/// runs on every suite run, so a platform/runtime that cannot do it fails loudly instead of
/// producing unimportable accounts. The rest pins the 50-character format, the account id, the
/// challenge/sign/verify contract (including cross-session replay rejection), the store's
/// round-trip on scratch files, the session flows, and the §1.1 boundary (no signing operation is
/// reachable from Lua). Runs with the other root suites from BuilderScene under #if DEBUG.
/// </summary>
public static class AccountSelfTest
{
	private const string ScratchPath = "user://account_selftest.cfg";
	private const string ScratchImportPath = "user://account_selftest_import.cfg";
	private const string ScratchResetPath = "user://account_selftest_reset.cfg";
	private static int _checks;
	private static int _failures;

	public static int Run()
	{
		_checks = 0;
		_failures = 0;

		CleanupScratchFiles();

		TestSeedOnlySpike();
		TestKeyString();
		TestAccountId();
		TestChallenge();
		TestStore();
		TestSessionFlows();
		TestReset();
		TestLuaBoundary();

		CleanupScratchFiles();

		GD.Print("[AccountSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
		return _failures;
	}

	private static void CleanupScratchFiles()
	{
		RemoveIfPresent(ScratchPath);
		RemoveIfPresent(ScratchImportPath);
		RemoveIfPresent(ScratchResetPath);
	}

	private static void RemoveIfPresent(string path)
	{
		if (Godot.FileAccess.FileExists(path)) DirAccess.RemoveAbsolute(path);
	}

	private static void Check(string name, bool condition)
	{
		_checks++;
		if (condition)
		{
			GD.Print("[AccountSelfTest] PASS  " + name);
		}
		else
		{
			_failures++;
			GD.PrintErr("[AccountSelfTest] FAIL  " + name);
		}
	}

	// --- the spike: a keypair rebuilt from its seed alone (the P-256/BCL gate) -------------------

	private static void TestSeedOnlySpike()
	{
		using (ECDsa generated = ECDsa.Create(ECCurve.NamedCurves.nistP256))
		{
			ECParameters full = generated.ExportParameters(true);
			Check("spike: a generated seed is 32 bytes", full.D != null && full.D.Length == 32);

			bool created = AccountKey.TryFromSeed(full.D, out AccountKey key, out string error);
			Check("spike: the keypair rebuilds from the seed alone (error: '" + error + "')", created && key != null);
			if (!created) return;

			byte[] expected = new byte[65];
			expected[0] = 0x04;
			Array.Copy(full.Q.X, 0, expected, 1, 32);
			Array.Copy(full.Q.Y, 0, expected, 33, 32);
			Check("spike: the derived public key matches the generated keypair",
				Convert.ToHexString(key.PublicKey) == Convert.ToHexString(expected));

			byte[] message = Encoding.UTF8.GetBytes("openlot-account-spike");
			byte[] signature = key.Sign(message);
			Check("spike: sign/verify round-trips", AccountKey.Verify(key.PublicKey, message, signature));
			Check("spike: a tampered message fails verification",
				!AccountKey.Verify(key.PublicKey, Encoding.UTF8.GetBytes("openlot-account-spike!"), signature));
		}
	}
	// --- the 50-character key string: shape, canonical encoding, checksum and version ------------

	private static void TestKeyString()
	{
		AccountKey key = AccountKey.Create();
		Check("key: exactly 50 characters", key.KeyString.Length == AccountKey.KeyStringLength);
		Check("key: the alphabet is base64url", IsBase64Url(key.KeyString));

		bool parsed = AccountKey.TryFromKeyString(key.KeyString, out AccountKey again, out string parseError);
		Check("key: round-trips through its string (error: '" + parseError + "')",
			parsed && again.KeyString == key.KeyString && again.AccountId == key.AccountId);

		Check("key: null is refused",
			!AccountKey.TryFromKeyString(null, out AccountKey _, out string nullError) && nullError.Contains("50"));
		Check("key: empty is refused", !AccountKey.TryFromKeyString("", out AccountKey _, out string _));
		Check("key: 49 characters are refused",
			!AccountKey.TryFromKeyString(key.KeyString.Substring(0, 49), out AccountKey _, out string shortError) && shortError.Contains("50"));
		Check("key: 51 characters are refused",
			!AccountKey.TryFromKeyString(key.KeyString + "A", out AccountKey _, out string longError) && longError.Contains("50"));

		string badChar = key.KeyString.Substring(0, 10) + "!" + key.KeyString.Substring(11);
		Check("key: a non-alphabet character is refused",
			!AccountKey.TryFromKeyString(badChar, out AccountKey _, out string charError) && charError.Contains("base64url"));

		string swapped = SwapFirstDifferingPair(key.KeyString);
		Check("key: two swapped characters are refused by the checksum",
			swapped != null && !AccountKey.TryFromKeyString(swapped, out AccountKey _, out string swapError) && swapError.Contains("checksum"));

		// The final character of a 50-character string carries 2 data bits + 4 zero padding bits.
		// Exactly four sextets have zero padding bits (0/16/32/48); only the one matching the
		// checksum is a key, so the last character is pinned to a single valid value.
		string prefix = key.KeyString.Substring(0, 49);
		int decodable = 0;
		int acceptable = 0;
		for (int i = 0; i < 64; i++)
		{
			string candidate = prefix + FromSextet(i);
			if (AccountEncoding.TryDecode(candidate, out byte[] _, out string _)) decodable++;
			if (AccountKey.TryFromKeyString(candidate, out AccountKey _, out string _)) acceptable++;
		}
		Check("encoding: exactly 4 final characters are canonically decodable (" + decodable + ")", decodable == 4);
		Check("key: exactly one of them passes the checksum (" + acceptable + ")", acceptable == 1);

		// A future version with a correctly recomputed checksum is refused by the version gate.
		AccountEncoding.TryDecode(key.KeyString, out byte[] versionPayload, out string _);
		versionPayload[0] = 2;
		Array.Copy(SHA256.HashData(versionPayload.AsSpan(0, 33)), 0, versionPayload, 33, 4);
		Check("key: a future version with a valid checksum is refused by the version gate",
			!AccountKey.TryFromKeyString(AccountEncoding.Encode(versionPayload), out AccountKey _, out string versionError) &&
			versionError.Contains("version 2"));

		// An altered seed whose checksum no longer covers it is refused by the checksum.
		AccountEncoding.TryDecode(key.KeyString, out byte[] tamperPayload, out string _);
		tamperPayload[5] ^= 0x40;
		Check("key: an altered seed with a stale checksum is refused",
			!AccountKey.TryFromKeyString(AccountEncoding.Encode(tamperPayload), out AccountKey _, out string tamperError) &&
			tamperError.Contains("checksum"));
	}
	// --- the account id: derived, stable, differentiating ----------------------------------------

	private static void TestAccountId()
	{
		AccountKey first = AccountKey.Create();
		AccountKey second = AccountKey.Create();
		Check("id: 22 characters", first.AccountId.Length == AccountKey.AccountIdLength);
		Check("id: the alphabet is base64url", IsBase64Url(first.AccountId));
		Check("id: verifies against its public key", AccountKey.VerifyAccountId(first.AccountId, first.PublicKey));
		Check("id: two keys differ", first.AccountId != second.AccountId);
		Check("id: a null id never verifies", !AccountKey.VerifyAccountId(null, first.PublicKey));

		byte[] tamperedKey = (byte[])first.PublicKey.Clone();
		tamperedKey[10] ^= 0x01;
		Check("id: a tampered public key no longer matches the id",
			!AccountKey.VerifyAccountId(first.AccountId, tamperedKey));

		AccountKey.TryFromKeyString(first.KeyString, out AccountKey reparsed, out string _);
		Check("id: stable across a string round-trip", reparsed != null && reparsed.AccountId == first.AccountId);
	}
	// --- the challenge contract: known-answer format + replay rejection --------------------------

	private static void TestChallenge()
	{
		byte[] nonce = new byte[] { 0x01, 0x02, 0x03, 0x04 };
		byte[] message = AccountChallenge.Build("lot-42", nonce, 1234567890L);
		Check("challenge: the known-answer format is exact",
			Encoding.UTF8.GetString(message) == "OPENLOT-AUTH-v1\nlot:lot-42\nnonce:AQIDBA\nexpiry:1234567890");

		byte[] otherNonce = new byte[] { 0x01, 0x02, 0x03, 0x05 };
		Check("challenge: a different nonce changes the message",
			Convert.ToHexString(AccountChallenge.Build("lot-42", otherNonce, 1234567890L)) != Convert.ToHexString(message));
		Check("challenge: a different expiry changes the message",
			Convert.ToHexString(AccountChallenge.Build("lot-42", nonce, 1234567891L)) != Convert.ToHexString(message));
		Check("challenge: a different lot changes the message",
			Convert.ToHexString(AccountChallenge.Build("lot-43", nonce, 1234567890L)) != Convert.ToHexString(message));

		AccountKey key = AccountKey.Create();
		AccountKey other = AccountKey.Create();
		byte[] signature = key.Sign(message);
		Check("auth: a signature verifies against its challenge", AccountKey.Verify(key.PublicKey, message, signature));
		Check("auth: replay in another session (nonce) is rejected",
			!AccountKey.Verify(key.PublicKey, AccountChallenge.Build("lot-42", otherNonce, 1234567890L), signature));
		Check("auth: replay on another lot is rejected",
			!AccountKey.Verify(key.PublicKey, AccountChallenge.Build("lot-43", nonce, 1234567890L), signature));
		Check("auth: another key's signature is rejected", !AccountKey.Verify(other.PublicKey, message, signature));
		Check("auth: malformed public keys and signatures verify false, never throw",
			!AccountKey.Verify(new byte[65], message, signature) &&
			!AccountKey.Verify(key.PublicKey, message, new byte[8]) &&
			!AccountKey.Verify(null, message, signature));
	}
	// --- the store: scratch-file round-trip, corruption refusal, delete -------------------------

	private static void TestStore()
	{
		Check("store: a missing file loads as no account",
			!AccountStore.TryLoad(ScratchPath, out AccountRecord missing, out string missingError) &&
			missing == null && missingError.Length == 0);

		AccountKey key = AccountKey.Create();
		AccountRecord record = new AccountRecord { Key = key, DisplayName = "Ada", CreatedUnix = 1234567890L };
		Check("store: save succeeds", AccountStore.Save(ScratchPath, record, out string _));
		Check("store: the file now exists", AccountStore.Exists(ScratchPath));

		bool loaded = AccountStore.TryLoad(ScratchPath, out AccountRecord back, out string loadError);
		Check("store: loads back (error: '" + loadError + "')", loaded);
		if (loaded)
		{
			Check("store: the key string round-trips", back.Key.KeyString == key.KeyString);
			Check("store: the account id round-trips", back.Key.AccountId == key.AccountId);
			Check("store: the display name round-trips", back.DisplayName == "Ada");
			Check("store: the creation stamp round-trips", back.CreatedUnix == 1234567890L);
		}

		ConfigFile corrupt = new ConfigFile();
		corrupt.SetValue("account", "key", "not-a-key");
		Check("store: a corrupt file could be written for the probe", corrupt.Save(ScratchPath) == Error.Ok);
		Check("store: a corrupt key is refused with a readable message",
			!AccountStore.TryLoad(ScratchPath, out AccountRecord _, out string corruptError) && corruptError.Contains("invalid"));

		AccountStore.Delete(ScratchPath);
		Check("store: delete removes the file", !AccountStore.Exists(ScratchPath));
	}
	// --- the session flows: register / unlock / import / lock with their guard rules -------------

	private static void TestSessionFlows()
	{
		AccountSession session = new AccountSession(ScratchPath);
		Check("session: starts locked", !session.IsUnlocked && session.AccountId.Length == 0 && session.DisplayName.Length == 0);

		bool unlockedEarly = session.Unlock(out string noAccountMessage);
		Check("session: unlocking before any account exists refuses with guidance",
			!unlockedEarly && !session.IsUnlocked && noAccountMessage.Contains("register"));

		Check("session: register requires a display name",
			!session.Register("   ", out string noNameMessage, out string _) && noNameMessage.Contains("display name"));

		bool registered = session.Register("Ada", out string registerMessage, out string revealKey);
		Check("session: register creates and unlocks (message: '" + registerMessage + "')", registered && session.IsUnlocked);
		Check("session: the reveal is the key string",
			revealKey.Length == AccountKey.KeyStringLength && revealKey == session.Key.KeyString);
		Check("session: register reports the account id", registerMessage.Contains(session.AccountId));
		Check("session: double register refuses and reveals nothing",
			!session.Register("Eve", out string doubleMessage, out string doubleReveal) &&
			doubleReveal.Length == 0 && doubleMessage.Contains("already logged in"));

		AccountSession reopening = new AccountSession(ScratchPath);
		Check("session: register on a device that holds an account refuses (no clobber)",
			!reopening.Register("Eve", out string takenMessage, out string takenReveal) &&
			takenReveal.Length == 0 && takenMessage.Contains("already holds"));
		// The refusal must be actionable: it names the stored account, the name it did NOT apply, and
		// the way forward — the three things that made the first cut read as a mystery.
		Check("session: the no-clobber refusal names the account, the refused name and the fix",
			takenMessage.Contains("Ada") && takenMessage.Contains("'Eve'") && takenMessage.Contains("login"));
		Check("session: login unlocks the stored account",
			reopening.Unlock(out string welcome) && reopening.IsUnlocked && welcome.Contains("Ada"));
		Check("session: unlock twice refuses",
			!reopening.Unlock(out string twiceMessage) && twiceMessage.Contains("already logged in"));
		Check("session: logout locks and keeps the file",
			reopening.Lock(out string _) && !reopening.IsUnlocked && AccountStore.Exists(ScratchPath));
		Check("session: logout twice refuses",
			!reopening.Lock(out string doubleLogout) && doubleLogout.Contains("not logged in"));
		Check("session: a locked session unlocks again", reopening.Unlock(out string _) && reopening.IsUnlocked);

		AccountSession importer = new AccountSession(ScratchImportPath);
		Check("session: a bad key is refused with the length message",
			!importer.Import("too-short", "", out string badKeyMessage) && badKeyMessage.Contains("50") && !importer.IsUnlocked);

		AccountKey imported = AccountKey.Create();
		Check("session: import takes the key",
			importer.Import(imported.KeyString, "", out string importMessage) && importer.IsUnlocked && importer.AccountId == imported.AccountId);
		Check("session: an unnamed import gets a neutral display name",
			importer.DisplayName == "player-" + imported.AccountId.Substring(0, 6));
		Check("session: the import is reported", importMessage.Contains(imported.AccountId));

		AccountSession reopeningImport = new AccountSession(ScratchImportPath);
		Check("session: the imported account unlocks on the next run",
			reopeningImport.Unlock(out string importedWelcome) && reopeningImport.AccountId == imported.AccountId && importedWelcome.Contains("player-"));

		// Re-pasting the SAME key is that account, not a clash: it unlocks, and the stored profile is
		// kept (a login must not rewrite presentation). This is the "I pasted my own key" case.
		AccountSession sameKey = new AccountSession(ScratchImportPath);
		Check("session: importing this device's own key unlocks instead of clashing",
			sameKey.Import(imported.KeyString, "Renamed", out string sameMessage) &&
			sameKey.IsUnlocked && sameKey.AccountId == imported.AccountId);
		Check("session: the re-paste keeps the stored display name and reports the account",
			sameKey.DisplayName == "player-" + imported.AccountId.Substring(0, 6) &&
			sameMessage.Contains(imported.AccountId));

		AccountSession clobber = new AccountSession(ScratchImportPath);
		Check("session: import refuses to clobber the stored account",
			!clobber.Import(AccountKey.Create().KeyString, "Bob", out string clobberMessage) && clobberMessage.Contains("already holds"));
	}
	/// <summary>
	/// The "I lost my key" reset (the `unregister` command's session half): the precondition query the
	/// terminal checks before it asks anything, and Remove's effect — file gone, session locked, and
	/// the device able to register a fresh account straight afterwards.
	/// </summary>
	private static void TestReset()
	{
		AccountSession fresh = new AccountSession(ScratchResetPath);
		Check("reset: nothing to remove on a device with no account",
			!fresh.Remove(out string emptyMessage) && emptyMessage.Contains("No account"));
		Check("reset: a device with no account can store one", fresh.CanStoreNewAccount(out string _));
		Check("startup: a device with no account does not sign in by itself",
			!AccountSession.UnlocksAtStartup(ScratchResetPath));
		Check("reset: register works on the scratch device", fresh.Register("Zoe", out string _, out string _) && fresh.IsUnlocked);
		Check("startup: a device that now holds an account signs in by itself",
			AccountSession.UnlocksAtStartup(ScratchResetPath));

		AccountSession locked = new AccountSession(ScratchResetPath);
		Check("reset: a device that holds an account refuses a new one before any prompt",
			!locked.CanStoreNewAccount(out string blocked) && blocked.Contains("already holds"));

		AccountSession unlocked = new AccountSession(ScratchResetPath);
		unlocked.Unlock(out string _);
		Check("reset: an unlocked session refuses a new account too",
			!unlocked.CanStoreNewAccount(out string busy) && busy.Contains("already logged in"));

		AccountSession remover = new AccountSession(ScratchResetPath);
		remover.Unlock(out string _);
		Check("reset: Remove deletes the file and locks the session",
			remover.Remove(out string removedMessage) && !remover.IsUnlocked && !AccountStore.Exists(ScratchResetPath));
		Check("reset: the removal is reported with the way forward", removedMessage.Contains("removed"));
		Check("startup: the reset stops the automatic sign-in",
			!AccountSession.UnlocksAtStartup(ScratchResetPath));

		AccountSession after = new AccountSession(ScratchResetPath);
		Check("reset: the account is really gone (unlock refuses with guidance)",
			!after.Unlock(out string goneMessage) && goneMessage.Contains("register"));
		Check("reset: the device can register a fresh account afterwards",
			after.Register("Zoe2", out string _, out string _) && after.IsUnlocked);

		AccountStore.Delete(ScratchResetPath);
	}
	// --- the §1.1 boundary: no signing/key operation is reachable from Lua -----------------------

	private static void TestLuaBoundary()
	{
		MethodInfo[] methods = typeof(LotLuaApi).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
		int forbidden = 0;
		string offender = "";
		for (int i = 0; i < methods.Length; i++)
		{
			string name = methods[i].Name;
			if (name.IndexOf("Sign", StringComparison.OrdinalIgnoreCase) >= 0 ||
				name.IndexOf("Seed", StringComparison.OrdinalIgnoreCase) >= 0 ||
				name.IndexOf("PrivateKey", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				forbidden++;
				offender = name;
			}
		}
		// A tripwire, deliberately: if a future bound method trips this, re-read the boundary
		// rule before widening it — signing stays a core-only, fixed-schema operation.
		Check("boundary: none of the " + methods.Length + " bound Lua methods signs or exposes key material" +
			(forbidden > 0 ? " (offender: " + offender + ")" : ""), forbidden == 0);
	}
	// --- helpers ---------------------------------------------------------------------------------

	private static bool IsBase64Url(string text)
	{
		for (int i = 0; i < text.Length; i++)
		{
			char c = text[i];
			bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
			if (!ok) return false;
		}
		return true;
	}

	private static char FromSextet(int value)
	{
		if (value < 26) return (char)('A' + value);
		if (value < 52) return (char)('a' + value - 26);
		if (value < 62) return (char)('0' + value - 52);
		return value == 62 ? '-' : '_';
	}

	/// <summary>Swaps the first differing adjacent pair from index 2 on — the first two characters
	/// carry the version byte, and the swap under test must fail the checksum, not the version.</summary>
	private static string SwapFirstDifferingPair(string text)
	{
		for (int i = 2; i + 1 < text.Length; i++)
		{
			if (text[i] != text[i + 1])
			{
				char[] chars = text.ToCharArray();
				char carried = chars[i];
				chars[i] = chars[i + 1];
				chars[i + 1] = carried;
				return new string(chars);
			}
		}
		return null;
	}
}
