using System.Data;
using Dapper;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Interfaces;

namespace PropertyGpsApi.Services;

/// <summary>
/// dbo.GpsOfficerSession in the master database, beside mst_Officer and Login_Tran.
/// One row per officer: DeviceId is the phone the officer is bound to (set by their first
/// sign-in, kept through sign-outs, cleared only by an administrator), SessionId the
/// sign-in currently live on it. Three statements, no procedures.
/// </summary>
internal sealed class OfficerSessionStore(ISqlConnectionFactory connections) : IOfficerSessionStore
{
    /// <summary>The longest phone id kept. The app sends "a:" plus a 16-hex ANDROID_ID.</summary>
    internal const int MaxDeviceIdLength = 64;

    // Read and write under one lock, so two phones signing in together cannot both bind.
    // An officer with no phone yet (no row, or DeviceId cleared by an administrator) is
    // bound to this one. Any other phone is refused for good. The binding is never moved
    // here - not even with the check switched off. Returns 1 when started, 0 when refused.
    internal const string StartSql = """
        SET XACT_ABORT ON;
        BEGIN TRAN;
        DECLARE @curSession uniqueidentifier, @curDevice nvarchar(64), @found bit = 0;
        SELECT @curSession = SessionId, @curDevice = DeviceId, @found = 1
        FROM dbo.GpsOfficerSession WITH (UPDLOCK, HOLDLOCK)
        WHERE OfficerId = @OfficerId;

        DECLARE @blocked bit = CASE
            WHEN @Enforce = 1 AND @curDevice IS NOT NULL
             AND (@DeviceId IS NULL OR @curDevice <> @DeviceId)
            THEN 1 ELSE 0 END;

        IF @blocked = 0 AND @found = 0
            INSERT dbo.GpsOfficerSession (OfficerId, SessionId, DeviceId, ClientIp, StartedAt)
            VALUES (@OfficerId, @SessionId, @DeviceId, @ClientIp, GETDATE());
        ELSE IF @blocked = 0
            UPDATE dbo.GpsOfficerSession
               SET SessionId = @SessionId, DeviceId = ISNULL(DeviceId, @DeviceId), ClientIp = @ClientIp,
                   StartedAt = GETDATE(), EndedAt = NULL,
                   ReplacedCount = ReplacedCount + CASE WHEN @curSession IS NULL THEN 0 ELSE 1 END
             WHERE OfficerId = @OfficerId;
        COMMIT;

        SELECT CAST(1 - @blocked AS bit);
        """;

    internal const string CheckSql = """
        SELECT CAST(1 AS bit) FROM dbo.GpsOfficerSession
        WHERE OfficerId = @OfficerId AND SessionId = @SessionId;
        """;

    internal const string EndSql = """
        UPDATE dbo.GpsOfficerSession
           SET SessionId = NULL, EndedAt = GETDATE()
         WHERE OfficerId = @OfficerId AND SessionId = @SessionId;
        """;

    public async Task<bool> TryStartAsync(
        long officerId, Guid sessionId, string? deviceId, string? clientIp, bool enforce, CancellationToken ct)
    {
        var p = Keys(officerId, sessionId);
        p.Add("@DeviceId", CleanDeviceId(deviceId), DbType.String, size: MaxDeviceIdLength);
        p.Add("@ClientIp", Truncate(clientIp, 64), DbType.String, size: 64);
        p.Add("@Enforce", enforce, DbType.Boolean);

        await using var connection = await connections.OpenAsync(DbTarget.Master, ct);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(StartSql, p, cancellationToken: ct));
    }

    public async Task<bool> IsCurrentAsync(long officerId, Guid sessionId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(DbTarget.Master, ct);
        return await connection.ExecuteScalarAsync<bool?>(
            new CommandDefinition(CheckSql, Keys(officerId, sessionId), cancellationToken: ct)) == true;
    }

    public async Task EndAsync(long officerId, Guid sessionId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(DbTarget.Master, ct);
        await connection.ExecuteAsync(new CommandDefinition(EndSql, Keys(officerId, sessionId), cancellationToken: ct));
    }

    /// <summary>
    /// The phone id as the app sends it. Trimmed, blank becomes null, and anything longer
    /// than the column is cut rather than refused.
    /// </summary>
    internal static string? CleanDeviceId(string? deviceId) =>
        string.IsNullOrWhiteSpace(deviceId) ? null : Truncate(deviceId.Trim(), MaxDeviceIdLength);

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

    private static DynamicParameters Keys(long officerId, Guid sessionId)
    {
        var p = new DynamicParameters();
        p.Add("@OfficerId", officerId, DbType.Int64);
        p.Add("@SessionId", sessionId, DbType.Guid);
        return p;
    }
}
