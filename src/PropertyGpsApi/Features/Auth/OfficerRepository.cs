using System.Data;
using Dapper;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Infrastructure.Options;

namespace PropertyGpsApi.Features.Auth;

public interface IOfficerRepository
{
    Task<long> StoreOtpAsync(string mobile, string otp, CancellationToken ct);
    Task<OtpValidationResult> ValidateOtpAsync(string mobile, string otp, CancellationToken ct);
    Task RecordLoginAsync(Officer officer, string? clientIp, CancellationToken ct);
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
