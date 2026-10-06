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
[Authorize]
[Route(ApiRoutes.Base + "/app")]
public sealed class AppController(IAppVersionService versions) : ControllerBase
{
    /// <summary>
    /// Whether the installed app is still current.
    ///
    /// Anonymous, because the app checks before sign-in: an app that is not the current
    /// version must not get as far as asking for an OTP. It checks again on every open once
    /// signed in, since an officer can stay signed in across days while a release goes out.
    /// Rate-limited per address like the OTP calls. When a token is sent, the procedure's
    /// log records the officer and role; before sign-in it records 0 and 0.
    ///
    /// Always 200. A version gate that fails loudly is worse than one that fails quietly:
    /// the officer's work does not depend on it, and a red error over a ward they are about
    /// to survey teaches them to ignore errors. The verdict, including the procedure's own
    /// ERROR status, is in the envelope for the app to read.
    /// </summary>
    [HttpPost("version-check")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.VersionCheck)]
    [ProducesResponseType<ApiResponse<VersionCheckResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<VersionCheckResponse>>> VersionCheck(
        [FromBody] VersionCheckRequest request, CancellationToken ct)
    {
        var officerId = User.OptionalLong(GpsClaims.UserId) ?? 0;
        var roleId = (int)(User.OptionalLong(GpsClaims.RoleId) ?? 0);

        return Ok(ApiResponse<VersionCheckResponse>.Ok(
            await versions.CheckAsync(request, officerId, roleId, ct)));
    }
}
