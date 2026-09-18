using PropertyGpsApi.Common;
using PropertyGpsApi.Features.Properties.Dtos;
using PropertyGpsApi.Infrastructure.Storage;

namespace PropertyGpsApi.Features.Properties;

/// <summary>
/// Matches uploaded parts to the survey answers that reference them, then stores them.
///
/// The legacy contract used six separately-named parts and matched private-road photos to
/// roads by array position, which is one mis-ordered list away from filing a notice
/// against the wrong road. Here every file arrives in one repeated part named "files",
/// and the JSON field that names a basename IS the slot. A photo cannot be attached to
/// the wrong road without the payload itself being wrong.
/// </summary>
public interface ISubmitMediaBinder
{
    Task<IReadOnlyDictionary<string, string>> StoreAsync(
        SubmitVerificationRequest request, IFormFileCollection files, CancellationToken ct);
}

internal sealed class SubmitMediaBinder(IMediaStore store, ILogger<SubmitMediaBinder> logger)
    : ISubmitMediaBinder
{
    public async Task<IReadOnlyDictionary<string, string>> StoreAsync(
        SubmitVerificationRequest request, IFormFileCollection files, CancellationToken ct)
    {
        var byName = new Dictionary<string, IFormFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var key = Path.GetFileName(file.FileName);
            if (!string.IsNullOrWhiteSpace(key)) byName[key] = file;
        }

        var slots = Collect(request);
        var stored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (clientName, slot, roadOrdinal) in slots)
        {
            if (stored.ContainsKey(clientName)) continue;   // the same file used in two slots

            if (!byName.TryGetValue(clientName, out var file))
                throw ApiException.Unprocessable(
                    $"The survey refers to '{clientName}' but no such file was uploaded.",
                    ApiErrorCodes.MediaMissing, recoverable: true);

            await using var input = file.OpenReadStream();
            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer, ct);

            var result = await store.SaveAsync(new MediaUpload(
                Slot: slot,
                Epid: request.Epid,
                RoadOrdinal: roadOrdinal,
                ClientFileName: clientName,
                ContentType: file.ContentType ?? "",
                Content: buffer.ToArray()), ct);

            stored[clientName] = result.Url;
        }

        var unused = byName.Keys.Where(k => !stored.ContainsKey(k)).ToList();
        if (unused.Count > 0)
            logger.LogWarning("{Count} uploaded file(s) were not referenced by the survey for {Epid}: {Names}",
                unused.Count, request.Epid, string.Join(", ", unused));

        return stored;
    }

    private static List<(string ClientName, string Slot, int? RoadOrdinal)> Collect(SubmitVerificationRequest r)
    {
        var slots = new List<(string, string, int?)>();

        void Add(string? name, string slot, int? road = null)
        {
            if (!string.IsNullOrWhiteSpace(name)) slots.Add((Path.GetFileName(name), slot, road));
        }

        Add(r.PropertyImage, "property");
        Add(r.MapImage, "map");
        Add(r.NoteSheetFile, MediaContentPolicy.NoteSheetSlot);

        foreach (var road in r.SiteDetails.RoadDetails)
        {
            Add(road.PrivateRoadImage, "private", road.RoadRowId);
            Add(road.PublicRoadImage, "public", road.RoadRowId);
            Add(road.NoticeImage, "notice", road.RoadRowId);
        }

        return slots;
    }
}
