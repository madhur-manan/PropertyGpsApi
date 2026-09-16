using System.ComponentModel.DataAnnotations;

namespace PropertyGpsApi.Infrastructure.Options;

/// <summary>
/// BBMP's SMS gateway, matching the wire protocol already in production in LayoutKhataAPI
/// (clsSendOtp). Every value here is a credential or a DLT registration detail, so all of
/// them come from user-secrets or environment variables - never from appsettings.json.
/// </summary>
public sealed class SmsOptions
{
    public const string Section = "Sms";

    [Required(AllowEmptyStrings = false)] public string ApiUrl { get; init; } = "";
    [Required(AllowEmptyStrings = false)] public string Username { get; init; } = "";
    [Required(AllowEmptyStrings = false)] public string Password { get; init; } = "";
    [Required(AllowEmptyStrings = false)] public string SenderId { get; init; } = "";
    [Required(AllowEmptyStrings = false)] public string SecretKey { get; init; } = "";
    [Required(AllowEmptyStrings = false)] public string ServiceType { get; init; } = "";

    /// <summary>
    /// The DLT-registered template id. India's DLT rules reject any message whose text does
    /// not match the template registered against this id, so it and MessageTemplate must be
    /// changed together.
    /// </summary>
    [Required(AllowEmptyStrings = false)] public string TemplateId { get; init; } = "";

    /// <summary>
    /// The registered template text. {#var1#} is the application name and {#var2#} the code,
    /// following the same two-placeholder template LayoutKhataAPI uses for its citizen login.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string MessageTemplate { get; init; } =
        "Your OTP for logging in to the {#var1#} is {#var2#}. Please do not share this OTP with anyone. Regards - BBMP";

    [Required(AllowEmptyStrings = false)]
    public string ApplicationName { get; init; } = "Single Plot GPS Application";

    /// <summary>The gateway answers with a comma-separated string whose first field is 402 on success.</summary>
    public string SuccessCode { get; init; } = "402";

    [Range(1, 120)] public int TimeoutSeconds { get; init; } = 20;
}
