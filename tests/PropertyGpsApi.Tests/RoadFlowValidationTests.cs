using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Common;
using PropertyGpsApi.Infrastructure.Options;
using PropertyGpsApi.Models;
using PropertyGpsApi.Services;

namespace PropertyGpsApi.Tests;

/// <summary>
/// Road details, submit side: what VerificationSubmitService.Validate accepts and refuses
/// for every road scenario, with the exact field, code and sentence the officer sees.
///
/// The app joins these messages into the parked record's reason (api_client.dart
/// _messageFrom), so a changed sentence is a changed screen, and a changed field breaks
/// anything that maps the error back to a card. Validate runs before any file is stored or
/// any SQL is sent, so every refusal here also means "nothing was written".
///
/// Invented fixture: TESTAPP0001 / EPID 9990000001, ward 54, row ids 91xxx. Scenario ids
/// refer to the road-flow scenario matrix of 2026-10-05.
/// </summary>
public sealed class RoadFlowValidationTests
{
    private const int RevenueInspector = 116;
    private const int Ward = 54;

    private const string Required = "This answer is needed before the survey can be submitted.";

    // Validate() reads nothing from the database, so no connection is needed.
    private static readonly VerificationSubmitService Service = new(
        null!, Options.Create(new StoredProcedureOptions()), NullLogger<VerificationSubmitService>.Instance);

    // P1 = road front, P2 = nearest public road.
    private const double P1Lat = 12.9101, P1Lng = 77.5601, P2Lat = 12.9105, P2Lng = 77.5609;

    private static SubmitRoadDetail Declared(
        int? rowId, string? roadId, string? roadName, string type = "public", int? inList = 1,
        int? status = 0, int? correct = 1, string? actual = null, bool caps = false) => new()
    {
        RoadRowId = rowId,
        RoadId = roadId,
        RoadName = roadName,
        ActualRoadName = actual ?? roadName,
        RoadType = type,
        IsPresentInPublicRoadList = inList,
        RoadStatus = status,
        IsRoadDetailsCorrect = correct,
        PrivateRoadLat = caps ? P1Lat : null,
        PrivateRoadLng = caps ? P1Lng : null,
        NearPublicRoadLat = caps ? P2Lat : null,
        NearPublicRoadLng = caps ? P2Lng : null,
        PrivateRoadImage = caps ? "road_A.jpg" : null,
        PublicRoadImage = caps ? "public_road_A.jpg" : null,
        NoticeImage = caps ? "notice_A.jpg" : null,
    };

    /// <summary>An officer-added road: no row id, no citizen name, status 3, isRoadDetailsCorrect 0.</summary>
    private static SubmitRoadDetail Added(string roadId, string actual, string type = "public", int inList = 1,
        bool caps = false) =>
        Declared(null, roadId, null, type, inList, status: 3, correct: 0, actual: actual, caps: caps);

    private static SubmitVerificationRequest Survey(int? corner, int? sides, params SubmitRoadDetail[] roads) =>
        Survey(corner, sides, roads, applicationId: "TESTAPP0001");

    private static SubmitVerificationRequest Survey(
        int? corner, int? sides, IReadOnlyList<SubmitRoadDetail> roads, string applicationId,
        int? declaredCorrect = 1) => new()
    {
        ApplicationId = applicationId,
        Epid = "9990000001",
        WardId = Ward,
        PropertyLandExists = 1,
        IsAllBhoomiSurveyNosCorrect = 1,
        SiteDetails = new SubmitSiteDetails
        {
            IsCornerPlot = corner,
            RoadFacingSides = sides,
            IsDeclaredRoadFacingSidesCorrect = declaredCorrect,
            IsRoadDetailsCorrect = null,   // the app always sends null here
            RoadDetails = roads,
        },
    };

    private static IReadOnlyList<ApiError> ErrorsFor(SubmitVerificationRequest request)
    {
        try
        {
            Service.Validate(request, RevenueInspector, Ward);
            return [];
        }
        catch (SubmitRejectedException e)
        {
            // Every refusal from Validate is VALIDATION_FAILED; the controller turns it
            // into 422 retryable=false, which parks the record on the device.
            Assert.Equal(ApiErrorCodes.ValidationFailed, e.Code);
            Assert.Equal("Some answers were not accepted. Nothing was saved.", e.Summary);
            return e.Errors;
        }
    }

