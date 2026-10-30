using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Common;
using PropertyGpsApi.Infrastructure.Options;

namespace PropertyGpsApi.Infrastructure.Storage;

public interface IScalityClient
{
    /// <summary>Stores the bytes and returns Scality's reference key. Throws a retryable ApiException on any failure.</summary>
    Task<string> UploadAsync(byte[] content, string fileName, string extension, CancellationToken ct);

    /// <summary>The file for a reference key, or null when Scality has no such file.</summary>
    Task<StoredFile?> FetchAsync(string referenceKey, CancellationToken ct);
}

/// <summary>
/// The three calls BBMP gave (2026-10-30): POST api/Auth/token (client credentials, a token
/// for 30 minutes), POST api/Scality/upload-base64, GET api/Scality/fetch-base64. The token is
/// kept and fetched again shortly before it runs out, or once after a 401.
/// </summary>
internal sealed class ScalityClient(
    HttpClient http,
    IOptions<ScalityOptions> options,
    TimeProvider clock,
    ILogger<ScalityClient> logger) : IScalityClient
{
    private static readonly SemaphoreSlim TokenLock = new(1, 1);
    private static string? cachedToken;
    private static DateTimeOffset cachedUntil;

    public async Task<string> UploadAsync(byte[] content, string fileName, string extension, CancellationToken ct)
    {
        var o = options.Value;
        var body = new UploadRequest
        {
            Base64String = Convert.ToBase64String(content),
            FileName = fileName,
            Extension = extension.TrimStart('.'),
            TableName = o.TableName,
            BucketName = o.BucketName,
        };

        using var response = await SendAsync(() =>
            new HttpRequestMessage(HttpMethod.Post, Url("api/Scality/upload-base64")) { Content = JsonContent.Create(body) }, ct);
        var result = response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<UploadResponse>(ct)
            : null;

        if (result is not { Success: true } || string.IsNullOrWhiteSpace(result.ReferenceKey))
        {
            logger.LogWarning("Scality upload of {File} failed: HTTP {Status} {Error}",
                fileName, (int)response.StatusCode, result?.ErrorMessage);
            throw Unavailable("The document store did not accept the photo. Please sync again.");
        }

        return result.ReferenceKey.Trim();
    }

    public async Task<StoredFile?> FetchAsync(string referenceKey, CancellationToken ct)
    {
        var o = options.Value;
        var url = Url($"api/Scality/fetch-base64?ReferenceNo={Uri.EscapeDataString(referenceKey)}&BucketName={Uri.EscapeDataString(o.BucketName)}");

        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Scality fetch failed: HTTP {Status}", (int)response.StatusCode);
            throw Unavailable("The document store could not be reached. Please try again.");
        }

        var result = await response.Content.ReadFromJsonAsync<FetchResponse>(ct);
        if (result is not { Success: true } || string.IsNullOrEmpty(result.Base64Data)) return null;

        byte[] bytes;
        try { bytes = Convert.FromBase64String(result.Base64Data); }
        catch (FormatException)
        {
            logger.LogWarning("Scality returned data that is not base64 for a document");
            return null;
        }

        var name = Path.GetFileName(referenceKey);
        return new StoredFile(name, ContentTypeFor(name, bytes), bytes);
    }

    /// <summary>
    /// The content type from the key's extension, or from the bytes themselves - Scality
    /// answers "application/octet-stream" for everything.
    /// </summary>
    internal static string ContentTypeFor(string fileName, byte[] bytes)
    {
        switch (Path.GetExtension(fileName).ToLowerInvariant())
        {
            case ".pdf": return MediaContentPolicy.Pdf;
            case ".jpg" or ".jpeg": return MediaContentPolicy.Jpeg;
            case ".png": return MediaContentPolicy.Png;
        }
        if (bytes.Length >= 4 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46) return MediaContentPolicy.Pdf;
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return MediaContentPolicy.Jpeg;
        if (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return MediaContentPolicy.Png;
        return "application/octet-stream";
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var request = build();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(attempt > 0, ct));
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Scality could not be reached");
                throw Unavailable("The document store could not be reached. Please try again.");
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                response.Dispose();
                continue;   // the token was refused: one fresh token, one more try
            }
            return response;
        }
    }

    private async Task<string> TokenAsync(bool renew, CancellationToken ct)
    {
        var o = options.Value;
        if (!o.IsConfigured)
            throw new ApiException(StatusCodes.Status503ServiceUnavailable,
                "DOCUMENT_STORE_NOT_CONFIGURED", "The document store is not set up on this server.", retryable: true);

        await TokenLock.WaitAsync(ct);
        try
        {
            if (!renew && cachedToken is not null && clock.GetUtcNow() < cachedUntil) return cachedToken;

            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = o.ClientId,
                ["client_secret"] = o.ClientSecret,
            });
            HttpResponseMessage response;
            try
            {
                response = await http.PostAsync(Url("api/Auth/token"), form, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Scality token service could not be reached");
                throw Unavailable("The document store could not be reached. Please try again.");
            }

            using (response)
            {
                var token = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<TokenResponse>(ct) : null;
                if (string.IsNullOrWhiteSpace(token?.AccessToken))
                {
                    logger.LogWarning("Scality refused the client credentials: HTTP {Status}", (int)response.StatusCode);
                    throw Unavailable("The document store refused this server. Please try again later.");
                }

                cachedToken = token.AccessToken;
                // Two minutes early, so a token never runs out in the middle of a submit.
                cachedUntil = clock.GetUtcNow().AddSeconds(Math.Max(60, token.ExpiresIn - 120));
                return cachedToken;
            }
        }
        finally
        {
            TokenLock.Release();
        }
    }

    internal static void ForgetToken()
    {
        cachedToken = null;
        cachedUntil = default;
    }

    private string Url(string path) => $"{options.Value.BaseUrl.TrimEnd('/')}/{path}";

    private static ApiException Unavailable(string message) =>
        new(StatusCodes.Status503ServiceUnavailable, "DOCUMENT_STORE_UNAVAILABLE", message, retryable: true);

    private sealed class UploadRequest
    {
        [JsonPropertyName("base64String")] public string Base64String { get; init; } = "";
        [JsonPropertyName("fileName")] public string FileName { get; init; } = "";
        [JsonPropertyName("extension")] public string Extension { get; init; } = "";
        [JsonPropertyName("tableName")] public string TableName { get; init; } = "";
        [JsonPropertyName("bucketName")] public string BucketName { get; init; } = "";
    }

    private sealed class UploadResponse
    {
        [JsonPropertyName("success")] public bool Success { get; init; }
        [JsonPropertyName("referenceKey")] public string? ReferenceKey { get; init; }
        [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
    }

    private sealed class FetchResponse
    {
        [JsonPropertyName("success")] public bool Success { get; init; }
        [JsonPropertyName("base64Data")] public string? Base64Data { get; init; }
        [JsonPropertyName("contentType")] public string? ContentType { get; init; }
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; init; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
    }
}
