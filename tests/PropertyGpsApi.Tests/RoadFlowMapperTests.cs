using System.Text.Json;
using PropertyGpsApi.Models;
using PropertyGpsApi.Services;

namespace PropertyGpsApi.Tests;

/// <summary>
/// Road details, fetch side: how the rows of USP_S_GpsWardSync and USP_S_GetAppDetails
/// become siteDetails.roads, siteDetails.roadRowIds and the single-road legacy fields.
///
/// Every value here is invented (App 900001, Site 800001, row ids 91xxx). The road names
/// and ids are KSRSAC master roads of ward 54 so the shapes read like real data, but no
/// owner, officer or application in these tests exists.
///
/// Scenario ids (S1, S8, ...) refer to the road-flow scenario matrix of 2026-10-05.
/// </summary>
public sealed class RoadFlowMapperTests
{
    private const int AppId = 900001;
    private const int SiteId = 800001;

    private static readonly IReadOnlyDictionary<int, AssignmentRow> NoAssignments = new Dictionary<int, AssignmentRow>();
    private static readonly IReadOnlyDictionary<int, OwnerSummary> NoOwners = new Dictionary<int, OwnerSummary>();

    private static PropertyRow Row(
        int? rowId,
        string? type,
        string? roadId,
        string? name,
        string? entered = null,
        bool? inList = null,
        string? privateId = null,
        string? privateName = null,
        string? privateText = null,
        int? corner = 0,
        int? sides = 1,
        string? latitude = "12.9100") => new()
    {
        AppID = AppId,
        AppDisplayId = "TESTAPP0001",
        MotherEPID = "9990000001",
        Site_Id = SiteId,
        ZoneId = 102,
        WardId = 54,
        StreetId = 88,
        Latitude = latitude,
        isCornorPlot = corner,
        Site_numberOfRoadFacingSides = sides,
        Rd_RoadRow_ID = rowId,
        RoadType = type,
        RoadId = roadId,
        Roadname = name,
        Rd_EnteredRoadName = entered,
        Rd_isPresentInPublicRoadList = inList,
        Rd_PrivateRoadId = privateId,
        Rd_PrivateRoadName = privateName,
        Rd_PrivateRoadText = privateText,
    };

    /// <summary>The blank row 10,018 applications carry: id 0, no names, flag 1, private id '0'.</summary>
    private static PropertyRow Blank(int rowId, int? corner = 0, int? sides = 1) =>
        Row(rowId, "", "0", null, null, true, privateId: "0", corner: corner, sides: sides);

    private static PropertyDto Map(params PropertyRow[] rows) =>
        PropertyMapper.Map(rows.GroupBy(r => r.AppID).Single(), NoAssignments, NoOwners, officerId: 77001);

    // ---- S1 / S2: one declared road -------------------------------------------------------

    [Fact]
    public void S1_a_declared_public_road_is_one_road_with_its_own_declaration()
    {
        var site = Map(Row(91001, "public", "976", "Bile Shivale", inList: true)).SiteDetails!;

        var road = Assert.Single(site.Roads);
        Assert.Equal(91001, road.RoadRowId);
        Assert.Equal("public", road.RoadType);
        Assert.Equal("976", road.RoadId);
        Assert.Equal("Bile Shivale", road.RoadName);
        Assert.Null(road.EnteredRoadName);
        Assert.Equal(1, road.IsPresentInPublicRoadList);

        Assert.Equal([91001], site.RoadRowIds);
        Assert.Equal(SiteId, site.SiteId);
        Assert.Equal(0, site.IsCornerPlot);
        Assert.Equal(1, site.RoadFacingSides);

        // The single-road fields older apps read.
        Assert.Equal("public", site.RoadType);
        Assert.Equal("976", site.RoadId);
        Assert.Equal("Bile Shivale", site.RoadName);
    }

    [Fact]
    public void S2_a_declared_private_road_keeps_the_name_the_citizen_typed()
    {
        var road = Assert.Single(Map(
            Row(91011, "private", "9320", "Byrthi", "Test 3rd Cross", inList: false)).SiteDetails!.Roads);

        Assert.Equal("private", road.RoadType);
        Assert.Equal("9320", road.RoadId);
        Assert.Equal("Byrthi", road.RoadName);
        Assert.Equal("Test 3rd Cross", road.EnteredRoadName);
        Assert.Equal(0, road.IsPresentInPublicRoadList);
    }

    // ---- S8: corner plot, two declared roads on the same master road, plus a blank row ----

