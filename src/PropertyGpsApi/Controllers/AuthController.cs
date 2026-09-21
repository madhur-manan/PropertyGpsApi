using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PropertyGpsApi.Common;
using PropertyGpsApi.Models;
using PropertyGpsApi.Infrastructure.Security;

using PropertyGpsApi.Interfaces;

using PropertyGpsApi.Services;

namespace PropertyGpsApi.Controllers;

[ApiController]
[Route(ApiRoutes.Base + "/auth")]
public sealed class AuthController(IOtpService otp, IOfficerService officers) : ControllerBase
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
    /// Derived from the token, never from a parameter: an officer can only ever ask who
    /// they themselves are. The mobile claim, not sub — sub is the officer id, and the two
    /// are both numeric strings, so reading the wrong one fails silently as "no such
    /// officer".
    /// </summary>
    [HttpGet("me")]
    [ProducesResponseType<ApiResponse<VerifyOtpResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<VerifyOtpResponse>>> Me(CancellationToken ct) =>
        Ok(ApiResponse<VerifyOtpResponse>.Ok(
            await officers.ProfileAsync(User.FindFirstValue(GpsClaims.Mobile), ct)));
}
