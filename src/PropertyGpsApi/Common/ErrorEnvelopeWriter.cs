using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace PropertyGpsApi.Common;

public static class JsonOptions
{
    /// <summary>
    /// DefaultIgnoreCondition is pinned to Never. Nulls MUST be written: across this
    /// contract null means "the question was never answered", which is semantically
    /// different from 0, and omitting the key collapses the two.
    /// </summary>
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };
}

/// <summary>
/// Every error leaves through here. Four separate entry points in ASP.NET can produce a
/// response body without touching a controller - the exception handler, the [ApiController]
/// model-state 400, the JWT challenge, and status-code pages - and each has its own default
/// shape. The mobile client cannot parse RFC 9457 ProblemDetails, so all four route here.
/// </summary>
public static class ErrorEnvelopeWriter
{
    public static Task WriteAsync(
        HttpContext http,
        int statusCode,
        string code,
        string message,
        bool retryable = false,
        bool recoverable = false,
        IReadOnlyList<ApiError>? errors = null)
    {
        if (http.Response.HasStarted) return Task.CompletedTask;

        http.Response.Clear();
        http.Response.StatusCode = statusCode;
        http.Response.ContentType = "application/json; charset=utf-8";

        var body = new ApiResponse<object>
        {
            Success = false,
            Code = code,
            Message = message,
            Data = null,
            Errors = errors ?? [],
            Retryable = retryable,
            Recoverable = recoverable,
            TraceId = http.TraceIdentifier
        };

        return http.Response.WriteAsJsonAsync(body, JsonOptions.Default, http.RequestAborted);
    }

    /// <summary>Model-binding and DataAnnotations failures.</summary>
    public static ApiResponse<object> FromModelState(ModelStateDictionary modelState, HttpContext http) =>
        new()
        {
            Success = false,
            Code = ApiErrorCodes.ValidationFailed,
            Message = "The request was not valid.",
            Errors = modelState
                .Where(e => e.Value is not null && e.Value.Errors.Count > 0)
                .SelectMany(e => e.Value!.Errors.Select(err => new ApiError
                {
                    Field = string.IsNullOrEmpty(e.Key) ? null : e.Key,
                    Code = ApiErrorCodes.ValidationFailed,
                    Message = string.IsNullOrWhiteSpace(err.ErrorMessage) ? "Invalid value." : err.ErrorMessage
                }))
                .ToList(),
            TraceId = http.TraceIdentifier
        };

    /// <summary>Responses that never reach MVC at all: routing 404, a 405, a 415.</summary>
    public static Task StatusCodeHandler(StatusCodeContext context)
    {
        var status = context.HttpContext.Response.StatusCode;
        var (code, message) = status switch
        {
            404 => (ApiErrorCodes.NotFound, "The requested endpoint does not exist."),
            405 => (ApiErrorCodes.BadRequest, "That HTTP method is not allowed here."),
            415 => (ApiErrorCodes.BadRequest, "Unsupported content type."),
            _ => (ApiErrorCodes.BadRequest, "The request could not be processed.")
        };

        return WriteAsync(context.HttpContext, status, code, message, retryable: status >= 500);
    }
}
