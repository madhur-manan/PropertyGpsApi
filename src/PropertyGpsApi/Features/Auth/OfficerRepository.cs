using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Common;
using PropertyGpsApi.Features.Auth.Dtos;
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

    /// <summary>
    /// Who the bearer of a token is, and where they work — the `auth/me` answer.
    ///
    /// Returns the same shape as a sign-in but deliberately without a token; see
    /// the implementation for why.
    /// </summary>
    Task<VerifyOtpResponse> ProfileAsync(string? mobile, CancellationToken ct);
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

        // The procedure cannot give a correct ward id for a ward-scoped officer, so for
        // those roles the jurisdictions are replaced with ones that carry it. Only when the
        // lookup finds something: an officer whose mapping does not resolve keeps what the
        // procedure returned rather than losing their ward entirely.
        var corrected = await WardScopedJurisdictionsAsync(connection, officer.OfficerId, ct);
        if (corrected.Count > 0)
            officer = officer with { Jurisdictions = corrected };

        return new OtpValidationResult(OtpValidationOutcome.Valid, officer);
    }

    /// <summary>
    /// The wards a ward-scoped officer (role 116 or 117) actually holds, with their real
    /// ward ids.
    ///
    /// Needed because USP_S_Officer_ValidateOTP joins on the right ward - via
    /// mst_Ofcr_WardMapping, falling back to the officer's own Ofcr_WardId - but its SELECT
    /// list returns USR.Ofcr_wardid and never ARO.BBMPWardId. An officer mapped to two
    /// wards therefore came back as two rows with two NAMES and one shared ID. The app
    /// listed both and downloaded whichever the id pointed at, so choosing the second ward
    /// silently fetched the first one's properties.
    ///
    /// Observed on officer 1036: "C.V. Ramannagar" and "Domlur", both carrying ward 57,
    /// where the true ids are 57 and 112.
    ///
    /// This mirrors the procedure's own 116/117 branch exactly and adds the missing column.
    /// The other role branches are left to the procedure rather than reimplemented here.
    ///
    /// FOR THE DBA: adding ARO.BBMPWardId to that SELECT would make this redundant and fix
    /// it for every client, not just ours.
    /// </summary>
    private const string WardScopedJurisdictionsSql = """
        SELECT DISTINCT
            GbaZoneId       = aro.GBAZoneID,
            GbaZoneName     = aro.GBAZoneName_En,
            BbmpZoneId      = aro.BBMPZoneID,
            BbmpZoneName    = aro.BBMPZoneName,
            BbmpWardId      = aro.BBMPWardId,
            BbmpWardName    = aro.BBMPWardName,
            CorporationId   = aro.easthiULBNAME_CorpId,
            CorporationName = aro.cityCorportationName
        FROM dbo.mst_Officer o WITH (NOLOCK)
        LEFT JOIN dbo.mst_Ofcr_WardMapping wm WITH (NOLOCK)
               ON wm.Map_OfcrId = o.Ofcr_Id
              AND wm.Map_OfcrRoleId = o.Ofcr_RoleId
              AND ISNULL(wm.isActive, 1) = 1
        JOIN dbo.mst_AROMapping aro WITH (NOLOCK)
               ON aro.GBAZoneID = o.Ofcr_ZoneId
              AND aro.easthiULBNAME_CorpId = o.Ofcr_CorporationId
              AND aro.BBMPWardId = ISNULL(wm.Map_WardId, o.Ofcr_WardId)
        WHERE o.Ofcr_Id = @officerId
          AND ISNULL(o.Ofcr_Active, 0) = 1
          AND o.Ofcr_RoleId IN (116, 117)
        ORDER BY aro.BBMPWardName;
        """;

    /// <summary>
    /// Empty for any role that is not ward-scoped, and for a ward-scoped officer whose
    /// zone/ward does not resolve - in both cases the caller keeps what it already had
    /// rather than replacing real jurisdictions with none.
    /// </summary>
    private async Task<List<Jurisdiction>> WardScopedJurisdictionsAsync(
        SqlConnection connection, long officerId, CancellationToken ct)
    {
        var rows = (await connection.QueryAsync<OfficerLoadRow>(
            new CommandDefinition(WardScopedJurisdictionsSql, new { officerId }, cancellationToken: ct)))
            .AsList();

        return rows.Select(r => new Jurisdiction
        {
            GbaZoneId = r.GbaZoneId,
            GbaZoneName = r.GbaZoneName,
            // The GBA id, never the BBMP one: every fetch is matched against MD_ZoneId.
            ZoneId = r.GbaZoneId ?? r.BbmpZoneId,
            ZoneName = r.BbmpZoneName ?? r.GbaZoneName,
            WardId = r.BbmpWardId,
            WardName = r.BbmpWardName,
            CorporationId = r.CorporationId,
            CorporationName = r.CorporationName
        }).ToList();
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

        // Same correction as the sign-in path, and for a second reason here: this query
        // joins on o.Ofcr_WardId alone and never consults mst_Ofcr_WardMapping, so a
        // two-ward officer saw both wards after signing in and only one after reopening
        // the app. The two routes now answer the same thing.
        var corrected = await WardScopedJurisdictionsAsync(connection, first.Ofcr_Id, ct);

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
            Jurisdictions = corrected.Count > 0 ? corrected : rows
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

    /// <summary>
    /// The `auth/me` answer: who this token belongs to, and where they work.
    ///
    /// The app is offline-first and keeps its session across restarts, but a JWT
    /// carries claims rather than names — it knows the ward id, not that the
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

        return new VerifyOtpResponse
        {
            // No token is minted here. This answers who the caller already is;
            // issuing a fresh one would turn a lookup into a silent, unbounded
            // session extension.
            Token = "",
            ExpiresAt = default,
            UserId = officer.OfficerId,
            RoleId = officer.RoleId,
            Mobile = officer.Mobile,
            UserName = officer.Name,
            Designation = officer.RoleName,
            CorporationId = officer.CorporationId,
            CorporationName = officer.CorporationName,
            ZoneId = officer.ZoneId,
            WardId = officer.WardId,
            Jurisdictions = officer.Jurisdictions.Select(j => new JurisdictionDto
            {
                GbaZoneId = j.GbaZoneId,
                GbaZoneName = j.GbaZoneName,
                ZoneId = j.ZoneId,
                ZoneName = j.ZoneName,
                WardId = j.WardId,
                WardName = j.WardName,
                CorporationId = j.CorporationId,
                CorporationName = j.CorporationName
            }).ToList()
        };
    }
}
