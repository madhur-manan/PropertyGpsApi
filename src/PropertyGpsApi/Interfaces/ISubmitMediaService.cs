using PropertyGpsApi.Common;
using PropertyGpsApi.Models;
using PropertyGpsApi.Infrastructure.Storage;

namespace PropertyGpsApi.Interfaces;

/// <summary>
/// Matches uploaded parts to the survey answers that reference them, then stores them.
///
/// The legacy contract used six separately-named parts and matched private-road photos to
/// roads by array position, which is one mis-ordered list away from filing a notice
/// against the wrong road. Here every file arrives in one repeated part named "files",
/// and the JSON field that names a basename IS the slot. A photo cannot be attached to
/// the wrong road without the payload itself being wrong.
/// </summary>
public interface ISubmitMediaService
{
    Task<IReadOnlyDictionary<string, string>> StoreAsync(
        SubmitVerificationRequest request, IFormFileCollection files, CancellationToken ct);
}
