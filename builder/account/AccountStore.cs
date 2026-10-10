using Godot;

/// <summary>
/// One stored account: the key plus the local profile the terminal shows. A thin record on
/// purpose — the key IS the account; the display name is presentation and can change without
/// touching identity.
/// </summary>
public sealed class AccountRecord
{
	public AccountKey Key;
	public string DisplayName = "";
	public long CreatedUnix;
}

/// <summary>
/// The on-disk account (milestone 3.12a): `user://account.cfg`, written with Godot's ConfigFile
/// like the wallpaper setting (one config format; §5.4 will adopt this file too).
///
/// Stored plaintext by design for v1 — the file is the account, like a saved password without a
/// master password; the design doc records the at-rest options (passphrase, OS keystore) as later
/// decisions. Every operation takes its path explicitly, so the self-test drives scratch files
/// and can never touch a real account.
/// </summary>
public static class AccountStore
{
	public const string DefaultPath = "user://account.cfg";
	private const string Section = "account";
	private const string KeyField = "key";
	private const string NameField = "display_name";
	private const string CreatedField = "created_unix";

	public static bool Exists(string path)
	{
		return Godot.FileAccess.FileExists(path);
	}

	/// <summary>
	/// Loads the stored account. False with an empty error when nothing is stored; false with a
	/// readable message when the file exists but does not hold a valid account (a corrupted key
	/// must not resurrect as some other identity).
	/// </summary>
	public static bool TryLoad(string path, out AccountRecord record, out string error)
	{
		record = null;
		if (!Godot.FileAccess.FileExists(path))
		{
			error = "";
			return false;
		}

		ConfigFile config = new ConfigFile();
		if (config.Load(path) != Error.Ok)
		{
			error = "the account file could not be read (" + path + ")";
			return false;
		}

		string keyString = config.GetValue(Section, KeyField, "").AsString();
		if (!AccountKey.TryFromKeyString(keyString, out AccountKey key, out string keyError))
		{
			error = "the stored key is invalid: " + keyError;
			return false;
		}

		record = new AccountRecord
		{
			Key = key,
			DisplayName = config.GetValue(Section, NameField, "").AsString(),
			CreatedUnix = config.GetValue(Section, CreatedField, 0L).AsInt64()
		};
		error = "";
		return true;
	}

	/// <summary>Writes the account, replacing what is there. False with a readable error on failure.</summary>
	public static bool Save(string path, AccountRecord record, out string error)
	{
		ConfigFile config = new ConfigFile();
		// Re-read first so keys a future settings writer added are not dropped (the same
		// discipline the wallpaper setting uses).
		if (Godot.FileAccess.FileExists(path)) config.Load(path);
		config.SetValue(Section, KeyField, record.Key.KeyString);
		config.SetValue(Section, NameField, record.DisplayName);
		config.SetValue(Section, CreatedField, record.CreatedUnix);
		Error result = config.Save(path);
		if (result != Error.Ok)
		{
			error = "the account could not be written (" + result + ")";
			return false;
		}
		error = "";
		return true;
	}

	/// <summary>Removes the stored account file, if any. Used by tests and a future delete flow.</summary>
	public static void Delete(string path)
	{
		if (Godot.FileAccess.FileExists(path)) DirAccess.RemoveAbsolute(path);
	}
}
