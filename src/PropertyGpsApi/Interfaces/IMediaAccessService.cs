using Dapper;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Models;

namespace PropertyGpsApi.Interfaces;

public interface IMediaAccessService
{
    Task<MediaScope?> ScopeForAsync(string epid, CancellationToken ct);
}
