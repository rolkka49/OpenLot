using System;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// The account identity primitive (milestone 3.12a): a locally generated P-256 keypair whose
/// 32-byte seed, wrapped as a 50-character string, IS the account. The string carries
/// version(1) + seed(32) + SHA-256 checksum(4) as unpadded Base64Url — exactly 50 characters —
/// and a single mistyped character is refused by the checksum.
///
/// The private half never leaves the client: hosts receive the 65-byte public key and verify
/// signatures (<see cref="Sign"/>/<see cref="Verify"/>) over challenges built by
/// <see cref="AccountChallenge"/>. There is no account server and no authority — the math is the
/// enforcement (design doc docs/design/milestone-3.12a-account-identity.md). Signing is a
/// fixed-schema core operation: no Lua-bound verb exposes the seed, the key string or a signing
/// operation (§1.1 boundary extension, pinned by the self-test).
/// </summary>
public sealed class AccountKey
{
	public const int SeedLength = 32;
	/// <summary>Exactly 50 characters: 37 payload bytes as unpadded Base64Url.</summary>
	public const int KeyStringLength = 50;
	/// <summary>0x04 || X(32) || Y(32) — the uncompressed P-256 point carried in the handshake.</summary>
	public const int PublicKeyLength = 65;
	/// <summary>16-byte truncated hash as Base64Url.</summary>
	public const int AccountIdLength = 22;
	public const byte FormatVersion = 1;

	private const int PayloadLength = 1 + SeedLength + 4;
	private const string IdHashTag = "OPENLOT-ACCOUNT-ID-v1";

	private readonly byte[] _seed;

	/// <summary>The 50-character key string — shown to its owner at registration only.</summary>
	public string KeyString { get; }

	/// <summary>The public account id (22 characters) — what games store, show and compare.</summary>
	public string AccountId { get; }

	/// <summary>The 65-byte uncompressed public key sent to hosts for verification.</summary>
	public byte[] PublicKey { get; }

	private AccountKey(byte[] seed, byte[] publicKey, string keyString, string accountId)
	{
		_seed = seed;
		PublicKey = publicKey;
		KeyString = keyString;
		AccountId = accountId;
	}

	/// <summary>Generates a brand-new account key. Purely local — no network call exists here.</summary>
	public static AccountKey Create()
	{
		using (ECDsa generated = ECDsa.Create(ECCurve.NamedCurves.nistP256))
		{
			ECParameters parameters = generated.ExportParameters(true);
			TryFromSeed(parameters.D, out AccountKey key, out string error);
			if (key == null) throw new CryptographicException("P-256 key generation failed: " + error);
			return key;
		}
	}

	/// <summary>
	/// Rebuilds the whole keypair from the 32-byte seed alone — the load-bearing property of the
	/// design (the 50-character string carries nothing else). This is the spike that chose the
	/// BCL path: `ECDsa.ImportParameters` with Curve+D and no Q derives the public key; the
	/// self-test pins it on every run.
	/// </summary>
	public static bool TryFromSeed(byte[] seed, out AccountKey key, out string error)
	{
		key = null;
		if (seed == null || seed.Length != SeedLength)
		{
			error = "the seed must be " + SeedLength + " bytes";
			return false;
		}

		byte[] publicKey;
		try
		{
			using (ECDsa pair = ECDsa.Create())
			{
				pair.ImportParameters(new ECParameters
				{
					Curve = ECCurve.NamedCurves.nistP256,
					D = (byte[])seed.Clone()
				});
				ECParameters parameters = pair.ExportParameters(true);
				byte[] x = parameters.Q.X;
				byte[] y = parameters.Q.Y;
				if (x == null || y == null || x.Length != 32 || y.Length != 32)
				{
					error = "the platform did not derive a P-256 public key from the seed";
					return false;
				}
				publicKey = new byte[PublicKeyLength];
				publicKey[0] = 0x04;
				Array.Copy(x, 0, publicKey, 1, 32);
				Array.Copy(y, 0, publicKey, 33, 32);
			}
		}
		catch (CryptographicException)
		{
			error = "the seed is not a valid P-256 private scalar";
			return false;
		}

		byte[] payload = new byte[PayloadLength];
		payload[0] = FormatVersion;
		Array.Copy(seed, 0, payload, 1, SeedLength);
		byte[] checksum = Checksum(payload);
		Array.Copy(checksum, 0, payload, 1 + SeedLength, 4);

		key = new AccountKey((byte[])seed.Clone(), publicKey, AccountEncoding.Encode(payload),
			ComputeAccountId(publicKey));
		error = "";
		return true;
	}

