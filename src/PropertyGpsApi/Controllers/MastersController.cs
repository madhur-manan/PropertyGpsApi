using Microsoft.AspNetCore.Mvc;
using PropertyGpsApi.Common;
using PropertyGpsApi.Models;

using PropertyGpsApi.Interfaces;

using PropertyGpsApi.Services;

namespace PropertyGpsApi.Controllers;

/// <summary>
/// ULB, ward and road pickers. Property GPS works by ULB + ward number, so "zones" now lists
/// ULBs and "wards" a ULB's wards by number; the routes are kept for older callers. The app
/// itself takes its wards from sign-in and asks here only for a ward's roads.
/// </summary>
[ApiController]
[Route(ApiRoutes.Base + "/masters")]
public sealed class MastersController(IMasterService masters) : ControllerBase
{
    /// <summary>The ULBs, of one district when districtCode is given.</summary>
    [HttpGet("zones")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<UlbDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<UlbDto>>>> Zones(
        [FromQuery] int? districtCode, CancellationToken ct)
    {
        if (districtCode is <= 0)
            throw ApiException.BadRequest("districtCode must be a positive number when given.");

        var ulbs = await masters.UlbsAsync(districtCode, ct);
        return Ok(ApiResponse<IReadOnlyList<UlbDto>>.Ok(ulbs));
    }

    /// <summary>The wards of one ULB, by ward number.</summary>
    [HttpGet("wards")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<WardDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<WardDto>>>> Wards(
        [FromQuery] int ulbCode, CancellationToken ct)
    {
        if (ulbCode <= 0)
            throw ApiException.BadRequest("ulbCode is required.");

        var wards = await masters.WardsAsync(ulbCode, ct);
        return Ok(ApiResponse<IReadOnlyList<WardDto>>.Ok(wards));
    }

    /// <summary>
    /// The ward's roads, for the road pickers on the survey form: streetId and streetName are
    /// the pair the submit sends back as roadId / roadName (see StreetDto). The ward number is
    /// text, URL-encoded.
    /// </summary>
    [HttpGet("streets")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<StreetDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<StreetDto>>>> Streets(
        [FromQuery] int ulbCode, [FromQuery] string? wardNumber,
        CancellationToken ct)
    {
        if (ulbCode <= 0 || !WardNumbers.IsValid(wardNumber))
            throw ApiException.BadRequest(
                $"ulbCode and wardNumber (at most {WardNumbers.MaxLength} characters) are both required.");

        var streets = await masters.StreetsAsync(ulbCode, WardNumbers.Clean(wardNumber)!, ct);

        return Ok(ApiResponse<IReadOnlyList<StreetDto>>.Ok(
            streets,
            page: new PageInfo
            {
                Start = 0, Range = streets.Count,
                Returned = streets.Count, Total = streets.Count
            }));
    }
}
