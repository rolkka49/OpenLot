using System;
using System.Collections.Generic;
using Godot;
using GodotDict = Godot.Collections.Dictionary;

/// <summary>
/// One stored player-data value (milestone 3.12): a number, a string or a boolean. A closed struct
/// rather than a boxed object, so iterating the store allocates nothing and the JSON write is
/// explicit about the three kinds the API admits.
/// </summary>
public struct PlayerDataValue
{
	public enum ValueKind { Number, String, Boolean }

	public ValueKind Kind;
	public double Number;
	public string Text;
	public bool Flag;

	public static PlayerDataValue OfNumber(double value) { return new PlayerDataValue { Kind = ValueKind.Number, Number = value }; }
	public static PlayerDataValue OfString(string value) { return new PlayerDataValue { Kind = ValueKind.String, Text = value ?? "" }; }
	public static PlayerDataValue OfBoolean(bool value) { return new PlayerDataValue { Kind = ValueKind.Boolean, Flag = value }; }
}

/// <summary>
/// The per-player key/value store (milestone 3.12). Keys are stable player identities — §3.12a
/// Account IDs, or a device's guest id (<see cref="GuestIdentity"/>) — never peer ids or display
/// names, both of which are session/presentation values. Plain C# + Godot's JSON types, no scene
/// dependency, so the store unit-tests without fixtures; the archive entry it serializes to is
/// written and read by the one save/load pipeline (§4.1/§4.2).
///
/// Every cap is a refusal with a readable reason, and the running byte budget is accounted on each
/// set/remove — an overwrite or a removal frees what it held, so a store at its budget becomes
/// writable again. Design doc: docs/design/milestone-3.12-player-data.md.
/// </summary>
public sealed class PlayerDataStore
{
	public const int FormatVersion = 1;
	public const int MaxPlayers = 256;
	public const int MaxKeysPerPlayer = 256;
	public const int MaxKeyLength = 64;
	public const int MaxStringLength = 4096;
	public const int MaxTotalBytes = 256 * 1024;

	private readonly Dictionary<string, Dictionary<string, PlayerDataValue>> _players = new Dictionary<string, Dictionary<string, PlayerDataValue>>();
	private int _bytes;

	public int PlayerCount { get { return _players.Count; } }

	/// <summary>The accounted size of everything stored (see <see cref="EntrySize"/>), so a caller
	/// (or a test) can see the budget move as data is written and removed.</summary>
	public int StoredBytes { get { return _bytes; } }

	/// <summary>
	/// True when a string is the player-id shape §3.12a defines: 22 Base64Url characters that decode
	/// canonically to 16 bytes. A real Account ID and a guest id are indistinguishable here — both
	/// are opaque keys — and nothing else is accepted as a player.
	/// </summary>
	public static bool LooksLikePlayerId(string id)
	{
		if (string.IsNullOrEmpty(id) || id.Length != AccountKey.AccountIdLength) return false;
		return AccountEncoding.TryDecode(id, out byte[] bytes, out string _) && bytes.Length == 16;
	}

	public bool TryGet(string playerId, string key, out PlayerDataValue value)
	{
		value = default(PlayerDataValue);
		if (string.IsNullOrEmpty(playerId) || string.IsNullOrEmpty(key)) return false;
		if (!_players.TryGetValue(playerId, out Dictionary<string, PlayerDataValue> player)) return false;
		return player.TryGetValue(key, out value);
	}

	public bool Set(string playerId, string key, PlayerDataValue value, out string error)
	{
		error = "";
		if (!LooksLikePlayerId(playerId))
		{
			error = "the player id must be a 22-character id (Lot.GetLocalPlayer() gives this device's)";
			return false;
		}
		if (string.IsNullOrEmpty(key))
		{
			error = "the key must not be empty";
			return false;
		}
		if (key.Length > MaxKeyLength)
		{
			error = "the key is longer than " + MaxKeyLength + " characters";
			return false;
		}
		if (value.Kind == PlayerDataValue.ValueKind.String && value.Text != null && value.Text.Length > MaxStringLength)
		{
			error = "the string value is longer than " + MaxStringLength + " characters";
			return false;
		}
		if (value.Kind == PlayerDataValue.ValueKind.Number && (double.IsNaN(value.Number) || double.IsInfinity(value.Number)))
		{
			error = "NaN and infinity cannot be stored";
			return false;
		}

		bool playerExists = _players.TryGetValue(playerId, out Dictionary<string, PlayerDataValue> player);
		if (!playerExists)
		{
			if (_players.Count >= MaxPlayers)
			{
				error = "this lot already holds the maximum of " + MaxPlayers + " players";
				return false;
			}
			player = new Dictionary<string, PlayerDataValue>();
		}
		bool keyExists = player.TryGetValue(key, out PlayerDataValue previous);
		int delta = EntrySize(key, value) - (keyExists ? EntrySize(key, previous) : 0);
		if (_bytes + delta > MaxTotalBytes)
		{
			error = "the lot's player data is at its total budget (" + MaxTotalBytes + " bytes)";
			return false;
		}

		player[key] = value;
		if (!playerExists) _players[playerId] = player;
		_bytes += delta;
		return true;
	}

