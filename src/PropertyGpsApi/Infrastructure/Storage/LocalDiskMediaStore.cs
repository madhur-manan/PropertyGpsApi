using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Common;
using PropertyGpsApi.Infrastructure.Options;

namespace PropertyGpsApi.Infrastructure.Storage;

/// <summary>
/// Writes captured files to a directory on the server.
///
/// This exists so the submit flow can be built and exercised before BBMP's object store
/// is available. It is a real implementation, not a stub: files are validated, hashed,
/// written durably and served back through the same URL shape production already uses.
/// </summary>
internal sealed class LocalDiskMediaStore(
    IOptions<MediaOptions> options,
    ILogger<LocalDiskMediaStore> logger) : IMediaStore
{
    public async Task<StoredMedia> SaveAsync(MediaUpload upload, CancellationToken ct)
    {
        var media = options.Value;

        if (upload.Content.LongLength > media.MaxFileBytes)
            throw ApiException.Unprocessable(
                $"'{upload.ClientFileName}' is larger than the {media.MaxFileBytes / (1024 * 1024)} MB limit.",
                ApiErrorCodes.MediaTooLarge, recoverable: true);

        var contentType = MediaContentPolicy.Resolve(
            upload.ClientFileName, upload.ContentType, upload.Content, upload.Slot);

        var sha = Convert.ToHexStringLower(SHA256.HashData(upload.Content));
        var name = BuildName(upload, contentType);

        // The EPID partitions the directory so one ward's worth of survey work does not
        // land in a single folder with a hundred thousand siblings.
        var relative = Path.Combine(SafeSegment(upload.Epid), name);
        var absolute = Path.Combine(media.RootPath, relative);

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);

        // Write to a temporary name and move into place, so a crash mid-write can never
        // leave a half-file that later reads as a valid photograph.
        var staging = absolute + ".partial";
        await File.WriteAllBytesAsync(staging, upload.Content, ct);
        File.Move(staging, absolute, overwrite: true);

        logger.LogInformation("Stored {Slot} for {Epid} as {Name} ({Bytes} bytes)",
            upload.Slot, upload.Epid, name, upload.Content.Length);

        return new StoredMedia(
            Url: $"{media.PublicBaseUrl.TrimEnd('/')}/{ApiRoutes.Base}/propertyinfo/file/view/{Uri.EscapeDataString(upload.Epid)}/{Uri.EscapeDataString(name)}",
            StorageKey: relative.Replace(Path.DirectorySeparatorChar, '/'),
            Sha256: sha,
            ByteLength: upload.Content.LongLength,
            ContentType: contentType);
    }

    /// <summary>
    /// The server names every file. The client's own name is only ever used to work out
    /// which slot a part belongs to, so traversal, absolute paths, alternate data streams
    /// and reserved device names are impossible here by construction rather than filtered.
    /// Shape follows what production already stores:
    /// {epid}-{roadRow}-private-{yyyyMMdd}_{HHmmss}.jpg
    /// </summary>
    private static string BuildName(MediaUpload upload, string contentType)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var road = upload.RoadOrdinal?.ToString() ?? "0";
        var unique = Guid.NewGuid().ToString("N")[..8];
        return $"{SafeSegment(upload.Epid)}-{road}-{SafeSegment(upload.Slot)}-{stamp}-{unique}"
             + MediaContentPolicy.ExtensionFor(contentType);
    }

    private static string SafeSegment(string value)
    {
        var cleaned = new string(value.Where(char.IsLetterOrDigit).ToArray());
        return cleaned.Length == 0 ? "unknown" : cleaned;
    }
}
