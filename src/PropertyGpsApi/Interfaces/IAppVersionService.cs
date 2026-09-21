using System.Data;
using Dapper;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Models;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Infrastructure.Options;

namespace PropertyGpsApi.Interfaces;

public interface IAppVersionService
{
    Task<VersionCheckResponse> CheckAsync(
        VersionCheckRequest request, long officerId, int roleId, CancellationToken ct);
}
