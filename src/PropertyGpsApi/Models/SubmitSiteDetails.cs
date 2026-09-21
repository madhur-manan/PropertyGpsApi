using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PropertyGpsApi.Models;

public sealed class SubmitSiteDetails
{
    [JsonPropertyName("siteArea")] public double? SiteArea { get; init; }
    [JsonPropertyName("eastWest")] public double? EastWest { get; init; }
    [JsonPropertyName("northSouth")] public double? NorthSouth { get; init; }

    [JsonPropertyName("isPropertyUseTypeCorrect")] public int? IsPropertyUseTypeCorrect { get; init; }
    [JsonPropertyName("propertyUseType")] public string? PropertyUseType { get; init; }
    [JsonPropertyName("propertyUseTypeId")] public int? PropertyUseTypeId { get; init; }
    [JsonPropertyName("mstPlanPropertyUseType")] public string? MstPlanPropertyUseType { get; init; }

    /// <summary>All areas are square metres.</summary>
    [JsonPropertyName("commercialArea")] public double? CommercialArea { get; init; }
    [JsonPropertyName("residentialArea")] public double? ResidentialArea { get; init; }
    [JsonPropertyName("industrialArea")] public double? IndustrialArea { get; init; }

    [JsonPropertyName("isGpsLocationCorrect")] public int? IsGpsLocationCorrect { get; init; }
    [JsonPropertyName("correctedLat")] public double? CorrectedLat { get; init; }
    [JsonPropertyName("correctedLng")] public double? CorrectedLng { get; init; }

    [JsonPropertyName("isCornerPlot")] public int? IsCornerPlot { get; init; }
    [JsonPropertyName("isDeclaredRoadFacingSidesCorrect")] public int? IsDeclaredRoadFacingSidesCorrect { get; init; }
    [JsonPropertyName("roadFacingSides")] public int? RoadFacingSides { get; init; }
    [JsonPropertyName("isRoadDetailsCorrect")] public int? IsRoadDetailsCorrect { get; init; }

    [JsonPropertyName("roadDetails")] public IReadOnlyList<SubmitRoadDetail> RoadDetails { get; init; } = [];
}

