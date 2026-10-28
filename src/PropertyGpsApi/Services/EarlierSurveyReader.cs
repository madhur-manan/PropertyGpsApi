using System.Data.Common;
using System.Text.Json;
using Dapper;
using PropertyGpsApi.Models;

namespace PropertyGpsApi.Services;

/// <summary>
/// The last survey an RI sent for an application, read back from the rows the submit wrote
/// (N2): BtoA_StatusDetail_Officer, BtoA_MainApp_Officer and BtoA_SiteRoadDetails_Officer.
/// Nothing is stored for this purpose - the user's rule is that a returned survey is rebuilt
/// from the survey's own tables - so every value here is the inverse of what
/// VerificationSubmitService writes, in the shape the phone sends.
///
/// Photos and GPS points are never returned: the officer correcting it takes them again.
/// The units the officer typed are not stored, so widths come back in metres and extents in
/// square metres - the values the phone filed, which it sends again unchanged.
/// </summary>
internal static class EarlierSurveyReader
{
    /// <summary>
    /// Five result sets for one application: the last RI submission (status row), the
    /// application row, the roads of that submission, and - for a survey the legacy GPS app
    /// filed - its road rows and the application's declared roads to tie them to.
    ///
    /// The last submitter is the newest active RI status row - a QC return does not change it
    /// (QC writes its own role-118 row). The application row is one per display id; it is
    /// overwritten by every resubmission. Roads are that officer's rows: the live ones, plus
    /// the "not found" ones (written inactive from the start) that this submission wrote - newer
    /// than the same officer's previous submission. One row per declared road (the live one
    /// first), and every officer-added road.
    /// </summary>
    internal const string Sql = """
        DECLARE @id nvarchar(50), @by bigint, @role int, @statusRowId int, @prevSubmitOn datetime;
        SELECT @id = App_DisplayId FROM dbo.BtoAMainApp WITH (NOLOCK) WHERE App_Id = @appId AND App_Active = 1;

        SELECT TOP (1) @by = s.CBy, @role = s.CRole, @statusRowId = s.Ofcr_RowId
        FROM dbo.BtoA_StatusDetail_Officer s WITH (NOLOCK)
        WHERE s.Ofcr_App_Id = @appId AND s.Status_Active = 1 AND s.CRole IN (116, 117)
        ORDER BY s.CDte DESC, s.Ofcr_RowId DESC;

        SELECT @prevSubmitOn = MAX(p.CDte) FROM dbo.BtoA_StatusDetail_Officer p WITH (NOLOCK)
        WHERE p.Ofcr_App_Id = @appId AND p.CBy = @by AND p.CRole = @role AND p.Ofcr_RowId < @statusRowId;

        SELECT SubmittedBy = s.CBy, SubmittedRole = s.CRole, SubmittedOn = s.CDte, Remark = s.Status_Remark
        FROM dbo.BtoA_StatusDetail_Officer s WITH (NOLOCK)
        WHERE s.Ofcr_RowId = @statusRowId;

        SELECT TOP (1)
            LandExists        = CAST(m.Ofcr_PropertyLandExistOnSpot AS int),
            BuildingExists    = CAST(m.IsBuildingExists AS int),
            GovtBit           = CAST(m.Ofcr_IsGovtProperty AS int),
            GovtValue         = m.Ofcr_IsGovtProperty_Value,
            GovtDetails       = m.Ofcr_GovtPropertyDetails,
            UseTypeCorrected  = CAST(m.Ofcr_CorrectedPropertyUseType AS int),
            UseTypeId         = m.Ofcr_CorrectedPropertyUseTypeId,
            UseTypeValue      = m.Ofcr_CorrectedPropertyUseTypeValue,
            CommercialArea    = m.Ofcr_CorrectedCommercialArea,
            ResidentialArea   = m.Ofcr_CorrectedResidentialArea,
            IndustrialArea    = m.Ofcr_Corrected_IndustrialArea,
            GpsCorrected      = CAST(m.Ofcr_CorrectedGpsLocation AS int),
            CornerPlot        = CAST(m.Ofcr_IsCornerPlotOrMoreThanTwoSidesRoadFacing AS int),
            RoadFacingSides   = m.Ofcr_RoadFacingSides,
            RoadWidthMetres   = m.Ofcr_RoadWidthInFrontOfPropertyInSqmt,
            Additional        = m.Ofcr_Aditional,
            LastWriter        = ISNULL(m.UBy, m.CBy)
        FROM dbo.BtoA_MainApp_Officer m WITH (NOLOCK)
        WHERE m.Ofcr_ApplicationDisplayId = @id
        ORDER BY CASE WHEN m.Ofcr_App_Id = @appId THEN 0 ELSE 1 END, ISNULL(m.UDte, m.CDte) DESC, m.Ofcr_RowId DESC;

        WITH r AS (
            SELECT o.Ofcr_RowId, RoadRowId = TRY_CAST(o.Ofcr_SiteRoadRowID AS int),
                   o.Ofcr_SiteRoadRowID, o.Ofcr_RoadId, o.Ofcr_RoadName, o.Ofcr_ActualRoadName, o.Ofcr_RoadType,
                   InPublicList = CAST(o.Ofcr_IsPresentInPublicRoadList AS int),
                   o.Ofcr_Correction_Type_Id, o.Ofcr_Correction_Type_Value,
                   Corrected = CAST(o.Ofcr_Corrected_Road_Details AS int),
                   rn = ROW_NUMBER() OVER (
                          PARTITION BY CASE WHEN o.Ofcr_SiteRoadRowID IS NULL THEN CONCAT(N'added:', o.Ofcr_RowId)
                                            ELSE o.Ofcr_SiteRoadRowID END
                          ORDER BY o.isActive DESC, ISNULL(o.UDte, o.CDte) DESC, o.Ofcr_RowId DESC)
            FROM dbo.BtoA_SiteRoadDetails_Officer o WITH (NOLOCK)
            WHERE o.Ofcr_ApplicationDisplayId = @id AND o.CBy = @by AND o.CRole = @role
              AND (o.isActive = 1
                   OR (o.isActive = 0 AND o.Ofcr_Correction_Type_Id = 2
                       AND ISNULL(o.UDte, o.CDte) > ISNULL(@prevSubmitOn, '19000101')))
        )
        SELECT RowId = r.Ofcr_RowId, r.RoadRowId, IsAdded = CASE WHEN r.Ofcr_SiteRoadRowID IS NULL THEN 1 ELSE 0 END,
               RoadId = CAST(r.Ofcr_RoadId AS nvarchar(50)), RoadName = r.Ofcr_RoadName, ActualRoadName = r.Ofcr_ActualRoadName,
               RoadType = r.Ofcr_RoadType, r.InPublicList,
               TypeId = r.Ofcr_Correction_Type_Id, TypeValue = r.Ofcr_Correction_Type_Value, r.Corrected
        FROM r
        WHERE r.rn = 1
        ORDER BY CASE WHEN r.Ofcr_SiteRoadRowID IS NULL THEN 1 ELSE 0 END, r.RoadRowId, r.Ofcr_RowId;

        -- The legacy GPS app filed its road rows as CBy 0 / CRole 0 (its status row carries the
        -- officer), never made them inactive, and left earlier sends active beside later ones.
        -- Its newest send: the rows within two minutes of its newest row.
        SELECT RowId = o.Ofcr_RowId, SiteRoadRowId = o.Ofcr_SiteRoadRowID,
               RoadId = CAST(o.Ofcr_RoadId AS nvarchar(50)), RoadName = o.Ofcr_RoadName, ActualRoadName = o.Ofcr_ActualRoadName,
               RoadType = o.Ofcr_RoadType, InPublicList = CAST(o.Ofcr_IsPresentInPublicRoadList AS int),
               TypeId = o.Ofcr_Correction_Type_Id, TypeValue = o.Ofcr_Correction_Type_Value,
               Corrected = CAST(o.Ofcr_Corrected_Road_Details AS int)
        FROM dbo.BtoA_SiteRoadDetails_Officer o WITH (NOLOCK)
        WHERE o.Ofcr_ApplicationDisplayId = @id AND ISNULL(o.CBy, 0) = 0
          AND o.CDte >= DATEADD(SECOND, -120,
                (SELECT MAX(x.CDte) FROM dbo.BtoA_SiteRoadDetails_Officer x WITH (NOLOCK)
                  WHERE x.Ofcr_ApplicationDisplayId = @id AND ISNULL(x.CBy, 0) = 0))
        ORDER BY o.CDte DESC, o.Ofcr_RowId DESC;

        -- The application's declared roads, as the phone builds its road cards from them.
        SELECT RoadRowId = d.Rd_RoadRow_ID, RoadId = CAST(d.Rd_RoadId AS nvarchar(50))
        FROM dbo.BtoA_SiteRoadDetails d WITH (NOLOCK)
        WHERE d.Rd_App_Id = @appId AND ISNULL(d.Rd_RoadActive, 1) = 1;
        """;

