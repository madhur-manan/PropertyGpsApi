using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PropertyGpsApi.Common;
using PropertyGpsApi.Features.Auth.Dtos;

namespace PropertyGpsApi.Features.Auth;

[ApiController]
[Route(ApiRoutes.Base + "/auth")]
public sealed class AuthController(IOtpService otp) : ControllerBase
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
}
