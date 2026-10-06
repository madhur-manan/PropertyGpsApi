using PropertyGpsApi.Common;
using PropertyGpsApi.Models;
using PropertyGpsApi.Services;

namespace PropertyGpsApi.Tests;

/// <summary>
/// What the submit hands the procedures, and the check of road row ids against the
/// application, without a database: VerificationSubmitService builds the parameters of
/// USP_IU_BtoA_MainApp_Officer and USP_IU_BtoA_SiteRoadDetails_Officer in two internal
/// functions, and CheckRoadRows takes the application's road rows as a list.
///
/// The SQL around them (healing Ofcr_App_Id, the "held" check, superseding earlier rows,
/// the private-road documents) is replayed against UDD_KHATABTOA_TEST by
/// db/27_udd_Check_RoadScenarios.sql.
/// </summary>
public sealed class RoadWriteParameterTests
{
    private static readonly IReadOnlyDictionary<string, string> NoMedia = new Dictionary<string, string>();

    private static SubmitVerificationRequest Request(SubmitSiteDetails site) => new()
    {
        ApplicationId = "TESTAPP0001",
        Epid = "9990000001",
        WardId = 54,
        PropertyLandExists = 1,
        IsAllBhoomiSurveyNosCorrect = 1,
        SiteDetails = site,
    };

    private static SubmitRoadDetail Road(int? status, int? correct, int? rowId = 91001) => new()
    {
        RoadRowId = rowId, RoadId = "976", RoadName = "Bile Shivale", RoadType = "public",
        IsPresentInPublicRoadList = 1, RoadStatus = status, IsRoadDetailsCorrect = correct,
    };

    // ---- D33: the application row carries the real App_Id --------------------------------

    /// <summary>
    /// @Ofcr_App_Id used to be 0, "the procedure resolves this itself". It does not: it
    /// stores the value and keys its UPDATE on it, so every API row had App_Id 0 and a
    /// resubmission for an application with a legacy officer row updated nothing.
    /// </summary>
    [Fact]
    public void D33_the_application_parameters_carry_the_resolved_app_id()
    {
        var p = VerificationSubmitService.ApplicationParameters(
            Request(new SubmitSiteDetails { RoadDetails = [Road(0, 1)] }), new SubmitSiteDetails { RoadDetails = [Road(0, 1)] },
            appId: 8313, officerId: 9900001, roleId: 116);

        Assert.Equal(8313, p.Get<int>("@Ofcr_App_Id"));
        Assert.Equal("TESTAPP0001", p.Get<string>("@Ofcr_ApplicationDisplayId"));
    }

    // ---- D9: Ofcr_CorrectedRoadDetails ---------------------------------------------------

    public static TheoryData<int?, int?[], bool> CorrectedCases => new()
    {
        // The officer's own answer wins.
        { 0, [0], true },
        { 1, [1, 3], false },
        // No answer (every install before the app fix): derived from the roads.
        { null, [0, 0], false },
        { null, [0, 1], true },
        { null, [0, 2], true },
        { null, [0, 3], true },
    };

    /// <summary>
    /// The app sends siteDetails.isRoadDetailsCorrect null, and AsBit(null is 0) stored
    /// "not corrected" on every survey, including ones whose roads were changed.
    /// </summary>
    [Theory]
    [MemberData(nameof(CorrectedCases))]
    public void D9_corrected_road_details_follows_the_answer_or_the_roads(int? answer, int?[] statuses, bool expected)
    {
        var site = new SubmitSiteDetails
        {
            IsRoadDetailsCorrect = answer,
            RoadDetails = statuses.Select((s, i) => Road(s, s is 0 ? 1 : 0, 91001 + i)).ToList(),
        };

        Assert.Equal(expected, VerificationSubmitService.RoadDetailsCorrected(site));
        Assert.Equal(expected, VerificationSubmitService.ApplicationParameters(Request(site), site, 1, 1, 116)
            .Get<bool>("@Ofcr_CorrectedRoadDetails"));
    }

    [Theory]
    [InlineData(0, 1, false)]
    [InlineData(1, 0, true)]
    [InlineData(1, null, true)]    // corrected road, answer missing
    [InlineData(2, null, true)]    // not found: the app sends no answer
    [InlineData(3, null, true)]    // added
    [InlineData(0, null, false)]
    [InlineData(3, 1, false)]      // an explicit answer is kept as given
    public void Each_road_corrected_flag_follows_its_answer_or_its_status(int status, int? correct, bool expected) =>
        Assert.Equal(expected, VerificationSubmitService.RoadParameters("TESTAPP0001", Road(status, correct), NoMedia, 1, 116)
            .Get<bool>("@BtoA_Corrected_Road_Details"));

    // ---- road parameters ------------------------------------------------------------------

    [Fact]
    public void Road_parameters_carry_the_payload_values()
    {
        var media = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["road_A.jpg"] = "https://example.test/x/9990000001-91001-private-a.jpg",
            ["public_A.jpg"] = "https://example.test/x/9990000001-91001-public-a.jpg",
        };
        var road = new SubmitRoadDetail
        {
            RoadRowId = 91001, RoadId = " 637 ", RoadName = "Green woods layout", ActualRoadName = "Test Lane 4",
            RoadType = "private", IsPresentInPublicRoadList = 0, RoadStatus = 1, IsRoadDetailsCorrect = 0,
            PrivateRoadLat = 12.9101, PrivateRoadLng = 77.5601, NearPublicRoadLat = 12.9105, NearPublicRoadLng = 77.5609,
            PrivateRoadImage = "field_photos/road_A.jpg", PublicRoadImage = "public_A.jpg", NoticeImage = "notice_A.jpg",
        };

