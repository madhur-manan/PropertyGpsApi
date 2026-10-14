using System.Text.Json;
using PropertyGpsApi.Models;
using PropertyGpsApi.Services;
using static PropertyGpsApi.Services.EarlierSurveyReader;

namespace PropertyGpsApi.Tests;

/// <summary>
/// N2: a returned survey is rebuilt from the rows the submit wrote - no copy is stored (the
/// user's rule: "the changes should happen in the same table only"). So every inverse here is
/// checked against the real write path: a request goes through VerificationSubmitService's own
/// parameter builders, the stored values are taken from those parameters exactly as the
/// procedures file them, and reading them back must give the request's answers again.
/// </summary>
public class EarlierSurveyReaderTests
{
    private static SubmitVerificationRequest Request(
        int? landExists = 1, int? buildingExists = 1, int? untraceable = null, int? locationUpdated = null,
        int? govt = 0, string? govtDetails = null, string? recommendation = null, string? comments = null,
        string? remark = "Owner present", double? width = 9.14,
        int? useCorrect = 1, string? useType = "Residential", int? useTypeId = 2,
        double? commercial = null, double? residential = null,
        int? gpsCorrect = 1, int? corner = 0, int? countCorrect = 1, int? sides = 1,
        params SubmitRoadDetail[] roads) => new()
    {
        ApplicationId = "202510150000113",
        Epid = "9990000113",
        VerifiedDate = new DateTimeOffset(2026, 10, 13, 10, 0, 0, TimeSpan.Zero),
        PropertyLandExists = landExists,
        BuildingExists = buildingExists,
        IsLandUntraceable = untraceable,
        IsLandLocationUpdated = locationUpdated,
        IsGovtProperty = govt,
        GovtPropertyDetails = govtDetails,
        KhataRecommendation = recommendation,
        KhataComments = comments,
        Remark = remark,
        ActualRoadWidth = width,
        SiteDetails = new SubmitSiteDetails
        {
            IsPropertyUseTypeCorrect = useCorrect,
            PropertyUseType = useType,
            PropertyUseTypeId = useTypeId,
            CommercialArea = commercial,
            ResidentialArea = residential,
            IsGpsLocationCorrect = gpsCorrect,
            IsCornerPlot = corner,
            IsDeclaredRoadFacingSidesCorrect = countCorrect,
            RoadFacingSides = sides,
            RoadDetails = roads
        }
    };

    private static SubmitRoadDetail Declared(int rowId, int status, int? correct, string type = "public",
        string? roadId = "637", string? name = "5th Cross", string? actual = "5th Cross", int? inList = 1) => new()
    {
        RoadRowId = rowId, RoadStatus = status, IsRoadDetailsCorrect = correct, RoadType = type,
        RoadId = roadId, RoadName = name, ActualRoadName = actual, IsPresentInPublicRoadList = inList
    };

    private static SubmitRoadDetail Added(string type, string roadId, string name, string actual, int inList) => new()
    {
        RoadRowId = null, RoadStatus = 3, IsRoadDetailsCorrect = 0, RoadType = type,
        RoadId = roadId, RoadName = name, ActualRoadName = actual, IsPresentInPublicRoadList = inList
    };

