using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Common;
using PropertyGpsApi.Infrastructure.Options;
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
public sealed class WardSyncController(IWardSyncService wardSync, IOptions<SurveyOptions> survey) : ControllerBase
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
        result.MinMixedExtentPercent = survey.Value.MinMixedExtentPercent;
        result.MaxCompletedOffline = survey.Value.MaxCompletedOffline;
        result.MaxAllottedPerOfficer = survey.Value.MaxAllottedPerOfficer;

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

    /// <summary>
    /// The pull-down on the property list: who holds each of the phone's applications now,
    /// so an allotment or a release made on another phone shows here. Downloads nothing.
    /// </summary>
    [HttpPost("holders")]
    [ProducesResponseType<ApiResponse<WardHoldersResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<WardHoldersResponse>>> Holders(
        [FromBody] WardHoldersRequest request, CancellationToken ct)
    {
        var result = await wardSync.HoldersAsync(request, User.RequireLong(GpsClaims.UserId), ct);

        return Ok(ApiResponse<WardHoldersResponse>.Ok(
            result,
            message: $"{result.Holders.Count(h => h.AssignedToUserId != 0)} of {result.Holders.Count} allotted."));
    }

    /// <summary>
    /// A survey QC returned: the earlier answers, for any RI of the ward to read before
    /// allotting it, and to start from after. Writes nothing.
    /// </summary>
    [HttpPost("returned-survey")]
    [ProducesResponseType<ApiResponse<ReturnedSurveyResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ReturnedSurveyResponse>>> ReturnedSurvey(
        [FromBody] ReturnedSurveyRequest request, CancellationToken ct)
    {
        var result = await wardSync.ReturnedSurveyAsync(request, User.RequireLong(GpsClaims.UserId), ct);

        return Ok(ApiResponse<ReturnedSurveyResponse>.Ok(
            result,
            message: result.Survey is null ? "No RI survey of this application is on the server." : "Earlier answers."));
    }
}
