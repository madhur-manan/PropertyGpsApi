namespace PropertyGpsApi.Infrastructure.Options;

/// <summary>
/// BBMP's Scality document service (user, 2026-10-30): the RI's photographs are stored there
/// when Media:Store is "Scality", and the citizen's documents (BtoA_DocumentTran.docTrn_url,
/// a Scality reference key) are always read from there.
///
/// ClientId and ClientSecret are never in appsettings.json or git: they come from the server's
/// web.config (Scality__ClientId, Scality__ClientSecret), like the database password.
/// </summary>
public sealed class ScalityOptions
{
    public const string Section = "Scality";

    public string BaseUrl { get; init; } = "https://bbmp.karnataka.gov.in/scalityservice";
    public string ClientId { get; init; } = "";
    public string ClientSecret { get; init; } = "";

    /// <summary>upload-base64's tableName. Keys come back as "{bucket}_{table}/{guid}.{ext}".</summary>
    public string TableName { get; init; } = "udd_b2a";

    public string BucketName { get; init; } = "bbmpit";

    public int TimeoutSeconds { get; init; } = 60;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