    /// <summary>
    /// What USP_IU_BtoA_MainApp_Officer, the extras procedure and the status procedure file,
    /// taken from the API's own parameters (the procedures store them as given; the land-use
    /// lookup maps each of the app's types to itself - mst_KsrsacMasterPlan2015, checked
    /// 2026-10-13).
    /// </summary>
    private static (SubmissionRow, ApplicationRow, List<RoadRow>) Store(SubmitVerificationRequest r)
    {
        var p = VerificationSubmitService.ApplicationParameters(r, r.SiteDetails, 113, 11320, 116);
        int Bit(string name) => p.Get<bool>(name) ? 1 : 0;

        var submission = new SubmissionRow
        {
            SubmittedBy = 11320, SubmittedRole = 116, SubmittedOn = new DateTime(2026, 10, 13, 15, 30, 0),
            Remark = r.Remark
        };
        var app = new ApplicationRow
        {
            LandExists = Bit("@Ofcr_PropertyLandExistOnSpot"),
            BuildingExists = p.Get<bool?>("@Ofcr_IsBuildingExists") is { } b ? (b ? 1 : 0) : null,
            GovtBit = Bit("@Ofcr_IsGovtProperty"),
            GovtValue = r.IsGovtProperty,
            GovtDetails = r.GovtPropertyDetails,
            UseTypeCorrected = Bit("@Ofcr_CorrectedPropertyUseType"),
            UseTypeId = p.Get<int?>("@Ofcr_CorrectedPropertyUseTypeId"),
            UseTypeValue = p.Get<string?>("@Ofcr_CorrectedPropertyUseTypeValue"),
            CommercialArea = p.Get<double?>("@Ofcr_CorrectedCommercialArea"),
            ResidentialArea = p.Get<double?>("@Ofcr_CorrectedResidentialArea"),
            GpsCorrected = Bit("@Ofcr_CorrectedGpsLocation"),
            CornerPlot = Bit("@Ofcr_IsCornerPlotOrMoreThanTwoSidesRoadFacing"),
            RoadFacingSides = p.Get<int?>("@Ofcr_RoadFacingSides"),
            RoadWidthMetres = p.Get<double?>("@Ofcr_RoadWidthInFrontOfPropertyInSqmt"),
            Additional = p.Get<string?>("@Ofcr_Aditional"),
            LastWriter = 11320
        };

        var rowId = 9000;
        var roads = r.SiteDetails.RoadDetails.Select(road =>
        {
            var q = VerificationSubmitService.RoadParameters(r.ApplicationId, road, new Dictionary<string, string>(), 11320, 116);
            return new RoadRow
            {
                RowId = rowId++,
                RoadRowId = int.TryParse(q.Get<string?>("@BtoA_SiteRoadRowID"), out var id) ? id : null,
                IsAdded = q.Get<string?>("@BtoA_SiteRoadRowID") is null ? 1 : 0,
                RoadId = q.Get<int?>("@BtoA_RoadId"),
                RoadName = q.Get<string?>("@BtoA_RoadName"),
                ActualRoadName = q.Get<string?>("@BtoA_ActualRoadName"),
                RoadType = q.Get<string?>("@BtoA_RoadType"),
                InPublicList = q.Get<bool>("@BtoA_IsPresentInPublicRoadList") ? 1 : 0,
                TypeId = q.Get<int?>("@BtoA_Correction_Type_Id"),
                TypeValue = q.Get<string?>("@BtoA_Correction_Type_Value"),
                Corrected = q.Get<bool>("@BtoA_Corrected_Road_Details") ? 1 : 0
            };
        }).ToList();

        return (submission, app, roads);
    }

    private static EarlierSurveyDto RoundTrip(SubmitVerificationRequest r)
    {
        var (s, a, roads) = Store(r);
        return Build(s, a, roads);
    }

    public class EveryAnswerComesBack
    {
        [Fact]
        public void A_plain_survey()
        {
            var r = Request(roads: Declared(5, 0, 1));
            var back = RoundTrip(r);

            Assert.Equal(1, back.PropertyLandExists);
            Assert.Equal(1, back.BuildingExists);
            Assert.Equal(0, back.IsGovtProperty);
            Assert.Equal("Owner present", back.Remark);
            Assert.Equal(9.14, back.ActualRoadWidth);
            Assert.Equal(1, back.SiteDetails.IsPropertyUseTypeCorrect);
            Assert.Equal(1, back.SiteDetails.IsGpsLocationCorrect);
            Assert.Equal(0, back.SiteDetails.IsCornerPlot);
            Assert.Equal(1, back.SiteDetails.IsDeclaredRoadFacingSidesCorrect);
            Assert.Equal(1, back.SiteDetails.RoadFacingSides);
        }

