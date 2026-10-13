using System;

/// <summary>
/// The client's active account session (milestone 3.12a): locked (no key) or unlocked, with the
/// register / unlock / import / lock flows and their guard rules in one testable place. The
/// Terminal is a thin shell over this (print the message, print the one-time key reveal); §10.1's
/// handshake and §3.12's data keying will read <see cref="Key"/>/<see cref="AccountId"/> from the
/// same session.
///
/// All messages are pre-worded and carry no color or prefix — the caller decides how to surface
/// them. The file path is explicit so the self-test drives scratch files and can never touch a
/// real account.
/// </summary>
public sealed class AccountSession
{
	private readonly string _filePath;

	/// <summary>Unlocked account key, or null while locked.</summary>
	public AccountKey Key { get; private set; }

	/// <summary>The local display name (presentation only — never an identity).</summary>
	public string DisplayName { get; private set; } = "";

	public bool IsUnlocked { get { return Key != null; } }

	/// <summary>The public account id, or "" while locked.</summary>
	public string AccountId { get { return Key != null ? Key.AccountId : ""; } }

	/// <summary>The account file this session talks to (the real one unless a test overrides it).</summary>
	public string FilePath { get { return _filePath; } }

	public AccountSession(string filePath = AccountStore.DefaultPath)
	{
		_filePath = string.IsNullOrEmpty(filePath) ? AccountStore.DefaultPath : filePath;
	}

	/// <summary>
	/// Whether a brand-new account could be stored right now: the session is locked and this device
	/// holds none. <paramref name="reason"/> is the same refusal <see cref="Register"/>/<see cref="Import"/>
	/// would print, so an interactive flow (the terminal asking "what name?") can check <em>before</em>
	/// asking a question it would then refuse.
	/// </summary>
	public bool CanStoreNewAccount(out string reason)
	{
		if (IsUnlocked)
		{
			reason = "You are already logged in (account " + AccountId + "). Run 'logout' first to register or import another account.";
			return false;
		}
		if (AccountStore.Exists(_filePath))
		{
			reason = AccountStore.TryLoad(_filePath, out AccountRecord existing, out string _)
				? NoClobberMessage(existing, "", "imported")
				: "This device has an account file that cannot be read. Remove " + _filePath + " first.";
			return false;
		}
		reason = "";
		return true;
	}

	/// <summary>
	/// True when a client should sign in at startup without asking for anything: this device holds an
	/// account, and nothing protects it. v1 stores the key plaintext (design doc D6), so a stored
	/// account is open by definition — making the user retype <c>login</c> on every launch is friction
	/// that protects nothing. A passphrase (a later surface) is the one thing that would make this
	/// false, which is exactly why the rule lives here rather than inline in the terminal.
	///
	/// <c>logout</c> still locks for the rest of the session; this only decides what a fresh launch
	/// starts as.
	/// </summary>
	public static bool UnlocksAtStartup(string filePath)
	{
		return AccountStore.Exists(filePath);
	}

	/// <summary>
	/// Creates the device's account: generate the key, write it, unlock, and hand the key string
	/// back once for the reveal. Refuses when the device already holds an account (overwriting
	/// would destroy a key that cannot be re-created) — a switch-account flow is a later surface.
	/// </summary>
	public bool Register(string displayName, out string message, out string revealKey)
	{
		message = "";
		revealKey = "";
		if (IsUnlocked)
		{
			message = "You are already logged in (account " + AccountId + "). Run 'logout' first to register or import another account.";
			return false;
		}
		displayName = (displayName ?? "").Trim();
		if (displayName.Length == 0)
		{
			message = "A display name is required: register <display name>";
			return false;
		}
		if (AccountStore.Exists(_filePath))
		{
			message = AccountStore.TryLoad(_filePath, out AccountRecord existing, out string loadError)
				? NoClobberMessage(existing, displayName, "registered")
				: "This device has an account file that cannot be read (" + loadError + "). Remove " + _filePath + " before registering.";
			return false;
		}

		AccountKey key = AccountKey.Create();
		AccountRecord record = new AccountRecord
		{
			Key = key,
			DisplayName = displayName,
			CreatedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
		};
		if (!AccountStore.Save(_filePath, record, out string saveError))
		{
			message = saveError;
			return false;
		}
		Key = key;
		DisplayName = displayName;
		revealKey = key.KeyString;
		message = "Account " + key.AccountId + " created.";
		return true;
	}

	/// <summary>Unlocks the account already stored on this device.</summary>
	public bool Unlock(out string message)
	{
		message = "";
		if (IsUnlocked)
		{
			message = "You are already logged in.";
			return false;
		}
		if (!AccountStore.TryLoad(_filePath, out AccountRecord record, out string error))
		{
			message = error.Length > 0
				? error
				: "No account exists on this device. Use 'register <name>' to create one, or 'login <key>' to import one.";
			return false;
		}
		Key = record.Key;
		DisplayName = record.DisplayName;
		message = "Welcome back, " + DisplayName + ". Account " + AccountId + ".";
		return true;
	}

