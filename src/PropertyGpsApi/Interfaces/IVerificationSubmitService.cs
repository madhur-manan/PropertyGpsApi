using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Common;
using PropertyGpsApi.Models;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Infrastructure.Options;

namespace PropertyGpsApi.Interfaces;

public interface IVerificationSubmitService
{
    /// <summary>
    /// The domain rules a survey must satisfy before anything is written.
    ///
    /// Separate from <see cref="SubmitAsync"/>, and called first, so a survey
    /// that was never going to be accepted does not leave its photographs on
    /// disk: media is stored between the two and is deliberately never rolled
    /// back. Throws SubmitRejectedException carrying one ApiError per problem,
    /// so the officer gets every fault at once rather than one per round trip.
    /// </summary>
    /// <summary>
    /// Also enforces that a ward officer only submits for their own ward - the
    /// same rule as fetch, because the token says who you are, not what you may
    /// write to. Checked here rather than at the edge so both callers of the
    /// rule share one implementation.
    /// </summary>
    void Validate(SubmitVerificationRequest request, int roleId, long? officerWardId);

    Task<SubmitVerificationResponse> SubmitAsync(
        SubmitVerificationRequest request,
        IReadOnlyDictionary<string, string> mediaUrls,
        long officerId,
        int roleId,
        string? officerMobile,
        CancellationToken ct);
}
