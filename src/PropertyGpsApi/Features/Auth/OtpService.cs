using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Common;
using PropertyGpsApi.Features.Auth.Dtos;
using PropertyGpsApi.Infrastructure.Options;
using PropertyGpsApi.Infrastructure.Security;

namespace PropertyGpsApi.Features.Auth;

public interface IOtpService
{
    Task<SendOtpResponse> SendAsync(SendOtpRequest request, CancellationToken ct);
    Task<VerifyOtpResponse> VerifyAsync(VerifyOtpRequest request, string? clientIp, CancellationToken ct);
}

internal sealed class OtpService(
    IOfficerRepository officers,
    IOtpSender sender,
    IJwtTokenService tokens,
    IOptions<OtpOptions> options,
    ILogger<OtpService> logger) : IOtpService
{
    public async Task<SendOtpResponse> SendAsync(SendOtpRequest request, CancellationToken ct)
    {
        var otpOptions = options.Value;
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

    public async Task<VerifyOtpResponse> VerifyAsync(
        VerifyOtpRequest request, string? clientIp, CancellationToken ct)
    {
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
    /// RandomNumberGenerator, not Random: a predictable authentication code is no code.
    /// Leading zeros are preserved, so "000123" stays six characters.
    /// </summary>
    private static string GenerateOtp(int length)
    {
        var upperExclusive = (int)Math.Pow(10, length);
        return RandomNumberGenerator.GetInt32(0, upperExclusive).ToString().PadLeft(length, '0');
    }
}
