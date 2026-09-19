using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PropertyGpsApi.Common;
using PropertyGpsApi.Features.Auth.Dtos;
using PropertyGpsApi.Infrastructure.Security;

namespace PropertyGpsApi.Features.Auth;

[ApiController]
[Route(ApiRoutes.Base + "/auth")]
public sealed class AuthController(IOtpService otp, IOfficerRepository officers) : ControllerBase
{
    /// <summary>Step 1: the officer enters a mobile number and we text them a code.</summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.OtpSend)]
    [HttpPost("otp/send")]
    [ProducesResponseType<ApiResponse<SendOtpResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<SendOtpResponse>>> SendOtp(
        [FromBody] SendOtpRequest request, CancellationToken ct)
    {
        var result = await otp.SendAsync(request, ct);
        return Ok(ApiResponse<SendOtpResponse>.Ok(result, message: "OTP sent."));
    }

    /// <summary>Step 2: the officer enters the code and we hand back a bearer token.</summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.OtpVerify)]
    [HttpPost("otp/verify")]
    [ProducesResponseType<ApiResponse<VerifyOtpResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<VerifyOtpResponse>>> VerifyOtp(
        [FromBody] VerifyOtpRequest request, CancellationToken ct)
    {
        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        var result = await otp.VerifyAsync(request, clientIp, ct);
        return Ok(ApiResponse<VerifyOtpResponse>.Ok(result, message: "Signed in."));
    }

    /// <summary>
    /// Who the bearer of this token is, and where they work.
    ///
    /// The app is offline-first and keeps its session across restarts, but a JWT carries
    /// claims rather than names - it knows the ward id, not that the ward is Hoodi. Without
    /// this an officer reopening the app would be asked to sign in again despite holding a
    /// valid token, purely because the client had forgotten the labels.
    ///
    /// Deliberately derived from the token, never from a parameter: an officer can only
    /// ever ask who they themselves are.
    /// </summary>
    [HttpGet("me")]
    [ProducesResponseType<ApiResponse<VerifyOtpResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<VerifyOtpResponse>>> Me(CancellationToken ct)
    {
        // The mobile claim, not sub. sub is the officer id - the two are both numeric
        // strings, so reading the wrong one fails silently as "no such officer".
        var mobile = User.FindFirstValue(GpsClaims.Mobile);
        if (string.IsNullOrWhiteSpace(mobile))
            throw ApiException.Unauthorized("Your session is not valid. Please sign in again.");

        var officer = await officers.LoadAsync(mobile, ct);
        if (officer is null)
            throw ApiException.Unauthorized("Your account is no longer active. Please sign in again.");

        return Ok(ApiResponse<VerifyOtpResponse>.Ok(new VerifyOtpResponse
        {
            // No token is minted here. This answers who the caller already is; issuing a
            // fresh one would turn a lookup into a silent, unbounded session extension.
            Token = "",
            ExpiresAt = default,
            UserId = officer.OfficerId,
            RoleId = officer.RoleId,
            Mobile = officer.Mobile,
            UserName = officer.Name,
            Designation = officer.RoleName,
            CorporationId = officer.CorporationId,
            CorporationName = officer.CorporationName,
            ZoneId = officer.ZoneId,
            WardId = officer.WardId,
            Jurisdictions = officer.Jurisdictions.Select(j => new JurisdictionDto
            {
                GbaZoneId = j.GbaZoneId,
                GbaZoneName = j.GbaZoneName,
                ZoneId = j.ZoneId,
                ZoneName = j.ZoneName,
                WardId = j.WardId,
                WardName = j.WardName,
                CorporationId = j.CorporationId,
                CorporationName = j.CorporationName
            }).ToList()
        }));
    }
}
