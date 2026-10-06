using PropertyGpsApi.Common;

namespace PropertyGpsApi.Infrastructure.Security.BodyEncryption;

/// <summary>
/// Every refusal the decryption filter can return. Messages are fixed strings - never built
/// from the request, an exception or a key - and no ApiException here carries an inner
/// exception, so nothing about the cryptography can reach the client or the Warning log line.
/// </summary>
internal static class BodyEncryptionErrors
{
    public const string RequiredMessage =
        "This request must be encrypted. Please install the latest version of the app; nothing saved on the phone is lost.";

    public const string KeyUnknownMessage =
        "This server cannot open the request with the app's encryption key. Please install the latest version of the app; nothing saved on the phone is lost.";

    public const string DecryptFailedMessage =
        "The request could not be decrypted. Please try again.";

    public const string TooLargeMessage =
        "The request is too large.";

    public const string FormUnreadableMessage =
        "The form data could not be read.";

    public const string BodyUnreadableMessage =
        "The request body could not be read. Please try again.";

    public static ApiException Required() =>
        new(StatusCodes.Status400BadRequest, ApiErrorCodes.BodyEncryptionRequired, RequiredMessage, retryable: true);

    public static ApiException KeyUnknown() =>
        new(StatusCodes.Status400BadRequest, ApiErrorCodes.BodyKeyUnknown, KeyUnknownMessage, retryable: true);

    public static ApiException DecryptFailed() =>
        new(StatusCodes.Status400BadRequest, ApiErrorCodes.BodyDecryptFailed, DecryptFailedMessage, retryable: true);

    public static ApiException TooLarge() =>
        new(StatusCodes.Status413PayloadTooLarge, ApiErrorCodes.PayloadTooLarge, TooLargeMessage, retryable: false);

    /// <summary>A malformed form (bad multipart framing): permanent for these bytes.</summary>
    public static ApiException FormUnreadable() =>
        new(StatusCodes.Status400BadRequest, ApiErrorCodes.BadRequest, FormUnreadableMessage, retryable: false);

    /// <summary>The connection failed mid-body: worth sending again.</summary>
    public static ApiException BodyUnreadable() =>
        new(StatusCodes.Status400BadRequest, ApiErrorCodes.BadRequest, BodyUnreadableMessage, retryable: true);

    public static ApiException ForOpenFailure(BodyOpenFailure failure) => failure switch
    {
        BodyOpenFailure.UnknownKey => KeyUnknown(),
        _ => DecryptFailed()
    };
}
