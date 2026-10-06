using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Infrastructure.Security.BodyEncryption;

namespace PropertyGpsApi.Infrastructure.Options;

/// <summary>What the API does with a request body that arrives without encryption.</summary>
public enum RequestEncryptionMode
{
    /// <summary>Nothing is decrypted, and a sealed body is refused. For a machine with no key.</summary>
    Off,

    /// <summary>
    /// Sealed bodies are opened; plain bodies still pass, with an Information line naming the
    /// path. The rollout setting: app builds older than 1.1.0 keep working while they retire.
    /// </summary>
    Optional,

    /// <summary>Any non-empty body that did not open is refused with BODY_ENCRYPTION_REQUIRED.</summary>
    Required
}

/// <summary>
/// Request-body encryption (wire format pgps-body/1). The app seals every JSON body, and the
/// JSON part of the survey submit, with the server's RSA public key; the API opens them in
/// RequestBodyDecryptionFilter before model binding, so controllers and the database still see
/// plain values.
///
/// The private keys NEVER go in appsettings.json. They come from appsettings.Local.json on a
/// developer machine and from RequestEncryption__Keys__N__PrivateKey on a server.
/// </summary>
public sealed class RequestEncryptionOptions
{
    public const string Section = "RequestEncryption";

    /// <summary>
    /// The kid of the throwaway key pair the cross-language test vector is built from. Its
    /// private key is public on purpose, so it is refused everywhere except Development.
    /// </summary>
    public const string TestOnlyKid = "3c67a513d1a00e0c";

    public const int MinimumKeyBits = 3072;
    public const long DefaultMaxEnvelopeBytes = 8 * 1024 * 1024;
    public const long MinimumMaxEnvelopeBytes = 1024;
    public const long MaximumMaxEnvelopeBytes = 64 * 1024 * 1024;

    public RequestEncryptionMode Mode { get; init; } = RequestEncryptionMode.Optional;

    /// <summary>
    /// One entry per key the server can open, by slot: "0" for RequestEncryption__Keys__0__*
    /// or the first element of a JSON array, "1" for the next. Rotation adds a new slot, ships
    /// an app with the new public key, then removes the old slot; an entry whose PrivateKey is
    /// blank is ignored.
    ///
    /// A dictionary, not a list, on purpose: the configuration binder packs a list's elements
    /// together, so after rotation left only Keys__1 a list would call it Keys:0 - and every
    /// startup message would send the operator to a slot that is not in web.config.
    /// </summary>
    public Dictionary<string, RequestEncryptionKeyOptions> Keys { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Request paths (below the site, e.g. "/v1/api/gbagps/singlesite/auth/otp/send") that may
    /// still send a plain body when Mode is Required. Exact match, case-insensitive. Empty.
    /// </summary>
    public List<string> PlainAllowedPaths { get; init; } = [];

    /// <summary>The largest sealed body (or sealed form field) that will be read.</summary>
    public long MaxEnvelopeBytes { get; init; } = DefaultMaxEnvelopeBytes;

    /// <summary>
    /// 0 (the default) only logs the envelope's ts. Above 0, an envelope whose ts is further
    /// than this from the server clock is refused - only ever after it has decrypted, because
    /// ts is authenticated, not secret.
    /// </summary>
    public int MaxAgeMinutes { get; init; }

    /// <summary>The configured entries that actually carry a key, each with the slot it was configured in.</summary>
    public IEnumerable<(string Slot, RequestEncryptionKeyOptions Key)> ConfiguredKeys() =>
        Keys.Select(e => (Slot: e.Key, Key: e.Value))
            .Where(e => e.Key is not null && !string.IsNullOrWhiteSpace(e.Key.PrivateKey));
}

public sealed class RequestEncryptionKeyOptions
{
    /// <summary>
    /// Optional. When set it must equal the kid derived from the key, which catches a key
    /// pasted into the wrong slot or the wrong environment.
    /// </summary>
    public string? Id { get; init; }

