using PropertyGpsApi.Models;

namespace PropertyGpsApi.Services;

/// <summary>
/// One application's rows, as USP_S_GetAppDetails and USP_S_GpsWardSync both return them,
/// into the shape the app stores. Shared so the old fetch and the ward sync cannot drift:
/// the device writes both into the same SQLite table.
/// </summary>
internal static class PropertyMapper
{
    internal static PropertyDto Map(
        IGrouping<int, PropertyRow> group,
        IReadOnlyDictionary<int, AssignmentRow> assignments,
        IReadOnlyDictionary<int, OwnerSummary> owners,
        long officerId)
    {
        // One row per road; blank road rows are not roads (see IsBlankRoad).
        var roadRows = group.Where(r => r.Rd_RoadRow_ID is not null && !IsBlankRoad(r)).ToList();

        // The application's own fields are the same on every row. The road fields are not.
        // Taking the lowest real road row (not whichever the procedure happened to emit
        // first) keeps the single-road fields older apps read stable; with no real road at
        // all, any row still carries the application.
        var row = roadRows.OrderBy(r => r.Rd_RoadRow_ID).FirstOrDefault()
                  ?? group.OrderBy(r => r.Rd_RoadRow_ID ?? int.MaxValue).First();
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
                RoadRowIds = roadRows
                    .Select(r => r.Rd_RoadRow_ID!.Value)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToList(),
                Roads = Roads(roadRows)
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

    /// <summary>
    /// A road row with nothing in it: no street-master road (id empty or 0) and no name of
    /// any kind. Not a citizen declaration - the test copy has 10,027 of them on 10,026
    /// applications, one each, none with a creator or a date (2026-10-05) - so it is not
    /// shown as a card. A row with an id or any name is a road, however sparse.
    /// </summary>
    internal static bool IsBlankRoad(PropertyRow r) =>
        IsBlankRoad(r.RoadId, r.Roadname, r.Rd_EnteredRoadName, r.Rd_PrivateRoadName, r.Rd_PrivateRoadText);

    internal static bool IsBlankRoad(
        string? roadId, string? roadName, string? enteredRoadName, string? privateRoadName, string? privateRoadText) =>
        (string.IsNullOrWhiteSpace(roadId) || roadId.Trim() == "0")
        && string.IsNullOrWhiteSpace(roadName)
        && string.IsNullOrWhiteSpace(enteredRoadName)
        && string.IsNullOrWhiteSpace(privateRoadName)
        && string.IsNullOrWhiteSpace(privateRoadText);

    /// <summary>
    /// Brings old-fetch rows to what the ward sync returns (defect D14): a road row the
    /// citizen deactivated is not a road (its Rd_RoadRow_ID is cleared, so the application
    /// row survives and the road is left out), and an active row gets its typed name.
    /// Rows absent from <paramref name="state"/> are left as they are.
    /// </summary>
    internal static void ApplyRoadState(IEnumerable<PropertyRow> rows, IReadOnlyDictionary<int, RoadRowState> state)
    {
        foreach (var row in rows)
        {
            if (row.Rd_RoadRow_ID is not { } id || !state.TryGetValue(id, out var s)) continue;
            if (!s.Active)
            {
                row.Rd_RoadRow_ID = null;
                continue;
            }
            row.Rd_EnteredRoadName ??= s.EnteredRoadName;
        }
    }

    /// <summary>
    /// USP_S_GetAppDetails applies TOP (n) to joined rows, not to applications, so when it
    /// returns exactly n rows the last application may be cut part-way through its road rows
    /// (a corner plot delivered with one of its two roads). That application is dropped: it
    /// is not acknowledged, so the next fetch delivers it whole. Proc order is kept.
    /// </summary>
    internal static List<PropertyRow> DropPossiblyTruncatedLast(List<PropertyRow> rows, int limit)
    {
        if (rows.Count < limit || rows.Count == 0) return rows;
        var last = rows[^1].AppID;
        var kept = rows.Where(r => r.AppID != last).ToList();
        // A single application filling the whole page cannot be cut by another; keep it.
        return kept.Count == 0 ? rows : kept;
    }

    /// <summary>One entry per road row, each from its own row.</summary>
    private static List<RoadDto> Roads(IEnumerable<PropertyRow> rows) =>
        rows.GroupBy(r => r.Rd_RoadRow_ID!.Value)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var r = g.First();
                return new RoadDto
                {
                    RoadRowId = g.Key,
                    RoadType = r.RoadType,
                    RoadId = r.RoadId,
                    RoadName = r.Roadname,
                    EnteredRoadName = r.Rd_EnteredRoadName,
                    IsPresentInPublicRoadList = r.Rd_isPresentInPublicRoadList switch
                    {
                        true => 1,
                        false => 0,
                        null => null
                    },
                    PrivateRoadId = r.Rd_PrivateRoadId,
                    PrivateRoadName = r.Rd_PrivateRoadName,
                    PrivateRoadText = r.Rd_PrivateRoadText
                };
            })
            .ToList();
}
