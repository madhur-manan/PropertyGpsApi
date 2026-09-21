using System.Text.Json.Serialization;

namespace PropertyGpsApi.Features.Properties.Dtos;

/// <summary>
/// One day's verification count, broken down by what BBMP have since done with
/// each survey.
/// </summary>
public sealed class DailyVerificationDto
{
    [JsonPropertyName("day")] public int Day { get; init; }
    [JsonPropertyName("date")] public string Date { get; init; } = string.Empty;
    [JsonPropertyName("total")] public int Total { get; init; }
    [JsonPropertyName("pending")] public int Pending { get; init; }
    [JsonPropertyName("approved")] public int Approved { get; init; }
    [JsonPropertyName("rejected")] public int Rejected { get; init; }
    [JsonPropertyName("returned")] public int Returned { get; init; }
}

/// <summary>
/// An officer's own month: how many surveys they filed and where each one has
/// got to. The Verification History screen's MONTHLY INSIGHTS panel.
///
/// Counted by the date the officer SUBMITTED, not by the citizen's application
/// date - the screen answers "what did I do this month", and a survey filed in
/// September for an application made in August is September's work.
/// </summary>
public sealed class VerificationSummaryDto
{
    [JsonPropertyName("year")] public int Year { get; init; }
    [JsonPropertyName("month")] public int Month { get; init; }
    [JsonPropertyName("total")] public int Total { get; init; }
    [JsonPropertyName("pending")] public int Pending { get; init; }
    [JsonPropertyName("approved")] public int Approved { get; init; }
    [JsonPropertyName("rejected")] public int Rejected { get; init; }
    [JsonPropertyName("returned")] public int Returned { get; init; }

    /// <summary>
    /// Only days the officer actually filed something. A month of empty rows
    /// would be thirty lines saying nothing.
    /// </summary>
    [JsonPropertyName("daily")] public IReadOnlyList<DailyVerificationDto> Daily { get; init; } = [];

    /// <summary>
    /// Every month this officer has ever filed in, newest first, as "yyyy-MM".
    /// Drives the month picker, so it only ever offers months with work in them.
    /// </summary>
    [JsonPropertyName("monthsWithWork")] public IReadOnlyList<string> MonthsWithWork { get; init; } = [];
}