    private static void AssertAccepted(SubmitVerificationRequest request)
    {
        var errors = ErrorsFor(request);
        Assert.True(errors.Count == 0,
            "Expected the survey to be accepted, got: " + string.Join("; ", errors.Select(e => $"{e.Field} {e.Code} {e.Message}")));
    }

    private static void AssertOnly(SubmitVerificationRequest request, string field, string code, string message)
    {
        var error = Assert.Single(ErrorsFor(request));
        Assert.Equal(field, error.Field);
        Assert.Equal(code, error.Code);
        Assert.Equal(message, error.Message);
    }

    // ---- accepted scenarios --------------------------------------------------------------

    [Fact]
    public void S1_declared_public_road_confirmed_is_accepted() =>
        AssertAccepted(Survey(0, 1, Declared(91001, "976", "Bile Shivale")));

    [Fact]
    public void S2_declared_private_road_confirmed_with_captures_is_accepted() =>
        AssertAccepted(Survey(0, 1,
            Declared(91011, "9320", "Byrthi", "private", 0, actual: "Test 3rd Cross", caps: true)));

    [Fact]
    public void S3_one_of_two_declared_roads_marked_not_found_is_accepted() =>
        AssertAccepted(Survey(0, 1,
            Declared(91021, "976", "Bile Shivale"),
            Declared(91022, "637", "Green woods layout", status: 2, correct: null)));

    [Fact]
    public void S4_details_wrong_corrected_to_a_picked_public_road_is_accepted() =>
        AssertAccepted(Survey(0, 1,
            Declared(91031, "279", "Bile Shivale", status: 1, correct: 0, actual: "Green woods layout")));

    [Fact]
    public void S5_details_wrong_corrected_to_a_typed_private_road_is_accepted() =>
        AssertAccepted(Survey(0, 1,
            Declared(91041, "999", "Bile Shivale", "private", 0, status: 1, correct: 0, actual: "Test Lane 4", caps: true)));

    [Fact]
    public void S6_officer_added_public_road_on_a_two_road_corner_plot_is_accepted() =>
        AssertAccepted(Survey(1, 2,
            Declared(91051, "976", "Bile Shivale"),
            Added("279", "Green woods layout")));

    [Fact]
    public void S7_officer_added_private_road_is_accepted() =>
        AssertAccepted(Survey(1, 2,
            Declared(91051, "976", "Bile Shivale"),
            Added("999", "Test Service Lane", "private", 0, caps: true)));

    [Fact]
    public void S8_two_declared_private_roads_on_one_master_road_are_not_a_duplicate() =>
        AssertAccepted(Survey(1, 2,
            Declared(91081, "9320", "Byrthi", "private", 0, actual: "Test 3rd Cross", caps: true),
            Declared(91082, "9320", "Byrthi", "private", 0, actual: "Test 1st Main", caps: true)));

    [Fact]
    public void S9a_count_corrected_down_with_the_extra_road_marked_not_found_is_accepted() =>
        AssertAccepted(Survey(0, 1, [
            Declared(91091, "976", "Bile Shivale"),
            Declared(91092, "637", "Green woods layout", status: 2, correct: null)],
            "TESTAPP0001", declaredCorrect: 0));

    [Fact]
    public void S9b_five_roads_is_the_maximum_and_is_accepted() =>
        AssertAccepted(Survey(1, 5, [
            Declared(91091, "976", "Bile Shivale"),
            Declared(91092, "637", "Green woods layout"),
            Added("279", "Green woods layout"),
            Added("473", "Motappa garden Phase 1 and 2"),
            Added("92", "Anugraha Layout")],
            "TESTAPP0001", declaredCorrect: 0));

    [Fact]
    public void S10b_two_declared_roads_confirmed_on_the_same_public_road_are_accepted() =>
        AssertAccepted(Survey(1, 2,
            Declared(91102, "976", "Bile Shivale"),
            Declared(91103, "976", "Bile Shivale")));

