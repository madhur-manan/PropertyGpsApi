using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PropertyGpsApi.Features.App.Dtos;

public sealed class VersionCheckRequest
{
    /// <summary>"Android" or "iOS", matched against Mst_AppVersion.App_Platform.</summary>
    [Required(AllowEmptyStrings = false)]
    [MaxLength(50)]
    [JsonPropertyName("platform")]
    public string Platform { get; init; } = "";

    /// <summary>
    /// The installed version, as MAJOR.MINOR.
    ///
    /// Two parts, not three, because USP_CheckMobileAppVersion compares with
    /// CAST(... AS FLOAT): "1.0.0" cannot cast and the check comes back ERROR. The app
    /// therefore sends "1.0" and keeps its own full version for its own comparison. See
    /// the note on the controller - this is a workaround for a defect in the procedure,
    /// not a contract we would choose.
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