    internal sealed class SubmissionRow
    {
        public long SubmittedBy { get; init; }
        public int SubmittedRole { get; init; }
        public DateTime SubmittedOn { get; init; }
        public string? Remark { get; init; }
    }

    internal sealed class ApplicationRow
    {
        public int? LandExists { get; init; }
        public int? BuildingExists { get; init; }
        public int? GovtBit { get; init; }
        public int? GovtValue { get; init; }
        public string? GovtDetails { get; init; }
        public int? UseTypeCorrected { get; init; }
        public int? UseTypeId { get; init; }
        public string? UseTypeValue { get; init; }
        public double? CommercialArea { get; init; }
        public double? ResidentialArea { get; init; }
        public double? IndustrialArea { get; init; }
        public int? GpsCorrected { get; init; }
        public int? CornerPlot { get; init; }
        public int? RoadFacingSides { get; init; }
        public double? RoadWidthMetres { get; init; }
        public string? Additional { get; init; }
        public long? LastWriter { get; init; }
    }

    // Road ids are text (the procedure's @BtoA_RoadId is NVARCHAR(50)); the query CASTs every
    // one, so a column still holding ints reads the same way.
    internal sealed class RoadRow
    {
        public int RowId { get; init; }
        public int? RoadRowId { get; init; }
        public int IsAdded { get; init; }
        public string? RoadId { get; init; }
        public string? RoadName { get; init; }
        public string? ActualRoadName { get; init; }
        public string? RoadType { get; init; }
        public int? InPublicList { get; init; }
        public int? TypeId { get; init; }
        public string? TypeValue { get; init; }
        public int? Corrected { get; init; }
    }

