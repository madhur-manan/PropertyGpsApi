using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using PropertyGpsApi.Models;

using PropertyGpsApi.Services;

using PropertyGpsApi.Interfaces;

namespace PropertyGpsApi.Tests;

/// <summary>
/// The wire contract. The app is offline-first, so a field that changes meaning between
/// versions corrupts surveys captured days earlier that cannot be recaptured.
/// </summary>
public class SubmitContractTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>
    /// The three-state rule. An officer who did not answer is not the same as one who
    /// answered "no", and the difference decides whether a surveyor is sent out.
    /// WhenWritingNull would erase that distinction silently.
    /// </summary>
    [Fact]
    public void An_unanswered_question_serialises_as_an_explicit_null()
    {
        var json = JsonSerializer.Serialize(new SubmitVerificationRequest
        {
            ApplicationId = "1", Epid = "2", IsGovtProperty = null, BuildingExists = null
        }, Json);

        Assert.Contains("\"isGovtProperty\":null", json);
        Assert.Contains("\"buildingExists\":null", json);
    }

    [Fact]
    public void Zero_and_null_are_distinguishable_on_the_wire()
    {
        var unanswered = JsonSerializer.Serialize(
            new SubmitVerificationRequest { ApplicationId = "1", Epid = "2", IsGovtProperty = null }, Json);
        var answeredNo = JsonSerializer.Serialize(
            new SubmitVerificationRequest { ApplicationId = "1", Epid = "2", IsGovtProperty = 0 }, Json);

        Assert.Contains("\"isGovtProperty\":null", unanswered);
        Assert.Contains("\"isGovtProperty\":0", answeredNo);
    }

    /// <summary>
    /// isGovtProperty carries three answers - no, yes, and refer to surveyor - so a bool
    /// cannot represent it. This test exists to stop someone tidying it into one.
    /// </summary>
    [Fact]
    public void The_government_property_answer_can_hold_its_third_value()
    {
        var json = JsonSerializer.Serialize(
            new SubmitVerificationRequest { ApplicationId = "1", Epid = "2", IsGovtProperty = 2 }, Json);
        Assert.Contains("\"isGovtProperty\":2", json);
    }

    /// <summary>
    /// roadId stays a string: it holds real ids and the sentinel "999", meaning the road is
    /// not in the public list. roadRowId is the opposite - always an int.
    /// </summary>
    [Fact]
    public void Road_identifiers_keep_their_types()
    {
        Assert.Equal(typeof(string),
            typeof(SubmitRoadDetail).GetProperty(nameof(SubmitRoadDetail.RoadId))!.PropertyType);
        Assert.Equal(typeof(int?),
            typeof(SubmitRoadDetail).GetProperty(nameof(SubmitRoadDetail.RoadRowId))!.PropertyType);

        var json = JsonSerializer.Serialize(new SubmitRoadDetail { RoadId = "999", RoadRowId = 42 }, Json);
        Assert.Contains("\"roadId\":\"999\"", json);
        Assert.Contains("\"roadRowId\":42", json);
    }

    /// <summary>
    /// Every property is named explicitly, so a C# rename cannot change the wire contract
    /// under an app already in the field.
    /// </summary>
    [Theory]
    [InlineData(typeof(SubmitVerificationRequest))]
    [InlineData(typeof(SubmitSiteDetails))]
    [InlineData(typeof(SubmitRoadDetail))]
    [InlineData(typeof(SubmitVerificationResponse))]
    [InlineData(typeof(HistoryEntryDto))]
    public void Every_property_declares_its_wire_name(Type dto)
    {
        var missing = dto.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() is null)
            .Select(p => p.Name)
            .ToList();

        Assert.True(missing.Count == 0,
            $"{dto.Name} is missing [JsonPropertyName] on: {string.Join(", ", missing)}");
    }
}

/// <summary>
/// Status_Id in BtoA_StatusDetail_Officer is a verdict code in this system's own
/// vocabulary, not an App_Status. Live rows show 10 = APPROVED, 11 = REJECTED and
/// 12 = RETURN_TO_RI, so recording the wrong one misreports what the officer decided
/// to everyone downstream who reads that row.
/// </summary>
public class OfficerVerdictTests
{
    // A direct call, not reflection. This used to be
    //     GetMethod("VerdictFor", NonPublic | Static)!
    // which meant renaming the method turned eight passing tests into a
    // NullReferenceException at runtime rather than a compile error - on the one
    // mapping that decides whether an officer is recorded as having approved or
    // rejected a khata. VerdictFor is `internal` and the test project already has
    // InternalsVisibleTo, so the compiler checks this now.
    private static (int StatusId, string? StatusValue) Verdict(string? recommendation) =>
        VerificationSubmitService.VerdictFor(recommendation);

    [Theory]
    [InlineData("Reject")]
    [InlineData("Rejected")]
    [InlineData("REJECT")]
    public void A_rejection_is_never_recorded_as_an_approval(string recommendation)
    {
        var (statusId, statusValue) = Verdict(recommendation);
        Assert.Equal(11, statusId);
        Assert.Equal("REJECTED", statusValue);
    }

    [Theory]
    [InlineData("Approve")]
    [InlineData("Approved")]
    public void An_approval_uses_the_vocabulary_already_in_the_table(string recommendation)
    {
        var (statusId, statusValue) = Verdict(recommendation);
        Assert.Equal(10, statusId);
        Assert.Equal("APPROVED", statusValue);
    }

    /// <summary>
    /// An ordinary BtoAKhata survey is a verification, not a decision. Recording a default
    /// verdict would assert something the officer never said.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_recommendation_records_no_verdict(string? recommendation)
        => Assert.Null(Verdict(recommendation).StatusValue);
}