    /// <summary>The server has no typed-name rule; two typed private roads differ only by name.</summary>
    [Fact]
    public void S10c_two_typed_private_roads_are_accepted_by_the_server() =>
        AssertAccepted(Survey(1, 2,
            Added("999", "Test Lane", "private", 0, caps: true),
            Added("999", " test lane ", "private", 0, caps: true)));

    [Fact]
    public void S11_a_plot_with_no_declared_road_and_one_added_road_is_accepted() =>
        AssertAccepted(Survey(0, 1, Added("279", "Green woods layout")));

    /// <summary>Unrecognised roads are a KSRSAC warning after the write, never a validation failure.</summary>
    [Fact]
    public void S13_a_road_the_master_does_not_know_is_not_refused_by_validation() =>
        AssertAccepted(Survey(0, 1, Declared(91131, "123456", "Test Unknown Road")));

    [Fact]
    public void S14_a_declared_public_row_carrying_the_999_sentinel_is_accepted() =>
        AssertAccepted(Survey(0, 1, Declared(91141, "999", "Test Unlisted Road", "public", 0, caps: true)));

    [Fact]
    public void S21_not_found_replaced_by_an_added_road_on_a_two_road_corner_plot_is_accepted() =>
        AssertAccepted(Survey(1, 2, [
            Declared(91211, "976", "Bile Shivale"),
            Declared(91212, "637", "Green woods layout", status: 2, correct: null),
            Added("473", "Motappa garden Phase 1 and 2")],
            "TESTAPP0001", declaredCorrect: 0));

    [Fact]
    public void Without_a_road_facing_count_the_live_roads_are_the_count() =>
        AssertAccepted(Survey(1, null,
            Declared(91081, "976", "Bile Shivale"),
            Declared(91082, "637", "Green woods layout")));

    // ---- road count and corner-plot answer ----------------------------------------------

    [Fact]
    public void S3_two_live_roads_on_a_one_road_plot_is_refused_with_the_count_message() =>
        AssertOnly(Survey(0, 1,
                Declared(91021, "976", "Bile Shivale"),
                Declared(91022, "637", "Green woods layout")),
            "siteDetails.roadDetails", "ROAD_COUNT_MISMATCH",
            "The plot faces 1 road, but 2 were submitted. Remove the extra road or correct the number of roads.");

    [Fact]
    public void S3_every_road_marked_not_found_is_refused_and_nothing_else_is_reported() =>
        AssertOnly(Survey(0, 1, Declared(91021, "976", "Bile Shivale", status: 2, correct: null)),
            "siteDetails.roadDetails", "REQUIRED",
            "Every road was marked not found. Record the road the plot actually faces.");

    [Fact]
    public void S9a_a_corner_plot_with_one_live_road_reports_both_count_and_corner_errors()
    {
        var errors = ErrorsFor(Survey(1, 1,
            Declared(91091, "976", "Bile Shivale"),
            Declared(91092, "637", "Green woods layout")));

        Assert.Collection(errors,
            e =>
            {
                Assert.Equal("siteDetails.roadDetails", e.Field);
                Assert.Equal("ROAD_COUNT_MISMATCH", e.Code);
                Assert.Equal("The plot faces 1 road, but 2 were submitted. Remove the extra road or correct the number of roads.", e.Message);
            },
            e =>
            {
                Assert.Equal("siteDetails.isCornerPlot", e.Field);
                Assert.Equal("CORNER_PLOT_MISMATCH", e.Code);
                Assert.Equal("A corner plot faces at least two roads, but only one was given.", e.Message);
            });
    }

    [Fact]
    public void Fewer_live_roads_than_the_count_uses_was_for_one() =>
        AssertOnly(Survey(1, 2, Declared(91091, "976", "Bile Shivale"), Declared(91092, "637", "Green woods layout", status: 2)),
            "siteDetails.roadDetails", "ROAD_COUNT_MISMATCH",
            "The plot faces 2 roads, but 1 was submitted. Remove the extra road or correct the number of roads.");

