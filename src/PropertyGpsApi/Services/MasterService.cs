using System.Data;
using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;
using PropertyGpsApi.Models;
using PropertyGpsApi.Infrastructure.Data;

using PropertyGpsApi.Interfaces;

namespace PropertyGpsApi.Services;


/// <summary>
/// The pickers' lists, from the master database (UDD_MASTERDB): ULBs, their wards, and a
/// ward's roads.
///
/// Property GPS works by ULB + ward number. UDD_MASTERDB's mst_AROMapping names ULBs and
/// wards (DistrictCode, ULBCode, ULBName_EN, WardNumber, WardName_EN, StreetNumber,
/// StreetName_EN) and has none of the GBA/BBMP zone columns the old master had, so nothing
/// here reads them. Its text columns carry stray tabs and spaces, stripped as they are read.
///
/// Every query is parameterised; no value is ever concatenated into the SQL.
/// </summary>
internal sealed class MasterService(ISqlConnectionFactory connections, ILogger<MasterService> logger) : IMasterService
{
    /// <summary>The ULBs (masters/zones, which listed zones when there were zones).</summary>
    internal const string UlbsSql = """
        SELECT DISTINCT u.UlbCode, u.UlbName
        FROM (SELECT UlbCode = CONVERT(int, a.ULBCode),
                     UlbName = LTRIM(RTRIM(REPLACE(a.ULBName_EN, CHAR(9), '')))
              FROM dbo.mst_AROMapping a WITH (NOLOCK)
              WHERE a.ULBCode IS NOT NULL
                AND (@districtCode IS NULL OR a.DistrictCode = @districtCode)) u
        ORDER BY u.UlbName, u.UlbCode;
        """;

    /// <summary>The wards of one ULB, by number (masters/wards).</summary>
    internal const string WardsSql = """
        SELECT DISTINCT w.UlbCode, w.WardNumber, w.WardName
        FROM (SELECT UlbCode    = CONVERT(int, a.ULBCode),
                     WardNumber = LTRIM(RTRIM(REPLACE(a.WardNumber, CHAR(9), ''))),
                     WardName   = LTRIM(RTRIM(REPLACE(a.WardName_EN, CHAR(9), '')))
              FROM dbo.mst_AROMapping a WITH (NOLOCK)
              WHERE a.ULBCode = @ulbCode) w
        WHERE w.WardNumber <> ''
        ORDER BY w.WardName, w.WardNumber;
        """;

    /// <summary>The road list's source: UDD_MASTERDB's own procedure, at ward level.</summary>
    internal const string RoadDataProcedure = "dbo.USP_S_GetRoadData";
    internal const int RoadDataLevel = 3;

    /// <summary>The procedure's catch-all row, which is not a road.</summary>
    internal const string NotARoadId = "99999";

    /// <summary>SQL Server's "Conversion failed" - the procedure could not take this ward.</summary>
    internal const int ConversionFailed = 245;

    /// <summary>The ULB's district, which the procedure needs alongside the ULB.</summary>
    internal const string DistrictSql = """
        SELECT TOP (1) CONVERT(int, a.DistrictCode)
        FROM dbo.mst_AROMapping a WITH (NOLOCK)
        WHERE a.ULBCode = @ulbCode AND a.DistrictCode IS NOT NULL;
        """;

    /// <summary>
    /// The ward's streets straight from mst_AROMapping, aliased as the procedure's columns -
    /// used when the procedure cannot answer (a ward number that is not a whole number, or
    /// one it fails to convert).
    /// </summary>
    internal const string WardStreetsSql = """
        SELECT DISTINCT id         = LTRIM(RTRIM(REPLACE(a.StreetNumber, CHAR(9), ''))),
                        streetName = LTRIM(RTRIM(REPLACE(a.StreetName_EN, CHAR(9), '')))
        FROM dbo.mst_AROMapping a WITH (NOLOCK)
        WHERE a.ULBCode = @ulbCode
          AND LTRIM(RTRIM(REPLACE(a.WardNumber, CHAR(9), ''))) = @wardNumber;
        """;

