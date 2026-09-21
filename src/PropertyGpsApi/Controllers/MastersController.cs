using Microsoft.AspNetCore.Mvc;
using PropertyGpsApi.Common;
using PropertyGpsApi.Models;

using PropertyGpsApi.Interfaces;

using PropertyGpsApi.Services;

namespace PropertyGpsApi.Controllers;

/// <summary>
/// Zone and ward pickers. The app needs these because USP_S_Officer_ValidateOTP returns
/// ward names but no ward ids, so a jurisdiction from sign-in cannot on its own be turned
/// into a fetch request.
/// </summary>
[ApiController]
[Route(ApiRoutes.Base + "/masters")]
public sealed class MastersController(IMasterService masters) : ControllerBase
{
    [HttpGet("zones")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<ZoneDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ZoneDto>>>> Zones(
        [FromQuery] int corporationId, CancellationToken ct)
    {
        if (corporationId <= 0)
            throw ApiException.BadRequest("corporationId is required.");

        var zones = await masters.ZonesAsync(corporationId, ct);
        return Ok(ApiResponse<IReadOnlyList<ZoneDto>>.Ok(zones));
    }

    [HttpGet("wards")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<WardDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<WardDto>>>> Wards(
        [FromQuery] int zoneId, CancellationToken ct)
    {
        if (zoneId <= 0)
            throw ApiException.BadRequest("zoneId is required.");

        var wards = await masters.WardsAsync(zoneId, ct);
        return Ok(ApiResponse<IReadOnlyList<WardDto>>.Ok(wards));
    }

    /// <summary>
    /// The ward street master, for the road pickers on the survey form. Without it both
    /// pickers degrade to free text and the road names stop matching the KSRSAC master.
    /// </summary>
    [HttpGet("streets")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<StreetDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<StreetDto>>>> Streets(
        [FromQuery] int corporationId, [FromQuery] int zoneId, [FromQuery] int wardId,
        CancellationToken ct)
    {
        if (corporationId <= 0 || zoneId <= 0 || wardId <= 0)
            throw ApiException.BadRequest("corporationId, zoneId and wardId are all required.");

        var streets = await masters.StreetsAsync(corporationId, zoneId, wardId, ct);

        return Ok(ApiResponse<IReadOnlyList<StreetDto>>.Ok(
            streets,
            page: new PageInfo
            {
                Start = 0, Range = streets.Count,
                Returned = streets.Count, Total = streets.Count
            }));
    }
}
