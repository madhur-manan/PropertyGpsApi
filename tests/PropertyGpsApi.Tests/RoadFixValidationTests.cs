using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Common;
using PropertyGpsApi.Infrastructure.Options;
using PropertyGpsApi.Models;
using PropertyGpsApi.Services;

namespace PropertyGpsApi.Tests;

/// <summary>
/// The road refusals added by the 2026-10-06 road-flow fixes (defects A1, A3, A4, A6, A7,
/// D15, D24). Each test is a payload the API used to accept - or, for A1, answer with a
/// retryable 500 - and now refuses with one exact field, code and sentence.
///
/// Invented fixture: TESTAPP0001 / EPID 9990000001, ward 54, row ids 91xxx; road names are
/// KSRSAC roads of ward 54 so the shapes read like real data.
/// </summary>
public sealed class RoadFixValidationTests
{
    private const int RevenueInspector = 116;
    private const int Ward = 54;

    private static readonly VerificationSubmitService Service = new(
        null!, Options.Create(new StoredProcedureOptions()), NullLogger<VerificationSubmitService>.Instance);

    private const double P1Lat = 12.9101, P1Lng = 77.5601, P2Lat = 12.9105, P2Lng = 77.5609;

    private static SubmitRoadDetail Public(int? rowId = 91001, string? roadId = "976", int? status = 0) => new()
    {
        RoadRowId = rowId,
        RoadId = roadId,
        RoadName = "Bile Shivale",
        ActualRoadName = "Bile Shivale",
        RoadType = "public",
        IsPresentInPublicRoadList = 1,
        RoadStatus = status,
        IsRoadDetailsCorrect = status is 0 ? 1 : 0,
    };

    /// <summary>A private road with all of its evidence; tests take pieces away.</summary>
    private static SubmitRoadDetail Private(int? rowId = 91001, string type = "private", int? status = 0) => new()
    {
        RoadRowId = rowId,
        RoadId = "976",
        RoadName = "Bile Shivale",
        ActualRoadName = "Test Lane",
        RoadType = type,
        IsPresentInPublicRoadList = 0,
        RoadStatus = status,
        IsRoadDetailsCorrect = 1,
        PrivateRoadLat = P1Lat,
        PrivateRoadLng = P1Lng,
        NearPublicRoadLat = P2Lat,
        NearPublicRoadLng = P2Lng,
        PrivateRoadImage = "road_A.jpg",
        PublicRoadImage = "public_road_A.jpg",
        NoticeImage = "notice_A.jpg",
    };

    private static SubmitVerificationRequest Survey(params SubmitRoadDetail?[] roads) =>
        Survey(new SubmitSiteDetails
        {
            IsCornerPlot = roads.Count(r => r?.RoadStatus != 2) > 1 ? 1 : 0,
            RoadFacingSides = roads.Count(r => r?.RoadStatus != 2),
            IsDeclaredRoadFacingSidesCorrect = 1,
            RoadDetails = roads!,
        });

