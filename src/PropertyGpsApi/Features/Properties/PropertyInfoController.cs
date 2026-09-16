using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PropertyGpsApi.Common;
using PropertyGpsApi.Features.Properties.Dtos;
using PropertyGpsApi.Infrastructure.Security;

namespace PropertyGpsApi.Features.Properties;

[ApiController]
[Authorize]
[Route(ApiRoutes.Base + "/propertyinfo")]
public sealed class PropertyInfoController(IApplicationRepository applications) : ControllerBase
{
    /// <summary>
    /// Step 3: the officer's ward worklist, which the app stores in SQLite for offline use.
    /// </summary>
    [HttpPost("fetch")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<ApplicationDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ApplicationDto>>>> Fetch(
        [FromBody] FetchApplicationsRequest request, CancellationToken ct)
    {
        var officerId = User.RequireLong(GpsClaims.UserId);
        var roleId = (int)User.RequireLong(GpsClaims.RoleId);

        // A ward-level officer (role 116) may only read their own ward. Without this the
        // token authenticates but authorises nothing, and any officer could page through
        // every ward in the city by changing three numbers in the body.
        if (roleId == 116)
        {
            var ownWard = User.OptionalLong(GpsClaims.WardId);
            if (ownWard is not null && ownWard != request.WardId)
                throw ApiException.Forbidden(
                    "You can only view applications for your own ward.",
                    ApiErrorCodes.OutsideJurisdiction);
        }

        var results = await applications.FetchAsync(request, officerId, roleId, ct);

        return Ok(ApiResponse<IReadOnlyList<ApplicationDto>>.Ok(
            results,
            page: new PageInfo { Start = 0, Range = results.Count, Returned = results.Count, Total = results.Count }));
    }
}

internal static class ClaimsPrincipalExtensions
{
    public static long RequireLong(this ClaimsPrincipal user, string claimType) =>
        long.TryParse(user.FindFirstValue(claimType), out var value)
            ? value
            : throw ApiException.Unauthorized("Your session is not valid. Please sign in again.");

    public static long? OptionalLong(this ClaimsPrincipal user, string claimType) =>
        long.TryParse(user.FindFirstValue(claimType), out var value) ? value : null;
}
