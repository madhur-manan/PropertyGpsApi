using System.Data;
using Dapper;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Models;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Infrastructure.Options;

namespace PropertyGpsApi.Interfaces;

public interface IPropertyService
{
    /// <summary>
    /// The ward worklist. Refuses outright if a ward officer asks for a ward
    /// that is not theirs - see JurisdictionRules.RequireOwnWard.
    /// </summary>
    Task<IReadOnlyList<PropertyDto>> FetchAsync(
        FetchApplicationsRequest request,
        long officerId,
        int roleId,
        long? officerWardId,
        CancellationToken ct);
}
