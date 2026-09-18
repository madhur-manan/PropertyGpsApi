using System.ComponentModel.DataAnnotations;

namespace PropertyGpsApi.Infrastructure.Options;

public sealed class MediaOptions
{
    public const string Section = "Media";

    /// <summary>
    /// "LocalDisk" today. BBMP intend a separate object store ("skaliti"); when its
    /// endpoint and credentials arrive that becomes another value here and another
    /// IMediaStore, with nothing in the submit flow changing.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Store { get; init; } = "LocalDisk";

    /// <summary>Where LocalDisk writes. Must be outside the application directory so a
    /// redeploy cannot remove a ward's evidence.</summary>
    [Required(AllowEmptyStrings = false)]
    public string RootPath { get; init; } = "";

    /// <summary>
    /// Prefix for the URLs written into the *_Document columns. Those values outlive this
    /// deployment and are read by the QC portal, so they are absolute.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string PublicBaseUrl { get; init; } = "";

    [Range(1024, 50 * 1024 * 1024)]
    public long MaxFileBytes { get; init; } = 12 * 1024 * 1024;

    /// <summary>A multi-road survey carries roughly fifteen files; this caps the whole
    /// request rather than each part.</summary>
    [Range(1024, 200 * 1024 * 1024)]
    public long MaxRequestBytes { get; init; } = 80 * 1024 * 1024;
}
