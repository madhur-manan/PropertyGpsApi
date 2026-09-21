using System.Data;
using Dapper;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Features.App.Dtos;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Infrastructure.Options;

namespace PropertyGpsApi.Features.App;

public interface IAppVersionRepository
{
    Task<VersionCheckResponse> CheckAsync(
        VersionCheckRequest request, long officerId, int roleId, CancellationToken ct);
}

/// <summary>
/// The mobile version gate, over USP_CheckMobileAppVersion.
///
/// The procedure reads masterDB_prod.dbo.Mst_AppVersion for the platform's latest and
/// minimum-required versions and writes a row to BtoA_AppVersion_CheckLog, so a check is a
/// write. It reports its own outcome in a Status column and never throws: a failure inside
/// it comes back as Status = ERROR with the SQL error in Message.
///
/// KNOWN DEFECT in the procedure, worked around by the client and worth fixing at source:
/// it compares versions with CAST(... AS FLOAT). A three-part version such as "1.0.0"
/// cannot cast, so the check returns ERROR and logs nothing, and even for two-part versions
/// the arithmetic is wrong at the tens - 1.10 ranks BELOW 1.9. The app therefore sends
/// MAJOR.MINOR and does its own comparison as well.
/// </summary>
internal sealed class AppVersionRepository(
    ISqlConnectionFactory connections,
    IOptions<StoredProcedureOptions> procedures,
    ILogger<AppVersionRepository> logger) : IAppVersionRepository
{
    public async Task<VersionCheckResponse> CheckAsync(
        VersionCheckRequest request, long officerId, int roleId, CancellationToken ct)
    {
        var p = new DynamicParameters();
        p.Add("@App_Platform", request.Platform, DbType.String, size: 50);
        p.Add("@Client_Version", request.Version, DbType.String, size: 20);
        p.Add("@Client_Device_Info", request.DeviceInfo, DbType.String, size: -1);
        p.Add("@Cby", officerId, DbType.Int32);
        p.Add("@Crole", roleId, DbType.Int32);

        await using var connection = await connections.OpenAsync(DbTarget.B2A, ct);

        var row = await connection.QueryFirstOrDefaultAsync<VersionRow>(
            Sp.Call(procedures.Value.CheckAppVersion, p, ct));

        if (row is null)
        {
            logger.LogWarning("Version check returned no row for {Platform} {Version}.",
                request.Platform, request.Version);
            return new VersionCheckResponse { Status = "ERROR", Message = "No answer from the version service." };
        }

        // ERROR and WARNING are the procedure's own words for "I could not answer". They are
        // reported as-is rather than turned into a failed request: a version check that
        // cannot complete must never stand between an officer and their work.
        if (row.Status is "ERROR" or "WARNING")
            logger.LogWarning(
                "Version check for {Platform} {Version} returned {Status}: {Message}",
                request.Platform, request.Version, row.Status, row.Message);

        return new VersionCheckResponse
        {
            NeedsUpdate = row.NeedsUpdate,
            ForceUpdate = row.IsForceUpdate,
            Status = row.Status ?? "",
            Message = row.Message?.Trim(),
            LatestVersion = row.Latest_Version?.Trim(),
            MinRequiredVersion = row.MinRequired_Version?.Trim(),
            DownloadUrl = row.Download_URL?.Trim(),
            FileSizeMb = row.File_Size_MB,
            ReleaseNotes = row.Release_Notes?.Trim(),
        };
    }

    /// <summary>Column names match the procedure's SELECT aliases exactly.</summary>
    private sealed class VersionRow
    {
        public bool NeedsUpdate { get; init; }
        public bool IsForceUpdate { get; init; }
        public string? Status { get; init; }
        public string? Message { get; init; }
        public string? Latest_Version { get; init; }
        public string? MinRequired_Version { get; init; }
        public string? Download_URL { get; init; }
        public decimal? File_Size_MB { get; init; }
        public string? Release_Notes { get; init; }
    }
}
