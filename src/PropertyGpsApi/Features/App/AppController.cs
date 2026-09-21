using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PropertyGpsApi.Common;
using PropertyGpsApi.Features.App.Dtos;
// RequireLong lives with the Properties controller; shared by two features now, so it
// belongs in Infrastructure.Security beside GpsClaims. Left where it is for now rather
// than moving a helper the submit path depends on as a side effect of this change.
using PropertyGpsApi.Features.Properties;
using PropertyGpsApi.Infrastructure.Security;

namespace PropertyGpsApi.Features.App;

[ApiController]
[Authorize]
[Route(ApiRoutes.Base + "/app")]
public sealed class AppController(IAppVersionRepository versions) : ControllerBase
{
    /// <summary>
    /// Whether the officer's installed app is still current.
    ///
    /// Authenticated because the procedure logs who checked, and because the app only runs
    /// this once an officer is signed in - on every open, since an officer can update the
    /// app while staying signed in across days.
    ///
    /// Always 200. A version gate that fails loudly is worse than one that fails quietly:
    /// the officer's work does not depend on it, and a red error over a ward they are about
    /// to survey teaches them to ignore errors. The verdict, including the procedure's own
    /// ERROR status, is in the envelope for the app to read.
    /// </summary>
    [HttpPost("version-check")]
    [ProducesResponseType<ApiResponse<VersionCheckResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<VersionCheckResponse>>> VersionCheck(
        [FromBody] VersionCheckRequest request, CancellationToken ct)
    {
        var officerId = User.RequireLong(GpsClaims.UserId);
        var roleId = (int)User.RequireLong(GpsClaims.RoleId);

        return Ok(ApiResponse<VersionCheckResponse>.Ok(
            await versions.CheckAsync(request, officerId, roleId, ct)));
    }
}
