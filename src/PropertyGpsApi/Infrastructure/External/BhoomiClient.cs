using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Infrastructure.Options;

namespace PropertyGpsApi.Infrastructure.External;

public interface IBhoomiClient
{
    Task<BhoomiLookup> LookupAsync(double latitude, double longitude, CancellationToken ct);
}

/// <summary>
/// Karnataka RD Services land records, by latitude and longitude.
///
/// Two things about this service shape the code:
///   - it answers HTTP 200 for its own failures, putting the real status in RESPONSE_CODE,
///     so nothing here may branch on the status code alone;
///   - the token it issues lasts four hours, so one is held and reused rather than bought
///     on every lookup, which would triple the cost of a question asked once.
/// </summary>
internal sealed class BhoomiClient(
    HttpClient http,
    IOptions<BhoomiOptions> options,
    ILogger<BhoomiClient> logger) : IBhoomiClient
{
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;

    public async Task<BhoomiLookup> LookupAsync(double latitude, double longitude, CancellationToken ct)
    {
        var bhoomi = options.Value;
        if (!bhoomi.IsUsable)
            return BhoomiLookup.Empty(BhoomiOutcome.Unavailable, "Bhoomi lookup is not configured.");

        try
        {
            var response = await SendLookupAsync(latitude, longitude, ct);

            // One retry, and only for 401: the token outlives most requests but not all, and
            // an officer should not be told to try again because a four-hour clock ran out
            // between two taps.
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                logger.LogInformation("Bhoomi rejected the cached token; fetching a new one.");
                Invalidate();
                response.Dispose();
                response = await SendLookupAsync(latitude, longitude, ct);
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning(
                        "Bhoomi lookup failed with {Status}: {Body}", (int)response.StatusCode, Clip(body));
                    return BhoomiLookup.Empty(
                        BhoomiOutcome.Unavailable, "The land records service could not be reached.");
                }

                return Parse(body);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Bhoomi lookup could not complete.");
            return BhoomiLookup.Empty(
                BhoomiOutcome.Unavailable, "The land records service did not respond.");
        }
    }

    internal static BhoomiLookup Parse(string body)
    {
        BhoomiWireResponse? wire;
        try
        {
            wire = JsonSerializer.Deserialize<BhoomiWireResponse>(body);
        }
        catch (JsonException)
        {
            return BhoomiLookup.Empty(
                BhoomiOutcome.Unavailable, "The land records service sent an unreadable answer.");
        }

        if (wire is null)
            return BhoomiLookup.Empty(
                BhoomiOutcome.Unavailable, "The land records service sent an empty answer.");

        // The whole contract is this switch. RESPONSE_CODE is a string in the payload even
        // where it looks numeric, and 400 arrives inside an HTTP 200.
        var outcome = wire.ResponseCode?.Trim() switch
        {
            "1" => BhoomiOutcome.Found,
            "2" => BhoomiOutcome.NoLandRecord,
            "3" => BhoomiOutcome.OutsideMappedArea,
            "400" => BhoomiOutcome.InvalidCoordinates,
            _ => BhoomiOutcome.Unavailable,
        };

        var parcels = (wire.LandInfo ?? [])
            .Select(l => new BhoomiParcel(
                Trimmed(l.District),
                Trimmed(l.Taluka),
                Trimmed(l.Hobli),
                Trimmed(l.Village),
                SurveyNumberOf(l.SurveyNumber),
                Trimmed(l.OwnerName),
                Trimmed(l.OwnerType)))
            .ToList();

        // Guard the pair rather than trusting it: "details available" with nothing in the
        // array would otherwise read to an officer as "checked, and nothing government".
        if (outcome == BhoomiOutcome.Found && parcels.Count == 0)
            outcome = BhoomiOutcome.NoLandRecord;

        return new BhoomiLookup(outcome, Trimmed(wire.ResponseMessage), parcels);
    }

    /// <summary>OWNER_NAME comes back as a single space in real responses, not as null.</summary>
    private static string? Trimmed(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string? SurveyNumberOf(JsonElement? element) => element switch
    {
        null => null,
        { ValueKind: JsonValueKind.Number } e => e.GetRawText(),
        { ValueKind: JsonValueKind.String } e => Trimmed(e.GetString()),
        _ => null,
    };

    private static string Clip(string value) => value.Length <= 300 ? value : value[..300];

    private async Task<HttpResponseMessage> SendLookupAsync(
        double latitude, double longitude, CancellationToken ct)
    {
        var token = await TokenAsync(ct);
        var bhoomi = options.Value;

        // Sent as strings because that is the service's own contract, and formatted with
        // InvariantCulture so a machine set to a comma decimal separator cannot post "12,97".
        var payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["LATITUDE"] = latitude.ToString("R", CultureInfo.InvariantCulture),
            ["LONGITUDE"] = longitude.ToString("R", CultureInfo.InvariantCulture),
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, bhoomi.LookupUrl)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await http.SendAsync(request, ct);
    }

    private void Invalidate()
    {
        _token = null;
        _tokenExpiresAt = DateTimeOffset.MinValue;
    }

    private async Task<string?> TokenAsync(CancellationToken ct)
    {
        var bhoomi = options.Value;
        var margin = TimeSpan.FromMinutes(bhoomi.RefreshTokenMinutesBeforeExpiry);

        if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiresAt - margin)
            return _token;

        await _tokenGate.WaitAsync(ct);
        try
        {
            // Re-checked inside the gate: several officers can arrive at once, and only the
            // first should spend a round trip buying what the others are about to share.
            if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiresAt - margin)
                return _token;

            using var request = new HttpRequestMessage(HttpMethod.Post, bhoomi.TokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "password",
                    ["username"] = bhoomi.Username,
                    ["password"] = bhoomi.Password,
                }),
            };

            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                // Never the body: it is the reply to a request carrying the service password.
                logger.LogWarning("Bhoomi token request failed with {Status}.", (int)response.StatusCode);
                return null;
            }

            using var json = JsonDocument.Parse(body);
            var token = json.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
            var seconds = json.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s)
                ? s
                : 3600;

            if (string.IsNullOrWhiteSpace(token)) return null;

            _token = token;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
            logger.LogInformation("Bhoomi token acquired, valid for {Seconds}s.", seconds);
            return _token;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Bhoomi token could not be acquired.");
            return null;
        }
        finally
        {
            _tokenGate.Release();
        }
    }
}
