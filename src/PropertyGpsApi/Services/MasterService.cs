using Dapper;
using PropertyGpsApi.Models;
using System.Data;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Infrastructure.Options;

using PropertyGpsApi.Interfaces;

namespace PropertyGpsApi.Services;


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
internal sealed class MasterService(
    ISqlConnectionFactory connections,
    IOptions<StoredProcedureOptions> procedures) : IMasterService
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

    /// <summary>
    /// Ward streets, from USP_S_GetMasterStreetDetails at level 5.
    ///
    /// Two things about that procedure shape the mapping. It returns the same fifteen
    /// columns for every one of its nine levels, with the irrelevant ones cast to NULL, so
    /// only four are read here. And level 5 INNER JOINs MstRoadKSRAC, which yields one row
    /// per road name - ward 102/54 comes back as 65 rows for 64 distinct streets, because
    /// one street carries two names. Grouping by id is what stops the picker showing a
    /// duplicate.
    /// </summary>
    public async Task<IReadOnlyList<StreetDto>> StreetsAsync(
        int corporationId, int zoneId, int wardId, CancellationToken ct)
    {
        var p = new DynamicParameters();
        p.Add("@Level", 5, DbType.Int32);
        p.Add("@CorporationID", corporationId, DbType.Int32);
        p.Add("@ZoneID", zoneId, DbType.Int32);
        p.Add("@WardID", wardId, DbType.Int32);
        p.Add("@StreetID", null, DbType.Int32);
        p.Add("@Btoa_RoadID", null, DbType.Int32);

        await using var connection = await connections.OpenAsync(DbTarget.Master, ct);
        var rows = await connection.QueryAsync<MasterStreetRow>(
            Sp.Call(procedures.Value.FetchStreets, p, ct));

        return rows
            .Where(r => r.StreetID is > 0)
            .GroupBy(r => r.StreetID!.Value)
            .Select(g => new StreetDto
            {
                StreetId = g.Key,
                // Longest name wins: where a street carries two, the fuller one is the more
                // useful label for an officer choosing from a list.
                StreetName = g.Select(r => r.StreetName?.Trim())
                              .Where(n => !string.IsNullOrEmpty(n))
                              .OrderByDescending(n => n!.Length)
                              .FirstOrDefault(),
                WardId = g.First().WardID ?? wardId,
                ZoneId = g.First().ZoneID ?? zoneId
            })
            .OrderBy(s => s.StreetName)
            .ToList();
    }

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
