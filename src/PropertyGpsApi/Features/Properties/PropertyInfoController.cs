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

        var results = await properties.FetchAsync(request, officerId, ct);

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
        var mobile = User.FindFirstValue("sub");

        var request = ParsePayload(payload);

        // A ward officer may only submit for their own ward, for the same reason fetch is
        // scoped: the token says who you are, not what you may write to.
        if (roleId == 116)
        {
            var ownWard = User.OptionalLong(GpsClaims.WardId);
            if (ownWard is not null && ownWard != request.WardId)
                throw ApiException.Forbidden(
                    "You can only submit surveys for your own ward.",
                    ApiErrorCodes.OutsideJurisdiction);
        }

        // Files are stored before the transaction opens and are never rolled back. An
        // orphaned file costs disk; a committed row pointing at a photograph that was never
        // written is evidence destroyed, and the device will have marked the record synced.
        try
        {
            // Validation lives inside the try so a rejected answer and a rejected road
            // return the same shape. Both are permanent for this payload and both are
            // fixable by the officer.
            Validate(request);

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
        if (scope is null || !MayView(roleId, scope)) return FileNotFound();

        var file = await mediaStore.OpenAsync(epid, fileName, ct);
        if (file is null) return FileNotFound();

        // Served as an attachment with a server-chosen name and no sniffing, so a file that
        // somehow got past upload validation still cannot execute in a browser.
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        Response.Headers["Cache-Control"] = "private, max-age=0, no-store";

        return File(file.Content, file.ContentType, file.FileName);
    }

    private bool MayView(int roleId, MediaScope scope) => roleId switch
    {
        // Ward officer: their own ward only.
        116 => User.OptionalLong(GpsClaims.WardId) is not { } ward || ward == scope.WardId,
        // Zone officer: anywhere in their zone. Role 125 legitimately has a null ward.
        125 => User.OptionalLong(GpsClaims.ZoneId) is not { } zone || zone == scope.ZoneId,
        _ => false
    };

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
        var officerId = User.RequireLong(GpsClaims.UserId);

        if (start < 0) start = 0;
        range = Math.Clamp(range, 1, 200);

        var entries = await history.ForOfficerAsync(officerId, start, range, ct);
        var total = await history.CountForOfficerAsync(officerId, ct);

        return Ok(ApiResponse<IReadOnlyList<HistoryEntryDto>>.Ok(
            entries,
            page: new PageInfo
            {
                Start = start, Range = range,
                Returned = entries.Count, Total = total
            }));
    }

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

    private static void Validate(SubmitVerificationRequest request)
    {
        var errors = new List<ApiError>();

        if (string.IsNullOrWhiteSpace(request.ApplicationId))
            errors.Add(new ApiError { Field = "applicationId", Code = "REQUIRED", Message = "The application id is required." });

        if (string.IsNullOrWhiteSpace(request.Epid))
            errors.Add(new ApiError { Field = "epid", Code = "REQUIRED", Message = "The EPID is required." });

        // The justification is the whole evidentiary value of asserting government land.
        if (request.IsGovtProperty == 1 && string.IsNullOrWhiteSpace(request.GovtPropertyDetails))
            errors.Add(new ApiError
            {
                Field = "govtPropertyDetails",
                Code = "REQUIRED",
                Message = "Please describe why this is government property."
            });

        // These land in bit/int NOT NULL columns on the legacy officer tables. Left
        // unanswered they reach SQL as NULL and the insert fails - which surfaced as a 500,
        // and the client treats 5xx as retryable, so a survey missing one answer would have
        // been resent forever. Naming them here turns that into one clear rejection the
        // officer can act on.
        Require(errors, "propertyLandExists", request.PropertyLandExists);
        Require(errors, "isAllBhoomiSurveyNosCorrect", request.IsAllBhoomiSurveyNosCorrect);
        Require(errors, "siteDetails.isCornerPlot", request.SiteDetails.IsCornerPlot);

        foreach (var (road, i) in request.SiteDetails.RoadDetails.Select((r, i) => (r, i)))
        {
            Require(errors, $"siteDetails.roadDetails[{i}].roadStatus", road.RoadStatus);
            Require(errors, $"siteDetails.roadDetails[{i}].isPresentInPublicRoadList",
                road.IsPresentInPublicRoadList);
        }

        if (request.SiteDetails.RoadDetails.Count == 0)
            errors.Add(new ApiError { Field = "siteDetails.roadDetails", Code = "REQUIRED", Message = "At least one road is required." });

        // 0,0 is in the Gulf of Guinea. It is what a device reports when it never got a
        // fix, and it is the commonest real GPS failure - worth catching here rather than
        // storing a survey that places a Bengaluru property in the Atlantic.
        RejectNullIsland(errors, "siteDetails.correctedLat", request.SiteDetails.CorrectedLat, request.SiteDetails.CorrectedLng);
        foreach (var (road, i) in request.SiteDetails.RoadDetails.Select((r, i) => (r, i)))
        {
            RejectNullIsland(errors, $"siteDetails.roadDetails[{i}].privateRoadLat", road.PrivateRoadLat, road.PrivateRoadLng);
            RejectNullIsland(errors, $"siteDetails.roadDetails[{i}].nearPublicRoadLat", road.NearPublicRoadLat, road.NearPublicRoadLng);
        }

        if (errors.Count > 0)
            throw new SubmitRejectedException(errors);
    }

    private static void Require(List<ApiError> errors, string field, int? value)
    {
        if (value is null)
            errors.Add(new ApiError
            {
                Field = field,
                Code = "REQUIRED",
                Message = "This answer is needed before the survey can be submitted."
            });
    }

    private static void RejectNullIsland(List<ApiError> errors, string field, double? lat, double? lng)
    {
        if (lat is null || lng is null) return;
        if (Math.Abs(lat.Value) < 0.0001 && Math.Abs(lng.Value) < 0.0001)
            errors.Add(new ApiError
            {
                Field = field,
                Code = "NO_GPS_FIX",
                Message = "The device recorded no GPS fix for this point. Please capture it again."
            });
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
