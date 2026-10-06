using PropertyGpsApi.Models;
using PropertyGpsApi.Services;

namespace PropertyGpsApi.Tests;

/// <summary>
/// D2 / D16: the street picker's list is the ward's KSRSAC roads (MstRoadKSRAC Road_ID and
/// Road_Name), not BBMP street ids. MasterService.ToStreets shapes the rows its query reads;
/// the ids and names below are real KSRSAC roads of ward 102/54 (2026-10-06).
/// </summary>
public sealed class WardRoadListTests
{
    private static KsracRoadRow R(int id, string? name) => new() { RoadId = id, RoadName = name };

    [Fact]
    public void Each_road_is_its_road_id_and_road_name()
    {
        var street = Assert.Single(MasterService.ToStreets([R(637, "Green woods layout")], zoneId: 102, wardId: 54));

        Assert.Equal(637, street.StreetId);
        Assert.Equal("Green woods layout", street.StreetName);
        Assert.Equal(102, street.ZoneId);
        Assert.Equal(54, street.WardId);
    }

    /// <summary>
    /// The master digitises a road as several segments under one name (976 and 9317 'Bile
    /// Shivale'). One entry, the lowest id: either verifies in the road procedure.
    /// </summary>
    [Fact]
    public void Segments_of_one_road_name_are_one_entry_with_the_lowest_id()
    {
        var streets = MasterService.ToStreets([R(9317, "Bile Shivale"), R(976, "Bile Shivale"), R(9320, "Byrthi"), R(978, "Byrthi")], 102, 54);

        Assert.Equal([(976, "Bile Shivale"), (978, "Byrthi")], streets.Select(s => (s.StreetId, s.StreetName!)));
    }

    /// <summary>
    /// D16: the level-5 list grouped by BBMP street and kept the longest name, so street 92
    /// showed 'Anugraha Layout' and hid 'Byrthi'. Every distinct name is now an entry.
    /// </summary>
    [Fact]
    public void Distinct_names_are_never_merged()
    {
        var streets = MasterService.ToStreets([R(980, "Anugraha Layout"), R(978, "Byrthi")], 102, 54);
        Assert.Equal(["Anugraha Layout", "Byrthi"], streets.Select(s => s.StreetName));
    }

    [Fact]
    public void Names_compare_trimmed_and_case_insensitively_and_keep_the_kept_rows_spelling()
    {
        var streets = MasterService.ToStreets([R(990, " bile shivale "), R(976, "Bile Shivale")], 102, 54);

        var street = Assert.Single(streets);
        Assert.Equal(976, street.StreetId);
        Assert.Equal("Bile Shivale", street.StreetName);
    }

    [Fact]
    public void Rows_with_no_id_or_no_name_are_left_out_and_the_list_is_sorted_by_name()
    {
        var streets = MasterService.ToStreets(
            [R(0, "Zero Road"), R(-1, "Negative Road"), R(700, null), R(701, "  "), R(655, "Motappa garden Phase 1 and 2"), R(624, "Belattur maruthi layout")],
            102, 54);

        Assert.Equal([624, 655], streets.Select(s => s.StreetId));
    }
}
