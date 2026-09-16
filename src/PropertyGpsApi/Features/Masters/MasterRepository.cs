using Dapper;
using PropertyGpsApi.Features.Masters.Dtos;
using PropertyGpsApi.Infrastructure.Data;

namespace PropertyGpsApi.Features.Masters;

public interface IMasterRepository
{
    Task<IReadOnlyList<ZoneDto>> ZonesAsync(int corporationId, CancellationToken ct);
    Task<IReadOnlyList<WardDto>> WardsAsync(int zoneId, CancellationToken ct);
}

/// <summary>
/// Reads mst_AROMapping directly rather than calling usp_s_GetZonesOrWardsByCorpId.
///
/// That procedure hardcodes a three-part reference to [masterDB_prod].[dbo].[mst_AROMapping],
/// a database name that only exists on servers where the copy has not been date-stamped - it
/// fails outright on masterDB_prod_30072026. Thirty-two objects across the two databases share
/// that defect and it is worth BBMP fixing centrally, but the queries themselves are a plain
/// DISTINCT over one table, so a two-part name here works on every copy and keeps this flow
/// independent of that fix.
///
/// Both queries are parameterised; no value is ever concatenated into the SQL.
/// </summary>
internal sealed class MasterRepository(ISqlConnectionFactory connections) : IMasterRepository
{
    private const string ZonesSql = """
        SELECT DISTINCT GBAZoneID AS ZoneId, GBAZoneName_En AS ZoneName
        FROM dbo.mst_AROMapping
        WHERE easthiULBNAME_CorpId = @corporationId AND GBAZoneID IS NOT NULL
        ORDER BY GBAZoneName_En;
        """;

    private const string WardsSql = """
        SELECT DISTINCT BBMPWardId AS WardId, BBMPWardName AS WardName, GBAZoneID AS ZoneId
        FROM dbo.mst_AROMapping
        WHERE GBAZoneID = @zoneId AND BBMPWardId IS NOT NULL
        ORDER BY BBMPWardName;
        """;

    public async Task<IReadOnlyList<ZoneDto>> ZonesAsync(int corporationId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(DbTarget.Master, ct);
        var rows = await connection.QueryAsync<ZoneDto>(
            new CommandDefinition(ZonesSql, new { corporationId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<WardDto>> WardsAsync(int zoneId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(DbTarget.Master, ct);
        var rows = await connection.QueryAsync<WardDto>(
            new CommandDefinition(WardsSql, new { zoneId }, cancellationToken: ct));
        return rows.AsList();
    }
}
