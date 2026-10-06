using PropertyGpsApi.Models;

namespace PropertyGpsApi.Interfaces;

public interface IWardSyncService
{
    /// <summary>
    /// The next batch of applications the phone lacks in one of the officer's mapped wards.
    /// Refuses a ward the officer is not mapped to before touching the B2A database.
    /// </summary>
    Task<WardSyncResponse> PageAsync(WardSyncRequest request, long officerId, CancellationToken ct);

    /// <summary>Records that the phone saved these applications (IsPushedToGps / PushedToGpsDate).</summary>
    Task<WardSyncAckResponse> AckAsync(WardSyncAckRequest request, long officerId, CancellationToken ct);
}