    internal sealed class LegacyRoadRow
    {
        public int RowId { get; init; }
        public string? SiteRoadRowId { get; init; }
        public string? RoadId { get; init; }
        public string? RoadName { get; init; }
        public string? ActualRoadName { get; init; }
        public string? RoadType { get; init; }
        public int? InPublicList { get; init; }
        public int? TypeId { get; init; }
        public string? TypeValue { get; init; }
        public int? Corrected { get; init; }
    }

    internal sealed class DeclaredRoad
    {
        public int RoadRowId { get; init; }
        public string? RoadId { get; init; }
    }

    /// <summary>The last RI survey, or null when no RI has submitted this application.</summary>
    internal static async Task<(SubmissionRow Submission, EarlierSurveyDto Survey)?> ReadAsync(
        DbConnection connection, int appId, CancellationToken ct)
    {
        await using var grid = await connection.QueryMultipleAsync(
            new CommandDefinition(Sql, new { appId }, commandTimeout: 60, cancellationToken: ct));
        var submission = await grid.ReadSingleOrDefaultAsync<SubmissionRow>();
        var application = await grid.ReadSingleOrDefaultAsync<ApplicationRow>();
        var roads = (await grid.ReadAsync<RoadRow>()).AsList();
        var legacy = (await grid.ReadAsync<LegacyRoadRow>()).AsList();
        var declared = (await grid.ReadAsync<DeclaredRoad>()).AsList();

        if (submission is null || application is null) return null;

        // This API files roads under the officer who sent them. Only when there are none is the
        // survey the legacy app's.
        var incomplete = false;
        if (roads.Count == 0 && legacy.Count > 0)
            (roads, incomplete) = ResolveLegacy(legacy, declared);

        return (submission, Build(submission, application, roads, incomplete));
    }

