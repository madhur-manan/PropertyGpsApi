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

        if (missing.Count == 0) return true;

        var isDevelopment = builder.Environment.IsDevelopment();

        var message = new StringBuilder()
            .AppendLine()
            .AppendLine("PropertyGpsApi cannot start: required configuration is missing.")
            .AppendLine()
            .AppendLine($"  Environment : {builder.Environment.EnvironmentName}")
            .AppendLine($"  Missing     : {string.Join(", ", missing)}")
            .AppendLine();

        if (!isDevelopment)
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
        else
        {
            message
                .AppendLine("  Set them with user secrets, from the src/PropertyGpsApi folder:")
                .AppendLine()
                .AppendLine("      dotnet user-secrets set \"Database:Master\" \"Server=...;Database=masterDB_prod;...\"")
                .AppendLine("      dotnet user-secrets set \"Database:B2A\"    \"Server=...;Database=KhataBtoA_prod;...\"")
                .AppendLine("      dotnet user-secrets set \"Jwt:Key\"         \"<48 random bytes, base64>\"");
        }

        message
            .AppendLine()
            .AppendLine("  Never put these in appsettings.json - it is committed to source control.");

        error = message.ToString();
        return false;
    }
}
