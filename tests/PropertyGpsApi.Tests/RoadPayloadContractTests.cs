using System.Reflection;
using System.Text.Json.Serialization;
using PropertyGpsApi.Common;
using PropertyGpsApi.Controllers;
using PropertyGpsApi.Models;

namespace PropertyGpsApi.Tests;

/// <summary>
/// The road objects of the add-new "payload" part, read exactly as the controller reads
/// them: strict, unknown keys refused, roadId a string.
///
/// The app's submit mapper (single_site_mapper.dart _road) writes these fifteen keys and no
/// others. A key added on one side only, or a type changed, turns every survey from that
/// build into a 400 - and a 400 parks the record on the device.
/// </summary>
public sealed class RoadPayloadContractTests
{
    /// <summary>The road keys the app sends, in the order it sends them.</summary>
    private static readonly string[] AppRoadKeys =
    [
        "roadRowId", "roadId", "roadName", "actualRoadName", "roadType", "isPresentInPublicRoadList",
        "roadStatus", "isRoadDetailsCorrect", "privateRoadLat", "privateRoadLng", "nearPublicRoadLat",
        "nearPublicRoadLng", "privateRoadImage", "publicRoadImage", "noticeImage",
    ];

    /// <summary>
    /// PropertyInfoController.ParsePayload is private and the controller may not be changed
    /// for this, so it is reached by reflection - guarded, so a rename fails with its name
    /// rather than a NullReferenceException. If it becomes internal, call it directly.
    /// </summary>
    private static SubmitVerificationRequest Parse(string payload)
    {
        var method = typeof(PropertyInfoController).GetMethod(
            "ParsePayload", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(method is not null,
            "PropertyInfoController.ParsePayload(string) was renamed or removed; update RoadPayloadContractTests.");

        try
        {
            return (SubmitVerificationRequest)method!.Invoke(null, [payload])!;
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    private static string Payload(string roadsJson) => $$"""
        {
          "applicationId": "TESTAPP0001",
          "epid": "9990000001",
          "wardId": 54,
          "propertyLandExists": 1,
          "isAllBhoomiSurveyNosCorrect": 1,
          "siteDetails": {
            "isCornerPlot": 1,
            "isDeclaredRoadFacingSidesCorrect": 1,
            "roadFacingSides": 2,
            "isRoadDetailsCorrect": null,
            "roadDetails": {{roadsJson}}
          }
        }
        """;

    /// <summary>S2 (declared private, caps) and S7 (added private, caps) exactly as the app writes them.</summary>
    private const string TwoRoads = """
        [
          {"roadRowId": 91011, "roadId": "9320", "roadName": "Byrthi", "actualRoadName": "Test 3rd Cross",
           "roadType": "private", "isPresentInPublicRoadList": 0, "roadStatus": 0, "isRoadDetailsCorrect": 1,
           "privateRoadLat": 12.9101, "privateRoadLng": 77.5601, "nearPublicRoadLat": 12.9105, "nearPublicRoadLng": 77.5609,
           "privateRoadImage": "road_A.jpg", "publicRoadImage": "public_road_A.jpg", "noticeImage": "notice_A.jpg"},
          {"roadRowId": null, "roadId": "999", "roadName": null, "actualRoadName": "Test Service Lane",
           "roadType": "private", "isPresentInPublicRoadList": 0, "roadStatus": 3, "isRoadDetailsCorrect": 0,
           "privateRoadLat": 12.9101, "privateRoadLng": 77.5601, "nearPublicRoadLat": 12.9105, "nearPublicRoadLng": 77.5609,
           "privateRoadImage": "road_B.jpg", "publicRoadImage": "public_road_B.jpg", "noticeImage": "notice_B.jpg"}
        ]
        """;

    [Fact]
    public void The_road_model_has_exactly_the_keys_the_app_sends()
    {
        var wireNames = typeof(SubmitRoadDetail)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name)
            .OrderBy(n => n, StringComparer.Ordinal);

        Assert.Equal(AppRoadKeys.OrderBy(n => n, StringComparer.Ordinal), wireNames);
    }

    [Fact]
    public void A_road_payload_as_the_app_writes_it_parses_into_the_right_fields()
    {
        var roads = Parse(Payload(TwoRoads)).SiteDetails.RoadDetails;

        Assert.Equal(2, roads.Count);

        var declared = roads[0];
        Assert.Equal(91011, declared.RoadRowId);
        Assert.Equal("9320", declared.RoadId);
        Assert.Equal("Byrthi", declared.RoadName);
        Assert.Equal("Test 3rd Cross", declared.ActualRoadName);
        Assert.Equal("private", declared.RoadType);
        Assert.Equal(0, declared.IsPresentInPublicRoadList);
        Assert.Equal(0, declared.RoadStatus);
        Assert.Equal(1, declared.IsRoadDetailsCorrect);
        Assert.Equal(12.9101, declared.PrivateRoadLat);
        Assert.Equal(77.5601, declared.PrivateRoadLng);
        Assert.Equal(12.9105, declared.NearPublicRoadLat);
        Assert.Equal(77.5609, declared.NearPublicRoadLng);
        Assert.Equal("road_A.jpg", declared.PrivateRoadImage);
        Assert.Equal("public_road_A.jpg", declared.PublicRoadImage);
        Assert.Equal("notice_A.jpg", declared.NoticeImage);

        var added = roads[1];
        Assert.Null(added.RoadRowId);
        Assert.Equal("999", added.RoadId);
        Assert.Null(added.RoadName);
        Assert.Equal(3, added.RoadStatus);
        Assert.Equal(0, added.IsRoadDetailsCorrect);
    }

    /// <summary>S14: the sentinel as a JSON number is a contract break, not a road id.</summary>
    [Fact]
    public void A_road_id_sent_as_a_number_is_refused_as_invalid_json()
    {
        var ex = Assert.Throws<ApiException>(() => Parse(Payload(TwoRoads.Replace("\"roadId\": \"999\"", "\"roadId\": 999"))));

        Assert.Equal(400, ex.StatusCode);
        Assert.StartsWith("The 'payload' part is not valid JSON: ", ex.Message);
    }

    /// <summary>S20: the app's stored road map carries gpsSid; it must never leak onto the wire.</summary>
    [Fact]
    public void An_unknown_key_inside_a_road_is_refused()
    {
        var ex = Assert.Throws<ApiException>(() => Parse(Payload(
            TwoRoads.Replace("\"roadRowId\": 91011,", "\"roadRowId\": 91011, \"gpsSid\": 800001,"))));

        Assert.Equal(400, ex.StatusCode);
        Assert.StartsWith("The 'payload' part is not valid JSON: ", ex.Message);
    }

    /// <summary>Numbers may arrive quoted (NumberHandling.AllowReadingFromString).</summary>
    [Fact]
    public void A_quoted_road_status_and_row_id_are_read_as_numbers()
    {
        var road = Parse(Payload(TwoRoads
            .Replace("\"roadRowId\": 91011", "\"roadRowId\": \"91011\"")
            .Replace("\"roadStatus\": 0", "\"roadStatus\": \"0\""))).SiteDetails.RoadDetails[0];

        Assert.Equal(91011, road.RoadRowId);
        Assert.Equal(0, road.RoadStatus);
    }

    [Fact]
    public void Missing_road_keys_read_as_null_and_are_then_refused_by_validation()
    {
        var road = Parse(Payload("""[{"roadRowId": 91001, "roadId": "976"}]""")).SiteDetails.RoadDetails.Single();

        Assert.Null(road.RoadStatus);
        Assert.Null(road.IsPresentInPublicRoadList);
    }

    [Fact]
    public void Road_details_null_in_json_reads_as_null_not_an_exception() =>
        Assert.Null(Parse(Payload("null")).SiteDetails.RoadDetails);

    [Fact]
    public void An_empty_road_list_parses_and_is_left_to_validation() =>
        Assert.Empty(Parse(Payload("[]")).SiteDetails.RoadDetails);
}
