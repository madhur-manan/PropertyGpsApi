using PropertyGpsApi.Models;
using PropertyGpsApi.Services;

namespace PropertyGpsApi.Tests;

/// <summary>
/// D14, API side: the old fetch (USP_S_GetAppDetails, BBMP's) returns inactive road rows,
/// no typed road name, and applies TOP (n) to joined rows. PropertyService reads each row's
/// active flag and typed name from BtoA_SiteRoadDetails and applies them with
/// PropertyMapper.ApplyRoadState, and drops an application the TOP may have cut in half,
/// so the old fetch delivers what the ward sync delivers. The procedure-side fix is
/// requested in docs/DBA_Request.md (F4).
///
/// Invented fixture: App 900001 / 900002, row ids 91xxx.
/// </summary>
public sealed class OldFetchRoadStateTests
{
    private static readonly IReadOnlyDictionary<int, AssignmentRow> NoAssignments = new Dictionary<int, AssignmentRow>();
    private static readonly IReadOnlyDictionary<int, OwnerSummary> NoOwners = new Dictionary<int, OwnerSummary>();

    private static PropertyRow Row(int app, int? rowId, string? roadId = "976", string? name = "Bile Shivale") => new()
    {
        AppID = app,
        AppDisplayId = $"TESTAPP{app}",
        Site_Id = 800001,
        isCornorPlot = 1,
        Site_numberOfRoadFacingSides = 2,
        Rd_RoadRow_ID = rowId,
        RoadType = "public",
        RoadId = roadId,
        Roadname = name,
        Rd_isPresentInPublicRoadList = true,
    };

    private static RoadRowState State(int rowId, bool active, string? entered = null) =>
        new() { RowId = rowId, Active = active, EnteredRoadName = entered };

    private static SiteDetailsDto Map(IEnumerable<PropertyRow> rows) =>
        PropertyMapper.Map(rows.GroupBy(r => r.AppID).Single(), NoAssignments, NoOwners, officerId: 77001).SiteDetails!;

    /// <summary>App 370 showed its inactive rows 2003 / 2005 as cards; the DB stage then stored 'delete' rows for them.</summary>
    [Fact]
    public void An_inactive_road_row_is_not_a_road()
    {
        List<PropertyRow> rows = [Row(900001, 91001), Row(900001, 91002), Row(900001, 91003, "637", "Green woods layout")];

        PropertyMapper.ApplyRoadState(rows, new Dictionary<int, RoadRowState>
        {
            [91001] = State(91001, true),
            [91002] = State(91002, false),
            [91003] = State(91003, true),
        });
        var site = Map(rows);

        Assert.Equal([91001, 91003], site.RoadRowIds);
        Assert.Equal([91001, 91003], site.Roads.Select(r => r.RoadRowId));
    }

    [Fact]
    public void An_active_road_row_gets_its_typed_name()
    {
        List<PropertyRow> rows = [Row(900001, 91001)];

        PropertyMapper.ApplyRoadState(rows, new Dictionary<int, RoadRowState> { [91001] = State(91001, true, "Test 3rd Cross") });

        Assert.Equal("Test 3rd Cross", Assert.Single(Map(rows).Roads).EnteredRoadName);
    }

    [Fact]
    public void A_row_the_road_table_does_not_know_is_left_as_it_is()
    {
        List<PropertyRow> rows = [Row(900001, 91001)];

        PropertyMapper.ApplyRoadState(rows, new Dictionary<int, RoadRowState>());

        var road = Assert.Single(Map(rows).Roads);
        Assert.Equal(91001, road.RoadRowId);
        Assert.Null(road.EnteredRoadName);
    }

    /// <summary>With every road row inactive the application still arrives, with no roads.</summary>
    [Fact]
    public void An_application_whose_rows_are_all_inactive_still_arrives()
    {
        List<PropertyRow> rows = [Row(900001, 91001)];

        PropertyMapper.ApplyRoadState(rows, new Dictionary<int, RoadRowState> { [91001] = State(91001, false) });
        var site = Map(rows);

        Assert.Empty(site.Roads);
        Assert.Empty(site.RoadRowIds);
    }

    // ---- TOP (n) over joined rows ---------------------------------------------------------

    [Fact]
    public void A_short_page_is_kept_whole()
    {
        List<PropertyRow> rows = [Row(900001, 91001), Row(900002, 91011)];
        Assert.Same(rows, PropertyMapper.DropPossiblyTruncatedLast(rows, limit: 3));
    }

    /// <summary>
    /// A full page may have cut the last application between its road rows (a corner plot
    /// delivered with one of its two roads). It is dropped, unacknowledged, so the next fetch
    /// delivers it whole.
    /// </summary>
    [Fact]
    public void A_full_page_drops_its_last_application()
    {
        List<PropertyRow> rows = [Row(900001, 91001), Row(900001, 91002), Row(900002, 91011)];

        var kept = PropertyMapper.DropPossiblyTruncatedLast(rows, limit: 3);

        Assert.Equal([900001, 900001], kept.Select(r => r.AppID));
    }

    [Fact]
    public void A_full_page_of_one_application_is_kept()
    {
        List<PropertyRow> rows = [Row(900001, 91001), Row(900001, 91002)];
        Assert.Equal(2, PropertyMapper.DropPossiblyTruncatedLast(rows, limit: 2).Count);
    }
}
