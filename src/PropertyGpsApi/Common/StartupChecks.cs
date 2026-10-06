using System.Text;

namespace PropertyGpsApi.Common;

/// <summary>
/// Fails fast with an explanation a human can act on.
///
/// The options validators already refuse to start on bad configuration, but they surface as
/// a nested AggregateException stack trace that does not say the one thing an operator needs
/// to know: user secrets are only loaded in the Development environment, so running the
/// published exe (or dotnet PropertyGpsApi.dll, or Visual Studio on a non-Development
/// profile) silently finds no connection strings at all.
/// </summary>
public static class StartupChecks
{
    public static bool TryEnsureConfigured(WebApplicationBuilder builder, out string error)
    {
        error = "";
        var missing = new List<string>();

        void Require(string key)
        {
            if (string.IsNullOrWhiteSpace(builder.Configuration[key])) missing.Add(key);
        }

        Require("Database:Master");
        Require("Database:B2A");

        var jwtKey = builder.Configuration["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
            missing.Add("Jwt:Key");

        var baseMissing = missing.Count > 0;

        // Request-body encryption needs at least one private key unless it is switched Off.
        // Any slot will do, not Keys:0 specifically: rotation removes the old key from slot 0
        // and leaves the new one in slot 1.
        var requestKeyMissing = RequestEncryptionNeedsKey(builder.Configuration);
        if (requestKeyMissing)
            missing.Add("RequestEncryption:Keys:<n>:PrivateKey (at least one)");

        if (missing.Count == 0) return true;

        var isDevelopment = builder.Environment.IsDevelopment();

        var message = new StringBuilder()
            .AppendLine()
            .AppendLine("PropertyGpsApi cannot start: required configuration is missing.")
            .AppendLine()
            .AppendLine($"  Environment : {builder.Environment.EnvironmentName}")
            .AppendLine($"  Missing     : {string.Join(", ", missing)}")
            .AppendLine();

        if (baseMissing && !isDevelopment)
        {
            message
                .AppendLine("  User secrets are ONLY loaded in the Development environment, and this process")
                .AppendLine("  is not running in it - so anything you set with 'dotnet user-secrets' is being")
                .AppendLine("  ignored right now. Either run in Development:")
                .AppendLine()
                .AppendLine("      set ASPNETCORE_ENVIRONMENT=Development")
                .AppendLine("      dotnet run")
                .AppendLine()
                .AppendLine("  or supply the values as environment variables, which is what production should do")
                .AppendLine("  (a double underscore stands for the colon):")
                .AppendLine()
                .AppendLine("      set Database__Master=Server=...;Database=masterDB_prod;...")
                .AppendLine("      set Database__B2A=Server=...;Database=KhataBtoA_prod;...")
                .AppendLine("      set Jwt__Key=<48 random bytes, base64>");
        }
        else if (baseMissing)
        {
            message
                .AppendLine("  Easiest fix - create src/PropertyGpsApi/appsettings.Local.json (git-ignored):")
                .AppendLine()
                .AppendLine("      {")
                .AppendLine("        \"Database\": { \"Master\": \"Server=...\", \"B2A\": \"Server=...\" },")
                .AppendLine("        \"Jwt\": { \"Key\": \"<48 random bytes, base64>\" }")
                .AppendLine("      }")
                .AppendLine()
                .AppendLine("  That works from any IDE, any Windows profile, and from the built exe.")
                .AppendLine()
                .AppendLine("  Or use user secrets, from the src/PropertyGpsApi folder - note these are tied to")
                .AppendLine("  the Windows profile that created them, so an IDE running as another user will")
                .AppendLine("  not see them:")
                .AppendLine()
                .AppendLine("      dotnet user-secrets set \"Database:Master\" \"Server=...;Database=masterDB_prod;...\"")
                .AppendLine("      dotnet user-secrets set \"Database:B2A\"    \"Server=...;Database=KhataBtoA_prod;...\"")
                .AppendLine("      dotnet user-secrets set \"Jwt:Key\"         \"<48 random bytes, base64>\"");
        }

        if (requestKeyMissing)
        {
            if (baseMissing) message.AppendLine();
            message
                .AppendLine("  Request-body encryption (RequestEncryption:Mode is not Off) needs the server's RSA")
                .AppendLine("  private key. Generate one pair per environment, outside every repository:")
                .AppendLine()
                .AppendLine("      dotnet run deploy/New-RequestEncryptionKey.cs -- <name> D:\\PropertyGpsKeys")
                .AppendLine()
                .AppendLine("  It writes <name>-<kid>.private.b64 (one line) and <name>-<kid>.public.pem; the public")
                .AppendLine("  key goes into the app (lib/services/http/request_encryption_keys.dart).");

            if (isDevelopment)
                message
                    .AppendLine()
                    .AppendLine("  On this machine, put the private key into src/PropertyGpsApi/appsettings.Local.json")
                    .AppendLine("  (git-ignored) without echoing it to the screen:")
                    .AppendLine()
                    .AppendLine("      \"RequestEncryption\": { \"Keys\": [ { \"PrivateKey\": \"<contents of local-<kid>.private.b64>\" } ] }")
                    .AppendLine()
                    .AppendLine("  or, to run without request encryption for now, set RequestEncryption:Mode to Off.");
            else
                message
                    .AppendLine()
                    .AppendLine("  On a server, add it to the package web.config as an environment variable (any slot")
                    .AppendLine("  N works; rotation adds the new key in the next slot, then removes the old one):")
                    .AppendLine()
                    .AppendLine("      RequestEncryption__Keys__0__PrivateKey = <contents of <name>-<kid>.private.b64>")
                    .AppendLine("      RequestEncryption__Mode                = Optional")
                    .AppendLine()
                    .AppendLine("  and keep a copy of the private key in BBMP's secret store: losing it means shipping a new APK.");
        }

        message
            .AppendLine()
            .AppendLine("  Never put these in appsettings.json - it is committed to source control.");

        error = message.ToString();
        return false;
    }

    /// <summary>
    /// True when RequestEncryption:Mode is anything but Off (Optional is the default) and no
    /// RequestEncryption:Keys entry has a non-blank PrivateKey.
    /// </summary>
    internal static bool RequestEncryptionNeedsKey(IConfiguration configuration)
    {
        var mode = configuration["RequestEncryption:Mode"]?.Trim();
        if (string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase)) return false;

        return !configuration.GetSection("RequestEncryption:Keys").GetChildren()
            .Any(slot => !string.IsNullOrWhiteSpace(slot["PrivateKey"]));
    }
}
