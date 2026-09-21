using PropertyGpsApi.Infrastructure.External;

namespace PropertyGpsApi.Tests;

/// <summary>
/// Reading the Bhoomi lat/long lookup.
///
/// The service answers HTTP 200 for every outcome it has, including its own validation
/// failures, so the entire contract lives in RESPONSE_CODE. Anything branching on the HTTP
/// status would report an invalid coordinate as a successful check with no government land
/// found - which is the shape of defect that has cost this project the most: a wrong answer
/// presented as fact, with no error anywhere. Every documented code gets a test.
///
/// The payloads below are real responses, captured from the service on 20 Sep 2026.
/// </summary>
public class BhoomiResponseTests
{
    [Fact]
    public void Land_records_found_are_read_with_their_owner_types()
    {
        // One point, one survey number, three records - two government and one private.
        const string body = """
            {"RESPONSE_MESSAGE":"Boundary polygon and land details are available.",
             "RESPONSE_CODE":"1",
             "LAND_INFO":[
               {"DISTRICT_NAME":"BENGALURU","TALUKA_NAME":"BANGALORE-NORTH","HOBLI_NAME":"YASHAVANTAPURA2",
                "VILLAGE_NAME":"HEROHALLI","SURVEY_NUMBER":63,"OWNER_NAME":" ","OWNER_TYPE":"GOVERNMENT"},
               {"DISTRICT_NAME":"BENGALURU","TALUKA_NAME":"BANGALORE-NORTH","HOBLI_NAME":"YASHAVANTAPURA2",
                "VILLAGE_NAME":"HEROHALLI","SURVEY_NUMBER":63,"OWNER_NAME":"","OWNER_TYPE":"PRIVATE"},
               {"DISTRICT_NAME":"BENGALURU","TALUKA_NAME":"BANGALORE-NORTH","HOBLI_NAME":"YASHAVANTAPURA2",
                "VILLAGE_NAME":"HEROHALLI","SURVEY_NUMBER":63,"OWNER_NAME":"  ","OWNER_TYPE":"GOVERNMENT"}]}
            """;

        var result = BhoomiClient.Parse(body);

        Assert.Equal(BhoomiOutcome.Found, result.Outcome);
        Assert.Equal(3, result.Parcels.Count);
        Assert.Equal("63", result.Parcels[0].SurveyNumber);
        Assert.Equal("HEROHALLI", result.Parcels[0].Village);
        Assert.True(result.Parcels[0].IsGovernment);
        Assert.False(result.Parcels[1].IsGovernment);
        Assert.True(result.AnyGovernmentParcel);
    }

    [Fact]
    public void Owner_names_that_are_only_whitespace_become_null()
    {
        // Real responses carry " " rather than null, which would print as a blank owner.
        const string body = """
            {"RESPONSE_CODE":"1","RESPONSE_MESSAGE":"ok",
             "LAND_INFO":[{"SURVEY_NUMBER":63,"OWNER_NAME":" ","OWNER_TYPE":"GOVERNMENT"}]}
            """;

        Assert.Null(BhoomiClient.Parse(body).Parcels.Single().OwnerName);
    }

    [Fact]
    public void A_point_with_no_bhoomi_record_is_not_reported_as_checked_and_clear()
    {
        // The common case for a city plot: Bhoomi is the RURAL land record system. This must
        // never read as "no government land found" - it is "the records do not cover here".
        const string body = """
            {"RESPONSE_MESSAGE":"Boundary polygon is available, but corresponding land details are not found in Bhoomi.",
             "RESPONSE_CODE":"2","LAND_INFO":null}
            """;

        var result = BhoomiClient.Parse(body);

        Assert.Equal(BhoomiOutcome.NoLandRecord, result.Outcome);
        Assert.Empty(result.Parcels);
        Assert.False(result.AnyGovernmentParcel);
    }

    [Fact]
    public void A_point_outside_mapped_areas_is_its_own_outcome()
    {
        const string body = """
            {"RESPONSE_MESSAGE":"Boundary polygon is not available (point falls outside of mapped areas)",
             "RESPONSE_CODE":"3","LAND_INFO":null}
            """;

        Assert.Equal(BhoomiOutcome.OutsideMappedArea, BhoomiClient.Parse(body).Outcome);
    }

    [Fact]
    public void A_rejected_coordinate_arrives_inside_an_http_200_and_is_not_a_clear_result()
    {
        const string body = """
            {"RESPONSE_MESSAGE":"406 | INVALID LATITUDE 0","RESPONSE_CODE":"400","LAND_INFO":null}
            """;

        var result = BhoomiClient.Parse(body);

        Assert.Equal(BhoomiOutcome.InvalidCoordinates, result.Outcome);
        Assert.False(result.AnyGovernmentParcel);
    }

    [Fact]
    public void An_unknown_response_code_is_unavailable_rather_than_assumed_good()
    {
        const string body = """{"RESPONSE_CODE":"7","RESPONSE_MESSAGE":"something new","LAND_INFO":null}""";

        Assert.Equal(BhoomiOutcome.Unavailable, BhoomiClient.Parse(body).Outcome);
    }

    [Fact]
    public void Details_available_with_an_empty_list_is_downgraded_to_no_record()
    {
        // Otherwise the officer is told the check ran and found nothing government, when in
        // fact it found nothing at all.
        const string body = """{"RESPONSE_CODE":"1","RESPONSE_MESSAGE":"ok","LAND_INFO":[]}""";

        Assert.Equal(BhoomiOutcome.NoLandRecord, BhoomiClient.Parse(body).Outcome);
    }

    [Fact]
    public void A_survey_number_sent_as_a_string_is_still_read()
    {
        // Karnataka survey numbers are not always integers - "63/1A" is ordinary.
        const string body = """
            {"RESPONSE_CODE":"1","RESPONSE_MESSAGE":"ok",
             "LAND_INFO":[{"SURVEY_NUMBER":"63/1A","OWNER_TYPE":"PRIVATE"}]}
            """;

        Assert.Equal("63/1A", BhoomiClient.Parse(body).Parcels.Single().SurveyNumber);
    }

    [Fact]
    public void An_unreadable_body_does_not_throw_into_the_officers_face()
    {
        Assert.Equal(BhoomiOutcome.Unavailable, BhoomiClient.Parse("<html>gateway error</html>").Outcome);
    }
}