    [Fact]
    public void S8_two_roads_off_one_master_road_stay_two_roads_and_the_blank_row_is_dropped()
    {
        var site = Map(
            Blank(91083, corner: 1, sides: 2),
            Row(91082, "private", "9320", "Byrthi", "Test 1st Main", false, corner: 1, sides: 2),
            Row(91081, "private", "9320", "Byrthi", "Test 3rd Cross", false, corner: 1, sides: 2)).SiteDetails!;

        Assert.Equal([91081, 91082], site.Roads.Select(r => r.RoadRowId));
        Assert.Equal([91081, 91082], site.RoadRowIds);
        Assert.Equal(["Test 3rd Cross", "Test 1st Main"], site.Roads.Select(r => r.EnteredRoadName));
        Assert.All(site.Roads, r => Assert.Equal("9320", r.RoadId));

        Assert.Equal(1, site.IsCornerPlot);
        Assert.Equal(2, site.RoadFacingSides);
    }

    /// <summary>
    /// The procedures emit rows in no promised order. Whatever order they arrive in, the
    /// roads come out ascending by row id and the single-road fields come from the lowest
    /// real row - otherwise the card order (and the road older apps file) would change from
    /// one sync to the next.
    /// </summary>
    [Theory]
    [InlineData(0, 1, 2)]
    [InlineData(2, 1, 0)]
    [InlineData(1, 2, 0)]
    [InlineData(2, 0, 1)]
    public void Road_order_and_the_legacy_road_do_not_depend_on_the_order_rows_arrive_in(int a, int b, int c)
    {
        PropertyRow[] rows =
        [
            Blank(26127, corner: 1, sides: 2),
            Row(224, "public", "976", "Bile Shivale", "Test 1st Main", true, corner: 1, sides: 2),
            Row(223, "private", "9320", "Byrthi", "Test 3rd Cross", false, corner: 1, sides: 2),
        ];

        var site = Map(rows[a], rows[b], rows[c]).SiteDetails!;

        Assert.Equal([223, 224], site.Roads.Select(r => r.RoadRowId));
        Assert.Equal([223, 224], site.RoadRowIds);
        Assert.Equal("private", site.RoadType);
        Assert.Equal("9320", site.RoadId);
        Assert.Equal("Byrthi", site.RoadName);   // row 223's, never the blank row's
    }

    // ---- S11: the only road row is blank -------------------------------------------------

    [Fact]
    public void S11_an_application_whose_only_road_row_is_blank_has_no_roads_and_falls_back_to_that_row()
    {
        var dto = Map(Blank(91111));
        var site = dto.SiteDetails!;

        Assert.Empty(site.Roads);
        Assert.Empty(site.RoadRowIds);
        Assert.Equal("0", site.RoadId);
        Assert.Equal("", site.RoadType);
        Assert.Equal(AppId, dto.AppId);
        Assert.Equal("TESTAPP0001", dto.ApplicationId);
        Assert.Equal("9990000001", dto.Epid);
    }

    [Fact]
    public void With_only_blank_rows_the_fallback_is_the_lowest_row_id_and_unjoined_rows_come_last()
    {
        var site = Map(
            Row(null, "unjoined", null, null),           // a LEFT JOIN that matched nothing
            Row(91115, "b", "0", null),
            Row(91114, "a", "0", null)).SiteDetails!;

        Assert.Empty(site.Roads);
        Assert.Equal("a", site.RoadType);
    }

    [Fact]
    public void A_row_whose_road_join_matched_nothing_is_not_a_road()
    {
        var site = Map(
            Row(null, null, null, null),
            Row(91001, "public", "976", "Bile Shivale", inList: true)).SiteDetails!;

        Assert.Equal([91001], site.RoadRowIds);
        Assert.Single(site.Roads);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData(" 0 ")]
    [InlineData("   ")]
    public void A_row_with_no_master_road_and_no_name_of_any_kind_is_blank(string? roadId)
    {
        Assert.True(PropertyMapper.IsBlankRoad(Row(1, "", roadId, null, null, true, privateId: "0")));
    }

    /// <summary>A road type on its own says nothing about which road; the row is still blank.</summary>
    [Fact]
    public void A_road_type_alone_does_not_make_a_road() =>
        Assert.True(PropertyMapper.IsBlankRoad(Row(1, "private", "0", null, null, true, privateId: "0")));

