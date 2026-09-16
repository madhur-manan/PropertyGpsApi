namespace PropertyGpsApi.Common;

/// <summary>
/// An outcome we chose to return. Anything else reaching the exception handler is a bug
/// and becomes an opaque 500, because unexpected exception text must never reach a client.
/// </summary>
public sealed class ApiException(
    int statusCode,
    string code,
    string message,
    bool retryable = false,
    bool recoverable = false) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public bool Recoverable { get; } = recoverable;

    public static ApiException BadRequest(string message, string? code = null) =>
        new(StatusCodes.Status400BadRequest, code ?? ApiErrorCodes.BadRequest, message);

    // Retryable: an expired token must send the officer back to sign-in, never park a
    // completed survey as permanently failed.
    public static ApiException Unauthorized(string message, string? code = null) =>
        new(StatusCodes.Status401Unauthorized, code ?? ApiErrorCodes.AuthRequired, message, retryable: true);

    public static ApiException Forbidden(string message, string? code = null) =>
        new(StatusCodes.Status403Forbidden, code ?? ApiErrorCodes.Forbidden, message);

    public static ApiException Unprocessable(string message, string code, bool recoverable = false) =>
        new(StatusCodes.Status422UnprocessableEntity, code, message, recoverable: recoverable);

    public static ApiException TooManyRequests(string message, string? code = null) =>
        new(StatusCodes.Status429TooManyRequests, code ?? ApiErrorCodes.TooManyRequests, message, retryable: true);

    public static ApiException Upstream(string message) =>
        new(StatusCodes.Status503ServiceUnavailable, ApiErrorCodes.Upstream, message, retryable: true);
}
