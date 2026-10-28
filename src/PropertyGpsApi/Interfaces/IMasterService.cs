using Dapper;
using PropertyGpsApi.Models;
using System.Data;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Infrastructure.Options;

namespace PropertyGpsApi.Interfaces;

public interface IMasterService
{
    /// <summary>The ULBs of mst_AROMapping, of one district when given.</summary>
    Task<IReadOnlyList<UlbDto>> UlbsAsync(int? districtCode, CancellationToken ct);

    /// <summary>The wards of one ULB, by ward number.</summary>
    Task<IReadOnlyList<WardDto>> WardsAsync(int ulbCode, CancellationToken ct);

    /// <summary>A ward's roads, for the road pickers. The ward number is trimmed text.</summary>
    Task<IReadOnlyList<StreetDto>> StreetsAsync(int ulbCode, string wardNumber, CancellationToken ct);
}