    [Fact]
    public void Not_a_corner_plot_with_two_roads_and_no_count_is_a_corner_mismatch() =>
        AssertOnly(Survey(0, null,
                Declared(91081, "976", "Bile Shivale"),
                Declared(91082, "637", "Green woods layout")),
            "siteDetails.isCornerPlot", "CORNER_PLOT_MISMATCH",
            "A plot that is not a corner plot faces one road, but 2 were given.");

    [Fact]
    public void An_unanswered_corner_plot_question_is_required_and_skips_the_corner_rule() =>
        AssertOnly(Survey(null, 2,
                Declared(91081, "976", "Bile Shivale"),
                Declared(91082, "637", "Green woods layout")),
            "siteDetails.isCornerPlot", "REQUIRED", Required);

    [Fact]
    public void S9c_six_live_roads_is_refused_as_too_many() =>
        AssertOnly(Survey(1, 6, SixAddedRoads()),
            "siteDetails.roadDetails", "TOO_MANY_ROADS",
            "A plot can face at most 5 roads, but 6 were submitted.");

    [Fact]
    public void S9c_six_live_roads_against_a_count_of_five_reports_both()
    {
        var errors = ErrorsFor(Survey(1, 5, SixAddedRoads()));

        Assert.Equal(["TOO_MANY_ROADS", "ROAD_COUNT_MISMATCH"], errors.Select(e => e.Code));
        Assert.Equal("The plot faces 5 roads, but 6 were submitted. Remove the extra road or correct the number of roads.",
            errors[1].Message);
    }

    [Fact]
    public void Max_roads_is_five_on_both_sides_of_the_wire() =>
        Assert.Equal(5, VerificationSubmitService.MaxRoads);

    /// <summary>
    /// A deleted road is not a side even when there are more than five entries in total:
    /// five live roads plus a not-found declared road is a valid five-road plot.
    /// </summary>
    [Fact]
    public void Not_found_roads_do_not_count_towards_the_maximum() =>
        AssertAccepted(Survey(1, 5, [
            Declared(91091, "976", "Bile Shivale", status: 2, correct: null),
            .. SixAddedRoads().Take(5)],
            "TESTAPP0001", declaredCorrect: 0));

    private static SubmitRoadDetail[] SixAddedRoads() =>
        Enumerable.Range(0, 6).Select(i => Added("999", $"Test Lane {i + 1}", "private", 0, caps: true)).ToArray();

    // ---- duplicates ---------------------------------------------------------------------

    [Fact]
    public void S10a_an_added_road_picking_the_declared_public_road_is_a_duplicate() =>
        AssertOnly(Survey(1, 2,
                Declared(91101, "279", "Green woods layout"),
                Added("279", "Green woods layout")),
            "siteDetails.roadDetails[1].roadId", "DUPLICATE_ROAD",
            "Roads 1 and 2 are the same road (Green woods layout). Each road facing the plot should appear once.");

    [Fact]
    public void A_public_road_id_differing_only_by_whitespace_is_still_a_duplicate() =>
        Assert.Contains("DUPLICATE_ROAD", ErrorsFor(Survey(1, 2,
            Declared(91101, " 279 ", "Green woods layout"),
            Added("279", "Green woods layout"))).Select(e => e.Code));

    [Fact]
    public void Two_added_public_roads_picking_the_same_street_are_a_duplicate() =>
        AssertOnly(Survey(1, 2,
                Added("473", "Motappa garden Phase 1 and 2"),
                Added("473", "Motappa garden Phase 1 and 2")),
            "siteDetails.roadDetails[1].roadId", "DUPLICATE_ROAD",
            "Roads 1 and 2 are the same road (Motappa garden Phase 1 and 2). Each road facing the plot should appear once.");

    /// <summary>
    /// The public-id rule only looks at roads on the public list: two private roads near the
    /// same public road share an id and are still two roads.
    /// </summary>
    [Fact]
    public void Two_added_private_roads_sharing_a_road_id_are_not_a_duplicate() =>
        AssertAccepted(Survey(1, 2,
            Added("473", "Test Lane A", "private", 0, caps: true),
            Added("473", "Test Lane B", "private", 0, caps: true)));

