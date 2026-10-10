using System;
using System.Globalization;
using System.Text;

/// <summary>
/// The canonical session-auth challenge (milestone 3.12a): the exact bytes a host asks a client to
/// sign when the client proves account ownership.
///
/// The format is the wire contract — domain-separated by the `OPENLOT-AUTH-v1` scheme tag and
/// bound to the lot, a fresh random nonce and an expiry — so a signature a malicious host captures
/// is useless on any other host, in any other session, or as any other kind of proof. Field order
/// and separators are pinned by a known-answer self-test; changing them is a protocol change.
/// §10.1's handshake is the consumer when the real transport lands.
/// </summary>
public static class AccountChallenge
{
	public const string SchemeTag = "OPENLOT-AUTH-v1";

	/// <summary>
	/// Builds the canonical challenge message: UTF-8 text, one field per line, no trailing
	/// newline. The nonce is the host's fresh random value; expiry is Unix seconds, checked by
	/// the verifier against its clock.
	/// </summary>
	public static byte[] Build(string lotId, byte[] nonce, long expiryUnix)
	{
		string text = SchemeTag + "\n" +
			"lot:" + (lotId ?? "") + "\n" +
			"nonce:" + AccountEncoding.Encode(nonce ?? Array.Empty<byte>()) + "\n" +
			"expiry:" + expiryUnix.ToString(CultureInfo.InvariantCulture);
		return Encoding.UTF8.GetBytes(text);
	}
}
