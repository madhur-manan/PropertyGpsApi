using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PropertyGpsApi.Common;
using PropertyGpsApi.Infrastructure.Security;
using PropertyGpsApi.Interfaces;
using PropertyGpsApi.Models;

namespace PropertyGpsApi.Controllers;

/// <summary>
/// The new Fetch / Update (the app's WARD_SYNC build). Sits beside propertyinfo/fetch and
/// propertyinfo/push-status, which are unchanged and still serve the old build.
/// </summary>
[ApiController]
[Authorize]
[Route(ApiRoutes.Base + "/propertyinfo/ward-sync")]
public sealed class WardSyncController(IWardSyncService wardSync) : ControllerBase
{
    /// <summary>
    /// The next batch (up to 100 applications) this phone lacks in one of the officer's
    /// mapped wards, with the counts the progress screen shows.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<ApiResponse<WardSyncResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<WardSyncResponse>>> Page(
        [FromBody] WardSyncRequest request, CancellationToken ct)
    {
        var result = await wardSync.PageAsync(request, User.RequireLong(GpsClaims.UserId), ct);

        return Ok(ApiResponse<WardSyncResponse>.Ok(
            result,
            page: new PageInfo
            {
                Start = 0,
                Range = Math.Clamp(request.BatchSize, 1, WardSyncLimits.MaxBatch),
                Returned = result.Returned,
                Total = result.MissingCount
            }));
    }

    /// <summary>
    /// Sent by the phone after it has saved a batch: records IsPushedToGps = 1 and, where it
    /// is empty, PushedToGpsDate. Recording only - nothing reads these to decide what to send.
    /// </summary>
    [HttpPost("ack")]
    [ProducesResponseType<ApiResponse<WardSyncAckResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<WardSyncAckResponse>>> Ack(
        [FromBody] WardSyncAckRequest request, CancellationToken ct)
    {
        var result = await wardSync.AckAsync(request, User.RequireLong(GpsClaims.UserId), ct);

        return Ok(ApiResponse<WardSyncAckResponse>.Ok(
            result,
            message: $"{result.Requested} recorded, {result.Updated} newly marked."));
    }
}