	/// <summary>
	/// Parses a 50-character key string back into the account: length, alphabet, canonical
	/// encoding, version and the SHA-256 checksum are all validated before the seed is imported.
	/// </summary>
	public static bool TryFromKeyString(string keyString, out AccountKey key, out string error)
	{
		key = null;
		if (keyString == null || keyString.Length != KeyStringLength)
		{
			error = "a key is exactly " + KeyStringLength + " characters";
			return false;
		}
		if (!AccountEncoding.TryDecode(keyString, out byte[] payload, out string decodeError))
		{
			error = decodeError;
			return false;
		}
		if (payload.Length != PayloadLength)
		{
			error = "the key decodes to " + payload.Length + " bytes, not " + PayloadLength;
			return false;
		}
		if (payload[0] != FormatVersion)
		{
			error = "the key is version " + payload[0] + "; this build understands version " + FormatVersion;
			return false;
		}
		byte[] expected = Checksum(payload);
		for (int i = 0; i < 4; i++)
		{
			if (payload[1 + SeedLength + i] != expected[i])
			{
				error = "the key checksum does not match: it was mistyped or altered";
				return false;
			}
		}
		byte[] seed = new byte[SeedLength];
		Array.Copy(payload, 1, seed, 0, SeedLength);
		return TryFromSeed(seed, out key, out error);
	}

	/// <summary>
	/// Signs arbitrary bytes with the account key — only ever a challenge built by
	/// <see cref="AccountChallenge"/>. The client core is the only caller by design; there is no
	/// creator-facing signing operation at any tier (§1.1 boundary).
	/// </summary>
	public byte[] Sign(byte[] message)
	{
		if (message == null) throw new ArgumentNullException(nameof(message));
		using (ECDsa pair = ECDsa.Create())
		{
			pair.ImportParameters(new ECParameters
			{
				Curve = ECCurve.NamedCurves.nistP256,
				D = (byte[])_seed.Clone()
			});
			return pair.SignData(message, HashAlgorithmName.SHA256);
		}
	}

	/// <summary>Verifies a signature against a public key. False for malformed input of any kind.</summary>
	public static bool Verify(byte[] publicKey, byte[] message, byte[] signature)
	{
		if (publicKey == null || message == null || signature == null) return false;
		if (publicKey.Length != PublicKeyLength || publicKey[0] != 0x04) return false;
		byte[] x = new byte[32];
		byte[] y = new byte[32];
		Array.Copy(publicKey, 1, x, 0, 32);
		Array.Copy(publicKey, 33, y, 0, 32);
		try
		{
			using (ECDsa verifier = ECDsa.Create())
			{
				verifier.ImportParameters(new ECParameters
				{
					Curve = ECCurve.NamedCurves.nistP256,
					Q = new ECPoint { X = x, Y = y }
				});
				return verifier.VerifyData(message, signature, HashAlgorithmName.SHA256);
			}
		}
		catch (CryptographicException)
		{
			// Not a point on the curve, or a malformed signature — a miss, not a crash.
			return false;
		}
	}

	/// <summary>The account id: a 16-byte truncated, domain-separated SHA-256 of the public key.</summary>
	public static string ComputeAccountId(byte[] publicKey)
	{
		if (publicKey == null) return "";
		byte[] tag = Encoding.UTF8.GetBytes(IdHashTag);
		byte[] input = new byte[tag.Length + publicKey.Length];
		Array.Copy(tag, 0, input, 0, tag.Length);
		Array.Copy(publicKey, 0, input, tag.Length, publicKey.Length);
		byte[] hash = SHA256.HashData(input);
		byte[] truncated = new byte[16];
		Array.Copy(hash, 0, truncated, 0, 16);
		return AccountEncoding.Encode(truncated);
	}

	/// <summary>True when the id really is the fingerprint of that public key (the host-side check).</summary>
	public static bool VerifyAccountId(string accountId, byte[] publicKey)
	{
		return accountId != null && accountId == ComputeAccountId(publicKey);
	}

	/// <summary>The 4-byte checksum: SHA-256 over version + seed (the first 33 bytes — never itself).</summary>
	private static byte[] Checksum(byte[] payload)
	{
		byte[] hash = SHA256.HashData(payload.AsSpan(0, 1 + SeedLength));
		byte[] result = new byte[4];
		Array.Copy(hash, 0, result, 0, 4);
		return result;
	}
}
