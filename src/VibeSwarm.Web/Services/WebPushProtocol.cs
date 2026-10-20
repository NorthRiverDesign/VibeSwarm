using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VibeSwarm.Web.Services;

/// <summary>
/// The Web Push wire format: message encryption (RFC 8291, aes128gcm from RFC 8188) and VAPID
/// signing (RFC 8292). Push services (FCM, Mozilla, Apple) deliver only messages built this way.
/// </summary>
public static class WebPushProtocol
{
	public const int RecordSize = 4096;

	private const int SaltLength = 16;
	private const int PublicKeyLength = 65;
	private const int TagLength = 16;

	/// <summary>A new P-256 key pair for VAPID, as base64url (public: uncompressed point; private: scalar).</summary>
	public static (string PublicKey, string PrivateKey) CreateVapidKeys()
	{
		using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
		var parameters = key.ExportParameters(includePrivateParameters: true);
		return (Base64Url.EncodeToString(ToUncompressedPoint(parameters.Q)), Base64Url.EncodeToString(parameters.D!));
	}

	/// <summary>
	/// Encrypts a payload for one browser subscription, with a fresh salt and sender key each time.
	/// </summary>
	public static byte[] Encrypt(byte[] plaintext, string userAgentPublicKey, string authSecret)
	{
		using var senderKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
		return Encrypt(plaintext, userAgentPublicKey, authSecret, senderKey, RandomNumberGenerator.GetBytes(SaltLength));
	}

	/// <summary>Encrypts with a given sender key and salt (the RFC's worked example pins both).</summary>
	public static byte[] Encrypt(byte[] plaintext, string userAgentPublicKey, string authSecret, ECDiffieHellman senderKey, byte[] salt)
	{
		var uaPublic = DecodePublicKey(userAgentPublicKey);
		var auth = Base64Url.DecodeFromChars(authSecret);
		if (auth.Length != 16)
		{
			throw new ArgumentException("The subscription's auth secret must be 16 bytes.", nameof(authSecret));
		}
		if (salt.Length != SaltLength)
		{
			throw new ArgumentException("The salt must be 16 bytes.", nameof(salt));
		}
		if (plaintext.Length > RecordSize - TagLength - 1 - (SaltLength + 4 + 1 + PublicKeyLength))
		{
			throw new ArgumentException("The push payload is too large.", nameof(plaintext));
		}

		var asPublic = ToUncompressedPoint(senderKey.ExportParameters(includePrivateParameters: false).Q);
		using var uaKey = ECDiffieHellman.Create(new ECParameters
		{
			Curve = ECCurve.NamedCurves.nistP256,
			Q = new ECPoint { X = uaPublic[1..33], Y = uaPublic[33..] }
		});
		var ecdhSecret = senderKey.DeriveRawSecretAgreement(uaKey.PublicKey);

		var keyInfo = Concat(Encoding.ASCII.GetBytes("WebPush: info\0"), uaPublic, asPublic);
		var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdhSecret, 32, auth, keyInfo);
		var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
		var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

		// One record: the plaintext, then the 0x02 delimiter that marks the last record.
		var padded = new byte[plaintext.Length + 1];
		plaintext.CopyTo(padded, 0);
		padded[^1] = 0x02;

		var ciphertext = new byte[padded.Length];
		var tag = new byte[TagLength];
		using (var aes = new AesGcm(cek, TagLength))
		{
			aes.Encrypt(nonce, padded, ciphertext, tag);
		}

		var header = new byte[SaltLength + 4 + 1 + PublicKeyLength];
		salt.CopyTo(header, 0);
		BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(SaltLength), RecordSize);
		header[SaltLength + 4] = PublicKeyLength;
		asPublic.CopyTo(header, SaltLength + 5);

		return Concat(header, ciphertext, tag);
	}

	/// <summary>
	/// The <c>Authorization</c> header value for a push endpoint: <c>vapid t=&lt;JWT&gt;, k=&lt;key&gt;</c>.
	/// </summary>
	public static string CreateVapidAuthorization(string endpoint, string subject, string publicKey, string privateKey, DateTimeOffset expires)
	{
		var audience = new Uri(endpoint).GetLeftPart(UriPartial.Authority);
		var header = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["typ"] = "JWT", ["alg"] = "ES256" }));
		var claims = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
		{
			["aud"] = audience,
			["exp"] = expires.ToUnixTimeSeconds(),
			["sub"] = subject
		}));
		var signingInput = $"{header}.{claims}";

		var publicPoint = DecodePublicKey(publicKey);
		using var signer = ECDsa.Create(new ECParameters
		{
			Curve = ECCurve.NamedCurves.nistP256,
			D = Base64Url.DecodeFromChars(privateKey),
			Q = new ECPoint { X = publicPoint[1..33], Y = publicPoint[33..] }
		});
		var signature = signer.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256,
			DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

		return $"vapid t={signingInput}.{Base64Url.EncodeToString(signature)}, k={publicKey}";
	}

	/// <summary>Whether a subscription's keys have the shapes browsers give out.</summary>
	public static bool AreValidSubscriptionKeys(string? p256dh, string? auth)
	{
		try
		{
			return !string.IsNullOrWhiteSpace(p256dh) && !string.IsNullOrWhiteSpace(auth)
				&& DecodePublicKey(p256dh).Length == PublicKeyLength
				&& Base64Url.DecodeFromChars(auth).Length == 16;
		}
		catch (Exception ex) when (ex is FormatException or ArgumentException)
		{
			return false;
		}
	}

	private static byte[] DecodePublicKey(string key)
	{
		var bytes = Base64Url.DecodeFromChars(key);
		if (bytes.Length != PublicKeyLength || bytes[0] != 0x04)
		{
			throw new ArgumentException("A P-256 public key must be a 65-byte uncompressed point.", nameof(key));
		}
		return bytes;
	}

	private static byte[] ToUncompressedPoint(ECPoint point)
		=> Concat([0x04], point.X!, point.Y!);

	private static byte[] Concat(params byte[][] parts)
	{
		var result = new byte[parts.Sum(p => p.Length)];
		var offset = 0;
		foreach (var part in parts)
		{
			part.CopyTo(result, offset);
			offset += part.Length;
		}
		return result;
	}
}
