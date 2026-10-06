using System.Security.Cryptography;
using System.Text;
using System.Text.Unicode;
using PropertyGpsApi.Common;

namespace PropertyGpsApi.Infrastructure.Security.BodyEncryption;

/// <summary>Why an envelope did not open. Logged by name; never sent to the client.</summary>
public enum BodyOpenFailure
{
    None,

    /// <summary>Not a well-formed envelope: shape, types, base64, or a length (k, n, c).</summary>
    Malformed,

    /// <summary>The enc field names a format this server does not implement.</summary>
    UnknownVersion,

    /// <summary>The kid is not one of this server's keys.</summary>
    UnknownKey,

    /// <summary>
    /// The key unwrap or the GCM tag failed. Deliberately ONE reason for both: telling them
    /// apart, even in timing, is what turns RSA-OAEP into a padding oracle.
    /// </summary>
    DecryptFailed,

    /// <summary>Authentic, but the plain text is not valid UTF-8.</summary>
    NotUtf8,

    /// <summary>Authentic, but ts is outside RequestEncryption:MaxAgeMinutes.</summary>
    Stale
}

/// <summary>The outcome of one open: the plain bytes, or the reason, plus what is safe to log.</summary>
public readonly record struct BodyOpenResult(byte[]? Plaintext, BodyOpenFailure Failure, string? Kid, long? Ts)
{
    public bool Succeeded => Failure == BodyOpenFailure.None;
}

/// <summary>
/// Opens one pgps-body/1 envelope. Kept apart from the HTTP plumbing so the cryptography can be
/// tested on its own and against the cross-language vector.
///
/// Order matters: everything about the envelope that can be checked without the key is checked
/// first; then the AES key is unwrapped, and if that fails - or yields anything but 32 bytes -
/// a random key is used instead and decryption carries on, so a bad k and a bad tag fail at the
/// same point with the same result.
/// </summary>
internal sealed class RequestBodyOpener(RequestKeyRing keys, TimeProvider clock)
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Opens and returns the plain text, or throws the fixed ApiException for the failure.</summary>
    public string Open(ReadOnlySpan<byte> envelope, BodyAadContext context, int maxAgeMinutes = 0)
    {
        var result = TryOpen(envelope, context, maxAgeMinutes);
        if (!result.Succeeded) throw BodyEncryptionErrors.ForOpenFailure(result.Failure);
        return StrictUtf8.GetString(result.Plaintext!);
    }

    public BodyOpenResult TryOpen(ReadOnlySpan<byte> envelopeUtf8, BodyAadContext context, int maxAgeMinutes = 0)
    {
        if (!SealedEnvelope.TryParse(envelopeUtf8, out var envelope))
            return new(null, BodyOpenFailure.Malformed, null, null);

        if (!string.Equals(envelope.Enc, BodyEncryptionFormat.Version, StringComparison.Ordinal))
            return new(null, BodyOpenFailure.UnknownVersion, envelope.Kid, envelope.Ts);

        if (!keys.TryGet(envelope.Kid, out var material))
            return new(null, BodyOpenFailure.UnknownKey, envelope.Kid, envelope.Ts);

        if (envelope.K.Length != material.ModulusBytes)
            return new(null, BodyOpenFailure.Malformed, envelope.Kid, envelope.Ts);

        var plaintext = Decrypt(envelope, material, BodyEncryptionFormat.Aad(envelope.Kid, envelope.Ts, context));
        if (plaintext is null)
            return new(null, BodyOpenFailure.DecryptFailed, envelope.Kid, envelope.Ts);

        if (!Utf8.IsValid(plaintext))
        {
            CryptographicOperations.ZeroMemory(plaintext);
            return new(null, BodyOpenFailure.NotUtf8, envelope.Kid, envelope.Ts);
        }

        // ts is only trustworthy now that the tag has verified it.
        if (maxAgeMinutes > 0)
        {
            var age = Math.Abs(clock.GetUtcNow().ToUnixTimeSeconds() - envelope.Ts);
            if (age > maxAgeMinutes * 60L)
            {
                CryptographicOperations.ZeroMemory(plaintext);
                return new(null, BodyOpenFailure.Stale, envelope.Kid, envelope.Ts);
            }
        }

        return new(plaintext, BodyOpenFailure.None, envelope.Kid, envelope.Ts);
    }

    private static byte[]? Decrypt(SealedEnvelope envelope, RequestKeyMaterial material, byte[] aad)
    {
        var key = new byte[BodyEncryptionFormat.AesKeyBytes];
        try
        {
            UnwrapOrRandom(envelope.K, material, key);

            var ciphertextLength = envelope.C.Length - BodyEncryptionFormat.TagBytes;
            var plaintext = new byte[ciphertextLength];
            using var aes = new AesGcm(key, BodyEncryptionFormat.TagBytes);
            try
            {
                aes.Decrypt(
                    envelope.N,
                    envelope.C.AsSpan(0, ciphertextLength),
                    envelope.C.AsSpan(ciphertextLength),
                    plaintext,
                    aad);
                return plaintext;
            }
            catch (CryptographicException)
            {
                // AuthenticationTagMismatchException is a CryptographicException. AesGcm has
                // already zeroed the output; nothing of the exception is kept or logged.
                return null;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Writes the unwrapped 32-byte key into <paramref name="key"/>, or random bytes.</summary>
    private static void UnwrapOrRandom(byte[] wrapped, RequestKeyMaterial material, byte[] key)
    {
        using var rsa = RSA.Create();
        try
        {
            rsa.ImportPkcs8PrivateKey(material.Pkcs8, out _);
        }
        catch (CryptographicException)
        {
            // The validator proved this key imports at startup. No inner exception: a key
            // parser's message is not something to carry into a log.
            throw new InvalidOperationException(
                $"The request-encryption key {material.Kid} could not be loaded.");
        }

        byte[]? unwrapped = null;
        try
        {
            unwrapped = rsa.Decrypt(wrapped, RSAEncryptionPadding.OaepSHA256);
        }
        catch (CryptographicException)
        {
            unwrapped = null;
        }

        if (unwrapped is { Length: BodyEncryptionFormat.AesKeyBytes })
            unwrapped.CopyTo(key, 0);
        else
            RandomNumberGenerator.Fill(key);

        if (unwrapped is not null) CryptographicOperations.ZeroMemory(unwrapped);
    }
}
