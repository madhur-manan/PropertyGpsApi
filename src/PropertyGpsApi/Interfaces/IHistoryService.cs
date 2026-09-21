using Dapper;
using PropertyGpsApi.Models;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Common;

namespace PropertyGpsApi.Interfaces;

public interface IHistoryService
{
    /// <summary>
    /// One page of this officer's submitted surveys, and the total they were
    /// drawn from. Returns both together because a page and its total have to
    /// agree — fetching them through two calls let a survey submitted between
    /// them produce a page whose count did not match its own total.
    ///
    /// <paramref name="start"/> and <paramref name="range"/> are clamped here
    /// rather than at the edge, so every caller gets the same bounds.
    /// </summary>
    Task<HistoryPage> PageForOfficerAsync(
        long officerId, int start, int range, CancellationToken ct);

    /// <summary>
    /// One month of this officer's work. A null year or month means the current
    /// month, decided by the server's clock rather than the caller's — a phone
    /// with a wrong date would otherwise ask for the wrong month and get a
    /// confidently empty answer.
    /// </summary>
    Task<VerificationSummaryDto> SummaryForOfficerAsync(
        long officerId, int? year, int? month, CancellationToken ct);
}
