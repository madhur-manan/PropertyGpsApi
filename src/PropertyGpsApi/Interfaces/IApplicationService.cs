using System.Data;
using Dapper;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Common;
using PropertyGpsApi.Models;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Infrastructure.Options;

namespace PropertyGpsApi.Interfaces;

public interface IApplicationService
{
    Task<AssignOutcome> AssignAsync(
        int appId, long officerId, int roleId, string? officerName, bool assign, CancellationToken ct);
}