        [Fact]
        public void A_survey_where_the_officer_corrected_everything()
        {
            var r = Request(
                landExists: 1, buildingExists: 0, untraceable: 0, locationUpdated: 1,
                govt: 1, govtDetails: "Govt school compound", recommendation: "Reject", comments: "Encroachment",
                width: 6.1, useCorrect: 0, useType: "Mixed", useTypeId: 3, commercial: 12.0, residential: 147.77,
                gpsCorrect: 0, corner: 1, countCorrect: 0, sides: 2,
                roads: [Declared(5, 1, 0, type: "private", roadId: "999", name: "4th Main", actual: "Lane behind temple", inList: 0),
                        Added("public", "812", "Ring Road", "Ring Road", 1)]);
            var back = RoundTrip(r);

            Assert.Equal((1, 0, 0, 1), (back.PropertyLandExists, back.BuildingExists, back.IsLandUntraceable, back.IsLandLocationUpdated));
            Assert.Equal((1, "Govt school compound"), (back.IsGovtProperty, back.GovtPropertyDetails));
            Assert.Equal(("Reject", "Encroachment"), (back.KhataRecommendation, back.KhataComments));
            Assert.Equal(6.1, back.ActualRoadWidth);
            Assert.Equal((0, "Mixed", 3), (back.SiteDetails.IsPropertyUseTypeCorrect, back.SiteDetails.PropertyUseType, back.SiteDetails.PropertyUseTypeId));
            Assert.Equal((12.0, 147.77), (back.SiteDetails.CommercialArea, back.SiteDetails.ResidentialArea));
            Assert.Equal(0, back.SiteDetails.IsGpsLocationCorrect);
            Assert.Equal((1, 0, 2), (back.SiteDetails.IsCornerPlot, back.SiteDetails.IsDeclaredRoadFacingSidesCorrect, back.SiteDetails.RoadFacingSides));
        }

        [Theory]
        [InlineData(0, 1)]   // unchanged, confirmed correct
        [InlineData(1, 0)]   // changed, said wrong
        public void Each_declared_road_answer(int status, int correct)
        {
            var road = Declared(7, status, correct, type: status == 1 ? "private" : "public",
                roadId: status == 1 ? "999" : "637", actual: status == 1 ? "Typed lane" : "5th Cross", inList: status == 1 ? 0 : 1);
            var back = RoundTrip(Request(roads: road)).SiteDetails.RoadDetails.Single();

            Assert.Equal(road.RoadRowId, back.RoadRowId);
            Assert.Equal(road.RoadStatus, back.RoadStatus);
            Assert.Equal(road.IsRoadDetailsCorrect, back.IsRoadDetailsCorrect);
            Assert.Equal(road.RoadType, back.RoadType);
            Assert.Equal(road.RoadId, back.RoadId);
            Assert.Equal(road.RoadName, back.RoadName);
            Assert.Equal(road.ActualRoadName, back.ActualRoadName);
            Assert.Equal(road.IsPresentInPublicRoadList, back.IsPresentInPublicRoadList);
        }

        [Fact]
        public void A_road_marked_not_found_comes_back_not_found_with_no_answer()
        {
            var back = RoundTrip(Request(roads: Declared(7, 2, null))).SiteDetails.RoadDetails.Single();
            Assert.Equal((7, 2), (back.RoadRowId, back.RoadStatus));
            Assert.Null(back.IsRoadDetailsCorrect);
        }

        [Fact]
        public void An_added_road_comes_back_added_with_no_row_id()
        {
            var back = RoundTrip(Request(roads: Added("private", "999", "4th Main", "Typed lane", 0))).SiteDetails.RoadDetails.Single();
            Assert.Null(back.RoadRowId);
            Assert.Equal(3, back.RoadStatus);
            Assert.Null(back.IsRoadDetailsCorrect);
            Assert.Equal(("private", "999", "4th Main", "Typed lane"), (back.RoadType, back.RoadId, back.RoadName, back.ActualRoadName));
        }

        [Fact]
        public void Nothing_is_returned_that_must_be_taken_again()
        {
            var json = JsonSerializer.Serialize(RoundTrip(Request(roads: Declared(5, 0, 1))));
            using var doc = JsonDocument.Parse(json);
            var site = doc.RootElement.GetProperty("siteDetails");
            foreach (var name in new[] { "correctedLat", "correctedLng" })
                Assert.Equal(JsonValueKind.Null, site.GetProperty(name).ValueKind);
            var road = site.GetProperty("roadDetails")[0];
            foreach (var name in new[] { "privateRoadLat", "privateRoadLng", "nearPublicRoadLat", "nearPublicRoadLng",
                                         "privateRoadImage", "publicRoadImage", "noticeImage" })
                Assert.Equal(JsonValueKind.Null, road.GetProperty(name).ValueKind);
            Assert.False(doc.RootElement.TryGetProperty("propertyImage", out _));
            Assert.False(doc.RootElement.TryGetProperty("verifiedDate", out _));
        }
    }

