using PropertyGpsApi.Models;
using PropertyGpsApi.Services;

namespace PropertyGpsApi.Tests;

/// <summary>
/// One application, several road rows: each road keeps its own declaration, and the blank
/// row many applications carry is not a road. Before this, every road card on the phone
/// showed whichever row the procedure emitted first, and an officer confirming a card filed
/// that road over the citizen's own.
/// </summary>
public class PropertyMapperRoadTests
{
    private static readonly IReadOnlyDictionary<int, AssignmentRow> NoAssignments = new Dictionary<int, AssignmentRow>();
    private static readonly IReadOnlyDictionary<int, OwnerSummary> NoOwners = new Dictionary<int, OwnerSummary>();

    // Invented values. Two private roads off one street-master road, and a blank row
    // (no road, no name, no creator).
    private static PropertyRow Row(int roadRowId, string? type, string? roadId, string? name, string? entered,
        bool? inList, string? privateText = null) => new()
    {
        AppID = 900113,
        AppDisplayId = "TEST-900113",
        Site_Id = 7001,
        RoadType = type,
        RoadId = roadId,
        Roadname = name,
        Rd_EnteredRoadName = entered,
        Rd_isPresentInPublicRoadList = inList,
        Rd_PrivateRoadText = privateText,
        Rd_RoadRow_ID = roadRowId
    };

    private static PropertyRow Blank(int roadRowId) => Row(roadRowId, null, "0", null, null, true);

    private static PropertyDto Map(params PropertyRow[] rows) =>
        PropertyMapper.Map(rows.GroupBy(r => r.AppID).Single(), NoAssignments, NoOwners, officerId: 11320);

    [Fact]
    public void Every_road_row_keeps_its_own_declaration_and_the_blank_row_is_left_out()
    {
        var dto = Map(
            Blank(26127),                                 // emitted first
            Row(223, "private", "9320", "Test Main Road", "Test 3rd Cross", false),
            Row(224, "private", "9320", "Test Main Road", "Test 1st Main", false));

        var roads = dto.SiteDetails!.Roads;

        Assert.Equal([223, 224], roads.Select(r => r.RoadRowId));
        Assert.Equal([223, 224], dto.SiteDetails.RoadRowIds);

        Assert.Equal("private", roads[0].RoadType);
        Assert.Equal("9320", roads[0].RoadId);
        Assert.Equal("Test Main Road", roads[0].RoadName);
        Assert.Equal("Test 3rd Cross", roads[0].EnteredRoadName);
        Assert.Equal(0, roads[0].IsPresentInPublicRoadList);

        Assert.Equal("Test 1st Main", roads[1].EnteredRoadName);
    }

    [Fact]
    public void The_single_road_fields_older_apps_read_come_from_the_lowest_real_road()
    {
        var dto = Map(
            Blank(100),
            Row(223, "private", "9320", "Test Main Road", "Test 3rd Cross", false));

        Assert.Equal("9320", dto.SiteDetails!.RoadId);
        Assert.Equal("Test Main Road", dto.SiteDetails.RoadName);
    }

    [Fact]
    public void An_application_whose_only_road_row_is_blank_has_no_roads_but_is_still_mapped()
    {
        var dto = Map(Blank(26127));

        Assert.Empty(dto.SiteDetails!.Roads);
        Assert.Empty(dto.SiteDetails.RoadRowIds);
        Assert.Equal(900113, dto.AppId);
        Assert.Equal("TEST-900113", dto.ApplicationId);
    }

    [Theory]
    [InlineData(null, null, "Test 3rd Cross", null)]   // only a typed name
    [InlineData("0", null, null, "Test lane")]         // only the private-road text
    [InlineData("0", "Test Main Road", null, null)]    // only a road name
    [InlineData("512", null, null, null)]              // only a street-master id
    public void A_row_with_any_id_or_name_is_a_road(string? roadId, string? name, string? entered, string? privateText)
    {
        var dto = Map(Row(301, null, roadId, name, entered, null, privateText));

        Assert.Equal([301], dto.SiteDetails!.RoadRowIds);
    }

    [Fact]
    public void A_road_row_repeated_by_other_joins_is_one_road()
    {
        var dto = Map(
            Row(223, "private", "9320", "Test Main Road", "Test 3rd Cross", false),
            Row(223, "private", "9320", "Test Main Road", "Test 3rd Cross", false));

        Assert.Single(dto.SiteDetails!.Roads);
    }

    [Fact]
    public void A_missing_public_list_flag_stays_unknown_rather_than_guessed()
    {
        var dto = Map(Row(7001, "public", "512", "Test Street", null, null));

        Assert.Null(dto.SiteDetails!.Roads.Single().IsPresentInPublicRoadList);
    }

    [Fact]
    public void An_application_with_no_road_rows_has_no_roads()
    {
        var dto = Map(new PropertyRow { AppID = 900114, Site_Id = 7002 });

        Assert.Empty(dto.SiteDetails!.Roads);
        Assert.Empty(dto.SiteDetails.RoadRowIds);
    }
}
