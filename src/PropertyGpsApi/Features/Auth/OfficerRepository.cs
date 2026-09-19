using System.Data;
using Dapper;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Infrastructure.Options;

namespace PropertyGpsApi.Features.Auth;

public interface IOfficerRepository
{
    Task<bool> OfficerExistsAsync(string mobile, int roleId, CancellationToken ct);
    Task<long> StoreOtpAsync(string mobile, string otp, CancellationToken ct);
    Task<OtpValidationResult> ValidateOtpAsync(string mobile, string otp, CancellationToken ct);
    Task RecordLoginAsync(Officer officer, string? clientIp, CancellationToken ct);

    /// <summary>Loads an officer without an OTP, for rehydrating a session from a token.</summary>
    Task<Officer?> LoadAsync(string mobile, CancellationToken ct);
}

public enum OtpValidationOutcome
{
    /// <summary>The code matched and the officer is active.</summary>
    Valid,

    /// <summary>Officer active, but this code is wrong or expired.</summary>
    InvalidCode,

    /// <summary>
    /// No rows at all. The procedure filters on Ofcr_Active = 1, so this covers both an
    /// unregistered mobile and an account locked out by repeated failures - the procedure
    /// deliberately does not let us tell them apart, and neither should we.
    /// </summary>
    UnknownOrLockedOfficer
}

public sealed record OtpValidationResult(OtpValidationOutcome Outcome, Officer? Officer);

internal sealed class OfficerRepository(
    ISqlConnectionFactory connections,
    IOptions<StoredProcedureOptions> procedures,
    IOptions<OtpOptions> otpOptions) : IOfficerRepository
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
            CorporationId = first.CorporationId,
            CorporationName = first.CorporationName,
            ZoneId = first.Ofcr_ZoneId,
            WardId = first.Ofcr_WardId,
            DivisionId = first.Ofcr_DivisionId,
            // The procedure aliases ARO.GBAZoneID as ZoneId and GBAZoneName_En as ZoneName,
            // so those columns are the JURISDICTION zone, not the officer own columns.
            // Ofcr_ZoneId/Ofcr_WardId are only populated for ward- and zone-scoped roles
            // (116, 125) and are null for the rest, hence the fallback.
            //
            // GAP: the procedure returns BBMPWardName but never BBMPWardId, so a ward here
            // can be displayed but not used to build a fetch request. Ward ids have to come
            // from the masters lookup until BBMP adds the column to the procedure.
            Jurisdictions = rows.Select(r => new Jurisdiction
            {
                GbaZoneId = r.ZoneId,
                GbaZoneName = r.ZoneName,
                ZoneId = r.ZoneId ?? r.Ofcr_ZoneId,
                ZoneName = r.ZoneName,
                WardId = r.Ofcr_WardId,
                WardName = r.BBMPWardName,
                CorporationId = r.CorporationId,
                CorporationName = r.CorporationName
            }).ToList()
        };

        return new OtpValidationResult(OtpValidationOutcome.Valid, officer);
    }

    /// <summary>
    /// The same officer and jurisdictions the OTP flow returns, without requiring a code.
    ///
    /// USP_S_Officer_ValidateOTP cannot serve this: it demands a valid OTP, which is
    /// exactly what a client holding a still-valid token does not have. The app is
    /// offline-first and keeps its session across restarts, so without this an officer
    /// reopening the app in the morning would be asked to sign in again despite holding a
    /// perfectly good token.
    ///
    /// Reads the tables directly, which also closes the gap noted above: the procedure
    /// returns BBMPWardName but never BBMPWardId, so its wards can be displayed but not
    /// used to build a fetch request. Here both come back.
    /// </summary>
    private const string LoadSql = """
        SELECT o.Ofcr_Id, o.Ofcr_RoleId, o.Ofcr_MNo, o.Ofcr_Name, o.Ofcr_ZoneId, o.Ofcr_WardId,
               CorporationId   = aro.easthiULBNAME_CorpId,
               CorporationName = aro.cityCorportationName,
               GbaZoneId       = aro.GBAZoneID,
               GbaZoneName     = aro.GBAZoneName_En,
               BbmpZoneId      = aro.BBMPZoneID,
               BbmpZoneName    = aro.BBMPZoneName,
               BbmpWardId      = aro.BBMPWardId,
               BbmpWardName    = aro.BBMPWardName
        FROM dbo.mst_Officer o WITH (NOLOCK)
        LEFT JOIN dbo.mst_AROMapping aro WITH (NOLOCK)
               ON aro.GBAZoneID = o.Ofcr_ZoneId AND aro.BBMPWardId = o.Ofcr_WardId
        WHERE o.Ofcr_MNo = @mobile AND ISNULL(o.Ofcr_Active, 0) = 1;
        """;

    public async Task<Officer?> LoadAsync(string mobile, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(DbTarget.Master, ct);
        var rows = (await connection.QueryAsync<OfficerLoadRow>(
            new CommandDefinition(LoadSql, new { mobile }, cancellationToken: ct))).AsList();

        if (rows.Count == 0) return null;
        var first = rows[0];

        return new Officer
        {
            OfficerId = first.Ofcr_Id,
            RoleId = first.Ofcr_RoleId,
            Mobile = first.Ofcr_MNo,
            Name = first.Ofcr_Name,
            CorporationId = first.CorporationId,
            CorporationName = first.CorporationName,
            ZoneId = first.Ofcr_ZoneId,
            WardId = first.Ofcr_WardId,
            Jurisdictions = rows
                .Where(r => r.BbmpWardId is not null)
                .GroupBy(r => (r.GbaZoneId, r.BbmpWardId))
                .Select(g => g.First())
                .Select(r => new Jurisdiction
                {
                    GbaZoneId = r.GbaZoneId,
                    GbaZoneName = r.GbaZoneName,
                    // zoneId is what the device sends back on every ward request, and the
                    // fetch procedure matches it against MD_ZoneId - a GBA zone id. This
                    // returned the BBMP zone instead (Mahadevapura: 2 rather than 102), so
                    // an officer who reopened the app and had their session restored through
                    // this path asked for a zone that holds no applications and was told
                    // their ward was empty. The OTP path has always returned the GBA id;
                    // the two disagreeing is what made the bug invisible until a restart.
                    ZoneId = r.GbaZoneId ?? r.BbmpZoneId,
                    ZoneName = r.BbmpZoneName ?? r.GbaZoneName,
                    WardId = r.BbmpWardId,
                    WardName = r.BbmpWardName,
                    CorporationId = r.CorporationId,
                    CorporationName = r.CorporationName
                }).ToList()
        };
    }

    private sealed class OfficerLoadRow
    {
        public long Ofcr_Id { get; init; }
        public int Ofcr_RoleId { get; init; }
        public string? Ofcr_MNo { get; init; }
        public string? Ofcr_Name { get; init; }
        public int? Ofcr_ZoneId { get; init; }
        public int? Ofcr_WardId { get; init; }
        public int? CorporationId { get; init; }
        public string? CorporationName { get; init; }
        public int? GbaZoneId { get; init; }
        public string? GbaZoneName { get; init; }
        public int? BbmpZoneId { get; init; }
        public string? BbmpZoneName { get; init; }
        public int? BbmpWardId { get; init; }
        public string? BbmpWardName { get; init; }
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
}
