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
			message = "You are already logged in (account " + AccountId + ").";
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
				? "This device already holds an account (" + existing.Key.AccountId + " as \"" + existing.DisplayName + "\"). Use 'login' to unlock it."
				: "This device has an account file that cannot be read (" + loadError + "). Remove it by hand before registering.";
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
	/// Imports an account from its 50-character key string (the cross-device move) and stores it
	/// as this device's account. Same no-clobber rule as register; a missing display name gets a
	/// neutral local one because the key carries no profile (the design doc's stated limitation).
	/// </summary>
	public bool Import(string keyString, string displayName, out string message)
	{
		message = "";
		if (IsUnlocked)
		{
			message = "You are already logged in (account " + AccountId + ").";
			return false;
		}
		if (AccountStore.Exists(_filePath))
		{
			message = AccountStore.TryLoad(_filePath, out AccountRecord existing, out string loadError)
				? "This device already holds an account (" + existing.Key.AccountId + "). Use 'login' to unlock it."
				: "This device has an account file that cannot be read (" + loadError + "). Remove it by hand before importing.";
			return false;
		}
		if (!AccountKey.TryFromKeyString(keyString, out AccountKey key, out string keyError))
		{
			message = keyError;
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
