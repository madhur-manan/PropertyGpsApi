using System.Text.Json.Serialization;

namespace PropertyGpsApi.Common;

/// <summary>
/// The single response envelope for every endpoint. The legacy contract used five
/// different shapes; one shape means the client has one deserializer and one error path.
/// </summary>
public sealed class ApiResponse<T>
{
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("code")] public string Code { get; init; } = ApiErrorCodes.Ok;
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("data")] public T? Data { get; init; }
    [JsonPropertyName("page")] public PageInfo? Page { get; init; }
    [JsonPropertyName("errors")] public IReadOnlyList<ApiError> Errors { get; init; } = [];

    /// <summary>
    /// Whether the client should try this request again. Authoritative — the client must
    /// prefer this over the HTTP status, because proxies and WAFs emit statuses we did not
    /// author. A missing or unparseable body must be treated as retryable.
    /// </summary>
    [JsonPropertyName("retryable")] public bool Retryable { get; init; }

    /// <summary>
    /// Permanent for this payload, but the officer can fix it and resend (a re-captured
    /// photo, a corrected answer). Distinct from a flat rejection, which is dead.
    /// </summary>
    [JsonPropertyName("recoverable")] public bool Recoverable { get; init; }

    [JsonPropertyName("traceId")] public string? TraceId { get; init; }

    public static ApiResponse<T> Ok(T data, PageInfo? page = null, string? message = null) =>
        new() { Success = true, Code = ApiErrorCodes.Ok, Data = data, Page = page, Message = message };
}

public sealed class PageInfo
{
    [JsonPropertyName("start")] public int Start { get; init; }
    [JsonPropertyName("range")] public int Range { get; init; }
    [JsonPropertyName("returned")] public int Returned { get; init; }
    [JsonPropertyName("total")] public int Total { get; init; }
}

public sealed class ApiError
{
    [JsonPropertyName("field")] public string? Field { get; init; }
    [JsonPropertyName("code")] public string Code { get; init; } = "";
    [JsonPropertyName("message")] public string Message { get; init; } = "";
}
