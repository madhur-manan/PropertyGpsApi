using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PropertyGpsApi.Common;
using PropertyGpsApi.Features.Properties.Dtos;
using PropertyGpsApi.Infrastructure.Security;
using PropertyGpsApi.Infrastructure.Storage;

namespace PropertyGpsApi.Features.Properties;

[ApiController]
[Authorize]
[Route(ApiRoutes.Base + "/propertyinfo")]
public sealed class PropertyInfoController(
    IPropertyRepository properties,
    IApplicationRepository applications,
    IPushStatusRepository pushStatus,
    IVerificationSubmitRepository submissions,
    ISubmitMediaBinder mediaBinder,
    IMediaStore mediaStore,
    IMediaAccessReader mediaAccess,
    IHistoryRepository history) : ControllerBase
{
    /// <summary>
    /// Step 3: the officer's ward worklist, which the app stores in SQLite for offline use.
    /// </summary>
    [HttpPost("fetch")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<PropertyDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PropertyDto>>>> Fetch(
        [FromBody] FetchApplicationsRequest request, CancellationToken ct)
    {
        var officerId = User.RequireLong(GpsClaims.UserId);
        var ownWard = User.OptionalLong(GpsClaims.WardId);
        var roleId = (int)User.RequireLong(GpsClaims.RoleId);
        var results = await properties.FetchAsync(
            request, officerId, roleId, ownWard, ct);

        return Ok(ApiResponse<IReadOnlyList<PropertyDto>>.Ok(
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
            throw ApiException.Unprocessable(
                outcome.Text, outcome.RejectionCode, recoverable: true);

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

    /// <summary>
    /// The completed survey coming back from the field. Multipart: one "payload" part
    /// carrying the JSON, and any number of "files" parts. A survey field naming a file by
    /// its basename is what binds that file to its slot.
    /// </summary>
    [HttpPost("add-new")]
    [RequestSizeLimit(80 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 80 * 1024 * 1024, ValueLengthLimit = 8 * 1024 * 1024)]
    [ProducesResponseType<ApiResponse<SubmitVerificationResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<SubmitVerificationResponse>>> AddNew(
        [FromForm] string payload, CancellationToken ct)
    {
        var officerId = User.RequireLong(GpsClaims.UserId);
        var roleId = (int)User.RequireLong(GpsClaims.RoleId);
        // The mobile claim, not sub. sub carries the officer id, and
        // USP_IU_BtoA_StatusDetail_Officer looks the officer up BY MOBILE to stamp the
        // audit trail - so passing the id left BtoA_Status_Master_Tran with no officer
        // attribution at all, silently.
        var mobile = User.FindFirstValue(GpsClaims.Mobile);

        var request = ParsePayload(payload);

        // Files are stored before the transaction opens and are never rolled back. An
        // orphaned file costs disk; a committed row pointing at a photograph that was never
        // written is evidence destroyed, and the device will have marked the record synced.
        try
        {
            // Validation runs before any file is written, and inside the try so that a
            // rejected answer and a rejected road return the same shape - both are
            // permanent for this payload and both are fixable by the officer.
            submissions.Validate(request, roleId, User.OptionalLong(GpsClaims.WardId));

            var mediaUrls = await mediaBinder.StoreAsync(request, Request.Form.Files, ct);

            var result = await submissions.SubmitAsync(
                request, mediaUrls, officerId, roleId, mobile, ct);

            return Ok(ApiResponse<SubmitVerificationResponse>.Ok(
                result, message: "Survey received."));
        }
        catch (SubmitRejectedException rejected)
        {
            // Nothing was written. The files are still on the server, so this is permanent
            // for this payload but recoverable once the road details are corrected.
            return UnprocessableEntity(new ApiResponse<SubmitVerificationResponse>
            {
                Success = false,
                Code = rejected.Code,
                Message = rejected.Summary,
                Errors = rejected.Errors,
                Retryable = false,
                Recoverable = true,
                TraceId = HttpContext.TraceIdentifier
            });
        }
    }

    /// <summary>
    /// Serves a stored survey photograph or note sheet. The route shape matches what
    /// production already stores in the *_Document columns, so URLs written by this API and
    /// by the existing system both resolve.
    /// </summary>
    [HttpGet("file/view/{epid}/{fileName}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ViewFile(string epid, string fileName, CancellationToken ct)
    {
        var roleId = (int)User.RequireLong(GpsClaims.RoleId);

        // A missing file and a file outside the officer's jurisdiction return exactly the
        // same 404. Distinguishing them would turn this endpoint into an oracle for which
        // EPIDs exist, and the URLs are guessable - they carry only an EPID and a timestamp.
        var scope = await mediaAccess.ScopeForAsync(epid, ct);
        var mayView = scope is not null && JurisdictionRules.MayViewMedia(
            roleId,
            User.OptionalLong(GpsClaims.WardId),
            User.OptionalLong(GpsClaims.ZoneId),
            scope);
        if (!mayView) return FileNotFound();

        var file = await mediaStore.OpenAsync(epid, fileName, ct);
        if (file is null) return FileNotFound();

        // Served as an attachment with a server-chosen name and no sniffing, so a file that
        // somehow got past upload validation still cannot execute in a browser.
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        Response.Headers["Cache-Control"] = "private, max-age=0, no-store";

        return File(file.Content, file.ContentType, file.FileName);
    }


    private IActionResult FileNotFound() =>
        NotFound(new ApiResponse<object>
        {
            Success = false,
            Code = ApiErrorCodes.NotFound,
            Message = "That file is not available.",
            TraceId = HttpContext.TraceIdentifier
        });

    /// <summary>
    /// The surveys this officer has already submitted.
    ///
    /// The officer comes from the token, never from the URL. The legacy shape was
    /// history/{userId}, which invites one officer to read another one by editing a number.
    /// </summary>
    [HttpGet("history")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<HistoryEntryDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<HistoryEntryDto>>>> History(
        [FromQuery] int start = 0, [FromQuery] int range = 50, CancellationToken ct = default)
    {
        var page = await history.PageForOfficerAsync(
            User.RequireLong(GpsClaims.UserId), start, range, ct);

        return Ok(ApiResponse<IReadOnlyList<HistoryEntryDto>>.Ok(
            page.Entries,
            page: new PageInfo
            {
                Start = page.Start, Range = page.Range,
                Returned = page.Entries.Count, Total = page.Total
            }));
    }


    /// <summary>
    /// One month of this officer's own work, for the Verification History screen.
    ///
    /// Counted by submission date, not by the citizen's application date: the
    /// screen answers "what did I do this month", and a survey filed in September
    /// against an August application is September's work.
    ///
    /// Defaults to the current month. The officer comes from the token for the
    /// same reason the history list does - a userId in the URL invites one
    /// officer to read another's figures by editing a number.
    /// </summary>
    [HttpGet("history/summary")]
    [ProducesResponseType<ApiResponse<VerificationSummaryDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<VerificationSummaryDto>>> HistorySummary(
        [FromQuery] int? year = null, [FromQuery] int? month = null, CancellationToken ct = default) =>
        Ok(ApiResponse<VerificationSummaryDto>.Ok(
            await history.SummaryForOfficerAsync(
                User.RequireLong(GpsClaims.UserId), year, month, ct)));

    private static SubmitVerificationRequest ParsePayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            throw ApiException.BadRequest("The 'payload' part is missing.");

        try
        {
            return JsonSerializer.Deserialize<SubmitVerificationRequest>(payload, PayloadJson)
                ?? throw ApiException.BadRequest("The 'payload' part was empty.");
        }
        catch (JsonException ex)
        {
            throw ApiException.BadRequest("The 'payload' part is not valid JSON: " + ex.Message);
        }
    }

    /// <summary>
    /// Strict on submit, and deliberately so: we own both ends of this contract, so an
    /// unrecognised key means the client has drifted and should hear about it now rather
    /// than have a field silently ignored.
    /// </summary>
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
}
