using Dapper;
using PropertyGpsApi.Models;
using PropertyGpsApi.Infrastructure.Data;

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
/// Every query is parameterised; no value is ever concatenated into the SQL.
/// </summary>
internal sealed class MasterService(ISqlConnectionFactory connections) : IMasterService
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
    /// The ward's KSRSAC roads: MstRoadKSRAC for the BBMP zone/ward the GBA ward maps to.
    ///
    /// This is the join USP_IU_BtoA_SiteRoadDetails_Officer verifies a submitted road with
    /// (R.BBMPZoneID / R.BBMPWardId, then Road_ID + Road_Name), and Road_ID is the id space
    /// every citizen Rd_RoadId lives in. The list used to come from USP_S_GetMasterStreetDetails
    /// level 5, which returns BBMP street ids (Road_KSRACId): not one of them equals a Road_ID
    /// in ward 102/54, so every road an officer picked was stored under the wrong id, reported
    /// unverified, and compared against citizen roads in a different id space (defect D2).
    /// It also hid every name but the longest one of a street (D16).
    /// </summary>
    private const string WardRoadsSql = """
        WITH w AS (
            SELECT DISTINCT BBMPZoneID, BBMPWardId
            FROM dbo.mst_AROMapping
            WHERE easthiULBNAME_CorpId = @corporationId AND GBAZoneID = @zoneId AND BBMPWardId = @wardId
              AND BBMPZoneID IS NOT NULL)
        SELECT RoadId = R.Road_ID, RoadName = R.Road_Name
        FROM dbo.MstRoadKSRAC R
        JOIN w ON R.BBMPZoneID = w.BBMPZoneID AND R.BBMPWardId = w.BBMPWardId
        WHERE R.Road_ID > 0 AND ISNULL(R.Road_Active, 1) = 1;
        """;

    public async Task<IReadOnlyList<StreetDto>> StreetsAsync(
        int corporationId, int zoneId, int wardId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(DbTarget.Master, ct);
        var rows = await connection.QueryAsync<KsracRoadRow>(new CommandDefinition(
            WardRoadsSql, new { corporationId, zoneId, wardId }, cancellationToken: ct));

        return ToStreets(rows, zoneId, wardId);
    }

    /// <summary>
    /// One picker entry per road name. The master digitises the roads of a ward as segments,
    /// several Road_IDs under one name (ward 102/54: 132 roads under 62 names, e.g. 976 and
    /// 9317 'Bile Shivale', both on one BBMP street); listing each would put identical entries
    /// side by side that the officer cannot tell apart. Any of the ids verifies in the road
    /// procedure (it matches Road_ID + Road_Name within the ward), so the lowest one is kept,
    /// with that row's own spelling. Names compare as the database does: trimmed,
    /// case-insensitive.
    /// </summary>
    internal static IReadOnlyList<StreetDto> ToStreets(IEnumerable<KsracRoadRow> rows, int zoneId, int wardId) =>
        rows.Where(r => r.RoadId > 0 && !string.IsNullOrWhiteSpace(r.RoadName))
            .GroupBy(r => r.RoadName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(r => r.RoadId).First())
            .Select(r => new StreetDto
            {
                StreetId = r.RoadId,
                StreetName = r.RoadName!.Trim(),
                WardId = wardId,
                ZoneId = zoneId
            })
            .OrderBy(s => s.StreetName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.StreetId)
            .ToList();

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
