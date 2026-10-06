using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace PropertyGpsApi.Infrastructure.Security.BodyEncryption;

/// <summary>
/// A parsed pgps-body/1 envelope. Parsing is strict on purpose: all six fields are required,
/// no other key is allowed, no key may repeat, names are case-sensitive, ts is a non-negative
/// integer, and k, n and c are canonical standard base64 with padding. Anything looser would
/// give two different byte strings the same meaning, and a decryptor should have no opinions.
///
/// Field semantics (the version, the kid's presence in the key ring, k's length against the
/// modulus) are the opener's job; this type only checks shape.
/// </summary>
internal sealed class SealedEnvelope
{
    public required string Enc { get; init; }
    public required string Kid { get; init; }
    public required long Ts { get; init; }
    public required byte[] K { get; init; }
    public required byte[] N { get; init; }
    public required byte[] C { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> utf8, [NotNullWhen(true)] out SealedEnvelope? envelope)
    {
        envelope = null;

        string? enc = null, kid = null;
        long? ts = null;
        byte[]? k = null, n = null, c = null;

        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = 2
            });

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;

            while (true)
            {
                if (!reader.Read()) return false;
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) return false;

                var name = reader.GetString();
                if (!reader.Read()) return false;

                switch (name)
                {
                    case "enc" when enc is null && reader.TokenType == JsonTokenType.String:
                        enc = reader.GetString();
                        break;
                    case "kid" when kid is null && reader.TokenType == JsonTokenType.String:
                        kid = reader.GetString();
                        if (!BodyEncryptionFormat.IsKid(kid)) return false;
                        break;
                    case "ts" when ts is null && reader.TokenType == JsonTokenType.Number:
                        if (!reader.TryGetInt64(out var t) || t < 0) return false;
                        ts = t;
                        break;
                    case "k" when k is null && reader.TokenType == JsonTokenType.String:
                        if (!TryReadBase64(ref reader, out k)) return false;
                        break;
                    case "n" when n is null && reader.TokenType == JsonTokenType.String:
                        if (!TryReadBase64(ref reader, out n)) return false;
                        break;
                    case "c" when c is null && reader.TokenType == JsonTokenType.String:
                        if (!TryReadBase64(ref reader, out c)) return false;
                        break;
                    default:
                        // An unknown key, a repeated key, or a value of the wrong type.
                        return false;
                }
            }

            // Anything but whitespace after the closing brace makes Read() throw.
            if (reader.Read()) return false;
        }
        catch (JsonException)
        {
            return false;
        }

        if (enc is null || kid is null || ts is null || k is null || n is null || c is null) return false;
        if (n.Length != BodyEncryptionFormat.NonceBytes) return false;
        if (c.Length < BodyEncryptionFormat.TagBytes) return false;

        envelope = new SealedEnvelope { Enc = enc, Kid = kid, Ts = ts.Value, K = k, N = n, C = c };
        return true;
    }

    private static bool TryReadBase64(ref Utf8JsonReader reader, out byte[]? bytes)
    {
        if (!reader.ValueIsEscaped && !reader.HasValueSequence)
            return TryDecodeCanonicalBase64(reader.ValueSpan, out bytes);

        // Base64 never needs escaping, but "+" is still a valid JSON spelling of '+'.
        var length = reader.HasValueSequence ? checked((int)reader.ValueSequence.Length) : reader.ValueSpan.Length;
        var buffer = new byte[length];
        var written = reader.CopyString(buffer);
        return TryDecodeCanonicalBase64(buffer.AsSpan(0, written), out bytes);
    }

    /// <summary>Standard alphabet, padded, no whitespace, and re-encodes to the same text.</summary>
    internal static bool TryDecodeCanonicalBase64(ReadOnlySpan<byte> text, out byte[]? bytes)
    {
        bytes = null;
        if (text.Length == 0 || text.Length % 4 != 0) return false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            var isAlphabet = ch is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'
                or >= (byte)'0' and <= (byte)'9' or (byte)'+' or (byte)'/';
            if (isAlphabet) continue;
            if (ch == (byte)'=' && i >= text.Length - 2 && (i == text.Length - 1 || text[^1] == (byte)'=')) continue;
            return false;
        }

        var decoded = new byte[Base64.GetMaxDecodedFromUtf8Length(text.Length)];
        if (Base64.DecodeFromUtf8(text, decoded, out var consumed, out var written) != OperationStatus.Done
            || consumed != text.Length)
            return false;

        var result = decoded.AsSpan(0, written).ToArray();

        // Rejects non-zero padding bits: exactly one spelling per byte string.
        var reencoded = new byte[Base64.GetMaxEncodedToUtf8Length(result.Length)];
        if (Base64.EncodeToUtf8(result, reencoded, out _, out var encodedLength) != OperationStatus.Done
            || !reencoded.AsSpan(0, encodedLength).SequenceEqual(text))
            return false;

        bytes = result;
        return true;
    }
}
