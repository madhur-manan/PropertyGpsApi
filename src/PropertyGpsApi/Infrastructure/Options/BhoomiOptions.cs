using System.ComponentModel.DataAnnotations;

namespace PropertyGpsApi.Infrastructure.Options;

/// <summary>
/// Karnataka RD Services "Bhoomi" land records, used to tell an officer what the state's
/// own records say about the land under a plot.
///
/// The credentials are service credentials with a long life, and the token they buy lasts
/// four hours. They live here, on the server, for one reason: an app that called this
/// service directly would have to ship them inside the APK, where anyone holding the file
/// can read them out.
/// </summary>
public sealed class BhoomiOptions
{
    public const string Section = "Bhoomi";

    /// <summary>
    /// False until BBMP hand over credentials for an environment. The endpoint then answers
    /// NOT_CONFIGURED rather than the process refusing to start - a deployment without
    /// Bhoomi is a deployment missing one hint on one question, not a broken one.
    /// </summary>
    public bool Enabled { get; init; }

    public string TokenUrl { get; init; } = "";

    public string LookupUrl { get; init; } = "";

    public string Username { get; init; } = "";

    public string Password { get; init; } = "";

    /// <summary>
    /// Real lookups measured at about 3.5 seconds; validation failures come back in under
    /// half a second. Fifteen leaves room for a slow day without an officer watching a
    /// spinner for a minute.
    /// </summary>
    [Range(1, 120)]
    public int TimeoutSeconds { get; init; } = 15;

    /// <summary>
    /// How early to replace the token. The service issues four hours; refreshing at five
    /// minutes left means a request never sets off holding one that expires mid-flight.
    /// </summary>
    [Range(0, 60)]
    public int RefreshTokenMinutesBeforeExpiry { get; init; } = 5;

    public bool IsUsable => Enabled
        && !string.IsNullOrWhiteSpace(TokenUrl)
        && !string.IsNullOrWhiteSpace(LookupUrl)
        && !string.IsNullOrWhiteSpace(Username)
        && !string.IsNullOrWhiteSpace(Password);
}
