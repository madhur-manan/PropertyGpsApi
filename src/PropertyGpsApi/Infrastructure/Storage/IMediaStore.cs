namespace PropertyGpsApi.Infrastructure.Storage;

/// <summary>
/// One captured file on its way to storage. Held as bytes rather than a stream because
/// every file is validated, hashed and written once, and the largest of them is a phone
/// photograph - small enough that buffering is simpler than passing a rewindable stream
/// through three layers.
/// </summary>
public sealed record MediaUpload(
    string Slot,
    string Epid,
    int? RoadOrdinal,
    string ClientFileName,
    string ContentType,
    byte[] Content);

/// <summary>
/// Where a file ended up. Url is what goes into the *_Document columns; StorageKey is how
/// this store finds it again, and the two differ once the cloud store is in use.
/// </summary>
public sealed record StoredMedia(
    string Url,
    string StorageKey,
    string Sha256,
    long ByteLength,
    string ContentType);

/// <summary>
/// The seam between the submit flow and wherever photographs actually live.
///
/// BBMP intend to keep these in a separate object store ("skaliti"), which is not
/// available yet. Nothing in the submit flow knows that: it asks this interface to store
/// bytes and gets back a URL. Adding the cloud driver later is a new implementation and a
/// config value, not a change to the endpoint.
/// </summary>
public interface IMediaStore
{
    Task<StoredMedia> SaveAsync(MediaUpload upload, CancellationToken ct);

    /// <summary>
    /// Reads a stored file back. Returns null when it does not exist - the caller turns
    /// that into a 404, and must return the same 404 for a file the officer may not see,
    /// so the endpoint never answers the question "does this EPID exist".
    /// </summary>
    Task<StoredFile?> OpenAsync(string epid, string fileName, CancellationToken ct);
}

/// <summary>A stored file on its way back out. Bytes rather than a stream because the
/// local store already has them and the cloud driver will fetch them whole anyway.</summary>
public sealed record StoredFile(string FileName, string ContentType, byte[] Content);
