using PropertyGpsApi.Common;

namespace PropertyGpsApi.Infrastructure.Storage;

/// <summary>
/// What a captured file is allowed to be.
///
/// Three things must agree before a byte is written: the extension, the declared
/// content type and the leading bytes of the file itself. A client can lie about the
/// first two; it cannot easily lie about the third and still have a usable photograph.
/// </summary>
public static class MediaContentPolicy
{
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string Pdf = "application/pdf";

    /// <summary>
    /// The khata note sheet is the only slot an officer may photograph OR attach as a
    /// PDF. Everything else is a camera capture, so a PDF there means the client is
    /// confused and we would rather find out now than at QC.
    /// </summary>
    public const string NoteSheetSlot = "noteSheetFile";

    public static string Resolve(string clientFileName, string declaredContentType, ReadOnlySpan<byte> content, string slot)
    {
        var extension = Path.GetExtension(clientFileName).ToLowerInvariant();
        var sniffed = Sniff(content);

        if (sniffed is null)
            throw ApiException.Unprocessable(
                $"'{clientFileName}' is not a JPEG, PNG or PDF.",
                ApiErrorCodes.MediaUnsupported, recoverable: true);

        if (sniffed == Pdf && slot != NoteSheetSlot)
            throw ApiException.Unprocessable(
                $"'{clientFileName}' is a PDF, and only the note sheet may be a PDF.",
                ApiErrorCodes.MediaUnsupported, recoverable: true);

        if (ExpectedFor(extension) != sniffed)
            throw ApiException.Unprocessable(
                $"'{clientFileName}' does not match its contents.",
                ApiErrorCodes.MediaMismatch, recoverable: true);

        // Worth being strict about: a JPEG that stops mid-file is what a dying mobile
        // connection produces, and it looks perfectly valid until somebody opens it at QC.
        if (sniffed == Jpeg && !EndsWithJpegMarker(content))
            throw ApiException.Unprocessable(
                $"'{clientFileName}' is incomplete. Please capture it again.",
                ApiErrorCodes.MediaTruncated, recoverable: true);

        if (!string.IsNullOrWhiteSpace(declaredContentType)
            && !declaredContentType.StartsWith(sniffed, StringComparison.OrdinalIgnoreCase))
            throw ApiException.Unprocessable(
                $"'{clientFileName}' was sent as {declaredContentType} but is a {sniffed}.",
                ApiErrorCodes.MediaMismatch, recoverable: true);

        return sniffed;
    }

    private static string? ExpectedFor(string extension) => extension switch
    {
        ".jpg" or ".jpeg" => Jpeg,
        ".png" => Png,
        ".pdf" => Pdf,
        _ => null
    };

    private static string? Sniff(ReadOnlySpan<byte> c)
    {
        if (c.Length >= 3 && c[0] == 0xFF && c[1] == 0xD8 && c[2] == 0xFF) return Jpeg;
        if (c.Length >= 8 && c[0] == 0x89 && c[1] == 0x50 && c[2] == 0x4E && c[3] == 0x47
            && c[4] == 0x0D && c[5] == 0x0A && c[6] == 0x1A && c[7] == 0x0A) return Png;
        if (c.Length >= 5 && c[0] == 0x25 && c[1] == 0x50 && c[2] == 0x44 && c[3] == 0x46
            && c[4] == 0x2D) return Pdf;
        return null;
    }

    private static bool EndsWithJpegMarker(ReadOnlySpan<byte> c)
    {
        // Some cameras pad with nulls after EOI, so look at the tail rather than the very
        // last two bytes.
        var tail = c.Length <= 64 ? c : c[^64..];
        for (var i = 0; i < tail.Length - 1; i++)
            if (tail[i] == 0xFF && tail[i + 1] == 0xD9) return true;
        return false;
    }

    public static string ExtensionFor(string contentType) => contentType switch
    {
        Jpeg => ".jpg",
        Png => ".png",
        Pdf => ".pdf",
        _ => ".bin"
    };
}
