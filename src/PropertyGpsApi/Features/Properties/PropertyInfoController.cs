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
public sealed class PropertyInfoController(
    IApplicationRepository applications,
    IPushStatusRepository pushStatus) : ControllerBase
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

    /// <summary>
    /// "Allot to me". The officer takes ownership of a record before they can survey it.
    /// </summary>
    [HttpPost("assign")]
    [ProducesResponseType<ApiResponse<AssignResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<AssignResponse>>> Assign(
        [FromBody] AssignRequest request, CancellationToken ct)
        => await ChangeAssignment(request, assign: true, ct);

    /// <summary>
    /// Release a record the officer is holding but has not completed, putting it back in the
    /// pool. The same procedure backs both directions via its IsActive flag.
    /// </summary>
    [HttpPost("unassign")]
    [ProducesResponseType<ApiResponse<AssignResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<AssignResponse>>> Unassign(
        [FromBody] AssignRequest request, CancellationToken ct)
        => await ChangeAssignment(request, assign: false, ct);

    private async Task<ActionResult<ApiResponse<AssignResponse>>> ChangeAssignment(
        AssignRequest request, bool assign, CancellationToken ct)
    {
        var officerId = User.RequireLong(GpsClaims.UserId);
        var roleId = (int)User.RequireLong(GpsClaims.RoleId);
        var officerName = User.FindFirstValue("name");

        var outcome = await applications.AssignAsync(
            request.AppId, officerId, roleId, officerName, assign, ct);

        if (!outcome.Succeeded)
        {
            // The procedure rejects with a single 400 and an English sentence, so the code is
            // recovered from the wording. Everything here is the officer's own situation to
            // resolve - a record someone else took, or their own queue being full - so all of
            // it is permanent for this request and recoverable once they act.
            var message = outcome.Message?.Trim() ?? "The record could not be allotted.";
            var code = message.Contains("Already Assigned", StringComparison.OrdinalIgnoreCase)
                ? ApiErrorCodes.AlreadyAssigned
                : message.Contains("pending in your queue", StringComparison.OrdinalIgnoreCase)
                    ? ApiErrorCodes.QueueFull
                    : message.Contains("maximum allowed limit", StringComparison.OrdinalIgnoreCase)
                        ? ApiErrorCodes.ReassignLimitReached
                        : ApiErrorCodes.AssignRejected;

            throw ApiException.Unprocessable(message, code, recoverable: true);
        }

        return Ok(ApiResponse<AssignResponse>.Ok(
            new AssignResponse
            {
                AssignId = outcome.AssignId,
                AppId = outcome.AppId,
                Message = outcome.Message?.Trim()
            },
            message: assign ? "Allotted to you." : "Released."));
    }

    /// <summary>
    /// The device reporting which fetched applications it stored. Accepting one marks it
    /// pushed, which is how the server stops offering it again.
    /// </summary>
    [HttpPost("push-status")]
    [ProducesResponseType<ApiResponse<PushStatusResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PushStatusResponse>>> PushStatus(
        [FromBody] PushStatusRequest request, CancellationToken ct)
    {
        var officerId = User.RequireLong(GpsClaims.UserId);
        var roleId = (int)User.RequireLong(GpsClaims.RoleId);

        var result = await pushStatus.RecordAsync(request, officerId, roleId, ct);

        // Per-item verdicts, so one unknown application cannot make the device re-send the
        // whole batch. success here means "we recorded every outcome", not "all succeeded" -
        // each item carries its own accepted flag.
        return Ok(ApiResponse<PushStatusResponse>.Ok(
            result,
            message: $"{result.Accepted} accepted, {result.Rejected} rejected."));
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