	/// <summary>Removes one value. False when the player or the key is not there — a removal of
	/// something that was never stored is not a mutation, so it reports as such.</summary>
	public bool Remove(string playerId, string key)
	{
		if (string.IsNullOrEmpty(playerId) || string.IsNullOrEmpty(key)) return false;
		if (!_players.TryGetValue(playerId, out Dictionary<string, PlayerDataValue> player)) return false;
		if (!player.TryGetValue(key, out PlayerDataValue previous)) return false;
		player.Remove(key);
		_bytes -= EntrySize(key, previous);
		if (player.Count == 0) _players.Remove(playerId);
		return true;
	}

	/// <summary>Serializes to the `playerdata.json` entry's shape: `{version, players: {id: {key: value}}}`.</summary>
	public GodotDict ToJson()
	{
		GodotDict playersOut = new GodotDict();
		foreach (KeyValuePair<string, Dictionary<string, PlayerDataValue>> player in _players)
		{
			GodotDict valuesOut = new GodotDict();
			foreach (KeyValuePair<string, PlayerDataValue> entry in player.Value)
			{
				switch (entry.Value.Kind)
				{
					case PlayerDataValue.ValueKind.Number: valuesOut[entry.Key] = entry.Value.Number; break;
					case PlayerDataValue.ValueKind.String: valuesOut[entry.Key] = entry.Value.Text ?? ""; break;
					case PlayerDataValue.ValueKind.Boolean: valuesOut[entry.Key] = entry.Value.Flag; break;
				}
			}
			playersOut[player.Key] = valuesOut;
		}
		return new GodotDict
		{
			{ "version", FormatVersion },
			{ "players", playersOut }
		};
	}

	/// <summary>
	/// Parses the `playerdata.json` entry. Lenient by the project's load rule — entries that do not
	/// fit the format (unknown value kinds, malformed ids, cap overruns in a hand-edited file) are
	/// skipped and counted in <paramref name="skipped"/>, so the caller can report them; only a
	/// structurally broken document or a newer version is a hard failure. The byte accounting is
	/// rebuilt from what actually loaded, so caps behave identically afterwards.
	/// </summary>
	public static bool FromJson(string text, out PlayerDataStore store, out int skipped, out string error)
	{
		store = null;
		skipped = 0;
		error = "";
		PlayerDataStore loaded = new PlayerDataStore();

		if (string.IsNullOrEmpty(text))
		{
			store = loaded;
			return true;
		}
		Variant parsed = Json.ParseString(text);
		if (parsed.VariantType != Variant.Type.Dictionary)
		{
			error = "the player data is not a JSON object";
			return false;
		}
		GodotDict root = parsed.AsGodotDictionary();
		int version = root.ContainsKey("version") ? (int)root["version"].AsInt64() : FormatVersion;
		if (version != FormatVersion)
		{
			error = "the player data is version " + version + "; this build understands version " + FormatVersion;
			return false;
		}

		if (!root.ContainsKey("players") || root["players"].VariantType != Variant.Type.Dictionary)
		{
			// An empty or absent player table is a valid, empty store.
			store = loaded;
			return true;
		}

		GodotDict players = root["players"].AsGodotDictionary();
		foreach (Variant playerKey in players.Keys)
		{
			string playerId = playerKey.AsString();
			if (!LooksLikePlayerId(playerId) || players[playerKey].VariantType != Variant.Type.Dictionary)
			{
				skipped++;
				continue;
			}
			GodotDict values = players[playerKey].AsGodotDictionary();
			foreach (Variant valueKey in values.Keys)
			{
				string key = valueKey.AsString();
				Variant raw = values[valueKey];
				PlayerDataValue value;
				switch (raw.VariantType)
				{
					case Variant.Type.Float: value = PlayerDataValue.OfNumber(raw.AsDouble()); break;
					case Variant.Type.Int: value = PlayerDataValue.OfNumber(raw.AsInt64()); break;
					case Variant.Type.String: value = PlayerDataValue.OfString(raw.AsString()); break;
					case Variant.Type.Bool: value = PlayerDataValue.OfBoolean(raw.AsBool()); break;
					default: skipped++; continue;
				}
				if (!loaded.Set(playerId, key, value, out string _)) skipped++;
			}
		}

		store = loaded;
		return true;
	}

	/// <summary>What one entry costs against the total budget: its key plus the value's size
	/// (string length, or a flat 8 bytes for a number/boolean).</summary>
	private static int EntrySize(string key, PlayerDataValue value)
	{
		return key.Length + (value.Kind == PlayerDataValue.ValueKind.String && value.Text != null ? value.Text.Length : 8);
	}
}