        var p = VerificationSubmitService.RoadParameters("TESTAPP0001", road, media, 9900001, 116);

        Assert.Equal(637, p.Get<int>("@BtoA_RoadId"));
        Assert.Equal("Green woods layout", p.Get<string>("@BtoA_RoadName"));
        Assert.Equal("Test Lane 4", p.Get<string>("@BtoA_ActualRoadName"));
        Assert.Equal("updated", p.Get<string>("@BtoA_Correction_Type_Value"));
        Assert.Equal(1, p.Get<int>("@BtoA_Correction_Type_Id"));
        Assert.False(p.Get<bool>("@BtoA_IsPresentInPublicRoadList"));
        Assert.Equal("91001", p.Get<string>("@BtoA_SiteRoadRowID"));
        Assert.Equal(media["road_A.jpg"], p.Get<string>("@BtoA_Nearest_Private_Road_Document"));   // A2
        Assert.Equal(media["public_A.jpg"], p.Get<string>("@BtoA_Nearest_Public_Road_Document"));
        Assert.Equal(9900001L, p.Get<long>("@CBy"));
    }

    [Fact]
    public void An_added_road_has_no_site_road_row_id() =>
        Assert.Null(VerificationSubmitService.RoadParameters("TESTAPP0001", Road(3, null, rowId: null), NoMedia, 1, 116)
            .Get<string?>("@BtoA_SiteRoadRowID"));

    // ---- N3: road row ids belong to the application -----------------------------------------

    private static DeclaredRoadRow Declared(int rowId, bool active = true, string? roadId = "976", string? name = "Bile Shivale") =>
        new() { RowId = rowId, Active = active, RoadId = roadId, RoadName = name };

    private static readonly DeclaredRoadRow[] App =
    [
        Declared(91001),
        Declared(91002),
        Declared(91003, active: false),
        Declared(91004, roadId: "0", name: null),   // a blank row: never shown as a card
    ];

    private static SubmitRoadDetail Answer(int? rowId, int status = 0) => Road(status, status is 0 ? 1 : null, rowId);

    [Fact]
    public void N3_answering_every_declared_road_passes_with_no_warning()
    {
        var (errors, warnings) = VerificationSubmitService.CheckRoadRows([Answer(91001), Answer(91002), Answer(null, 3)], App);
        Assert.Empty(errors);
        Assert.Empty(warnings);
    }

    /// <summary>
    /// Another application's road row, a Site_Id sent as the row id (D25: the DB stage stored
    /// 12066), or a row a positional restore made up (D6) used to be filed as given.
    /// </summary>
    [Theory]
    [InlineData(12066)]
    [InlineData(26491)]
    public void N3_a_row_id_that_is_not_this_applications_is_refused(int foreign)
    {
        var (errors, _) = VerificationSubmitService.CheckRoadRows([Answer(91001), Answer(foreign)], App);

        var error = Assert.Single(errors);
        Assert.Equal("siteDetails.roadDetails[1].roadRowId", error.Field);
        Assert.Equal(ApiErrorCodes.UnknownRoadRow, error.Code);
        Assert.Equal("Road 2 is not one of this application's declared roads. Fetch the property again and redo this road.",
            error.Message);
    }

    /// <summary>An inactive or blank row is still the application's own; an old-fetch payload may name it.</summary>
    [Fact]
    public void N3_inactive_and_blank_rows_of_the_application_are_accepted()
    {
        var (errors, _) = VerificationSubmitService.CheckRoadRows(
            [Answer(91001), Answer(91002), Answer(91003, status: 2), Answer(91004, status: 2)], App);
        Assert.Empty(errors);
    }

    /// <summary>
    /// A declared road with no entry is reported, not refused: an added road may stand in
    /// for it in the count. Inactive and blank rows are never cards, so never missing.
    /// </summary>
    [Fact]
    public void N3_an_active_declared_road_with_no_entry_is_a_warning()
    {
        var (errors, warnings) = VerificationSubmitService.CheckRoadRows([Answer(91001), Answer(null, 3)], App);

        Assert.Empty(errors);
        var warning = Assert.Single(warnings);
        Assert.Equal("siteDetails.roadDetails", warning.Field);
        Assert.Equal(ApiErrorCodes.DeclaredRoadNotAnswered, warning.Code);
        Assert.Equal("Declared road row 91002 has no answer in this survey.", warning.Message);
    }

    [Fact]
    public void N3_a_road_marked_not_found_counts_as_an_answer()
    {
        var (_, warnings) = VerificationSubmitService.CheckRoadRows([Answer(91001), Answer(91002, status: 2), Answer(null, 3)], App);
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData(null, null, null, null, null, true)]
    [InlineData("0", " ", null, null, null, true)]
    [InlineData("976", null, null, null, null, false)]
    [InlineData("0", null, "Test 3rd Cross", null, null, false)]
    [InlineData("0", null, null, "Test Lane", null, false)]
    [InlineData("0", null, null, null, "Bile Shivale", false)]
    public void A_declared_row_is_blank_by_the_fetch_rule(
        string? roadId, string? name, string? entered, string? privateName, string? privateText, bool blank) =>
        Assert.Equal(blank, new DeclaredRoadRow
        {
            RowId = 1, Active = true, RoadId = roadId, RoadName = name, EnteredRoadName = entered,
            PrivateRoadName = privateName, PrivateRoadText = privateText,
        }.IsBlank);
}
