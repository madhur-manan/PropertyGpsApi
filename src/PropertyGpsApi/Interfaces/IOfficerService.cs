using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Common;
using PropertyGpsApi.Models;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Infrastructure.Options;

namespace PropertyGpsApi.Interfaces;

public interface IOfficerService
{
    Task<bool> OfficerExistsAsync(string mobile, int roleId, CancellationToken ct);
    Task<long> StoreOtpAsync(string mobile, string otp, CancellationToken ct);
    Task<OtpValidationResult> ValidateOtpAsync(string mobile, string otp, CancellationToken ct);
    Task RecordLoginAsync(Officer officer, string? clientIp, CancellationToken ct);

    /// <summary>Loads an officer without an OTP, for rehydrating a session from a token.</summary>
    Task<Officer?> LoadAsync(string mobile, CancellationToken ct);

    /// <summary>
    /// Who the bearer of a token is, and where they work — the `auth/me` answer.
    ///
    /// Returns the same shape as a sign-in but deliberately without a token; see
    /// the implementation for why.
    /// </summary>
    Task<VerifyOtpResponse> ProfileAsync(string? mobile, CancellationToken ct);
}
