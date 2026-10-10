using System.Security.Cryptography;
using Godot;

/// <summary>
/// The machine-local guest identity (milestone 3.12 design doc D2): when no account exists on this
/// device, player data is still keyed by something stable across runs, so Test mode and
/// unregistered play keep per-player data without a registration wall.
///
/// A guest id is NOT an account: it cannot sign, nothing ever leaves the machine, and its data does
/// not follow the user (a later claim/merge is deliberately out of scope). It is simply 16 random
/// bytes in the same 22-character shape the Account IDs use, so the store treats both identically.
/// </summary>
public static class GuestIdentity
{
	public const string DefaultPath = "user://guest.cfg";
	private const string Section = "guest";
	private const string IdField = "id";

	/// <summary>The stored guest id, or "" when none exists (or the stored one is malformed).</summary>
	public static string Read(string path)
	{
		if (!Godot.FileAccess.FileExists(path)) return "";
		ConfigFile config = new ConfigFile();
		if (config.Load(path) != Error.Ok) return "";
		string id = config.GetValue(Section, IdField, "").AsString();
		return PlayerDataStore.LooksLikePlayerId(id) ? id : "";
	}

	/// <summary>Reads the guest id, creating and persisting one on first use.</summary>
	public static string GetOrCreate(string path = DefaultPath)
	{
		string existing = Read(path);
		if (existing.Length > 0) return existing;

		string id = AccountEncoding.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
		ConfigFile config = new ConfigFile();
		// Re-read first so keys a future writer added are not dropped (the same discipline the
		// other config writers use).
		if (Godot.FileAccess.FileExists(path)) config.Load(path);
		config.SetValue(Section, IdField, id);
		config.Save(path);
		return id;
	}
}