	/// <summary>
	/// Imports an account from its 50-character key string (the cross-device move) and stores it as
	/// this device's account.
	///
	/// Passing <b>this device's own key</b> is not an import at all — it is the same account, so
	/// nothing would be destroyed and no name is needed: it unlocks and reports the match. A key for a
	/// <em>different</em> account on a device that already holds one is refused (the no-clobber rule),
	/// because overwriting would destroy a key that can never be re-created. Otherwise a missing
	/// display name gets a neutral local one, because the key carries no profile (the design doc's
	/// stated limitation).
	/// </summary>
	public bool Import(string keyString, string displayName, out string message)
	{
		message = "";
		if (IsUnlocked)
		{
			message = "You are already logged in (account " + AccountId + "). Run 'logout' first to register or import another account.";
			return false;
		}

		// Validate the key before anything else: a typo gets the key error, never a clobber complaint.
		if (!AccountKey.TryFromKeyString(keyString, out AccountKey key, out string keyError))
		{
			message = keyError;
			return false;
		}

		if (AccountStore.Exists(_filePath))
		{
			if (!AccountStore.TryLoad(_filePath, out AccountRecord existing, out string loadError))
			{
				message = "This device has an account file that cannot be read (" + loadError + "). Remove " + _filePath + " before importing.";
				return false;
			}
			if (existing.Key.AccountId == key.AccountId)
			{
				// Same account: unlock it. The stored profile is kept as-is — a login must not rewrite
				// presentation, and re-pasting a key must never read as a clash when it is the same
				// identity (the key IS the account).
				Key = existing.Key;
				DisplayName = existing.DisplayName;
				message = "That key is this device's account (" + key.AccountId + "). Welcome back, " + DisplayName + ".";
				return true;
			}
			message = NoClobberMessage(existing, "", "imported");
			return false;
		}

		displayName = (displayName ?? "").Trim();
		if (displayName.Length == 0) displayName = "player-" + key.AccountId.Substring(0, 6);

		AccountRecord record = new AccountRecord
		{
			Key = key,
			DisplayName = displayName,
			CreatedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
		};
		if (!AccountStore.Save(_filePath, record, out string saveError))
		{
			message = saveError;
			return false;
		}
		Key = key;
		DisplayName = displayName;
		message = "Imported account " + key.AccountId + " as \"" + displayName + "\".";
		return true;
	}

	/// <summary>
	/// One factory for the "this device already holds an account" refusal, so `register` and
	/// `login &lt;key&gt;` word it identically. It says the three things the user actually needs: the
	/// name they typed was <b>not</b> applied, why the operation is refused (re-registering would
	/// destroy a key that can never be re-created), and the two ways forward. Account switching is a
	/// deliberately deferred surface (design doc), so the only reset is removing the account file —
	/// and that path is named rather than left to be guessed.
	/// </summary>
	private string NoClobberMessage(AccountRecord existing, string requestedName, string verb)
	{
		string requested = string.IsNullOrEmpty(requestedName)
			? ""
			: ", so '" + requestedName + "' was not " + verb;
		return "This device already holds an account (" + existing.Key.AccountId +
			", named \"" + existing.DisplayName + "\")" + requested + ".\n" +
			"Re-registering would destroy a key that can never be re-created, so it is refused.\n" +
			"Run 'login' to use that account, or remove " + _filePath + " to start a different one.";
	}

	/// <summary>
	/// Deletes this device's account file — the "I lost my key" reset. Irreversible: the key can never
	/// be re-created, and per-player lot data keyed to this account stays inside the lots but becomes
	/// unreachable. Refuses when nothing is stored; a success also locks the session, so the terminal's
	/// next step is asking for a new account rather than believing it is still signed in.
	/// </summary>
	public bool Remove(out string message)
	{
		if (!AccountStore.Exists(_filePath))
		{
			message = "No account exists on this device - nothing to remove.";
			return false;
		}
		AccountStore.Delete(_filePath);
		Key = null;
		DisplayName = "";
		message = "Account removed. Run 'register <name>' to create a new one, or 'login <key>' to import one you kept.";
		return true;
	}

	/// <summary>Locks the session. The account stays on disk — 'login' unlocks it again.</summary>
	public bool Lock(out string message)
	{
		if (!IsUnlocked)
		{
			message = "You are not logged in.";
			return false;
		}
		Key = null;
		DisplayName = "";
		message = "Logged out. The account stays on this device - 'login' unlocks it again.";
		return true;
	}
}
