using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Models;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Infrastructure.Options;

namespace PropertyGpsApi.Interfaces;

public interface IPushStatusService
{
    Task<PushStatusResponse> RecordAsync(
        PushStatusRequest request, long officerId, int roleId, CancellationToken ct);
}