    [Fact]
    public void A_deleted_road_sharing_an_id_with_an_added_road_is_not_a_duplicate() =>
        AssertAccepted(Survey(0, 1,
            Declared(91101, "279", "Green woods layout", status: 2, correct: null),
            Added("279", "Green woods layout")));

    [Fact]
    public void The_999_sentinel_on_the_public_list_is_never_a_duplicate() =>
        Assert.DoesNotContain("DUPLICATE_ROAD", ErrorsFor(Survey(1, 2,
            Added("999", "Test Unlisted A", "public", 1),
            Added("999", "Test Unlisted B", "public", 1))).Select(e => e.Code));

    [Fact]
    public void S10d_two_entries_for_one_declared_road_row_are_refused() =>
        AssertOnly(Survey(1, 2,
                Declared(91102, "976", "Bile Shivale"),
                Declared(91102, "976", "Bile Shivale")),
            "siteDetails.roadDetails[1].roadRowId", "DUPLICATE_ROAD",
            "Roads 1 and 2 are the same declared road.");

    /// <summary>
    /// S15 variant: a stored entry without a roadRowId is sent with the site id instead
    /// (single_site_mapper.dart _road). Two of them collide here rather than being filed as
    /// two roads under one bogus row id.
    /// </summary>
    [Fact]
    public void S15_two_roads_sent_under_the_site_id_are_refused_as_one_road_row() =>
        Assert.Contains(ErrorsFor(Survey(1, 2,
                Declared(800001, "976", "Bile Shivale"),
                Declared(800001, "637", "Green woods layout"))),
            e => e.Code == "DUPLICATE_ROAD" && e.Field == "siteDetails.roadDetails[1].roadRowId");

    // ---- S17(d): a restored not-found answer sent as an added road -------------------------

    /// <summary>
    /// The server counts status 3 as live. A saved "not found" answer restored onto a new
    /// officer-added card (app defect D6) arrives as a third live road on a two-road plot.
    /// </summary>
    [Fact]
    public void S17d_an_empty_added_road_counts_as_live_and_breaks_the_count() =>
        AssertOnly(Survey(1, 2,
                Declared(91081, "9320", "Byrthi", "private", 0, actual: "Test 3rd Cross", caps: true),
                Declared(91082, "9320", "Byrthi", "private", 0, actual: "Test 1st Main", caps: true),
                new SubmitRoadDetail { RoadStatus = 3, IsPresentInPublicRoadList = 0 }),
            "siteDetails.roadDetails", "ROAD_COUNT_MISMATCH",
            "The plot faces 2 roads, but 3 were submitted. Remove the extra road or correct the number of roads.");

    // ---- S11 / S20: required fields and contract backstops ------------------------------

    [Fact]
    public void S11_no_roads_at_all_is_refused() =>
        AssertOnly(Survey(0, 1), "siteDetails.roadDetails", "REQUIRED", "At least one road is required.");

    [Fact]
    public void S20_a_road_without_a_status_is_refused_by_index() =>
        AssertOnly(Survey(1, 2,
                Declared(91081, "976", "Bile Shivale"),
                Declared(91082, "637", "Green woods layout", status: null)),
            "siteDetails.roadDetails[1].roadStatus", "REQUIRED", Required);

    [Fact]
    public void S20_a_road_without_the_public_list_flag_is_refused_by_index() =>
        AssertOnly(Survey(0, 1, Declared(91001, "976", "Bile Shivale", inList: null)),
            "siteDetails.roadDetails[0].isPresentInPublicRoadList", "REQUIRED", Required);

