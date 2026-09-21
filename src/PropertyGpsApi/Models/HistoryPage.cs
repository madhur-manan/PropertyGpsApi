namespace PropertyGpsApi.Models;

/// <summary>One page of history, with the total the page came out of.</summary>
public sealed record HistoryPage(IReadOnlyList<HistoryEntryDto> Entries, int Total, int Start, int Range);
