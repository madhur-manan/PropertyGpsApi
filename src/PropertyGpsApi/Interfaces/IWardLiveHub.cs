using System.Net.WebSockets;
using PropertyGpsApi.Models;
using PropertyGpsApi.Services;

namespace PropertyGpsApi.Interfaces;

/// <summary>
/// The phones that have a ward's property list open, each on a WebSocket, and the pushes to
/// them. A change of holder made through this API - an allot or an unassign - reaches every
/// other phone of the ward at once, instead of at its next pull-down.
///
/// A ward is its ULB code and ward number; the number is matched trimmed and ignoring case.
/// </summary>
internal interface IWardLiveHub
{
    /// <summary>
    /// Registers an accepted socket. An older socket of the same officer in the same ward is
    /// closed: one per officer per ward.
    /// </summary>
    WardLiveConnection Add(int ulbCode, string wardNumber, long officerId, WebSocket socket);

    void Remove(WardLiveConnection connection);

    /// <summary>
    /// Sends these holders to every socket of the ward and returns how many received them.
    /// Never throws: a push is a courtesy, and the phones catch up by pull-down anyway.
    /// </summary>
    Task<int> PublishAsync(int ulbCode, string wardNumber, IReadOnlyList<WardSyncHolderDto> holders);

    int Count(int ulbCode, string wardNumber);
}