    private static SubmitVerificationRequest Survey(SubmitSiteDetails site, double? nearLat = null, double? nearLng = null) => new()
    {
        ApplicationId = "TESTAPP0001",
        Epid = "9990000001",
        WardId = Ward,
        PropertyLandExists = 1,
        IsAllBhoomiSurveyNosCorrect = 1,
        NearestPublicRoadLat = nearLat,
        NearestPublicRoadLng = nearLng,
        SiteDetails = site,
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
            Assert.Equal(ApiErrorCodes.ValidationFailed, e.Code);
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
        var errors = ErrorsFor(request);
        var error = Assert.Single(errors);
        Assert.Equal(field, error.Field);
        Assert.Equal(code, error.Code);
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void The_baseline_fixtures_are_accepted()
    {
        AssertAccepted(Survey(Public()));
        AssertAccepted(Survey(Private()));
    }

    // ---- A1: a null road ----------------------------------------------------------------

    /// <summary>
    /// "roadDetails": [null] used to throw a NullReferenceException in Validate: a 500 with
    /// retryable=true, so the device resent the same survey on every sync, forever.
    /// </summary>
    [Fact]
    public void A1_a_null_road_is_refused_not_a_server_error() =>
        AssertOnly(Survey((SubmitRoadDetail?)null), "siteDetails.roadDetails[0]", "INVALID", "Road 1 is empty.");

    [Fact]
    public void A1_every_null_road_is_named()
    {
        var errors = ErrorsFor(Survey(Public(), null, Public(91002), null));
        Assert.Equal(["siteDetails.roadDetails[1]", "siteDetails.roadDetails[3]"], errors.Select(e => e.Field));
        Assert.Equal(["Road 2 is empty.", "Road 4 is empty."], errors.Select(e => e.Message));
    }

    // ---- A3: road id that is not a number -------------------------------------------------

    [Theory]
    [InlineData("ST1001")]
    [InlineData("97.6")]
    [InlineData("99999999999")]
    public void A3_a_road_id_that_is_not_an_int_is_refused(string roadId) =>
        AssertOnly(Survey(Public(roadId: roadId)), "siteDetails.roadDetails[0].roadId", "INVALID",
            $"Road 1 has a road id that is not a number ({roadId}).");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" 976 ")]
    [InlineData("999")]
    public void A3_a_numeric_or_absent_road_id_is_accepted(string? roadId) =>
        AssertAccepted(Survey(Public(roadId: roadId)));

    // ---- A4: 0/1 answers and coordinates ---------------------------------------------------

    private const string NotABit = "This answer must be 0 or 1, not 2.";

    [Fact]
    public void A4_a_corner_plot_answer_of_2_is_refused() =>
        AssertOnly(Survey(new SubmitSiteDetails
        {
            IsCornerPlot = 2, RoadFacingSides = 1, IsDeclaredRoadFacingSidesCorrect = 1, RoadDetails = [Public()],
        }), "siteDetails.isCornerPlot", "INVALID", NotABit);

    [Fact]
    public void A4_site_answers_outside_0_1_are_refused()
    {
        var errors = ErrorsFor(Survey(new SubmitSiteDetails
        {
            IsCornerPlot = 0, RoadFacingSides = 1, IsDeclaredRoadFacingSidesCorrect = 2, IsRoadDetailsCorrect = 7,
            RoadDetails = [Public()],
        }));
        Assert.Equal(["siteDetails.isRoadDetailsCorrect", "siteDetails.isDeclaredRoadFacingSidesCorrect"],
            errors.Select(e => e.Field));
        Assert.All(errors, e => Assert.Equal("INVALID", e.Code));
    }

    [Fact]
    public void A4_road_answers_outside_0_1_are_refused()
    {
        var road = Public();
        var errors = ErrorsFor(Survey(new SubmitRoadDetail
        {
            RoadRowId = road.RoadRowId, RoadId = road.RoadId, RoadName = road.RoadName, RoadType = "public",
            RoadStatus = 0, IsPresentInPublicRoadList = 2, IsRoadDetailsCorrect = 2,
        }));
        Assert.Equal(["siteDetails.roadDetails[0].isPresentInPublicRoadList", "siteDetails.roadDetails[0].isRoadDetailsCorrect"],
            errors.Select(e => e.Field));
        Assert.All(errors, e => Assert.Equal(NotABit, e.Message));
    }

    [Fact]
    public void A4_a_latitude_without_a_longitude_is_refused()
    {
        var road = Private();
        AssertOnly(Survey(new SubmitRoadDetail
            {
                RoadRowId = road.RoadRowId, RoadId = road.RoadId, RoadName = road.RoadName, ActualRoadName = road.ActualRoadName,
                RoadType = "public", IsPresentInPublicRoadList = 1, RoadStatus = 0, IsRoadDetailsCorrect = 1,
                NearPublicRoadLat = P2Lat,
            }),
            "siteDetails.roadDetails[0].nearPublicRoadLat", "INVALID",
            "A location needs both a latitude and a longitude. Please capture it again.");
    }

    [Theory]
    [InlineData(95, 77.5)]
    [InlineData(12.9, 400)]
    [InlineData(-91, 0.5)]
    [InlineData(double.NaN, 77.5)]
    public void A4_a_point_off_the_globe_is_refused(double lat, double lng) =>
        AssertOnly(Survey(new SubmitSiteDetails
            {
                IsCornerPlot = 0, RoadFacingSides = 1, IsDeclaredRoadFacingSidesCorrect = 1, RoadDetails = [Public()],
            }, nearLat: lat, nearLng: lng),
            "nearestPublicRoadLat", "INVALID",
            "This location is not a valid latitude and longitude. Please capture it again.");

    // ---- A6: road row id 0 or below ---------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A6_a_road_row_id_of_0_or_below_is_refused(int rowId) =>
        AssertOnly(Survey(Public(rowId)), "siteDetails.roadDetails[0].roadRowId", "INVALID",
            $"Road 1 has an invalid road row id ({rowId}).");

    // ---- A7: one declared road filed twice ---------------------------------------------------

    /// <summary>
    /// The row-id duplicate check used to look only at live roads, so a road marked not found
    /// and the same road kept were both written: two officer rows for one citizen road.
    /// </summary>
    [Fact]
    public void A7_one_road_row_marked_deleted_and_kept_is_refused() =>
        AssertOnly(Survey(Public(91001, "976", status: 2), Public(91001, "999", status: 1)),
            "siteDetails.roadDetails[1].roadRowId", "DUPLICATE_ROAD", "Roads 1 and 2 are the same declared road.");

    // ---- D15: evidence for a live private road ---------------------------------------------

    public static TheoryData<string, string> MissingEvidence => new()
    {
        { "privateRoadLat", "Road 1 is a private road and needs the location in front of the property." },
        { "privateRoadImage", "Road 1 is a private road and needs the photograph of the road in front of the property." },
        { "nearPublicRoadLat", "Road 1 is a private road and needs the location on the nearest public road." },
        { "publicRoadImage", "Road 1 is a private road and needs the photograph of the nearest public road." },
        { "noticeImage", "Road 1 is a private road and needs the photograph of the served notice." },
    };

    [Theory]
    [MemberData(nameof(MissingEvidence))]
    public void D15_a_private_road_without_each_piece_of_evidence_is_refused(string field, string message)
    {
        var r = Private();
        var road = new SubmitRoadDetail
        {
            RoadRowId = r.RoadRowId, RoadId = r.RoadId, RoadName = r.RoadName, ActualRoadName = r.ActualRoadName,
            RoadType = r.RoadType, IsPresentInPublicRoadList = 0, RoadStatus = 0, IsRoadDetailsCorrect = 1,
            // Both coordinates go together, so the "Lat" field stands for the pair.
            PrivateRoadLat = field == "privateRoadLat" ? null : P1Lat,
            PrivateRoadLng = field == "privateRoadLat" ? null : P1Lng,
            NearPublicRoadLat = field == "nearPublicRoadLat" ? null : P2Lat,
            NearPublicRoadLng = field == "nearPublicRoadLat" ? null : P2Lng,
            PrivateRoadImage = field == "privateRoadImage" ? null : r.PrivateRoadImage,
            PublicRoadImage = field == "publicRoadImage" ? " " : r.PublicRoadImage,
            NoticeImage = field == "noticeImage" ? null : r.NoticeImage,
        };

        AssertOnly(Survey(road), $"siteDetails.roadDetails[0].{field}", "REQUIRED", message);
    }

    [Fact]
    public void D15_a_private_road_with_no_evidence_at_all_names_all_five()
    {
        var errors = ErrorsFor(Survey(new SubmitRoadDetail
        {
            RoadRowId = 91001, RoadId = "0", RoadName = "Elus Road", RoadType = "Private",
            IsPresentInPublicRoadList = 1, RoadStatus = 0, IsRoadDetailsCorrect = 1,
        }));
        Assert.Equal(5, errors.Count(e => e.Code == "REQUIRED"));
    }

    [Theory]
    [InlineData("Private")]
    [InlineData(" PRIVATE ")]
    public void D15_the_road_type_matches_in_any_case(string type)
    {
        var r = Private(type: type);
        var errors = ErrorsFor(Survey(new SubmitRoadDetail
        {
            RoadRowId = r.RoadRowId, RoadId = r.RoadId, RoadName = r.RoadName, RoadType = type,
            IsPresentInPublicRoadList = 0, RoadStatus = 0, IsRoadDetailsCorrect = 1,
        }));
        Assert.Equal(5, errors.Count);
    }

    [Fact]
    public void D15_a_private_road_marked_not_found_needs_no_evidence() =>
        AssertAccepted(Survey(new SubmitRoadDetail
        {
            RoadRowId = 91002, RoadId = "976", RoadName = "Bile Shivale", RoadType = "private",
            IsPresentInPublicRoadList = 0, RoadStatus = 2,
        }, Public()));

    [Theory]
    [InlineData("public")]
    [InlineData("Tar")]
    [InlineData("")]
    [InlineData(null)]
    public void D15_a_road_that_is_not_private_needs_no_evidence(string? type) =>
        AssertAccepted(Survey(new SubmitRoadDetail
        {
            RoadRowId = 91001, RoadId = "976", RoadName = "Bile Shivale", RoadType = type,
            IsPresentInPublicRoadList = 1, RoadStatus = 0, IsRoadDetailsCorrect = 1,
        }));

    // ---- D24: lengths the procedure can store ------------------------------------------------

    [Theory]
    [InlineData("roadName")]
    [InlineData("actualRoadName")]
    public void D24_a_road_name_over_250_characters_is_refused(string field)
    {
        var name = new string('x', 251);
        var road = Public();
        var errors = ErrorsFor(Survey(new SubmitRoadDetail
        {
            RoadRowId = road.RoadRowId, RoadId = road.RoadId, RoadType = "public", IsPresentInPublicRoadList = 1,
            RoadStatus = 0, IsRoadDetailsCorrect = 1,
            RoadName = field == "roadName" ? name : road.RoadName,
            ActualRoadName = field == "actualRoadName" ? name : road.ActualRoadName,
        }));

        var error = Assert.Single(errors);
        Assert.Equal($"siteDetails.roadDetails[0].{field}", error.Field);
        Assert.Equal("TOO_LONG", error.Code);
        Assert.Equal("Road 1: this text is 251 characters; at most 250 can be stored.", error.Message);
    }

    [Fact]
    public void D24_a_road_name_of_exactly_250_characters_is_accepted() =>
        AssertAccepted(Survey(new SubmitRoadDetail
        {
            RoadRowId = 91001, RoadId = "999", RoadName = "Bile Shivale", ActualRoadName = new string('x', 250),
            RoadType = "public", IsPresentInPublicRoadList = 1, RoadStatus = 1, IsRoadDetailsCorrect = 0,
        }));

    [Fact]
    public void D24_a_road_type_over_50_characters_is_refused() =>
        AssertOnly(Survey(new SubmitRoadDetail
            {
                RoadRowId = 91001, RoadId = "976", RoadName = "Bile Shivale", RoadType = new string('t', 51),
                IsPresentInPublicRoadList = 1, RoadStatus = 0, IsRoadDetailsCorrect = 1,
            }),
            "siteDetails.roadDetails[0].roadType", "TOO_LONG",
            "Road 1: this text is 51 characters; at most 50 can be stored.");
}