    /// <summary>One line of base64 PKCS#8 DER (what the key tool writes), or PEM.</summary>
    public string PrivateKey { get; init; } = "";
}

/// <summary>
/// Refuses to start on a key the API could not use. Every message names the setting and never
/// its value: these messages end up in a console, an event log and a support email.
/// </summary>
public sealed class RequestEncryptionOptionsValidator : IValidateOptions<RequestEncryptionOptions>
{
    internal static readonly IReadOnlySet<string> DefaultTestOnlyKids =
        new HashSet<string>(StringComparer.Ordinal) { RequestEncryptionOptions.TestOnlyKid };

    private readonly IHostEnvironment _environment;
    private readonly IReadOnlySet<string> _testOnlyKids;

    public RequestEncryptionOptionsValidator(IHostEnvironment environment)
        : this(environment, DefaultTestOnlyKids)
    {
    }

    /// <summary>For tests: the TEST-ONLY rule exercised with a generated key.</summary>
    internal RequestEncryptionOptionsValidator(IHostEnvironment environment, IReadOnlySet<string> testOnlyKids)
    {
        _environment = environment;
        _testOnlyKids = testOnlyKids;
    }

    public ValidateOptionsResult Validate(string? name, RequestEncryptionOptions options)
    {
        var failures = new List<string>();
        var s = RequestEncryptionOptions.Section;

        if (!Enum.IsDefined(options.Mode))
            failures.Add($"{s}:Mode must be Off, Optional or Required.");

        if (options.MaxEnvelopeBytes is < RequestEncryptionOptions.MinimumMaxEnvelopeBytes
            or > RequestEncryptionOptions.MaximumMaxEnvelopeBytes)
            failures.Add($"{s}:MaxEnvelopeBytes must be between {RequestEncryptionOptions.MinimumMaxEnvelopeBytes} " +
                         $"and {RequestEncryptionOptions.MaximumMaxEnvelopeBytes}.");

        if (options.MaxAgeMinutes < 0)
            failures.Add($"{s}:MaxAgeMinutes must be 0 (log only) or a positive number of minutes.");

        if (options.PlainAllowedPaths.Any(p => string.IsNullOrWhiteSpace(p) || !p.StartsWith('/')))
            failures.Add($"{s}:PlainAllowedPaths entries must be request paths starting with '/'.");

        var kids = new Dictionary<string, string>(StringComparer.Ordinal);
        var configured = 0;
        foreach (var (index, key) in options.ConfiguredKeys())
        {
            configured++;
            var slot = $"{s}:Keys:{index}";

            if (!RequestKeyMaterial.TryParse(key.PrivateKey, out var material))
            {
                failures.Add($"{slot}:PrivateKey is not a readable RSA private key " +
                             "(expected one line of base64 PKCS#8, or an unencrypted PEM).");
                continue;
            }

            if (material.KeySizeBits < RequestEncryptionOptions.MinimumKeyBits)
                failures.Add($"{slot}:PrivateKey is shorter than {RequestEncryptionOptions.MinimumKeyBits} bits.");

            if (!string.IsNullOrWhiteSpace(key.Id) && !string.Equals(key.Id.Trim(), material.Kid, StringComparison.Ordinal))
                failures.Add($"{slot}:Id does not match the kid derived from {slot}:PrivateKey.");

            if (kids.TryGetValue(material.Kid, out var first))
                failures.Add($"{slot} holds the same key as {s}:Keys:{first}.");
            else
                kids[material.Kid] = index;

            if (_testOnlyKids.Contains(material.Kid) && !_environment.IsDevelopment())
                failures.Add($"{slot} is the public TEST-ONLY key from the test vector; it may only be used in Development.");
        }

        if (options.Mode != RequestEncryptionMode.Off)
        {
            if (configured == 0)
                failures.Add($"{s}:Mode is {options.Mode} but no {s}:Keys entry has a PrivateKey.");

            if (!AesGcm.IsSupported)
                failures.Add($"AES-GCM is not available on this machine, so {s}:Mode must be Off.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