    /// <summary>Every inverse in one place, so it can be checked against the writes without a database.</summary>
    internal static EarlierSurveyDto Build(
        SubmissionRow submission, ApplicationRow app, IReadOnlyList<RoadRow> roads, bool roadsIncomplete = false)
    {
        var extra = Additional.Parse(app.Additional);
        return new EarlierSurveyDto
        {
            PropertyLandExists = app.LandExists,
            BuildingExists = app.BuildingExists,
            IsLandUntraceable = extra.Int("IsLandUntraceable"),
            IsLandLocationUpdated = extra.Int("IsLandLocationUpdated"),
            IsGovtProperty = GovtAnswer(app.GovtValue, app.GovtBit),
            GovtPropertyDetails = app.GovtDetails,
            KhataRecommendation = extra.Text("KhataRecommendation"),
            KhataComments = extra.Text("KhataComments"),
            Remark = submission.Remark,
            ActualRoadWidth = app.RoadWidthMetres,
            RoadsIncomplete = roadsIncomplete,
            SiteDetails = new SubmitSiteDetails
            {
                // Stored as "corrected" bits: the inverse of the officer's "is it correct" answer.
                IsPropertyUseTypeCorrect = Invert(app.UseTypeCorrected),
                PropertyUseType = app.UseTypeValue,
                PropertyUseTypeId = app.UseTypeId,
                CommercialArea = app.CommercialArea,
                ResidentialArea = app.ResidentialArea,
                IndustrialArea = app.IndustrialArea,
                IsGpsLocationCorrect = Invert(app.GpsCorrected),
                IsCornerPlot = app.CornerPlot,
                IsDeclaredRoadFacingSidesCorrect = extra.Int("isDeclaredRoadFacingSidesCorrect"),
                RoadFacingSides = app.RoadFacingSides,
                RoadDetails = roads.Select(Road).ToList()
            }
        };
    }

    /// <summary>
    /// Ties the legacy app's road rows to the application's declared roads. That app filed the
    /// SITE id where the road row id belongs on most rows (D25), and no id at all on many, so:
    /// the row id when it is a declared road; else the one declared road with the same road
    /// id; else, on an application with a single declared road, that road. A road it added is
    /// recognised by its word ('Add'), not by a missing id. Newest row first, one per road.
    /// A declared row that cannot be tied to a road is left out - the officer answers that
    /// road again - and reported, rather than being put on the wrong card.
    /// </summary>
    internal static (List<RoadRow> Roads, bool Incomplete) ResolveLegacy(
        IReadOnlyList<LegacyRoadRow> rows, IReadOnlyList<DeclaredRoad> declared)
    {
        var declaredIds = declared.Select(d => d.RoadRowId).ToHashSet();
        var used = new HashSet<int>();
        var addedSeen = new HashSet<string>();
        var result = new List<RoadRow>();
        var incomplete = false;

        foreach (var r in rows)
        {
            var status = RoadStatus(r.TypeId, r.TypeValue, added: false);
            if (status == 3)
            {
                var key = $"{r.RoadId}|{r.RoadType?.Trim().ToLowerInvariant()}|{r.RoadName?.Trim()}|{r.ActualRoadName?.Trim()}";
                if (addedSeen.Add(key)) result.Add(ToRow(r, null, added: true));
                continue;
            }

            int? match = null;
            if (int.TryParse(r.SiteRoadRowId?.Trim(), out var id) && declaredIds.Contains(id))
                match = id;
            else
            {
                var sameRoad = declared.Where(d => SameRoadId(d.RoadId, r.RoadId)).ToList();
                if (sameRoad.Count == 1) match = sameRoad[0].RoadRowId;
                else if (sameRoad.Count == 0 && declared.Count == 1) match = declared[0].RoadRowId;
            }

            if (match is null)
            {
                incomplete = true;
                continue;
            }
            // Newest first: an older send of the same road is not this survey's answer.
            if (used.Add(match.Value)) result.Add(ToRow(r, match, added: false));
        }

        var ordered = result
            .OrderBy(x => x.IsAdded)
            .ThenBy(x => x.RoadRowId ?? int.MaxValue)
            .ThenBy(x => x.RowId)
            .ToList();
        return (ordered, incomplete);

        static RoadRow ToRow(LegacyRoadRow r, int? roadRowId, bool added) => new()
        {
            RowId = r.RowId,
            RoadRowId = roadRowId,
            IsAdded = added ? 1 : 0,
            RoadId = r.RoadId,
            RoadName = r.RoadName,
            ActualRoadName = r.ActualRoadName,
            RoadType = r.RoadType,
            InPublicList = r.InPublicList,
            TypeId = r.TypeId,
            TypeValue = r.TypeValue,
            Corrected = r.Corrected
        };
    }

