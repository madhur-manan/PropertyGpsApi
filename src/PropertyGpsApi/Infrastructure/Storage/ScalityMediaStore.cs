using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Common;
using PropertyGpsApi.Infrastructure.Options;

namespace PropertyGpsApi.Infrastructure.Storage;

/// <summary>
/// The RI's photographs in BBMP's Scality store (Media:Store = "Scality", user 2026-10-30).
///
/// The link written into the survey's *_Document columns keeps today's shape -
/// .../propertyinfo/file/view/{epid}/{name} - so the QC portal and the app open it as before;
/// only {name} is "sc_{reference key}.{signature}". The signature ties the key to its EPID, so
/// a link cannot be edited into one for somebody else's file. Photos stored on the disk before
/// the switch keep their links and are still read from the disk.
/// </summary>
internal sealed class ScalityMediaStore(
    IScalityClient scality,
    LocalDiskMediaStore disk,
    IOptions<MediaOptions> media,
    ScalityLinks links,
    ILogger<ScalityMediaStore> logger) : IMediaStore
{
    public async Task<StoredMedia> SaveAsync(MediaUpload upload, CancellationToken ct)
    {
        var options = media.Value;
        if (upload.Content.LongLength > options.MaxFileBytes)
            throw ApiException.Unprocessable(
                $"'{upload.ClientFileName}' is larger than the {options.MaxFileBytes / (1024 * 1024)} MB limit.",
                ApiErrorCodes.MediaTooLarge, recoverable: true);

        var contentType = MediaContentPolicy.Resolve(upload.ClientFileName, upload.ContentType, upload.Content, upload.Slot);
        var extension = MediaContentPolicy.ExtensionFor(contentType).TrimStart('.');
        var road = upload.RoadOrdinal?.ToString() ?? "0";
        var name = $"{Safe(upload.Epid)}-{road}-{Safe(upload.Slot)}-{DateTime.Now:yyyyMMdd_HHmmss}";

        var key = await scality.UploadAsync(upload.Content, name, extension, ct);
        logger.LogInformation("Stored {Slot} for {Epid} in Scality as {Key} ({Bytes} bytes)",
            upload.Slot, upload.Epid, key, upload.Content.Length);

        return new StoredMedia(
            Url: links.Url(options.PublicBaseUrl, upload.Epid, key),
            StorageKey: key,
            Sha256: Convert.ToHexStringLower(SHA256.HashData(upload.Content)),
            ByteLength: upload.Content.LongLength,
            ContentType: contentType);
    }

    public async Task<StoredFile?> OpenAsync(string epid, string fileName, CancellationToken ct)
    {
        if (!fileName.StartsWith(ScalityLinks.Prefix, StringComparison.Ordinal))
            return await disk.OpenAsync(epid, fileName, ct);

        var key = links.KeyFor(epid, fileName);
        return key is null ? null : await scality.FetchAsync(key, ct);
    }

    /// <summary>
    /// Scality has no delete in the API BBMP gave, so a photo of a survey that was not saved
    /// stays there; its key is logged. Photos on the disk are deleted as before.
    /// </summary>
    public async Task DiscardAsync(IEnumerable<string> urls, CancellationToken ct)
    {
        var list = urls.ToList();
        foreach (var url in list.Where(u => links.KeyFromUrl(u) is not null))
            logger.LogWarning("Photo left in Scality (its survey was not saved): {Key}", links.KeyFromUrl(url));
        await disk.DiscardAsync(list.Where(u => links.KeyFromUrl(u) is null), ct);
    }

    private static string Safe(string value)
    {
        var cleaned = new string(value.Where(char.IsLetterOrDigit).ToArray());
        return cleaned.Length == 0 ? "unknown" : cleaned;
    }
}

/// <summary>
/// Builds and reads the "sc_{key}.{signature}" link names. The signature is an HMAC of the
/// EPID and the key under the API's JWT key, cut to 16 characters.
/// </summary>
public sealed class ScalityLinks(IOptions<JwtOptions> jwt)
{
    public const string Prefix = "sc_";

    private static readonly Regex Shape = new(@"^sc_([A-Za-z0-9_-]{4,800})\.([A-Za-z0-9_-]{16})$", RegexOptions.Compiled);

    public string Url(string publicBaseUrl, string epid, string key) =>
        $"{publicBaseUrl.TrimEnd('/')}/{ApiRoutes.Base}/propertyinfo/file/view/{Uri.EscapeDataString(epid)}/{Name(epid, key)}";

    public string Name(string epid, string key) => $"{Prefix}{Base64Url(Encoding.UTF8.GetBytes(key))}.{Sign(epid, key)}";

    /// <summary>The key a link name stands for, or null when it is not one of ours for this EPID.</summary>
    public string? KeyFor(string epid, string fileName)
    {
        var m = Shape.Match(fileName);
        if (!m.Success) return null;

        string key;
        try { key = Encoding.UTF8.GetString(FromBase64Url(m.Groups[1].Value)); }
        catch (FormatException) { return null; }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Sign(epid, key)), Encoding.ASCII.GetBytes(m.Groups[2].Value)) ? key : null;
    }

    /// <summary>The key inside a full link of ours (".../file/view/{epid}/sc_..."), or null.</summary>
    public string? KeyFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 && segments[^1].StartsWith(Prefix, StringComparison.Ordinal)
            ? KeyFor(Uri.UnescapeDataString(segments[^2]), Uri.UnescapeDataString(segments[^1]))
            : null;
    }

    private string Sign(string epid, string key)
    {
        var mac = HMACSHA256.HashData(jwt.Value.KeyBytes, Encoding.UTF8.GetBytes($"scality|{epid.Trim()}|{key}"));
        return Base64Url(mac)[..16];
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
