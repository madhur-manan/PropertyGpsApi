using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Infrastructure.Options;

namespace PropertyGpsApi.Infrastructure.Security.BodyEncryption;

/// <summary>
/// One configured private key, normalised: PKCS#8 DER, the modulus length that k must match,
/// and the kid derived from the public half.
/// </summary>
internal sealed class RequestKeyMaterial
{
    public required string Kid { get; init; }
    public required byte[] Pkcs8 { get; init; }
    public required int ModulusBytes { get; init; }
    public required int KeySizeBits { get; init; }

    /// <summary>
    /// Accepts one line of base64 PKCS#8 DER (what deploy/New-RequestEncryptionKey.cs writes)
    /// or an unencrypted PEM ("PRIVATE KEY" or "RSA PRIVATE KEY"). Never says why a key was
    /// refused: exception text from a key parser has no business in a log.
    /// </summary>
    public static bool TryParse(string? configured, [NotNullWhen(true)] out RequestKeyMaterial? material)
    {
        material = null;
        if (string.IsNullOrWhiteSpace(configured)) return false;

        var text = configured.Trim();
        try
        {
            using var rsa = RSA.Create();
            if (text.StartsWith("-----BEGIN", StringComparison.Ordinal))
            {
                rsa.ImportFromPem(text);
            }
            else
            {
                // The key file is one line; a value pasted across lines into web.config is not.
                if (text.Any(char.IsWhiteSpace)) return false;
                var der = Convert.FromBase64String(text);
                try
                {
                    rsa.ImportPkcs8PrivateKey(der, out var read);
                    if (read != der.Length) return false;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(der);
                }
            }

            // A public key imports without complaint; only a private key can export PKCS#8.
            var pkcs8 = rsa.ExportPkcs8PrivateKey();
            material = new RequestKeyMaterial
            {
                Kid = BodyEncryptionFormat.Kid(rsa.ExportSubjectPublicKeyInfo()),
                Pkcs8 = pkcs8,
                ModulusBytes = (rsa.KeySize + 7) / 8,
                KeySizeBits = rsa.KeySize
            };
            return true;
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>
/// The private keys this server can open, by kid. Built once from validated options; the
/// validator has already refused anything that would not parse, so a failure here is a bug.
/// RSA objects are not shared between requests: the opener imports per request from Pkcs8.
/// </summary>
internal sealed class RequestKeyRing
{
    private readonly Dictionary<string, RequestKeyMaterial> _keys = new(StringComparer.Ordinal);

    public RequestKeyRing(IOptions<RequestEncryptionOptions> options)
    {
        foreach (var (index, key) in options.Value.ConfiguredKeys())
        {
            if (!RequestKeyMaterial.TryParse(key.PrivateKey, out var material))
                throw new InvalidOperationException(
                    $"{RequestEncryptionOptions.Section}:Keys:{index}:PrivateKey is not a readable RSA private key.");
            _keys.TryAdd(material.Kid, material);
        }
    }

    /// <summary>Kids in slot order, for the startup log line.</summary>
    public IReadOnlyCollection<string> Kids => _keys.Keys;

    public bool TryGet(string kid, [NotNullWhen(true)] out RequestKeyMaterial? material) =>
        _keys.TryGetValue(kid, out material);
}
