using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Common;
using PropertyGpsApi.Infrastructure.Options;
using PropertyGpsApi.Infrastructure.Security.BodyEncryption;

namespace PropertyGpsApi.Tests;

/// <summary>
/// The RequestEncryption options validator and the startup check. Every refusal must name the
/// setting and never echo the key: these messages land in consoles, event logs and emails.
/// </summary>
public class RequestEncryptionOptionsTests
{
    private static ValidateOptionsResult Validate(RequestEncryptionOptions options, string environment = "Production",
        IReadOnlySet<string>? testOnlyKids = null) =>
        (testOnlyKids is null
            ? new RequestEncryptionOptionsValidator(new Env(environment))
            : new RequestEncryptionOptionsValidator(new Env(environment), testOnlyKids))
        .Validate(Options.DefaultName, options);

    /// <summary>The keys in slots "0", "1", ... - as a JSON array would bind.</summary>
    private static RequestEncryptionOptions WithKeys(RequestEncryptionMode mode, params RequestEncryptionKeyOptions[] keys) =>
        new()
        {
            Mode = mode,
            Keys = keys.Select((key, slot) => (key, slot))
                .ToDictionary(e => e.slot.ToString(System.Globalization.CultureInfo.InvariantCulture), e => e.key)
        };

    private static RequestEncryptionOptions Bind(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build()
            .GetSection(RequestEncryptionOptions.Section).Get<RequestEncryptionOptions>()!;

    private static RequestEncryptionKeyOptions Key(string privateKey, string? id = null) => new() { PrivateKey = privateKey, Id = id };

    /// <summary>No failure message may carry any 20-character run of the configured value.</summary>
    private static void AssertNoKeyMaterial(ValidateOptionsResult result, string configured)
    {
        var text = string.Join("\n", result.Failures ?? []);
        var compact = new string(configured.Where(c => !char.IsWhiteSpace(c)).ToArray());
        for (var i = 0; i + 20 <= compact.Length; i += 7)
            Assert.DoesNotContain(compact.Substring(i, 20), text);
    }

    private static string Failures(ValidateOptionsResult result) => string.Join("\n", result.Failures ?? []);

    // ---- Accepted ---------------------------------------------------------------------------

    [Fact]
    public void A_3072_bit_key_as_one_line_of_base64_PKCS8_is_accepted()
    {
        var result = Validate(WithKeys(RequestEncryptionMode.Optional, Key(TestKeys.Base64(TestKeys.Primary))));
        Assert.True(result.Succeeded, Failures(result));
    }

    [Fact]
    public void PEM_in_either_PKCS8_or_PKCS1_form_is_accepted_and_derives_the_same_kid()
    {
        foreach (var pem in new[] { TestKeys.Pem(TestKeys.Primary), TestKeys.Pkcs1Pem(TestKeys.Primary) })
        {
            var result = Validate(WithKeys(RequestEncryptionMode.Required, Key(pem, TestKeys.Kid(TestKeys.Primary))));
            Assert.True(result.Succeeded, Failures(result));
        }
    }

    [Fact]
    public void An_Id_equal_to_the_derived_kid_is_accepted()
    {
        var result = Validate(WithKeys(RequestEncryptionMode.Optional,
            Key(TestKeys.Base64(TestKeys.Primary), TestKeys.Kid(TestKeys.Primary))));
        Assert.True(result.Succeeded, Failures(result));
    }

    [Fact]
    public void Off_needs_no_key()
    {
        Assert.True(Validate(new RequestEncryptionOptions { Mode = RequestEncryptionMode.Off }).Succeeded);
    }

    [Fact]
    public void Two_different_keys_are_accepted_for_rotation()
    {
        var result = Validate(WithKeys(RequestEncryptionMode.Required,
            Key(TestKeys.Base64(TestKeys.Primary)), Key(TestKeys.Base64(TestKeys.Secondary))));
        Assert.True(result.Succeeded, Failures(result));
    }

    [Fact]
    public void A_blank_entry_is_ignored_so_rotation_can_empty_slot_0()
    {
        var options = WithKeys(RequestEncryptionMode.Required, Key("  "), Key(TestKeys.Base64(TestKeys.Primary)));

        Assert.True(Validate(options).Succeeded);
        Assert.Equal([TestKeys.Kid(TestKeys.Primary)], new RequestKeyRing(Options.Create(options)).Kids);
    }

    [Fact]
    public void Binding_from_configuration_reads_a_sparse_slot_and_a_case_insensitive_mode()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RequestEncryption:Mode"] = "required",
            ["RequestEncryption:Keys:1:PrivateKey"] = TestKeys.Base64(TestKeys.Primary),
            ["RequestEncryption:PlainAllowedPaths:0"] = "/v1/api/gbagps/singlesite/auth/otp/send"
        }).Build();

        var options = configuration.GetSection(RequestEncryptionOptions.Section).Get<RequestEncryptionOptions>()!;

        Assert.Equal(RequestEncryptionMode.Required, options.Mode);
        Assert.Equal(["1"], options.Keys.Keys);   // the slot keeps its name
        Assert.Equal(RequestEncryptionOptions.DefaultMaxEnvelopeBytes, options.MaxEnvelopeBytes);
        Assert.Equal(0, options.MaxAgeMinutes);
        Assert.True(Validate(options).Succeeded);
    }

    [Fact]
    public void After_rotation_a_bad_key_in_slot_1_alone_is_reported_as_slot_1()
    {
        // The end state the rotation steps describe: only RequestEncryption__Keys__1__* is left.
        var options = Bind(new Dictionary<string, string?>
        {
            ["RequestEncryption:Keys:1:PrivateKey"] = "this is not a key",
            ["RequestEncryption:Keys:1:Id"] = TestKeys.Kid(TestKeys.Primary)
        });

        var failures = Failures(Validate(options));
        Assert.Contains("RequestEncryption:Keys:1:PrivateKey", failures);
        Assert.DoesNotContain("Keys:0", failures);
    }

    [Fact]
    public void Slot_names_survive_a_sparse_pair_and_reach_every_message()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["RequestEncryption:Keys:3:PrivateKey"] = TestKeys.Base64(TestKeys.Primary),
            ["RequestEncryption:Keys:7:PrivateKey"] = TestKeys.Pem(TestKeys.Primary),
            ["RequestEncryption:Keys:7:Id"] = TestKeys.Kid(TestKeys.Secondary)
        });

        var failures = Failures(Validate(options));
        Assert.Equal(["3", "7"], options.Keys.Keys);
        Assert.Contains("RequestEncryption:Keys:7:Id does not match", failures);
        Assert.Contains("RequestEncryption:Keys:7 holds the same key as RequestEncryption:Keys:3", failures);
        Assert.DoesNotContain("Keys:0", failures);
        Assert.DoesNotContain("Keys:1", failures);
    }

    [Fact]
    public void A_json_array_binds_to_slots_0_and_1_and_an_empty_array_to_no_keys()
    {
        static RequestEncryptionOptions FromJson(string json) =>
            new ConfigurationBuilder().AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json))).Build()
                .GetSection(RequestEncryptionOptions.Section).Get<RequestEncryptionOptions>()!;

        var primary = TestKeys.Base64(TestKeys.Primary);
        var secondary = TestKeys.Base64(TestKeys.Secondary);
        var two = FromJson($$"""
            { "RequestEncryption": { "Keys": [ { "PrivateKey": "{{primary}}" }, { "PrivateKey": "{{secondary}}" } ] } }
            """);
        Assert.Equal(["0", "1"], two.Keys.Keys);
        Assert.Equal([TestKeys.Kid(TestKeys.Primary), TestKeys.Kid(TestKeys.Secondary)],
            new RequestKeyRing(Options.Create(two)).Kids);

        // What appsettings.json ships: Mode Optional and no keys.
        var none = FromJson("""{ "RequestEncryption": { "Mode": "Optional", "Keys": [], "PlainAllowedPaths": [] } }""");
        Assert.NotNull(none.Keys);
        Assert.Empty(none.Keys);
        Assert.Contains("no RequestEncryption:Keys entry has a PrivateKey", Failures(Validate(none)));
    }

    [Fact]
    public void The_defaults_are_Optional_8_MiB_and_log_only_ts()
    {
        var options = new RequestEncryptionOptions();
        Assert.Equal(RequestEncryptionMode.Optional, options.Mode);
        Assert.Equal(8 * 1024 * 1024, options.MaxEnvelopeBytes);
        Assert.Equal(0, options.MaxAgeMinutes);
        Assert.Empty(options.Keys);
        Assert.Empty(options.PlainAllowedPaths);
    }

    // ---- Refused ----------------------------------------------------------------------------

    public static TheoryData<string, string> UnusableKeys()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var rsa = TestKeys.Import(TestKeys.Primary);
        var b64 = TestKeys.Base64(TestKeys.Primary);

        return new TheoryData<string, string>
        {
            { "not base64 at all", "this is not a key" },
            { "base64 of nothing useful", Convert.ToBase64String(RandomNumberGenerator.GetBytes(1200)) },
            { "base64 split across lines", b64[..800] + "\n" + b64[800..] },
            { "base64 with a trailing byte", Convert.ToBase64String(TestKeys.Primary.Concat(new byte[] { 0 }).ToArray()) },
            { "a public key as base64", Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()) },
            { "a public key as PEM", rsa.ExportSubjectPublicKeyInfoPem() },
            { "an EC key as base64 PKCS#8", Convert.ToBase64String(ec.ExportPkcs8PrivateKey()) },
            { "an EC key as PEM", ec.ExportPkcs8PrivateKeyPem() },
            { "an encrypted PEM", rsa.ExportEncryptedPkcs8PrivateKeyPem("pw",
                new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000)) },
        };
    }

    [Theory]
    [MemberData(nameof(UnusableKeys))]
    public void A_key_that_is_not_a_usable_RSA_private_key_is_refused(string why, string configured)
    {
        _ = why;
        var result = Validate(WithKeys(RequestEncryptionMode.Optional, Key(configured)));

        Assert.True(result.Failed);
        Assert.Contains("RequestEncryption:Keys:0:PrivateKey", Failures(result));
        AssertNoKeyMaterial(result, configured);
    }

    [Fact]
    public void A_key_under_3072_bits_is_refused()
    {
        var configured = TestKeys.Base64(TestKeys.Short);
        var result = Validate(WithKeys(RequestEncryptionMode.Optional, Key(configured)));

        Assert.True(result.Failed);
        Assert.Contains("3072", Failures(result));
        AssertNoKeyMaterial(result, configured);
    }

    [Fact]
    public void An_Id_that_is_not_the_derived_kid_is_refused_without_printing_either()
    {
        var configured = TestKeys.Base64(TestKeys.Primary);
        var wrongId = TestKeys.Kid(TestKeys.Secondary);
        var result = Validate(WithKeys(RequestEncryptionMode.Optional, Key(configured, wrongId)));

        Assert.True(result.Failed);
        Assert.Contains("RequestEncryption:Keys:0:Id", Failures(result));
        Assert.DoesNotContain(wrongId, Failures(result));
        AssertNoKeyMaterial(result, configured);
    }

    [Fact]
    public void The_same_key_twice_is_refused_even_spelled_differently()
    {
        var result = Validate(WithKeys(RequestEncryptionMode.Optional,
            Key(TestKeys.Base64(TestKeys.Primary)), Key(TestKeys.Pem(TestKeys.Primary))));

        Assert.True(result.Failed);
        Assert.Contains("RequestEncryption:Keys:1 holds the same key as RequestEncryption:Keys:0", Failures(result));
    }

    [Theory]
    [InlineData(RequestEncryptionMode.Optional)]
    [InlineData(RequestEncryptionMode.Required)]
    public void Optional_and_Required_need_a_key(RequestEncryptionMode mode)
    {
        var result = Validate(WithKeys(mode, Key("")));
        Assert.True(result.Failed);
        Assert.Contains("no RequestEncryption:Keys entry has a PrivateKey", Failures(result));
    }

    [Theory]
    [InlineData(100L)]
    [InlineData(65L * 1024 * 1024)]
    public void MaxEnvelopeBytes_out_of_range_is_refused(long value)
    {
        var options = new RequestEncryptionOptions { Mode = RequestEncryptionMode.Off, MaxEnvelopeBytes = value };
        Assert.Contains("RequestEncryption:MaxEnvelopeBytes", Failures(Validate(options)));
    }

    [Fact]
    public void A_negative_MaxAgeMinutes_is_refused()
    {
        var options = new RequestEncryptionOptions { Mode = RequestEncryptionMode.Off, MaxAgeMinutes = -1 };
        Assert.Contains("RequestEncryption:MaxAgeMinutes", Failures(Validate(options)));
    }

    [Fact]
    public void A_plain_allowed_path_must_be_a_path()
    {
        var options = new RequestEncryptionOptions { Mode = RequestEncryptionMode.Off, PlainAllowedPaths = ["auth/otp/send"] };
        Assert.Contains("RequestEncryption:PlainAllowedPaths", Failures(Validate(options)));
    }

    [Fact]
    public void An_undefined_mode_is_refused()
    {
        var options = new RequestEncryptionOptions { Mode = (RequestEncryptionMode)7 };
        Assert.Contains("RequestEncryption:Mode", Failures(Validate(options)));
    }

    // ---- The TEST-ONLY key ------------------------------------------------------------------

    [Fact]
    public void The_test_only_kid_is_the_test_vector_key()
    {
        Assert.Equal("3c67a513d1a00e0c", RequestEncryptionOptions.TestOnlyKid);
        Assert.Equal(["3c67a513d1a00e0c"], RequestEncryptionOptionsValidator.DefaultTestOnlyKids.ToArray());
    }

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Staging", false)]
    [InlineData("Testapps", false)]
    [InlineData("Development", true)]
    public void A_test_only_key_is_refused_everywhere_but_Development(string environment, bool accepted)
    {
        // The real TEST key is exercised by RequestEncryptionVectorTests, from the fixture; here
        // a generated key stands in for it so the rule is tested on its own as well.
        var testOnly = new HashSet<string> { TestKeys.Kid(TestKeys.Primary) };
        var configured = TestKeys.Base64(TestKeys.Primary);

        var result = Validate(WithKeys(RequestEncryptionMode.Optional, Key(configured)), environment, testOnly);

        Assert.Equal(accepted, result.Succeeded);
        if (!accepted)
        {
            Assert.Contains("TEST-ONLY", Failures(result));
            AssertNoKeyMaterial(result, configured);
        }
    }

    // ---- StartupChecks ------------------------------------------------------------------------

    [Theory]
    [InlineData(null, null, null, true)]                 // Mode defaults to Optional: a key is needed
    [InlineData("Optional", null, null, true)]
    [InlineData("Required", null, null, true)]
    [InlineData("Off", null, null, false)]
    [InlineData("off", null, null, false)]
    [InlineData("Required", "KEY", null, false)]        // slot 0
    [InlineData("Required", null, "KEY", false)]        // slot 1 only: rotation removed slot 0
    [InlineData("Required", "  ", null, true)]          // blank does not count
    public void StartupChecks_needs_at_least_one_key_in_any_slot_unless_Off(
        string? mode, string? slot0, string? slot1, bool needsKey)
    {
        var values = new Dictionary<string, string?>();
        if (mode is not null) values["RequestEncryption:Mode"] = mode;
        if (slot0 is not null) values["RequestEncryption:Keys:0:PrivateKey"] = slot0;
        if (slot1 is not null) values["RequestEncryption:Keys:1:PrivateKey"] = slot1;
        values["RequestEncryption:Keys:2:Id"] = "an id alone is not a key";

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        Assert.Equal(needsKey, StartupChecks.RequestEncryptionNeedsKey(configuration));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void StartupChecks_explains_a_missing_request_key_and_never_prints_one(string environment)
    {
        var builder = Builder(environment, new Dictionary<string, string?>
        {
            ["Database:Master"] = "Server=x",
            ["Database:B2A"] = "Server=y",
            ["Jwt:Key"] = new string('k', 48),
            ["RequestEncryption:Mode"] = "Optional"
        });

        Assert.False(StartupChecks.TryEnsureConfigured(builder, out var error));
        Assert.Contains("RequestEncryption:Keys", error);
        Assert.Contains("New-RequestEncryptionKey.cs", error);
        Assert.Contains(environment == "Development" ? "appsettings.Local.json" : "RequestEncryption__Keys__0__PrivateKey", error);
        Assert.DoesNotContain("Database__Master", error);  // only the request key is missing
    }

    [Fact]
    public void StartupChecks_passes_with_a_key_or_with_Mode_Off()
    {
        var baseValues = new Dictionary<string, string?>
        {
            ["Database:Master"] = "Server=x",
            ["Database:B2A"] = "Server=y",
            ["Jwt:Key"] = new string('k', 48)
        };

        var withKey = new Dictionary<string, string?>(baseValues)
        {
            ["RequestEncryption:Keys:1:PrivateKey"] = TestKeys.Base64(TestKeys.Primary)
        };
        Assert.True(StartupChecks.TryEnsureConfigured(Builder("Production", withKey), out _));

        var off = new Dictionary<string, string?>(baseValues) { ["RequestEncryption:Mode"] = "Off" };
        Assert.True(StartupChecks.TryEnsureConfigured(Builder("Production", off), out _));
    }

    private static WebApplicationBuilder Builder(string environment, Dictionary<string, string?> values)
    {
        // An empty content root, so no appsettings file on this machine can change the answer.
        var root = Path.Combine(AppContext.BaseDirectory, "empty-content-root");
        Directory.CreateDirectory(root);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
            ContentRootPath = root
        });
        builder.Configuration.AddInMemoryCollection(values);
        return builder;
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "PropertyGpsApi.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
