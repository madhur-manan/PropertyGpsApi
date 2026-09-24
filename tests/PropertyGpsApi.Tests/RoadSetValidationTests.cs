using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Infrastructure.Options;
using PropertyGpsApi.Models;
using PropertyGpsApi.Services;
using Xunit;

namespace PropertyGpsApi.Tests;

/// <summary>
/// The server's backstop for roads that contradict each other or the corner-plot answer.
///
/// Before these rules, a field survey - not a corner plot, one declared road, two
/// submitted - would have been accepted with 200 and an extra road row stored. The app now
/// refuses these before sending; this is what an older install meets instead.
/// </summary>
public sealed class RoadSetValidationTests
{
    private const int RevenueInspector = 116;
    private const int Ward = 54;

    // Validate() reads nothing from the database, so no connection is needed.
    private static readonly VerificationSubmitService Service = new(
        null!, Options.Create(new StoredProcedureOptions()), NullLogger<VerificationSubmitService>.Instance);

    private static SubmitRoadDetail Road(
        string? roadId = "618", int status = 0, int? rowId = 106518, int publicList = 1) => new()
    {
        RoadRowId = rowId,
        RoadId = roadId,
        RoadName = "Ayyappa Nagar and Chikka Devasandra",
        RoadStatus = status,
        IsPresentInPublicRoadList = publicList,
    };

    private static SubmitVerificationRequest Survey(int? cornerPlot, int? sides, params SubmitRoadDetail[] roads) => new()
    {
        ApplicationId = "202601010000001",
        Epid = "1000000001",
        WardId = Ward,
        PropertyLandExists = 1,
        IsAllBhoomiSurveyNosCorrect = 1,
        SiteDetails = new SubmitSiteDetails
        {
            IsCornerPlot = cornerPlot,
            RoadFacingSides = sides,
            RoadDetails = roads,
        },
    };

    private static IReadOnlyList<string> CodesFor(SubmitVerificationRequest request)
    {
        try
        {
            Service.Validate(request, RevenueInspector, Ward);
            return [];
        }
        catch (SubmitRejectedException e)
        {
            return e.Errors.Select(x => x.Code).ToList();
        }
    }

    [Fact]
    public void A_consistent_one_road_plot_is_accepted() =>
        Assert.Empty(CodesFor(Survey(cornerPlot: 0, sides: 1, Road())));

    [Fact]
    public void Two_roads_on_a_one_road_plot_is_refused()
    {
        // What was actually sent: the declared road confirmed, plus an officer-added one.
        var codes = CodesFor(Survey(cornerPlot: 0, sides: 1,
            Road(), Road(roadId: "700", status: 3, rowId: null)));

        Assert.Contains("ROAD_COUNT_MISMATCH", codes);
    }

    [Fact]
    public void Not_a_corner_plot_with_two_roads_is_refused_even_when_the_count_agrees()
    {
        var codes = CodesFor(Survey(cornerPlot: 0, sides: 2,
            Road(), Road(roadId: "700", status: 3, rowId: null)));

        Assert.Contains("CORNER_PLOT_MISMATCH", codes);
        Assert.DoesNotContain("ROAD_COUNT_MISMATCH", codes);
    }

    [Fact]
    public void A_corner_plot_with_one_road_is_refused() =>
        Assert.Contains("CORNER_PLOT_MISMATCH", CodesFor(Survey(cornerPlot: 1, sides: 1, Road())));

    [Fact]
    public void A_road_marked_not_found_does_not_count_as_a_side()
    {
        // Declared road deleted, the real one added: one live road on a one-road plot.
        var codes = CodesFor(Survey(cornerPlot: 0, sides: 1,
            Road(status: 2), Road(roadId: "700", status: 3, rowId: null)));

        Assert.Empty(codes);
    }

    [Fact]
    public void Every_road_marked_not_found_is_refused() =>
        Assert.Contains("REQUIRED", CodesFor(Survey(cornerPlot: 0, sides: 1, Road(status: 2))));

    [Fact]
    public void More_than_five_roads_is_refused()
    {
        var roads = Enumerable.Range(0, 6)
            .Select(i => Road(roadId: (700 + i).ToString(), status: 3, rowId: null))
            .ToArray();

        Assert.Contains("TOO_MANY_ROADS", CodesFor(Survey(cornerPlot: 1, sides: 6, roads)));
    }

    [Fact]
    public void An_officer_picking_the_declared_road_again_is_refused()
    {
        var codes = CodesFor(Survey(cornerPlot: 1, sides: 2,
            Road(), Road(roadId: "618", status: 3, rowId: null)));

        Assert.Contains("DUPLICATE_ROAD", codes);
    }

    [Fact]
    public void Declared_corner_plot_roads_sharing_an_id_are_not_a_duplicate()
    {
        // The fetch copies one site-level road onto every declared entry, so they share an
        // id by construction. Refusing that would reject every confirmed corner plot.
        var codes = CodesFor(Survey(cornerPlot: 1, sides: 2,
            Road(rowId: 106518), Road(rowId: 106519)));

        Assert.Empty(codes);
    }

    [Fact]
    public void Two_entries_for_the_same_road_row_are_refused()
    {
        // The road procedure matches on the row id; the second would overwrite the first.
        var codes = CodesFor(Survey(cornerPlot: 1, sides: 2,
            Road(rowId: 106518), Road(roadId: "700", status: 1, rowId: 106518)));

        Assert.Contains("DUPLICATE_ROAD", codes);
    }

    [Fact]
    public void The_not_in_list_sentinel_is_never_a_duplicate()
    {
        var codes = CodesFor(Survey(cornerPlot: 1, sides: 2,
            Road(roadId: "999", status: 3, rowId: null, publicList: 0),
            Road(roadId: "999", status: 3, rowId: null, publicList: 0)));

        Assert.DoesNotContain("DUPLICATE_ROAD", codes);
    }

    [Fact]
    public void An_unknown_road_status_is_refused() =>
        Assert.Contains("INVALID", CodesFor(Survey(cornerPlot: 0, sides: 1, Road(status: 7))));

    [Fact]
    public void Missing_site_details_is_a_rejection_not_a_crash()
    {
        // Used to be a NullReferenceException -> 500 -> retried by the device forever.
        var request = Survey(cornerPlot: 0, sides: 1, Road());
        var broken = new SubmitVerificationRequest
        {
            ApplicationId = request.ApplicationId,
            Epid = request.Epid,
            WardId = Ward,
            PropertyLandExists = 1,
            IsAllBhoomiSurveyNosCorrect = 1,
            SiteDetails = null!,
        };

        Assert.Contains("REQUIRED", CodesFor(broken));
    }

    [Fact]
    public void Null_road_details_is_a_rejection_not_a_crash()
    {
        var request = Survey(cornerPlot: 0, sides: 1);
        var broken = new SubmitVerificationRequest
        {
            ApplicationId = request.ApplicationId,
            Epid = request.Epid,
            WardId = Ward,
            PropertyLandExists = 1,
            IsAllBhoomiSurveyNosCorrect = 1,
            SiteDetails = new SubmitSiteDetails { IsCornerPlot = 0, RoadFacingSides = 1, RoadDetails = null! },
        };

        Assert.Contains("REQUIRED", CodesFor(broken));
    }
}
