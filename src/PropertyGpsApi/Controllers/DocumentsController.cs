using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PropertyGpsApi.Common;
using PropertyGpsApi.Infrastructure.Security;
using PropertyGpsApi.Interfaces;
using PropertyGpsApi.Models;
using PropertyGpsApi.Services;

namespace PropertyGpsApi.Controllers;

/// <summary>
/// The citizen's documents of an application, for the RI to read before surveying it (user,
/// 2026-10-30). Only for an application in one of the officer's wards - the same rule as the
/// survey photos - and a document the officer may not see answers exactly like a missing one.
/// </summary>
[ApiController]
[Authorize]
[Route(ApiRoutes.Base + "/propertyinfo/documents")]
public sealed class DocumentsController(
    ICitizenDocumentService documents,
    IMediaAccessService mediaAccess,
    IOfficerService officers) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<ApiResponse<IReadOnlyList<CitizenDocumentDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<CitizenDocumentDto>>>> List(
        [FromQuery] long appId, CancellationToken ct)
    {
        if (!await MayViewAsync(appId, ct)) return NotFoundResult();
        var list = await documents.ListAsync(appId, ct);
        return Ok(ApiResponse<IReadOnlyList<CitizenDocumentDto>>.Ok(list, message: $"{list.Count} document(s)."));
    }

    [HttpGet("{documentId:long}/file")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> File(long documentId, CancellationToken ct)
    {
        var found = await documents.FindAsync(documentId, ct);
        if (found is not { } doc || !await MayViewAsync(doc.AppId, ct)) return NotFoundResult();

        var file = await documents.OpenAsync(doc.Url, ct);
        if (file is null) return NotFoundResult();

        // As the survey photos: an attachment with no sniffing, so nothing in it can run in a browser.
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        Response.Headers["Cache-Control"] = "private, max-age=0, no-store";
        return File(file.Content, file.ContentType, file.FileName);
    }

    private async Task<bool> MayViewAsync(long appId, CancellationToken ct)
    {
        var epid = await documents.EpidOfAsync(appId, ct);
        if (string.IsNullOrWhiteSpace(epid)) return false;

        var scope = await mediaAccess.ScopeForAsync(epid, ct);
        if (scope is null) return false;

        var roleId = (int)User.RequireLong(GpsClaims.RoleId);
        var wards = JurisdictionRules.IsWardScoped(roleId)
            ? await officers.MappedWardsAsync(User.RequireLong(GpsClaims.UserId), ct)
            : null;
        return JurisdictionRules.MayViewMedia(roleId, wards, User.OptionalLong(GpsClaims.UlbCode), scope);
    }

    private NotFoundObjectResult NotFoundResult() =>
        NotFound(new ApiResponse<object>
        {
            Success = false,
            Code = ApiErrorCodes.NotFound,
            Message = "That document is not available.",
            TraceId = HttpContext.TraceIdentifier
        });
}
