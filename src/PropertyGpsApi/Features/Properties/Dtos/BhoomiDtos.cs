using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PropertyGpsApi.Features.Properties.Dtos;

public sealed class BhoomiCheckRequest
{
    /// <summary>Karnataka spans roughly 11.5 to 18.5 N; the wider bound still rejects a zero.</summary>
    [Range(-90, 90)]
    [JsonPropertyName("latitude")]
    public double Latitude { get; init; }

    [Range(-180, 180)]
    [JsonPropertyName("longitude")]
    public double Longitude { get; init; }
}

public sealed class BhoomiCheckResponse
{
    /// <summary>
    /// FOUND, NO_LAND_RECORD, OUTSIDE_MAPPED_AREA, INVALID_COORDINATES or UNAVAILABLE.
    ///
    /// NO_LAND_RECORD is the one to read carefully: Bhoomi is the rural land record system,
    /// so a city plot is regularly absent from it. It means "the state's records do not
    /// cover this point", never "this is not government land".
    /// </summary>
    [JsonPropertyName("status")] public string Status { get; init; } = "";

    /// <summary>The service's own sentence, for the officer to read.</summary>
    [JsonPropertyName("message")] public string? Message { get; init; }

    /// <summary>
    /// True when any record under the point is government-held. A prompt to look, not a
    /// verdict: one point can return the same survey number as GOVERNMENT and PRIVATE
    /// together, and the officer still answers the question themselves.
    /// </summary>
    [JsonPropertyName("anyGovernmentParcel")] public bool AnyGovernmentParcel { get; init; }

    [JsonPropertyName("parcels")] public IReadOnlyList<BhoomiParcelDto> Parcels { get; init; } = [];
}

public sealed class BhoomiParcelDto
{
    [JsonPropertyName("district")] public string? District { get; init; }
    [JsonPropertyName("taluk")] public string? Taluk { get; init; }
    [JsonPropertyName("hobli")] public string? Hobli { get; init; }
    [JsonPropertyName("village")] public string? Village { get; init; }
    [JsonPropertyName("surveyNumber")] public string? SurveyNumber { get; init; }
    [JsonPropertyName("ownerName")] public string? OwnerName { get; init; }
    [JsonPropertyName("ownerType")] public string? OwnerType { get; init; }
    [JsonPropertyName("isGovernment")] public bool IsGovernment { get; init; }
}
