using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Common;
using PropertyGpsApi.Models;
using PropertyGpsApi.Infrastructure.Options;
using PropertyGpsApi.Infrastructure.Security;

using PropertyGpsApi.Interfaces;

namespace PropertyGpsApi.Services;


internal sealed class OtpService( IOfficerService officers,IOtpSender sender,IJwtTokenService tokens,IOptions<OtpOptions> options, ILogger<OtpService> logger) : IOtpService
{
    public async Task<SendOtpResponse> SendAsync(SendOtpRequest request, CancellationToken ct)
    {
        var otpOptions = options.Value;
        var roleId = request.RoleId ?? otpOptions.DefaultRoleId;

        // Check the officer exists and is active BEFORE generating anything. Without this we
        // happily store a code for any number that arrives, which wastes an SMS on a typo and
        // lets an outsider probe the endpoint at no cost to themselves.
        if (!await officers.OfficerExistsAsync(request.Mobile, roleId, ct))
            throw ApiException.Unprocessable(
                "This mobile number is not registered as an officer, or the account has been "
                + "locked. Please contact your administrator.",
                ApiErrorCodes.OfficerNotRegistered);

        var otp = GenerateOtp(otpOptions.Length);

        // Store first, then send. The reverse order can deliver a code the database has no
        // record of, which the officer can never successfully use.
        var otpId = await officers.StoreOtpAsync(request.Mobile, otp, ct);

        try
        {
            await sender.SendAsync(request.Mobile, otp, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to deliver an OTP to {Mobile}", request.Mobile);
            throw ApiException.Upstream("Could not send the OTP right now. Please try again.");
        }

        return new SendOtpResponse
        {
            OtpRequestId = otpId.ToString(),
            ResendAfterSeconds = otpOptions.ResendAfterSeconds,
            OtpValidForSeconds = otpOptions.ValidForSeconds,
            OtpLength = otpOptions.Length,
            // Tied to the sender type, not to a config flag: Program.cs already refuses to
            // register DevelopmentOtpSender outside Development, so one guard covers both.
            DevOtp = sender is DevelopmentOtpSender ? otp : null
        };
    }

    public async Task<VerifyOtpResponse> VerifyAsync(VerifyOtpRequest request, string? clientIp, CancellationToken ct)
    {
        // Checked before the database, so a blocked code never counts toward the per-officer
        // lockout inside USP_S_Officer_ValidateOTP.
        if (IsBlockedFixedCode(request.Otp))
            throw ApiException.Unprocessable(
                "That code is not correct or has expired.", ApiErrorCodes.OtpInvalid);

        var result = await officers.ValidateOtpAsync(request.Mobile, request.Otp, ct);

        switch (result.Outcome)
        {
            case OtpValidationOutcome.UnknownOrLockedOfficer:
                // Deliberately one message for both cases. The procedure cannot distinguish
                // an unregistered number from a locked account, and telling them apart would
                // let anyone enumerate which mobile numbers belong to officers.
                throw ApiException.Unprocessable(
                    "This mobile number is not registered, or the account has been locked. "
                    + "Please contact your administrator.",
                    ApiErrorCodes.OfficerNotRegistered);

            case OtpValidationOutcome.InvalidCode:
                throw ApiException.Unprocessable(
                    "That code is not correct or has expired.", ApiErrorCodes.OtpInvalid);
        }

        var officer = result.Officer!;
        var (token, expiresAt) = tokens.Issue(officer);

        // Audit the login the same way every other BBMP app does. A failure here must not
        // cost the officer their session - they have already authenticated.
        try
        {
            await officers.RecordLoginAsync(officer, clientIp, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not write the Login_Tran audit row for officer {OfficerId}",
                officer.OfficerId);
        }

        return new VerifyOtpResponse
        {
            Token = token,
            ExpiresAt = expiresAt,
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

    /// <summary>
    /// USP_I_OTP ignores the code we generate for a hardcoded list of mobile numbers and
    /// stores one of these instead - so anyone who knows one of those numbers could sign in
    /// with a code that is printed in the procedure. Refused everywhere except Development
    /// (local testing), which means those accounts cannot sign in through this API.
    /// </summary>
    internal static readonly IReadOnlySet<string> LegacyFixedOtps = new HashSet<string> { "999999", "673489" };

    internal bool IsBlockedFixedCode(string? otp) =>
        sender is not DevelopmentOtpSender && otp is not null && LegacyFixedOtps.Contains(otp.Trim());

    /// <summary>
    /// RandomNumberGenerator, not Random: a predictable authentication code is no code.
    /// Leading zeros are preserved, so "000123" stays six characters. Never one of the
    /// blocked fixed codes, so a genuine code can always be used.
    /// </summary>
    private static string GenerateOtp(int length)
    {
        var upperExclusive = (int)Math.Pow(10, length);
        string otp;
        do { otp = RandomNumberGenerator.GetInt32(0, upperExclusive).ToString().PadLeft(length, '0'); }
        while (LegacyFixedOtps.Contains(otp));
        return otp;
    }
}