    internal static SubmitRoadDetail Road(RoadRow r)
    {
        var status = RoadStatus(r.TypeId, r.TypeValue, r.IsAdded == 1);
        return new SubmitRoadDetail
        {
            RoadRowId = r.IsAdded == 1 ? null : r.RoadRowId,
            RoadId = VerificationSubmitService.RoadIdText(r.RoadId),
            RoadName = r.RoadName,
            ActualRoadName = r.ActualRoadName,
            RoadType = r.RoadType,
            IsPresentInPublicRoadList = r.InPublicList,
            RoadStatus = status,
            // Not asked on a road marked not found or added (the phone sends null there); the
            // stored bit on those is derived, not an answer.
            IsRoadDetailsCorrect = status is 2 or 3 ? null : Invert(r.Corrected)
        };
    }

    /// <summary>
    /// Ofcr_Correction_Type_Id is the phone's roadStatus. Rows the legacy app wrote carry its
    /// own words instead ('No Change' / 'Update' / 'Delete' / 'Add'), and their Delete rows are
    /// still active - so the words decide when present, the id otherwise.
    /// </summary>
    internal static int? RoadStatus(int? typeId, string? typeValue, bool added) =>
        typeValue?.Trim().ToLowerInvariant() switch
        {
            "unchanged" or "no change" => 0,
            "updated" or "update" => 1,
            "delete" or "deleted" => 2,
            "added" or "add" => 3,
            _ => typeId is >= 0 and <= 3 ? typeId : added ? 3 : null
        };

    /// <summary>Two road ids are the same road when both are present and match trimmed, ignoring case.</summary>
    internal static bool SameRoadId(string? a, string? b) =>
        VerificationSubmitService.RoadIdText(a) is { } x && VerificationSubmitService.RoadIdText(b) is { } y
        && string.Equals(x, y, StringComparison.OrdinalIgnoreCase);

    /// <summary>1 stored means "corrected", which is the answer No (0), and the reverse.</summary>
    internal static int? Invert(int? correctedBit) => correctedBit switch
    {
        1 => 0,
        0 => 1,
        _ => null
    };

    /// <summary>
    /// Ofcr_IsGovtProperty_Value keeps the full answer (0/1/2) but is written with COALESCE, so a
    /// later "not answered" leaves the earlier value; the bit is rewritten every time (1 only for
    /// Yes). A Yes in the value that the bit no longer confirms was therefore not answered last
    /// time. Legacy rows have no value, only the bit.
    /// </summary>
    internal static int? GovtAnswer(int? value, int? bit) => value switch
    {
        null => bit,
        1 when bit == 0 => null,
        _ => value
    };

    /// <summary>
    /// Ofcr_Aditional: the API's JSON of answers that have no column (BuildAdditionalInfo).
    /// Legacy rows hold free text there, which reads as no answers.
    /// </summary>
    internal sealed class Additional
    {
        private readonly Dictionary<string, JsonElement> _values;

        private Additional(Dictionary<string, JsonElement> values) => _values = values;

        internal static Additional Parse(string? text)
        {
            var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith('{'))
            {
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        foreach (var p in doc.RootElement.EnumerateObject())
                            values[p.Name] = p.Value.Clone();
                }
                catch (JsonException)
                {
                    values.Clear();
                }
            }
            return new Additional(values);
        }

        internal int? Int(string key) =>
            _values.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
                ? i
                : null;

        internal string? Text(string key) =>
            _values.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
