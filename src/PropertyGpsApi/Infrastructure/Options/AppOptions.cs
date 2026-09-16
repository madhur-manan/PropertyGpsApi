using System.ComponentModel.DataAnnotations;

namespace PropertyGpsApi.Infrastructure.Options;

public sealed class DatabaseOptions
{
    public const string Section = "Database";

    [Required(AllowEmptyStrings = false)] public string Master { get; init; } = "";
    [Required(AllowEmptyStrings = false)] public string B2A { get; init; } = "";
}

public sealed class JwtOptions
{
    public const string Section = "Jwt";

    [Required(AllowEmptyStrings = false)] public string Key { get; init; } = "";
    [Required(AllowEmptyStrings = false)] public string Issuer { get; init; } = "";
    [Required(AllowEmptyStrings = false)] public string Audience { get; init; } = "";

    [Range(5, 10080)] public int ExpiryMinutes { get; init; } = 480;

    public byte[] KeyBytes => System.Text.Encoding.UTF8.GetBytes(Key);

    /// <summary>A short key makes HS256 forgeable; refuse to start rather than pretend.</summary>
    public bool KeyIsStrongEnough => KeyBytes.Length >= 32;

    public bool IsPlaceholder => Key.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase);
}

public sealed class OtpOptions
{
    public const string Section = "Otp";

    /// <summary>
    /// The value passed to the legacy procedures. Note the quirk this has to survive:
    /// USP_I_OTP stores the device id in OTP_Tran.DeviceId, but USP_S_Officer_ValidateOTP
    /// filters on DeviceId = @Source. So the SAME value must be sent as @DeviceId on send
    /// and as @Source on verify, or validation never matches. Do not "fix" this.
    /// </summary>
    [Required(AllowEmptyStrings = false)] public string Source { get; init; } = "";

    /// <summary>Six digits, because USP_I_OTP hardcodes test values 999999 and 673489.</summary>
    [Range(4, 8)] public int Length { get; init; } = 6;

    /// <summary>
    /// What the client counts down before enabling Resend. The app consumes this as its
    /// resend timer, not as the code's validity.
    /// </summary>
    [Range(10, 600)] public int ResendAfterSeconds { get; init; } = 30;

    /// <summary>
    /// Informational only. The authoritative window lives inside
    /// USP_S_Officer_ValidateOTP, which accepts a code for 100 minutes.
    /// </summary>
    [Range(60, 6000)] public int ValidForSeconds { get; init; } = 6000;

    /// <summary>
    /// The role the mobile app signs in as. USP_S_ValidateOfficer matches on role as well as
    /// mobile number, so this has to be right: 116 is the ward-level case worker (the RI).
    /// </summary>
    [Range(1, 9999)] public int DefaultRoleId { get; init; } = 116;

    /// <summary>Development logs the code instead of sending an SMS.</summary>
    [Required(AllowEmptyStrings = false)] public string Sender { get; init; } = "Development";
}

public sealed class StoredProcedureOptions
{
    public const string Section = "StoredProcedures";

    [Required(AllowEmptyStrings = false)] public string ValidateOfficer { get; init; } = "";
    [Required(AllowEmptyStrings = false)] public string InsertOtp { get; init; } = "";
    [Required(AllowEmptyStrings = false)] public string AssignApplication { get; init; } = "";
    [Required(AllowEmptyStrings = false)] public string ValidateOfficerOtp { get; init; } = "";
    [Required(AllowEmptyStrings = false)] public string InsertLoginData { get; init; } = "";
    [Required(AllowEmptyStrings = false)] public string FetchApplications { get; init; } = "";
    [Required(AllowEmptyStrings = false)] public string FetchApplicationsCount { get; init; } = "";

    /// <summary>Written into Login_Tran.AppID so BBMP can tell this app's logins apart.</summary>
    [Required(AllowEmptyStrings = false)] public string AppId { get; init; } = "";
}