    /// <summary>
    /// The ward's roads, for the road pickers: USP_S_GetRoadData at level 3 (its columns id
    /// and streetName), or mst_AROMapping's StreetNumber / StreetName_EN when the procedure
    /// cannot take this ward. The id is the text the submit sends back as roadId.
    /// </summary>
    public async Task<IReadOnlyList<StreetDto>> StreetsAsync(int ulbCode, string wardNumber, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(DbTarget.Master, ct);

        IEnumerable<KsracRoadRow>? rows = null;
        if (RoadDataWardId(wardNumber) is { } wardId)
        {
            try
            {
                var district = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                    DistrictSql, new { ulbCode }, cancellationToken: ct));
                rows = await connection.QueryAsync<KsracRoadRow>(
                    Sp.Call(RoadDataProcedure, RoadDataParameters(district, ulbCode, wardId), ct));
            }
            catch (SqlException ex) when (FallsBackOn(ex.Number))
            {
                logger.LogWarning(
                    "Streets for ULB {Ulb} ward {Ward}: {Procedure} could not convert the ward ({Number}); " +
                    "reading mst_AROMapping instead",
                    ulbCode, wardNumber, RoadDataProcedure, ex.Number);
            }
        }

        rows ??= await connection.QueryAsync<KsracRoadRow>(new CommandDefinition(
            WardStreetsSql, new { ulbCode, wardNumber }, cancellationToken: ct));

        return ToStreets(rows, ulbCode, wardNumber);
    }

    /// <summary>
    /// The ward as the procedure's @wardId (an int): only a whole number - digits, nothing
    /// else - is passed; anything else is read from mst_AROMapping instead.
    /// </summary>
    internal static int? RoadDataWardId(string? wardNumber) =>
        int.TryParse(WardNumbers.Clean(wardNumber), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;

    /// <summary>Only a conversion failure falls back; any other error is a real one.</summary>
    internal static bool FallsBackOn(int sqlErrorNumber) => sqlErrorNumber == ConversionFailed;

    internal static DynamicParameters RoadDataParameters(int? districtCode, int ulbCode, int wardId)
    {
        var p = new DynamicParameters();
        p.Add("@DistCode", districtCode, DbType.Int32);
        p.Add("@ULBCode", ulbCode, DbType.Int32);
        p.Add("@wardId", wardId, DbType.Int32);
        p.Add("@blockid", null, DbType.Int32);
        p.Add("@Level", RoadDataLevel, DbType.Int32);
        return p;
    }

    /// <summary>
    /// One picker entry per street NUMBER - the street's identity in UDD (Eaasthi's StreetId is
    /// mst_AROMapping.StreetNumber). The table has one row per block a street runs through, and
    /// the blocks spell the name their own way ("Sarvajanika Hostel Road" / "Saravajanika ..."),
    /// so the number's most common spelling is shown (ties: alphabetical). Trimmed, without the
    /// procedure's '99999' row or rows with no id or no name. Different numbers may share a
    /// name - a ward has many "1st Cross" - so names are never merged. Sorted by name, then id.
    /// </summary>
    internal static IReadOnlyList<StreetDto> ToStreets(IEnumerable<KsracRoadRow> rows, int ulbCode, string wardNumber) =>
        rows.Select(r => (Id: Text(r.Id), Name: Text(r.StreetName)))
            .Where(r => r.Id is not null && r.Name is not null && r.Id != NotARoadId)
            .GroupBy(r => r.Id!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new StreetDto
            {
                StreetId = g.First().Id!,
                StreetName = g.GroupBy(r => r.Name!, StringComparer.OrdinalIgnoreCase)
                              .OrderByDescending(n => n.Count())
                              .ThenBy(n => n.Key, StringComparer.OrdinalIgnoreCase)
                              .First().Key,
                UlbCode = ulbCode,
                WardNumber = WardNumbers.Clean(wardNumber) ?? ""
            })
            .OrderBy(s => s.StreetName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.StreetId, StringComparer.Ordinal)
            .ToList();

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public async Task<IReadOnlyList<UlbDto>> UlbsAsync(int? districtCode, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(DbTarget.Master, ct);
        var rows = await connection.QueryAsync<UlbDto>(
            new CommandDefinition(UlbsSql, new { districtCode }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<WardDto>> WardsAsync(int ulbCode, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(DbTarget.Master, ct);
        var rows = await connection.QueryAsync<WardDto>(
            new CommandDefinition(WardsSql, new { ulbCode }, cancellationToken: ct));
        return rows.AsList();
    }
}
