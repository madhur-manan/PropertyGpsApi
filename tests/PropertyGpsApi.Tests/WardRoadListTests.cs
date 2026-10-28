using PropertyGpsApi.Models;
using PropertyGpsApi.Services;

namespace PropertyGpsApi.Tests;

/// <summary>
/// The street picker's list (masters/streets): a ward's roads from UDD_MASTERDB's
/// USP_S_GetRoadData at level 3, or from mst_AROMapping's StreetNumber / StreetName_EN when
/// the procedure cannot take the ward. MasterService.ToStreets shapes the rows either source
/// reads (columns id and streetName); the ids are text and are sent back as roadId.
/// </summary>
public sealed class WardRoadListTests
{
    private const int Ulb = 1;
    private const string Ward = "54";

    private static KsracRoadRow R(string? id, string? name) => new() { Id = id, StreetName = name };

    [Fact]
    public void Each_road_is_its_id_and_name_in_the_ulb_and_ward_asked_for()
    {
        var street = Assert.Single(MasterService.ToStreets([R("637", "Green woods layout")], Ulb, Ward));

        Assert.Equal("637", street.StreetId);
        Assert.Equal("Green woods layout", street.StreetName);
        Assert.Equal(Ulb, street.UlbCode);
        Assert.Equal(Ward, street.WardNumber);
        // Kept in the JSON for older builds, never filled.
        Assert.Null(street.ZoneId);
        Assert.Null(street.WardId);
    }

    /// <summary>The id is text: a non-numeric one is a road like any other, never parsed.</summary>
    [Fact]
    public void A_non_numeric_id_is_kept_as_text()
    {
        var streets = MasterService.ToStreets([R("KA-12/a", "Temple Street"), R("007", "Old Road")], Ulb, Ward);

        Assert.Equal([("007", "Old Road"), ("KA-12/a", "Temple Street")], streets.Select(s => (s.StreetId, s.StreetName)));
    }

    /// <summary>USP_S_GetRoadData's catch-all row '99999' is not a road.</summary>
    [Fact]
    public void The_procedures_99999_row_is_dropped()
    {
        var streets = MasterService.ToStreets([R("99999", "Others"), R(" 99999 ", "Others"), R("980", "Byrthi")], Ulb, Ward);

        Assert.Equal(["980"], streets.Select(s => s.StreetId));
    }

    /// <summary>A ward has many "1st Cross": different roads that share a name stay separate.</summary>
    [Fact]
    public void Different_roads_with_one_name_are_never_merged()
    {
        var streets = MasterService.ToStreets([R("9317", "1st Cross"), R("976", "1st Cross"), R("980", "Anugraha Layout")], Ulb, Ward);

        Assert.Equal([("9317", "1st Cross"), ("976", "1st Cross"), ("980", "Anugraha Layout")],
            streets.Select(s => (s.StreetId, s.StreetName)));
    }

    /// <summary>mst_AROMapping has one row per street per block: an exact repeat is listed once.</summary>
    [Fact]
    public void An_exact_repeat_is_one_entry_trimmed_and_ignoring_case()
    {
        var street = Assert.Single(MasterService.ToStreets([R(" 976 ", " Bile Shivale "), R("976", "bile shivale")], Ulb, Ward));

        Assert.Equal(("976", "Bile Shivale"), (street.StreetId, street.StreetName));
    }

    /// <summary>
    /// The street number is the street (Eaasthi's StreetId): blocks spell one street their own
    /// way (Mysore ward 9, street 33), so it is one entry with its most common spelling.
    /// </summary>
    [Fact]
    public void One_street_number_is_one_entry_with_its_most_common_spelling()
    {
        var street = Assert.Single(MasterService.ToStreets(
        [
            R("33", "Saravajanika Hostel Road"),
            R("33", "Sarvajanika Hostel Road"),
            R("33", "Sarva Janika Hastel Road"),
            R("33", "Sarvajanika Hostel Road"),
        ], Ulb, Ward));

        Assert.Equal(("33", "Sarvajanika Hostel Road"), (street.StreetId, street.StreetName));
    }

    [Fact]
    public void Rows_with_no_id_or_no_name_are_left_out_and_the_list_is_sorted_by_name()
    {
        var streets = MasterService.ToStreets(
            [R(null, "No Id Road"), R("  ", "Blank Id Road"), R("700", null), R("701", "  "),
             R("655", "Motappa garden Phase 1 and 2"), R("624", "Belattur maruthi layout")],
            Ulb, Ward);

        Assert.Equal(["624", "655"], streets.Select(s => s.StreetId));
    }