    [Theory]
    [InlineData("976", null, null, null, null)]
    [InlineData("0", "Bile Shivale", null, null, null)]
    [InlineData("0", null, "Test 3rd Cross", null, null)]
    [InlineData("0", null, null, "Test Private Lane", null)]
    [InlineData("0", null, null, null, "Test Private Lane")]
    public void Any_id_or_name_makes_a_road(
        string roadId, string? name, string? entered, string? privateName, string? privateText) =>
        Assert.False(PropertyMapper.IsBlankRoad(
            Row(1, "", roadId, name, entered, true, privateName: privateName, privateText: privateText)));

    // ---- S12: inactive citizen rows ------------------------------------------------------

    /// <summary>
    /// The ward sync only returns active rows (ISNULL(Rd_RoadActive,1)=1) and adds the
    /// typed name. What it sends is what becomes the cards.
    /// </summary>
    [Fact]
    public void S12_ward_sync_shape_maps_only_the_active_rows_it_was_given()
    {
        var site = Map(
            Row(91123, "private", "655", "Motappa garden Phase 1 and 2", "Test 5th Cross", false, corner: 1, sides: 2),
            Row(91124, "private", "9277", "Motappa garden Phase 1 and 2", "Test 1st Main", false, corner: 1, sides: 2),
            Blank(91125, corner: 1, sides: 2)).SiteDetails!;

        Assert.Equal([91123, 91124], site.RoadRowIds);
        Assert.Equal(["655", "9277"], site.Roads.Select(r => r.RoadId));
        Assert.Equal(["Test 5th Cross", "Test 1st Main"], site.Roads.Select(r => r.EnteredRoadName));
    }

    /// <summary>
    /// The old fetch (USP_S_GetAppDetails) has no Rd_RoadActive filter and no
    /// Rd_EnteredRoadName, and PropertyRow carries no active flag, so the mapper cannot tell
    /// an inactive row from an active one: every non-blank row it is given is a road. The
    /// filter lives before it: PropertyService reads the road table and applies
    /// PropertyMapper.ApplyRoadState first (D14, see OldFetchRoadStateTests). This pins the
    /// mapper's half of that contract.
    /// </summary>
    [Fact]
    public void S12_old_fetch_shape_maps_every_non_blank_row_and_has_no_typed_names()
    {
        var site = Map(
            Row(91121, "private", "637", "Green woods layout", corner: 1, sides: 2, inList: false),
            Row(91122, "private", "637", "Green woods layout", corner: 1, sides: 2, inList: false),
            Row(91123, "private", "655", "Motappa garden Phase 1 and 2", corner: 1, sides: 2, inList: false),
            Row(91124, "private", "9277", "Motappa garden Phase 1 and 2", corner: 1, sides: 2, inList: false),
            Blank(91125, corner: 1, sides: 2)).SiteDetails!;

        Assert.Equal([91121, 91122, 91123, 91124], site.RoadRowIds);
        Assert.All(site.Roads, r => Assert.Null(r.EnteredRoadName));
        Assert.False(typeof(PropertyRow).GetProperties().Any(p => p.Name.Contains("Active")),
            "PropertyRow now carries an active flag: decide whether the mapper should filter on it, and update this test.");
    }

    // ---- duplicate rows from joins -------------------------------------------------------

    /// <summary>
    /// The procedures join owners and documents, so one road row can arrive several times
    /// with different non-road columns. It is still one road, and one roadRowIds entry.
    /// </summary>
    [Fact]
    public void One_road_row_repeated_by_other_joins_is_one_road()
    {
        var site = Map(
            Row(91001, "public", "976", "Bile Shivale", inList: true, latitude: "12.9100"),
            Row(91001, "public", "976", "Bile Shivale", inList: true, latitude: "12.9101"),
            Row(91001, "public", "976", "Bile Shivale", inList: true, latitude: null)).SiteDetails!;

        Assert.Single(site.Roads);
        Assert.Equal([91001], site.RoadRowIds);
    }

    [Fact]
    public void A_corner_plot_repeated_by_joins_keeps_one_entry_per_road_row()
    {
        var a = Row(91081, "private", "9320", "Byrthi", "Test 3rd Cross", false, corner: 1, sides: 2);
        var b = Row(91082, "private", "9320", "Byrthi", "Test 1st Main", false, corner: 1, sides: 2);

        var site = Map(a, b, a, b, Blank(91083, 1, 2), Blank(91083, 1, 2)).SiteDetails!;

        Assert.Equal([91081, 91082], site.Roads.Select(r => r.RoadRowId));
        Assert.Equal([91081, 91082], site.RoadRowIds);
    }

    // ---- S18: type and public-list flag disagree (4,470 + 4,879 active rows) -------------

