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
    /// that is not one of theirs - see JurisdictionRules.RequireOwnWard.
    /// <paramref name="officerWards"/> is every ward mapped to a ward officer
    /// (IOfficerService.MappedWardsAsync), and null for other roles.
    /// </summary>
    Task<IReadOnlyList<PropertyDto>> FetchAsync( FetchApplicationsRequest request,long officerId,int roleId,IReadOnlyCollection<Jurisdiction>? officerWards,CancellationToken ct);
}