    [Fact]
    public void The_ward_number_on_each_road_is_trimmed()
    {
        var street = Assert.Single(MasterService.ToStreets([R("1", "A Road")], Ulb, " 54 "));
        Assert.Equal("54", street.WardNumber);
    }

    // ---- which source answers ---------------------------------------------------------------

    /// <summary>
    /// The procedure's @wardId is an int, so only a ward number that is a whole number goes to
    /// it; anything else reads mst_AROMapping directly.
    /// </summary>
    [Theory]
    [InlineData("54", 54)]
    [InlineData(" 54 ", 54)]
    [InlineData("007", 7)]
    public void A_whole_number_ward_goes_to_the_procedure(string ward, int wardId) =>
        Assert.Equal(wardId, MasterService.RoadDataWardId(ward));

    [Theory]
    [InlineData("12A")]
    [InlineData("12-1")]
    [InlineData("-5")]
    [InlineData("+5")]
    [InlineData("5.0")]
    [InlineData("99999999999")]
    [InlineData("")]
    [InlineData(null)]
    public void Any_other_ward_falls_back_to_the_mapping_table(string? ward) =>
        Assert.Null(MasterService.RoadDataWardId(ward));

    /// <summary>SQL Server error 245, "Conversion failed": the procedure could not take the ward.</summary>
    [Fact]
    public void Only_a_conversion_failure_falls_back()
    {
        Assert.True(MasterService.FallsBackOn(245));
        Assert.False(MasterService.FallsBackOn(208));   // invalid object name: a real fault
        Assert.False(MasterService.FallsBackOn(2812));  // procedure not found: a real fault
        Assert.False(MasterService.FallsBackOn(0));
    }

    [Fact]
    public void The_procedure_is_asked_at_ward_level_with_no_block()
    {
        var p = MasterService.RoadDataParameters(districtCode: 20, ulbCode: Ulb, wardId: 54);

        Assert.Equal("dbo.USP_S_GetRoadData", MasterService.RoadDataProcedure);
        Assert.Equal(20, p.Get<int?>("@DistCode"));
        Assert.Equal(Ulb, p.Get<int>("@ULBCode"));
        Assert.Equal(54, p.Get<int>("@wardId"));
        Assert.Null(p.Get<int?>("@blockid"));
        Assert.Equal(3, p.Get<int>("@Level"));
    }

    [Fact]
    public void The_district_is_the_ulbs_own_from_the_mapping_table()
    {
        Assert.Contains("SELECT TOP (1) CONVERT(int, a.DistrictCode)", MasterService.DistrictSql);
        Assert.Contains("WHERE a.ULBCode = @ulbCode", MasterService.DistrictSql);
    }

    /// <summary>The fallback reads the ward's streets, aliased as the procedure's columns.</summary>
    [Fact]
    public void The_fallback_reads_the_wards_streets_from_the_mapping_table()
    {
        var sql = MasterService.WardStreetsSql;

        Assert.Contains("id         = LTRIM(RTRIM(REPLACE(a.StreetNumber, CHAR(9), '')))", sql);
        Assert.Contains("streetName = LTRIM(RTRIM(REPLACE(a.StreetName_EN, CHAR(9), '')))", sql);
        Assert.Contains("FROM dbo.mst_AROMapping", sql);
        Assert.Contains("a.ULBCode = @ulbCode", sql);
        Assert.Contains("LTRIM(RTRIM(REPLACE(a.WardNumber, CHAR(9), ''))) = @wardNumber", sql);
    }

    /// <summary>UDD_MASTERDB has none of the old master's GBA/BBMP or zone columns.</summary>
    [Fact]
    public void No_master_query_reads_a_column_that_is_gone()
    {
        foreach (var sql in new[] { MasterService.UlbsSql, MasterService.WardsSql, MasterService.DistrictSql, MasterService.WardStreetsSql })
        {
            Assert.DoesNotContain("GBA", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("BBMP", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Zone", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CorpId", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("MstRoadKSRAC", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_road_id_parameter_type_is_text_end_to_end()
    {
        // StreetDto.streetId is what the submit sends back as roadId, which the road
        // procedure takes as NVARCHAR(50).
        Assert.Equal(typeof(string), typeof(StreetDto).GetProperty(nameof(StreetDto.StreetId))!.PropertyType);
        var p = VerificationSubmitService.RoadParameters("APP1",
            new SubmitRoadDetail { RoadRowId = 1, RoadId = "KA-12/a", RoadType = "public" },
            new Dictionary<string, string>(), 11320, 117);
        Assert.IsType<string>(p.Get<object>("@BtoA_RoadId"));
        Assert.Equal("KA-12/a", p.Get<string?>("@BtoA_RoadId"));
    }
}
