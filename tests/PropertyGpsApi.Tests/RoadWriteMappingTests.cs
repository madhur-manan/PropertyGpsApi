using PropertyGpsApi.Common;
using PropertyGpsApi.Models;
using PropertyGpsApi.Services;

namespace PropertyGpsApi.Tests;

/// <summary>
/// The value conversions WriteRoadsAsync applies before calling
/// dbo.USP_IU_BtoA_SiteRoadDetails_Officer: roadStatus to Correction_Type_Value, roadId to
/// the int @BtoA_RoadId, the 0/1 answers to bits, a payload file name to its stored URL,
/// and the shape of the KSRSAC warning.
///
/// The helpers are internal (they used to be private and were reached by reflection), so a
/// rename is a compile error here. The full parameter set of one road is asserted in
/// RoadWriteParameterTests through VerificationSubmitService.RoadParameters.
/// </summary>
public sealed class RoadWriteMappingTests
{
    private static string? CorrectionTypeValue(int? status) => VerificationSubmitService.CorrectionTypeValue(status);
    private static int? ParseRoadId(string? roadId) => VerificationSubmitService.ParseRoadId(roadId);
    private static bool? AsBit(int? value) => VerificationSubmitService.AsBit(value);

    private static string? UrlFor(IReadOnlyDictionary<string, string> urls, string? name) =>
        VerificationSubmitService.UrlFor(urls, name);

    // ---- roadStatus -> @BtoA_Correction_Type_Id / _Value --------------------------------

    /// <summary>
    /// The procedure has no roadStatus parameter; the value is what drives isactive
    /// (value 'delete' or id 2 stores the row inactive). Legacy rows use 'No Change',
    /// 'Update', 'Delete', 'Add' - see decision D10 - but this vocabulary is what the API
    /// writes today and what db/17's checks read.
    /// </summary>
    [Theory]
    [InlineData(0, "unchanged")]
    [InlineData(1, "updated")]
    [InlineData(2, "delete")]
    [InlineData(3, "added")]
    public void Each_road_status_has_its_correction_value(int status, string value) =>
        Assert.Equal(value, CorrectionTypeValue(status));

    [Theory]
    [InlineData(null)]
    [InlineData(4)]
    [InlineData(-1)]
    public void An_unknown_status_has_no_correction_value(int? status) =>
        Assert.Null(CorrectionTypeValue(status));

    // ---- roadId -> @BtoA_RoadId (int) ---------------------------------------------------

    [Theory]
    [InlineData("976", 976)]
    [InlineData("9320", 9320)]
    [InlineData("999", 999)]       // the "not in the list" sentinel is a real value
    [InlineData("0", 0)]           // S18a: a declared private road with no master road
    [InlineData(" 279 ", 279)]
    [InlineData("123456", 123456)]
    public void A_numeric_road_id_is_passed_as_that_int(string roadId, int expected) =>
        Assert.Equal(expected, ParseRoadId(roadId));

    /// <summary>
    /// Anything that is not an int has no int to pass. Validate now refuses a non-blank one
    /// (A3, see RoadFixValidationTests), so only a road with no id reaches the procedure
    /// with NULL.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ST1001")]
    [InlineData("97.6")]
    [InlineData("99999999999")]
    public void A_road_id_that_is_not_an_int_has_no_int(string? roadId) =>
        Assert.Null(ParseRoadId(roadId));

    // ---- 0/1 answers -> bits ------------------------------------------------------------

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]   // never reaches here: Validate refuses anything but 0/1 (A4)
    public void A_three_state_answer_becomes_a_nullable_bit(int? value, bool? bit) =>
        Assert.Equal(bit, AsBit(value));

    // ---- payload file name -> stored document URL --------------------------------------

    private static readonly IReadOnlyDictionary<string, string> Stored =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["road_A.jpg"] = "https://example.test/x/9990000001-91011-private-20261005_101500-0a1b2c3d.jpg",
            ["notice_A.jpg"] = "https://example.test/x/9990000001-91011-notice-20261005_101500-1a2b3c4d.jpg",
        };

    [Fact]
    public void A_road_image_name_resolves_to_the_url_the_media_store_returned() =>
        Assert.Equal(Stored["road_A.jpg"], UrlFor(Stored, "road_A.jpg"));

    [Fact]
    public void Image_names_match_case_insensitively_like_the_upload_lookup() =>
        Assert.Equal(Stored["notice_A.jpg"], UrlFor(Stored, "NOTICE_A.JPG"));

    /// <summary>
    /// A2: SubmitMediaService stores a file under Path.GetFileName of the payload value, so a
    /// payload naming a path must resolve by its basename too. It used to look the raw value
    /// up, find nothing, and write NULL into the document column with a 200.
    /// </summary>
    [Theory]
    [InlineData("field_photos/road_A.jpg")]
    [InlineData("/data/user/0/app/field_photos/road_A.jpg")]
    [InlineData(" field_photos/road_A.jpg ")]
    public void A2_a_payload_path_resolves_by_its_file_name(string name) =>
        Assert.Equal(Stored["road_A.jpg"], UrlFor(Stored, name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("never_uploaded.jpg")]
    [InlineData("field_photos/never_uploaded.jpg")]
    public void No_image_or_an_unknown_one_stores_no_document(string? name) =>
        Assert.Null(UrlFor(Stored, name));

    // ---- KSRSAC warning shape -----------------------------------------------------------

    /// <summary>
    /// A8: the warning names the card by its position in the payload. It used to be keyed
    /// "roadDetails[roadId=…]", which two roads with id 999 shared, and the write loop runs
    /// in RoadRowId order, so neither an id nor a loop counter identified the card.
    /// </summary>
    [Fact]
    public void A8_a_road_warning_is_keyed_by_payload_index()
    {
        var error = VerificationSubmitService.RoadError(3, "m");

        Assert.Equal("siteDetails.roadDetails[3]", error.Field);
        Assert.Equal(ApiErrorCodes.RoadNotRecognised, error.Code);
        Assert.Equal("m", error.Message);
    }

    /// <summary>A8: the warning names the road as the officer did - an added road has no RoadName.</summary>
    [Theory]
    [InlineData(null, "Test Service Lane", "Test Service Lane")]
    [InlineData("Bile Shivale", "  ", "Bile Shivale")]
    [InlineData("Bile Shivale", " Test Lane 4 ", "Test Lane 4")]
    [InlineData(null, null, "")]
    public void A8_the_warning_names_the_typed_name_before_the_master_name(
        string? roadName, string? actualRoadName, string expected) =>
        Assert.Equal(expected, VerificationSubmitService.NameOf(
            new SubmitRoadDetail { RoadName = roadName, ActualRoadName = actualRoadName }));
}
