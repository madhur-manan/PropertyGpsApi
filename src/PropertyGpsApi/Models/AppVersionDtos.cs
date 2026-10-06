using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PropertyGpsApi.Models;

public sealed class VersionCheckRequest
{
    /// <summary>"Android" or "iOS", matched against Mst_AppVersion.App_Platform.</summary>
    [Required(AllowEmptyStrings = false)]
    [MaxLength(50)]
    [JsonPropertyName("platform")]
    public string Platform { get; init; } = "";

    /// <summary>
    /// The installed version, in full ("1.1.0"). Builds before 1.1.0 send only MAJOR.MINOR
    /// ("1.0"), which still compares correctly.
    ///
    /// Needs db/24: the original USP_CheckMobileAppVersion compared with
    /// CAST(... AS FLOAT), so "1.1.0" could not cast and the check came back ERROR.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [MaxLength(20)]
    [JsonPropertyName("version")]
    public string Version { get; init; } = "";

    /// <summary>Free text for the check log - model, OS build. Never anything identifying.</summary>
    [MaxLength(500)]
    [JsonPropertyName("deviceInfo")]
    public string? DeviceInfo { get; init; }
}

public sealed class VersionCheckResponse
{
    [JsonPropertyName("needsUpdate")] public bool NeedsUpdate { get; init; }

    /// <summary>
    /// The installed version is below MinRequired_Version. The app must not let the
    /// officer dismiss this one - it is the only part of the verdict the server alone
    /// knows, because it is policy rather than arithmetic.
    /// </summary>
    [JsonPropertyName("forceUpdate")] public bool ForceUpdate { get; init; }

    /// <summary>UP_TO_DATE, UPDATE_AVAILABLE, FORCE_UPDATE_REQUIRED, WARNING or ERROR.</summary>
    [JsonPropertyName("status")] public string Status { get; init; } = "";

    /// <summary>The sentence the procedure wants shown to the officer.</summary>
    [JsonPropertyName("message")] public string? Message { get; init; }

    [JsonPropertyName("latestVersion")] public string? LatestVersion { get; init; }
    [JsonPropertyName("minRequiredVersion")] public string? MinRequiredVersion { get; init; }
    [JsonPropertyName("downloadUrl")] public string? DownloadUrl { get; init; }
    [JsonPropertyName("fileSizeMb")] public decimal? FileSizeMb { get; init; }
    [JsonPropertyName("releaseNotes")] public string? ReleaseNotes { get; init; }
}