    public class TheStoredWords
    {
        [Theory]
        [InlineData(null, "unchanged", 0)] [InlineData(null, "updated", 1)]
        [InlineData(null, "delete", 2)] [InlineData(null, "added", 3)]
        [InlineData(null, "No Change", 0)] [InlineData(null, "Update", 1)]
        [InlineData(null, "Delete", 2)] [InlineData(null, "Add", 3)]
        [InlineData(1, null, 1)]
        public void A_road_status_is_read_from_the_API_words_the_legacy_words_or_the_id(int? id, string? value, int expected) =>
            Assert.Equal(expected, RoadStatus(id, value, added: false));

        [Theory]
        [InlineData(1, 1, 1)]       // Yes, confirmed by the bit
        [InlineData(0, 0, 0)]
        [InlineData(2, 0, 2)]       // the referral, which the bit cannot hold
        [InlineData(1, 0, null)]    // a later "not answered" left the earlier Yes behind (COALESCE)
        [InlineData(null, 1, 1)]    // legacy rows: bit only
        public void The_government_land_answer_survives_the_COALESCE(int? value, int? bit, int? expected) =>
            Assert.Equal(expected, GovtAnswer(value, bit));

        [Theory]
        [InlineData("Mixed Area MissMatch Case")]
        [InlineData("{not json")]
        [InlineData(null)]
        public void Free_text_in_Ofcr_Aditional_reads_as_no_answers(string? text)
        {
            var extra = Additional.Parse(text);
            Assert.Null(extra.Int("IsLandUntraceable"));
            Assert.Null(extra.Text("KhataRecommendation"));
        }
    }

    /// <summary>
    /// Surveys the legacy GPS app filed: its road rows are CBy 0 / CRole 0 while its status row
    /// carries the officer, most hold the SITE id where the road row id belongs (D25), many
    /// none, and an added road is '0' or NULL with the word 'Add' (laptop DB, 2026-10-13: 30,506
    /// of 39,829 numbered legacy ids are the Site_Id; App 102401 is the reviewer's example).
    /// </summary>
    public class LegacySurveys
    {
        private const int SiteId = 800001;

        private static LegacyRoadRow Row(int rowId, string? siteRoadRowId, string word, int? roadId,
            string? name = "Byrthi", string? actual = "3rd Cross", string? type = "private") => new()
        {
            RowId = rowId, SiteRoadRowId = siteRoadRowId, TypeValue = word,
            TypeId = word switch { "No Change" => 0, "Update" => 1, "Delete" => 2, "Add" => 3, _ => null },
            RoadId = roadId, RoadName = name, ActualRoadName = actual, RoadType = type, InPublicList = 0, Corrected = 0
        };

        private static DeclaredRoad Declared(int rowId, int? roadId) => new() { RoadRowId = rowId, RoadId = roadId };

        [Fact]
        public void A_row_holding_the_declared_row_id_is_that_road() =>
            Assert.Equal(83508, ResolveLegacy([Row(1, "83508", "Update", 9320)], [Declared(83508, 9320), Declared(83509, 976)])
                .Roads.Single().RoadRowId);

        [Fact]
        public void A_row_holding_the_site_id_is_tied_by_its_road_id()
        {
            var (roads, incomplete) = ResolveLegacy(
                [Row(1, $"{SiteId}", "Update", 976, name: "Bile Shivale", type: "public")],
                [Declared(83508, 9320), Declared(83509, 976)]);
            Assert.Equal(83509, roads.Single().RoadRowId);
            Assert.False(incomplete);
        }

        [Fact]
        public void On_a_single_road_application_any_declared_row_is_that_road() =>
            Assert.Equal(83508, ResolveLegacy([Row(1, null, "No Change", 1234)], [Declared(83508, 9320)]).Roads.Single().RoadRowId);

