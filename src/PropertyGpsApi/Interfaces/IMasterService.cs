using Dapper;
using PropertyGpsApi.Models;
using System.Data;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Infrastructure.Options;

namespace PropertyGpsApi.Interfaces;

public interface IMasterService
{
    Task<IReadOnlyList<ZoneDto>> ZonesAsync(int corporationId, CancellationToken ct);
    Task<IReadOnlyList<WardDto>> WardsAsync(int zoneId, CancellationToken ct);
    Task<IReadOnlyList<StreetDto>> StreetsAsync(
        int corporationId, int zoneId, int wardId, CancellationToken ct);
}
