using System.Data;
using Dapper;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Features.Properties.Dtos;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Infrastructure.Options;

namespace PropertyGpsApi.Features.Properties;

public interface IPropertyRepository
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

/// <summary>
/// The mobile fetch, over USP_S_GetAppDetails: applications at App_Status 10 with the
/// processing fee paid that no device has taken delivery of yet, scoped to one ward.
/// </summary>
internal sealed class PropertyRepository(
    ISqlConnectionFactory connections,
    IOptions<StoredProcedureOptions> procedures,
    IAssignmentReader assignments,
    IOwnerReader owners,
    ILogger<PropertyRepository> logger) : IPropertyRepository
{
    /// <summary>
    /// Passed as @AppID, which the procedure uses as TOP (n) rather than as an identifier.
    /// A ward's undelivered backlog is small, but the cap stops a first sync from becoming
    /// unbounded if that ever stops being true.
    /// </summary>
    private const int RowLimit = 500;

    public async Task<IReadOnlyList<PropertyDto>> FetchAsync(
        FetchApplicationsRequest request,
        long officerId,
        int roleId,
        long? officerWardId,
        CancellationToken ct)
    {
        JurisdictionRules.RequireOwnWard(
            roleId, officerWardId, request.WardId,
            "You can only view applications for your own ward.");

        var p = new DynamicParameters();
        p.Add("@AppID", RowLimit, DbType.Int32);
        p.Add("@Epid",
            string.IsNullOrWhiteSpace(request.Epid) ? null : request.Epid.Trim(),
            DbType.String, size: 20);
        p.Add("@zoneId", request.ZoneId, DbType.Int32);
        p.Add("@wardId", request.WardId, DbType.Int32);
        p.Add("@Level", 1, DbType.Int32);

        await using var connection = await connections.OpenAsync(DbTarget.B2A, ct);

        var rows = (await connection.QueryAsync<PropertyRow>(
            Sp.Call(procedures.Value.FetchApplications, p, ct, timeoutSeconds: 120))).AsList();

        // A corner plot joins to one row per declared road, so the procedure returns several
        // rows for the same application. Group rather than letting the application duplicate.
        var grouped = rows.GroupBy(r => r.AppID).ToList();

        var appIds = grouped.Select(g => g.Key).ToList();
        var assignmentsByApp = await assignments.ActiveForAsync(connection, appIds, ct);
        var ownersByApp = await owners.ForAsync(connection, appIds, ct);

        logger.LogInformation(
            "Fetched {Apps} applications ({Rows} rows) for officer {OfficerId} zone {Zone} ward {Ward}",
            grouped.Count, rows.Count, officerId, request.ZoneId, request.WardId);

        return grouped.Select(g => Map(g, assignmentsByApp, ownersByApp, officerId)).ToList();
    }

    private static PropertyDto Map(
        IGrouping<int, PropertyRow> group,
        IReadOnlyDictionary<int, AssignmentRow> assignments,
        IReadOnlyDictionary<int, OwnerSummary> owners,
        long officerId)
    {
        var row = group.First();
        assignments.TryGetValue(group.Key, out var held);
        owners.TryGetValue(group.Key, out var owner);

        return new PropertyDto
        {
            AppId = row.AppID,
            ApplicationId = row.AppDisplayId,
            Epid = row.MotherEPID,
            SasId = row.MotherSASID,
            OwnerNames = owner?.Names,
            OwnerNumbers = owner?.Numbers,
            ApplicationType = row.ApplicationType,
            AppType = row.Type,
            Status = row.Status,
            Remarks = row.Remarks,
            AdditionalInfo = row.AdditionalInfo,
            ProcessingFeePaid = row.isProcessingFeePaid,
            ProcessingFee = row.ProcessingFee,
            ZoneId = row.ZoneId,
            ZoneName = row.GBAZoneName_En,
            WardId = row.WardId,
            // Naive datetime in the column; stamp IST explicitly rather than letting the
            // server's local zone decide, since this is read on a phone in another process.
            AppliedOn = row.App_Cdte is { } d ? new DateTimeOffset(d, TimeSpan.FromHours(5.5)) : null,
            KhataType = row.Khatatype,
            KhataLat = row.KhataLatitude,
            KhataLng = row.KhataLongitude,
            EpidJson = row.EpidJSON,
            SiteDetails = new SiteDetailsDto
            {
                SiteId = row.Site_Id,
                StreetId = row.StreetId,
                RoadType = row.RoadType,
                RoadId = row.RoadId,
                RoadName = row.Roadname,
                PublicRoadName = row.publicRoadname,
                PrivateRoadId = row.PrivateRoadId,
                PrivateRoadName = row.PrivateRoadName,
                PrivateRoadText = row.PrivateRoadText,
                IsSameLocationAsKhata = row.IsSameLocationAsKhata,
                Latitude = row.Latitude,
                Longitude = row.Longitude,
                SiteOrder = row.SiteOrder,
                IsPropertySurvey = row.App_IsPropertySurvey,
                IsPropertySurveyLocated = row.App_IsPropertySurveyLocated,
                DcConversionType = row.DcConversionType,
                AdditionalInfo = row.Site_AdditionalInfo,
                PropertyUseType = row.propertyUseType,
                CommercialExtentSqft = row.comercialExtentinSqft,
                ResidentialExtentSqft = row.residentailsExtentinSqft,
                IsCornerPlot = row.isCornorPlot,
                RoadFacingSides = row.Site_numberOfRoadFacingSides,
                RoadRowIds = group
                    .Select(r => r.Rd_RoadRow_ID)
                    .Where(id => id is not null)
                    .Select(id => id!.Value)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToList()
            },
            AssignedToUserId = held?.Arch_Id,
            AssignedToName = held?.Arch_Name,
            AssignmentStatus = held?.Status,
            AssignedOn = held?.AssignmentDate is { } a
                ? new DateTimeOffset(a, TimeSpan.FromHours(5.5))
                : null,
            IsAssignedToMe = held is not null && held.Arch_Id == officerId,
            IsAssignedToOther = held is not null && held.Arch_Id != officerId
        };
    }
}
