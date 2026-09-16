using Microsoft.AspNetCore.Mvc;
using PropertyGpsApi.Common;
using PropertyGpsApi.Features.Masters.Dtos;

namespace PropertyGpsApi.Features.Masters;

/// <summary>
/// Zone and ward pickers. The app needs these because USP_S_Officer_ValidateOTP returns
/// ward names but no ward ids, so a jurisdiction from sign-in cannot on its own be turned
/// into a fetch request.
/// </summary>
[ApiController]
[Route(ApiRoutes.Base + "/masters")]
public sealed class MastersController(IMasterRepository masters) : ControllerBase
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
}