    /// <summary>
    /// The API passes the citizen's road type and the public-list flag through separately
    /// and reconciles neither. Real rows disagree (a 'private' road with flag 1), and what
    /// the app does with that is the app's decision - the API must not quietly pick one.
    /// </summary>
    [Fact]
    public void S18_road_type_and_the_public_list_flag_are_passed_through_independently()
    {
        var site = Map(
            Row(91181, "private", "0", null, null, true,
                privateId: "PR-1", privateName: "Test Private Lane", privateText: "Test Private Lane"),
            Row(91182, "public", "976", "Bile Shivale", "Test Typed Public Rd", false)).SiteDetails!;

        var privateWithFlag1 = site.Roads[0];
        Assert.Equal("private", privateWithFlag1.RoadType);
        Assert.Equal(1, privateWithFlag1.IsPresentInPublicRoadList);
        Assert.Equal("0", privateWithFlag1.RoadId);
        Assert.Null(privateWithFlag1.RoadName);
        Assert.Equal("PR-1", privateWithFlag1.PrivateRoadId);
        Assert.Equal("Test Private Lane", privateWithFlag1.PrivateRoadName);
        Assert.Equal("Test Private Lane", privateWithFlag1.PrivateRoadText);

        var publicWithFlag0 = site.Roads[1];
        Assert.Equal("public", publicWithFlag0.RoadType);
        Assert.Equal(0, publicWithFlag0.IsPresentInPublicRoadList);
        Assert.Equal("Test Typed Public Rd", publicWithFlag0.EnteredRoadName);
    }

    [Theory]
    [InlineData("Tar")]
    [InlineData("BT")]
    [InlineData("Concrete")]
    [InlineData(null)]
    public void A_junk_declared_road_type_is_passed_through_unchanged(string? type) =>
        Assert.Equal(type, Assert.Single(Map(Row(91001, type, "976", "Bile Shivale", inList: true)).SiteDetails!.Roads).RoadType);

    [Fact]
    public void Corner_plot_and_road_facing_sides_stay_null_when_never_recorded()
    {
        var site = Map(Row(91001, "public", "976", "Bile Shivale", inList: true, corner: null, sides: null)).SiteDetails!;

        Assert.Null(site.IsCornerPlot);
        Assert.Null(site.RoadFacingSides);
    }

    [Fact]
    public void The_public_list_flag_maps_to_one_zero_or_null()
    {
        var site = Map(
            Row(1, "public", "976", "Bile Shivale", inList: true),
            Row(2, "private", "9320", "Byrthi", inList: false),
            Row(3, "public", "637", "Green woods layout", inList: null)).SiteDetails!;

        Assert.Equal([1, 0, null], site.Roads.Select(r => r.IsPresentInPublicRoadList));
    }

    // ---- wire shape the app's single_site_mapper reads -----------------------------------

    /// <summary>
    /// The app reads siteDetails.roads by these names (single_site_mapper.dart _declaredRoad)
    /// and treats roadId as a string. A rename here silently empties every card.
    /// </summary>
    [Fact]
    public void Roads_serialise_with_the_names_and_types_the_app_reads()
    {
        var dto = Map(
            Row(91081, "private", "9320", "Byrthi", "Test 3rd Cross", false, privateId: "0", corner: 1, sides: 2),
            Row(91082, "private", "9320", "Byrthi", "Test 1st Main", false, corner: 1, sides: 2));

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var site = doc.RootElement.GetProperty("siteDetails");

        Assert.Equal(JsonValueKind.Array, site.GetProperty("roadRowIds").ValueKind);
        Assert.Equal(91081, site.GetProperty("roadRowIds")[0].GetInt32());
        Assert.Equal(1, site.GetProperty("isCornerPlot").GetInt32());
        Assert.Equal(2, site.GetProperty("roadFacingSides").GetInt32());

        var road = site.GetProperty("roads")[0];
        Assert.Equal(
            ["roadRowId", "roadType", "roadId", "roadName", "enteredRoadName", "isPresentInPublicRoadList",
             "privateRoadId", "privateRoadName", "privateRoadText"],
            road.EnumerateObject().Select(p => p.Name));
        Assert.Equal(91081, road.GetProperty("roadRowId").GetInt32());
        Assert.Equal(JsonValueKind.String, road.GetProperty("roadId").ValueKind);
        Assert.Equal("9320", road.GetProperty("roadId").GetString());
        Assert.Equal("Test 3rd Cross", road.GetProperty("enteredRoadName").GetString());
        Assert.Equal(0, road.GetProperty("isPresentInPublicRoadList").GetInt32());
    }
}
