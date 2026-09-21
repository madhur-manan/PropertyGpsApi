using System.Text.Json;
using System.Text.Json.Serialization;

namespace PropertyGpsApi.Infrastructure.External;

/// <summary>
/// What the lookup actually told us. The service answers HTTP 200 for every one of these -
/// including its own validation failures - so the status lives in the body and nowhere
/// else. Reading the HTTP code would report an invalid coordinate as a success.
/// </summary>
public enum BhoomiOutcome
{
    /// <summary>RESPONSE_CODE 1 - a boundary polygon and land records were both found.</summary>
    Found,

    /// <summary>
    /// RESPONSE_CODE 2 - the point is inside a mapped polygon but Bhoomi holds no land
    /// record for it. Expected for city plots: Bhoomi is the RURAL land record system, and
    /// a BBMP site frequently has no entry. This is NOT "the land is not government land";
    /// it is "the state's rural records do not cover this point", and the two must never be
    /// collapsed into one answer.
    /// </summary>
    NoLandRecord,

    /// <summary>RESPONSE_CODE 3 - outside every mapped area.</summary>
    OutsideMappedArea,

    /// <summary>RESPONSE_CODE 400 - the service rejected the coordinates.</summary>
    InvalidCoordinates,

    /// <summary>Not configured, unreachable, unauthorised, or an answer we could not read.</summary>
    Unavailable,
}

/// <summary>One Bhoomi land record under the point.</summary>
public sealed record BhoomiParcel(
    string? District,
    string? Taluk,
    string? Hobli,
    string? Village,
    string? SurveyNumber,
    string? OwnerName,
    string? OwnerType)
{
    /// <summary>
    /// Whether this particular record is held by government. One point can return several
    /// records for the same survey number with different owner types - a real response for
    /// survey number 63 carried GOVERNMENT, PRIVATE and GOVERNMENT together - so this is a
    /// property of the record, never of the plot.
    /// </summary>
    public bool IsGovernment =>
        string.Equals(OwnerType?.Trim(), "GOVERNMENT", StringComparison.OrdinalIgnoreCase);
}

public sealed record BhoomiLookup(
    BhoomiOutcome Outcome,
    string? Message,
    IReadOnlyList<BhoomiParcel> Parcels)
{
    public static BhoomiLookup Empty(BhoomiOutcome outcome, string? message) =>
        new(outcome, message, []);

    /// <summary>
    /// True when at least one record under the point is government-held. Deliberately not a
    /// verdict on the plot: it is the trigger for the officer to look, and the officer still
    /// answers the question.
    /// </summary>
    public bool AnyGovernmentParcel => Parcels.Any(p => p.IsGovernment);
}

/// <summary>The service's own wire shape, kept separate from what we hand upwards.</summary>
internal sealed class BhoomiWireResponse
{
    [JsonPropertyName("RESPONSE_CODE")] public string? ResponseCode { get; init; }
    [JsonPropertyName("RESPONSE_MESSAGE")] public string? ResponseMessage { get; init; }
    [JsonPropertyName("LAND_INFO")] public List<BhoomiWireLand>? LandInfo { get; init; }
}

internal sealed class BhoomiWireLand
{
    [JsonPropertyName("DISTRICT_NAME")] public string? District { get; init; }
    [JsonPropertyName("TALUKA_NAME")] public string? Taluka { get; init; }
    [JsonPropertyName("HOBLI_NAME")] public string? Hobli { get; init; }
    [JsonPropertyName("VILLAGE_NAME")] public string? Village { get; init; }

    /// <summary>
    /// Arrives as a JSON number in every response seen so far, but the field is a survey
    /// number and those are not always integers in Karnataka records ("63/1A"). Taken as raw
    /// JSON so a future string cannot make the whole response unreadable.
    /// </summary>
    [JsonPropertyName("SURVEY_NUMBER")] public JsonElement? SurveyNumber { get; init; }
    [JsonPropertyName("OWNER_NAME")] public string? OwnerName { get; init; }
    [JsonPropertyName("OWNER_TYPE")] public string? OwnerType { get; init; }
}