        [Fact]
        public void A_row_that_cannot_be_tied_is_left_out_and_reported_not_put_on_a_wrong_card()
        {
            var (roads, incomplete) = ResolveLegacy(
                [Row(1, $"{SiteId}", "Update", 5555)],
                [Declared(83508, 9320), Declared(83509, 976)]);
            Assert.Empty(roads);
            Assert.True(incomplete);
        }

        [Fact]
        public void Two_declared_roads_with_the_same_road_id_are_not_guessed_between() =>
            Assert.True(ResolveLegacy([Row(1, null, "No Change", 9320)], [Declared(83508, 9320), Declared(83509, 9320)]).Incomplete);

        [Fact]
        public void An_added_road_is_known_by_its_word_whatever_its_id_says()
        {
            var roads = ResolveLegacy(
                [Row(1, "0", "Add", 999, actual: "Lane behind temple"), Row(2, null, "Add", 812, name: "Ring Road", actual: "Ring Road", type: "public")],
                [Declared(83508, 9320)]).Roads;
            Assert.All(roads, r => Assert.Equal((1, (int?)null), (r.IsAdded, r.RoadRowId)));
            Assert.Equal(2, roads.Count);
            Assert.All(roads.Select(Road), r => Assert.Equal(3, r.RoadStatus));
        }

        [Fact]
        public void The_newest_send_of_a_road_wins_and_exact_duplicates_of_an_added_road_collapse()
        {
            // Newest first, as the query orders them.
            var roads = ResolveLegacy(
                [Row(5, "83508", "Update", 9320, actual: "Newer"), Row(4, "83508", "No Change", 9320, actual: "Older"),
                 Row(3, "0", "Add", 999, actual: "Lane"), Row(2, "0", "Add", 999, actual: "Lane")],
                [Declared(83508, 9320)]).Roads;
            Assert.Equal("Newer", roads.Single(r => r.IsAdded == 0).ActualRoadName);
            Assert.Single(roads, r => r.IsAdded == 1);
        }

        [Fact]
        public void An_active_legacy_Delete_row_is_a_road_not_found() =>
            Assert.Equal(2, Road(ResolveLegacy([Row(1, "83508", "Delete", 9320)], [Declared(83508, 9320)]).Roads.Single()).RoadStatus);

        [Fact]
        public void Declared_roads_come_first_in_row_order_then_added_ones()
        {
            var roads = ResolveLegacy(
                [Row(9, "0", "Add", 999), Row(8, "83509", "No Change", 976), Row(7, "83508", "No Change", 9320)],
                [Declared(83508, 9320), Declared(83509, 976)]).Roads;
            Assert.Equal(new int?[] { 83508, 83509, null }, roads.Select(r => r.RoadRowId).ToArray());
        }

        [Fact]
        public void The_reply_says_when_roads_were_left_out() =>
            Assert.Contains("\"roadsIncomplete\":true",
                JsonSerializer.Serialize(new EarlierSurveyDto { RoadsIncomplete = true }));
    }

    public class TheQuery
    {
        [Fact]
        public void Reads_the_legacy_apps_newest_send_by_its_own_marks() =>
            Assert.Contains("ISNULL(o.CBy, 0) = 0", Sql);

        [Fact]
        public void Takes_the_last_RI_survey_not_QCs_row() =>
            Assert.Contains("s.Status_Active = 1 AND s.CRole IN (116, 117)", Sql);

        [Fact]
        public void Reads_only_that_officers_roads() =>
            Assert.Contains("o.CBy = @by AND o.CRole = @role", Sql);

        [Fact]
        public void Keeps_the_roads_that_survey_marked_not_found() =>
            Assert.Contains("o.isActive = 0 AND o.Ofcr_Correction_Type_Id = 2", Sql);

        [Fact]
        public void Writes_nothing()
        {
            foreach (var word in new[] { "INSERT", "UPDATE", "DELETE", "MERGE", "EXEC" })
                Assert.DoesNotContain(word, Sql, StringComparison.OrdinalIgnoreCase);
        }
    }
}
