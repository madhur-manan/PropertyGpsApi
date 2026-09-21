namespace PropertyGpsApi.Models;

/// <summary>Which zone and ward an EPID belongs to.</summary>
public sealed record MediaScope(int ZoneId, int WardId);
