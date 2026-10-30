using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Common;
using PropertyGpsApi.Models;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Infrastructure.Options;

using PropertyGpsApi.Interfaces;

namespace PropertyGpsApi.Services;

/// <summary>
/// Officers, their sign-in and where they work - all in the master database (UDD_MASTERDB).
///
/// Property GPS works by ULB + ward NUMBER only. UDD_MASTERDB's mst_Officer has Ofcr_ULBId and
/// Ofcr_WardId (the ward number, nvarchar) and no zone, corporation or division column, and
/// its mst_AROMapping names ULBs and wards (ULBName_EN, WardNumber, WardName_EN) with no GBA
/// or BBMP columns. Nothing here reads any of the columns that are gone.
/// </summary>
internal sealed class OfficerService(
    ISqlConnectionFactory connections,
    IOptions<StoredProcedureOptions> procedures,
    IOptions<OtpOptions> otpOptions) : IOfficerService
{
    public async Task<long> StoreOtpAsync(string mobile, string otp, CancellationToken ct)
    {
        var p = new DynamicParameters();
        p.Add("@OTP", int.Parse(otp), DbType.Int32);
        p.Add("@MobileNo", mobile, DbType.String, size: 20);
        // Both @DeviceId and @Source get the same value, and that is not a mistake.
        // USP_I_OTP writes @DeviceId into OTP_Tran.DeviceId, but
        // USP_S_Officer_ValidateOTP matches on DeviceId = @Source. Sending anything else
        // as @DeviceId makes every verification fail. Do not "correct" this.
        p.Add("@DeviceId", otpOptions.Value.Source, DbType.String, size: 20);
        p.Add("@Source", otpOptions.Value.Source, DbType.String, size: 20);

        await using var connection = await connections.OpenAsync(DbTarget.Master, ct);

        // The procedure returns SELECT @@IDENTITY AS identityvalue.
        var identity = await connection.ExecuteScalarAsync<decimal?>(
            Sp.Call(procedures.Value.InsertOtp, p, ct));

        return identity is null ? 0 : (long)identity.Value;
    }


    /// <summary>
    /// USP_S_ValidateOfficer. Returns zero rows for an unknown mobile AND for a known but
    /// deactivated one - the procedure cannot distinguish them, and neither should we,
    /// since telling them apart would let anyone enumerate officer mobile numbers.
    /// </summary>
    public async Task<bool> OfficerExistsAsync(string mobile, int roleId, CancellationToken ct)
    {
        var p = new DynamicParameters();
        p.Add("@USR_MOBILENO", mobile, DbType.String, size: 10);
        p.Add("@Ofcr_RoleId", roleId, DbType.Int32);

        await using var connection = await connections.OpenAsync(DbTarget.Master, ct);
        var rows = await connection.QueryAsync(
            Sp.Call(procedures.Value.ValidateOfficer, p, ct));

        return rows.Any();
    }

    /// <summary>
    /// USP_S_Officer_ValidateOTP: one row per active officer row for the mobile, roles 116/117
    /// first, each carrying IsValidOtp. Zero rows is an unknown (or locked) officer.
    ///
    /// The procedure gives the officer's own ULB and ward but no names and no mapped wards, so
    /// the jurisdictions are read after it (JurisdictionsAsync), the same way auth/me reads
    /// them - the two routes always answer the same thing.
    /// </summary>
    public async Task<OtpValidationResult> ValidateOtpAsync(string mobile, string otp, CancellationToken ct)
    {
        var p = new DynamicParameters();
        p.Add("@OTP", int.Parse(otp), DbType.Int32);
        p.Add("@MobileNO", mobile, DbType.String, size: 20);
        p.Add("@Source", otpOptions.Value.Source, DbType.String, size: 20);

        await using var connection = await connections.OpenAsync(DbTarget.Master, ct);

        var rows = (await connection.QueryAsync<OfficerOtpRow>(
            Sp.Call(procedures.Value.ValidateOfficerOtp, p, ct))).AsList();

        if (rows.Count == 0)
            return new OtpValidationResult(OtpValidationOutcome.UnknownOrLockedOfficer, null);

        var first = rows[0];
        if (!first.IsValidOtp)
            return new OtpValidationResult(OtpValidationOutcome.InvalidCode, null);

        var officer = new Officer
        {
            OfficerId = first.Ofcr_Id,
            RoleId = first.Ofcr_RoleId,
            Mobile = first.Ofcr_MNo,
            Name = first.Ofcr_Name,
            RoleName = first.RoleName,
            UlbCode = first.Ofcr_ULBId,
            WardNumber = WardNumbers.Clean(first.Ofcr_WardId)
        };

        officer = officer with
        {
            Jurisdictions = await JurisdictionsAsync(connection, officer.OfficerId, officer.RoleId, ct)
        };

        return new OtpValidationResult(OtpValidationOutcome.Valid, officer);
    }

    /// <summary>
    /// The ULB and ward names of a jurisdiction row <c>w</c>, from mst_AROMapping: one row per
    /// street there, so TOP (1). Its text columns carry stray tabs and spaces, stripped as the
    /// match and the names are read. The ULB name is the same on every row of a ULB.
    /// </summary>
    private const string NamesSql = """
        OUTER APPLY (SELECT TOP (1) UlbName = LTRIM(RTRIM(REPLACE(a.ULBName_EN, CHAR(9), '')))
                     FROM dbo.mst_AROMapping a WITH (NOLOCK)
                     WHERE a.ULBCode = w.UlbCode AND a.ULBName_EN IS NOT NULL) u
        OUTER APPLY (SELECT TOP (1) WardName = LTRIM(RTRIM(REPLACE(a.WardName_EN, CHAR(9), '')))
                     FROM dbo.mst_AROMapping a WITH (NOLOCK)
                     WHERE a.ULBCode = w.UlbCode
                       AND LTRIM(RTRIM(REPLACE(a.WardNumber, CHAR(9), ''))) = w.WardNumber) d
        """;

    /// <summary>
    /// The column of mst_Ofcr_WardMapping that holds the ULB code. The table was rebuilt on
    /// the test DB server on 2026-10-29 with Map_ULBId in place of Map_CorpId; other
    /// databases (the laptop copy, possibly live) still have Map_CorpId. Asked on every read
    /// - one cheap metadata lookup - so a table changed while the API runs is followed at once.
    /// </summary>
    internal const string MappingUlbColumnSql =
        "SELECT CASE WHEN COL_LENGTH(N'dbo.mst_Ofcr_WardMapping', N'Map_ULBId') IS NOT NULL THEN N'Map_ULBId' ELSE N'Map_CorpId' END";

    /// <summary>
    /// The wards an RI (role 117, the only ward-scoped role) holds: their own ward
    /// (mst_Officer.Ofcr_ULBId + Ofcr_WardId) plus every active mst_Ofcr_WardMapping row for
    /// the officer in that role, whose <paramref name="ulbColumn"/> (Map_ULBId, or Map_CorpId
    /// on the older table) is the ULB code and Map_WardId the ward number. Own ward first
    /// (Src 0); duplicates are dropped in ToJurisdictions.
    ///
    /// This is the list the app's dropdown shows and every own-ward check accepts - the ward
    /// sync, the live socket, the submit, the media and the old fetch.
    /// </summary>
    internal static string WardScopedJurisdictionsSql(string ulbColumn) =>
        ulbColumn is "Map_ULBId" or "Map_CorpId"
            ? WardScopedJurisdictionsTemplate.Replace("{ulbColumn}", ulbColumn, StringComparison.Ordinal)
            : throw new ArgumentOutOfRangeException(nameof(ulbColumn), ulbColumn, "Not a ULB column of mst_Ofcr_WardMapping.");

    private const string WardScopedJurisdictionsTemplate = $$"""
        WITH w AS (
            SELECT Src = 0, UlbCode = o.Ofcr_ULBId,
                   WardNumber = NULLIF(LTRIM(RTRIM(REPLACE(o.Ofcr_WardId, CHAR(9), ''))), '')
            FROM dbo.mst_Officer o WITH (NOLOCK)
            WHERE o.Ofcr_Id = @officerId AND ISNULL(o.Ofcr_Active, 0) = 1 AND o.Ofcr_RoleId = 117
            UNION ALL
            SELECT Src = 1, UlbCode = wm.{ulbColumn},
                   WardNumber = NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(20), wm.Map_WardId))), '')
            FROM dbo.mst_Officer o WITH (NOLOCK)
            JOIN dbo.mst_Ofcr_WardMapping wm WITH (NOLOCK)
                   ON wm.Map_OfcrId = o.Ofcr_Id
                  AND wm.Map_OfcrRoleId = o.Ofcr_RoleId
                  AND ISNULL(wm.isActive, 1) = 1
            WHERE o.Ofcr_Id = @officerId AND ISNULL(o.Ofcr_Active, 0) = 1 AND o.Ofcr_RoleId = 117
        )
        SELECT w.Src, w.UlbCode, w.WardNumber, u.UlbName, d.WardName
        FROM w
        {{NamesSql}}
        WHERE w.UlbCode > 0 AND w.WardNumber IS NOT NULL
        ORDER BY w.Src, w.UlbCode, w.WardNumber;
        """;

    /// <summary>
    /// Any other role's own ULB and ward (the ward may be absent above ward level). Mappings
    /// are not read for them: mst_Ofcr_WardMapping is the ward officers' table.
    /// </summary>
    internal const string OwnWardSql = $$"""
        WITH w AS (
            SELECT Src = 0, UlbCode = o.Ofcr_ULBId,
                   WardNumber = NULLIF(LTRIM(RTRIM(REPLACE(o.Ofcr_WardId, CHAR(9), ''))), '')
            FROM dbo.mst_Officer o WITH (NOLOCK)
            WHERE o.Ofcr_Id = @officerId AND ISNULL(o.Ofcr_Active, 0) = 1
        )
        SELECT w.Src, w.UlbCode, w.WardNumber, u.UlbName, d.WardName
        FROM w
        {{NamesSql}}
        WHERE w.UlbCode > 0;
        """;

    /// <summary>Role 117 (RI): the field officer whose work is ward by ward.</summary>
    internal static bool IsWardScoped(int roleId) => JurisdictionRules.IsWardScoped(roleId);

    /// <summary>
    /// The ULBs/wards the app's dropdowns list.
    ///
    /// A ward-scoped officer gets exactly the wards they are mapped to and nothing else -
    /// even when that is none. An empty list shows "No ward is mapped to your account" in
    /// the app, which is the truth and is fixed in the master tables. Other roles get their
    /// own ULB and ward.
    /// </summary>
    internal static IReadOnlyList<Jurisdiction> ChooseJurisdictions(
        int roleId, IReadOnlyList<Jurisdiction> mapped, IReadOnlyList<Jurisdiction> fallback)
        => IsWardScoped(roleId) ? mapped : (mapped.Count > 0 ? mapped : fallback);

    public async Task<IReadOnlyList<Jurisdiction>> MappedWardsAsync(long officerId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(DbTarget.Master, ct);
        return await QueryJurisdictionsAsync(connection, await WardScopedSqlAsync(connection, ct), officerId, ct);
    }

    private static async Task<string> WardScopedSqlAsync(SqlConnection connection, CancellationToken ct) =>
        WardScopedJurisdictionsSql(await connection.ExecuteScalarAsync<string>(
            new CommandDefinition(MappingUlbColumnSql, cancellationToken: ct)) ?? "Map_CorpId");

    private static async Task<IReadOnlyList<Jurisdiction>> JurisdictionsAsync(
        SqlConnection connection, long officerId, int roleId, CancellationToken ct)
    {
        var mapped = await QueryJurisdictionsAsync(connection, await WardScopedSqlAsync(connection, ct), officerId, ct);
        // Read only when it can be used: a ward officer never falls back.
        IReadOnlyList<Jurisdiction> fallback = IsWardScoped(roleId) || mapped.Count > 0
            ? []
            : await QueryJurisdictionsAsync(connection, OwnWardSql, officerId, ct);
        return ChooseJurisdictions(roleId, mapped, fallback);
    }

    private static async Task<List<Jurisdiction>> QueryJurisdictionsAsync(
        SqlConnection connection, string sql, long officerId, CancellationToken ct) =>
        ToJurisdictions(await connection.QueryAsync<JurisdictionRow>(
            new CommandDefinition(sql, new { officerId }, cancellationToken: ct)));

    /// <summary>
    /// One jurisdiction per ULB + ward number, in the order read - the officer's own ward
    /// first, so it is the one the token and the top of the sign-in reply carry. A mapping
    /// row repeating the own ward (or another mapping) is dropped; ward numbers compare
    /// trimmed and ignoring case. A row with no ULB is no jurisdiction.
    /// </summary>
    internal static List<Jurisdiction> ToJurisdictions(IEnumerable<JurisdictionRow> rows) =>
        rows.Where(r => r.UlbCode is > 0)
            .OrderBy(r => r.Src)
            .GroupBy(r => $"{r.UlbCode}|{WardNumbers.Clean(r.WardNumber)}", WardNumbers.Comparer)
            .Select(g => g.First())
            .Select(r => new Jurisdiction
            {
                UlbCode = r.UlbCode,
                UlbName = Text(r.UlbName),
                WardNumber = WardNumbers.Clean(r.WardNumber),
                WardName = Text(r.WardName)
            })
            .ToList();

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// The same officer and jurisdictions the OTP flow returns, without requiring a code.
    ///
    /// USP_S_Officer_ValidateOTP cannot serve this: it demands a valid OTP, which is
    /// exactly what a client holding a still-valid token does not have. The app is
    /// offline-first and keeps its session across restarts, so without this an officer
    /// reopening the app in the morning would be asked to sign in again despite holding a
    /// perfectly good token.
    ///
    /// Only the RI row (role 117), lowest Ofcr_Id first - exactly the row
    /// USP_S_Officer_ValidateOTP (db/41) signs in - so both routes pick the same officer.
    /// </summary>
    private const string LoadSql = """
        SELECT o.Ofcr_Id, o.Ofcr_RoleId, o.Ofcr_MNo, o.Ofcr_Name, o.Ofcr_ULBId,
               Ofcr_WardId = NULLIF(LTRIM(RTRIM(REPLACE(o.Ofcr_WardId, CHAR(9), ''))), '')
        FROM dbo.mst_Officer o WITH (NOLOCK)
        WHERE o.Ofcr_MNo = @mobile AND o.Ofcr_RoleId = 117 AND ISNULL(o.Ofcr_Active, 0) = 1
        ORDER BY o.Ofcr_Id;
        """;

    public async Task<Officer?> LoadAsync(string mobile, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(DbTarget.Master, ct);
        var first = await connection.QueryFirstOrDefaultAsync<OfficerLoadRow>(
            new CommandDefinition(LoadSql, new { mobile }, cancellationToken: ct));

        if (first is null) return null;

        return new Officer
        {
            OfficerId = first.Ofcr_Id,
            RoleId = first.Ofcr_RoleId,
            Mobile = first.Ofcr_MNo,
            Name = first.Ofcr_Name,
            UlbCode = first.Ofcr_ULBId,
            WardNumber = WardNumbers.Clean(first.Ofcr_WardId),
            Jurisdictions = await JurisdictionsAsync(connection, first.Ofcr_Id, first.Ofcr_RoleId, ct)
        };
    }

    private sealed class OfficerLoadRow
    {
        public long Ofcr_Id { get; init; }
        public int Ofcr_RoleId { get; init; }
        public string? Ofcr_MNo { get; init; }
        public string? Ofcr_Name { get; init; }
        public int? Ofcr_ULBId { get; init; }
        public string? Ofcr_WardId { get; init; }
    }

    public async Task RecordLoginAsync(Officer officer, string? clientIp, CancellationToken ct)
    {
        var p = new DynamicParameters();
        p.Add("@login_tran_id", 0, DbType.Int32);
        p.Add("@mobile", officer.Mobile, DbType.String, size: 30);
        p.Add("@macaddress", null, DbType.String, size: 100);
        p.Add("@lanip", clientIp, DbType.String, size: 100);
        p.Add("@localip", null, DbType.String, size: 100);
        p.Add("@additionalinfo", "PropertyGPS mobile", DbType.String, size: 100);
        p.Add("@cBy", officer.OfficerId, DbType.Int64);
        p.Add("@CRole", officer.RoleId, DbType.Int32);
        p.Add("@AppID", procedures.Value.AppId, DbType.String, size: 100);

        await using var connection = await connections.OpenAsync(DbTarget.Master, ct);
        await connection.ExecuteAsync(Sp.Call(procedures.Value.InsertLoginData, p, ct));
    }

    /// <summary>
    /// The `auth/me` answer: who this token belongs to, and where they work.
    ///
    /// The app is offline-first and keeps its session across restarts, but a JWT
    /// carries claims rather than names — it knows the ward number, not that the
    /// ward is Hoodi. Without this an officer reopening the app would be asked
    /// to sign in again despite holding a valid token, purely because the client
    /// had forgotten the labels.
    ///
    /// Takes the mobile from the caller because only the controller can read a
    /// claim; it is never a request parameter, so an officer can only ever ask
    /// who they themselves are.
    /// </summary>
    public async Task<VerifyOtpResponse> ProfileAsync(string? mobile, CancellationToken ct)
    {
        // A token without the mobile claim is one we should not have accepted,
        // so this is "sign in again", not a validation error.
        if (string.IsNullOrWhiteSpace(mobile))
            throw ApiException.Unauthorized("Your session is not valid. Please sign in again.");

        var officer = await LoadAsync(mobile, ct)
            ?? throw ApiException.Unauthorized(
                "Your account is no longer active. Please sign in again.");

        // No token is minted here. This answers who the caller already is;
        // issuing a fresh one would turn a lookup into a silent, unbounded
        // session extension.
        return ToResponse(officer, token: "", expiresAt: default);
    }

    /// <summary>
    /// The sign-in reply, shared by otp/verify and auth/me so the two cannot drift. The
    /// top-level ULB and ward are the first jurisdiction's - the pair the token carries.
    /// The zone, ward-id and corporation fields older builds read are left null.
    /// </summary>
    internal static VerifyOtpResponse ToResponse(Officer officer, string token, DateTimeOffset expiresAt)
    {
        var jurisdictions = officer.Jurisdictions.Where(j => j.UlbCode is > 0).ToList();
        var first = jurisdictions.FirstOrDefault();

        return new VerifyOtpResponse
        {
            Token = token,
            ExpiresAt = expiresAt,
            UserId = officer.OfficerId,
            RoleId = officer.RoleId,
            Mobile = officer.Mobile,
            UserName = officer.Name,
            Designation = officer.RoleName,
            UlbCode = first?.UlbCode,
            UlbName = first?.UlbName,
            WardNumber = first?.WardNumber,
            WardName = first?.WardName,
            Jurisdictions = jurisdictions.Select(j => new JurisdictionDto
            {
                UlbCode = j.UlbCode!.Value,
                UlbName = j.UlbName,
                WardNumber = j.WardNumber,
                WardName = j.WardName
            }).ToList()
        };
    }
}
