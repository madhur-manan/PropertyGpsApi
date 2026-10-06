using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PropertyGpsApi.Infrastructure.Security.BodyEncryption;

/// <summary>
/// The fixed parts of wire format pgps-body/1. The app (lib/services/http/request_sealer.dart)
/// builds exactly these bytes; any change here is a new format version, never an edit.
///
/// Envelope, compact JSON in this key order:
///   {"enc":"pgps-body/1","kid":"&lt;16 hex&gt;","ts":&lt;unix s&gt;,"k":"&lt;b64&gt;","n":"&lt;b64&gt;","c":"&lt;b64&gt;"}
/// k = RSA-OAEP (SHA-256 hash and MGF1, empty label) of a fresh 32-byte AES key, left-padded
/// to the modulus length; n = 12-byte nonce; c = AES-256-GCM ciphertext followed by the
/// 16-byte tag, over the AAD below.
/// </summary>
public static class BodyEncryptionFormat
{
    public const string Version = "pgps-body/1";

    /// <summary>What a sealed body or form field starts with, once leading whitespace is trimmed.</summary>
    public const string MarkerText = "{\"enc\":\"pgps-body/";

    public static ReadOnlySpan<byte> Marker => "{\"enc\":\"pgps-body/"u8;

    /// <summary>Set by the app on every sealed request. Value "1".</summary>
    public const string HeaderName = "X-Body-Enc";

    public const string BodyPart = "body";
    public const string FormPartPrefix = "form:";

    /// <summary>The AAD's token field when the request carries no bearer token (the OTP calls).</summary>
    public const string NoToken = "-";

    public const int AesKeyBytes = 32;
    public const int NonceBytes = 12;
    public const int TagBytes = 16;
    public const int KidHexLength = 16;

    /// <summary>
    /// pgps-body/1|kid|ts|METHOD|path|part|tok, UTF-8. Binds the ciphertext to one endpoint,
    /// one part of the request and one officer's token, so a captured envelope cannot be
    /// replayed anywhere else.
    /// </summary>
    public static byte[] Aad(string kid, long ts, BodyAadContext context) =>
        Encoding.UTF8.GetBytes(string.Join('|',
            Version,
            kid,
            ts.ToString(CultureInfo.InvariantCulture),
            context.Method.ToUpperInvariant(),
            context.Path.ToLowerInvariant(),
            context.Part,
            context.TokenTag));

    /// <summary>
    /// First 16 lower-case hex characters of SHA-256 over the raw bearer token (the text after
    /// "Bearer "), or "-" when there is none.
    /// </summary>
    public static string TokenTag(string? bearerToken) =>
        string.IsNullOrEmpty(bearerToken)
            ? NoToken
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(bearerToken)))[..KidHexLength];

    /// <summary>The bearer token from an Authorization header value, or null.</summary>
    public static string? BearerToken(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader)) return null;
        const string scheme = "Bearer ";
        var value = authorizationHeader.Trim();
        if (!value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;
        var token = value[scheme.Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    /// <summary>First 8 bytes of SHA-256 over the SubjectPublicKeyInfo DER, lower-case hex.</summary>
    public static string Kid(ReadOnlySpan<byte> subjectPublicKeyInfo) =>
        Convert.ToHexStringLower(SHA256.HashData(subjectPublicKeyInfo).AsSpan(0, KidHexLength / 2));

    public static bool IsKid(string? value) =>
        value is { Length: KidHexLength } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>True when the bytes, after leading JSON whitespace, start with the marker.</summary>
    public static bool StartsWithMarker(ReadOnlySpan<byte> utf8)
    {
        var i = 0;
        while (i < utf8.Length && utf8[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
        return utf8[i..].StartsWith(Marker);
    }

    public static bool StartsWithMarker(string? text) =>
        text is not null && text.AsSpan().TrimStart(JsonWhitespace).StartsWith(MarkerText, StringComparison.Ordinal);

    private const string JsonWhitespace = " \t\r\n";
}

/// <summary>The request-side half of the AAD: where the sealed bytes were sent.</summary>
/// <param name="Method">HTTP method; upper-cased in the AAD.</param>
/// <param name="Path">Request path below the site (HttpRequest.Path, which excludes PathBase); lower-cased in the AAD.</param>
/// <param name="Part">"body" for a JSON body, "form:&lt;field&gt;" for a multipart field.</param>
/// <param name="TokenTag">BodyEncryptionFormat.TokenTag of the request's bearer token.</param>
public readonly record struct BodyAadContext(string Method, string Path, string Part, string TokenTag);
