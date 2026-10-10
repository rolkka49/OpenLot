using System;

/// <summary>
/// The account formats' single text encoding (milestone 3.12a): unpadded Base64Url (RFC 4648 §5,
/// 'A'–'Z' 'a'–'z' '0'–'9' '-' '_'). Decoding is strict — the alphabet, the length and the
/// canonical zero padding bits are all validated — so a mistyped key is rejected with a readable
/// message instead of silently decoding into a different account.
/// </summary>
public static class AccountEncoding
{
	/// <summary>Encodes bytes as unpadded Base64Url (no '=' padding).</summary>
	public static string Encode(byte[] bytes)
	{
		return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
	}

	/// <summary>
	/// Decodes unpadded Base64Url. False (with a message) on empty input, an impossible length, a
	/// character outside the alphabet, or non-zero padding bits — the last one matters because two
	/// different strings can otherwise decode to the same bytes, and a key string must round-trip
	/// to exactly the string it was.
	/// </summary>
	public static bool TryDecode(string text, out byte[] bytes, out string error)
	{
		bytes = null;
		if (string.IsNullOrEmpty(text))
		{
			error = "the value is empty";
			return false;
		}
		// A single trailing character can never carry a whole byte in base64.
		if (text.Length % 4 == 1)
		{
			error = "the value has an impossible length";
			return false;
		}

		byte[] result = new byte[text.Length * 6 / 8];
		int index = 0;
		int buffer = 0;
		int bits = 0;
		for (int i = 0; i < text.Length; i++)
		{
			int value = DecodeChar(text[i]);
			if (value < 0)
			{
				error = "the value contains '" + text[i] + "', which is not a base64url character";
				return false;
			}
			buffer = (buffer << 6) | value;
			bits += 6;
			if (bits >= 8)
			{
				bits -= 8;
				result[index++] = (byte)((buffer >> bits) & 0xFF);
				buffer &= (1 << bits) - 1;
			}
		}
		// Whatever remains must be zero padding; anything else is a non-canonical encoding.
		if (buffer != 0)
		{
			error = "the value has non-zero padding bits (it is not a canonical encoding)";
			return false;
		}
		bytes = result;
		error = "";
		return true;
	}

	private static int DecodeChar(char c)
	{
		if (c >= 'A' && c <= 'Z') return c - 'A';
		if (c >= 'a' && c <= 'z') return c - 'a' + 26;
		if (c >= '0' && c <= '9') return c - '0' + 52;
		if (c == '-') return 62;
		if (c == '_') return 63;
		return -1;
	}
}
