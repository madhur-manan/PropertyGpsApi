using System.ComponentModel.DataAnnotations;

namespace PropertyGpsApi.Infrastructure.Options;

/// <summary>
/// BBMP's SMS gateway. Every value here is a credential or a DLT registration detail, so all
/// of them come from user-secrets or environment variables - never from appsettings.json.
/// </summary>
public sealed class SmsOptions
{
    public const string Section = "Sms";

    /// <summary>
    /// The protocol of the gateway in ApiUrl - the two in use at BBMP differ on the wire:
    /// "Mgov" as LayoutKhataAPI's clsSendOtp speaks it (password as SHA-1, a SHA-512 key over
    /// the message), and "SmsMobile" for smsmobile1.karnataka.gov.in as B2A_WEBAPI's
    /// clSendSMS speaks it (password as given, the service key DES-encrypted).
    /// </summary>
    [RegularExpression("^(Mgov|SmsMobile)$", ErrorMessage = "Sms:Protocol must be Mgov or SmsMobile.")]
    public string Protocol { get; init; } = Mgov;

    public const string Mgov = "Mgov";
    public const string SmsMobile = "SmsMobile";

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
    public string ApplicationName { get; init; } = "BToA Application";

    /// <summary>
    /// Both gateways answer with a comma-separated string whose first field is 402 on success
    /// ("402,MsgID = ..."); for SmsMobile confirmed on the test server on 2026-10-12.
    /// </summary>
    public string SuccessCode { get; init; } = "402";

    [Range(1, 120)] public int TimeoutSeconds { get; init; } = 20;
}