    [Theory]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(-1)]
    public void S20_an_unknown_road_status_is_refused(int status) =>
        AssertOnly(Survey(0, 1, Declared(91001, "976", "Bile Shivale", status: status)),
            "siteDetails.roadDetails[0].roadStatus", "INVALID", $"Road 1 has an unknown status ({status}).");

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void Every_known_live_status_is_accepted(int status) =>
        AssertAccepted(Survey(0, 1, Declared(status == 3 ? null : 91001, "976", "Bile Shivale", status: status)));

    [Fact]
    public void S20_a_road_front_point_at_zero_zero_is_refused() =>
        AssertOnly(Survey(0, 1, Point(91011, privateLat: 0, privateLng: 0)),
            "siteDetails.roadDetails[0].privateRoadLat", "NO_GPS_FIX",
            "The device recorded no GPS fix for this point. Please capture it again.");

    [Fact]
    public void S20_a_nearest_public_road_point_at_zero_zero_is_refused() =>
        AssertOnly(Survey(0, 1, Point(91011, nearLat: 0.00001, nearLng: -0.00001)),
            "siteDetails.roadDetails[0].nearPublicRoadLat", "NO_GPS_FIX",
            "The device recorded no GPS fix for this point. Please capture it again.");

    [Fact]
    public void A_point_with_only_one_coordinate_at_zero_is_not_null_island() =>
        AssertAccepted(Survey(0, 1, Point(91011, privateLat: 0, privateLng: P1Lng)));

    private static SubmitRoadDetail Point(int rowId, double privateLat = P1Lat, double privateLng = P1Lng,
        double nearLat = P2Lat, double nearLng = P2Lng)
    {
        var road = Declared(rowId, "9320", "Byrthi", "private", 0, actual: "Test 3rd Cross", caps: true);
        return new SubmitRoadDetail
        {
            RoadRowId = road.RoadRowId, RoadId = road.RoadId, RoadName = road.RoadName,
            ActualRoadName = road.ActualRoadName, RoadType = road.RoadType,
            IsPresentInPublicRoadList = road.IsPresentInPublicRoadList, RoadStatus = road.RoadStatus,
            IsRoadDetailsCorrect = road.IsRoadDetailsCorrect,
            PrivateRoadLat = privateLat, PrivateRoadLng = privateLng,
            NearPublicRoadLat = nearLat, NearPublicRoadLng = nearLng,
            PrivateRoadImage = road.PrivateRoadImage, PublicRoadImage = road.PublicRoadImage,
            NoticeImage = road.NoticeImage,
        };
    }

    [Fact]
    public void S20_missing_site_details_is_refused_together_with_earlier_errors()
    {
        var errors = ErrorsFor(new SubmitVerificationRequest
        {
            ApplicationId = "",
            Epid = "9990000001",
            WardId = Ward,
            PropertyLandExists = 1,
            IsAllBhoomiSurveyNosCorrect = 1,
            SiteDetails = null!,
        });

        Assert.Equal(["applicationId", "siteDetails"], errors.Select(e => e.Field));
        Assert.Equal("The site and road answers are missing from this survey.", errors[1].Message);
    }

    [Fact]
    public void S20_null_road_details_is_refused_as_no_roads() =>
        AssertOnly(new SubmitVerificationRequest
            {
                ApplicationId = "TESTAPP0001",
                Epid = "9990000001",
                WardId = Ward,
                PropertyLandExists = 1,
                IsAllBhoomiSurveyNosCorrect = 1,
                SiteDetails = new SubmitSiteDetails { IsCornerPlot = 0, RoadFacingSides = 1, RoadDetails = null! },
            },
            "siteDetails.roadDetails", "REQUIRED", "At least one road is required.");

    /// <summary>All problems are reported at once, so the officer fixes them in one pass.</summary>
    [Fact]
    public void Every_road_problem_is_reported_in_one_refusal()
    {
        var errors = ErrorsFor(Survey(1, 2,
            Declared(91081, "976", "Bile Shivale", status: null),
            Point(91082, nearLat: 0, nearLng: 0)));

        Assert.Equal(
            ["siteDetails.roadDetails[0].roadStatus", "siteDetails.roadDetails[1].nearPublicRoadLat"],
            errors.Select(e => e.Field));
    }

    // ---- jurisdiction comes first -------------------------------------------------------

    [Fact]
    public void A_ward_officer_submitting_for_another_ward_is_forbidden_before_any_road_rule()
    {
        var ex = Assert.Throws<ApiException>(() =>
            Service.Validate(Survey(0, 1), RevenueInspector, officerWardId: Ward + 1));

        Assert.Equal(ApiErrorCodes.OutsideJurisdiction, ex.Code);
    }
}
