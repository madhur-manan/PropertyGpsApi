using System.Reflection;
using System.Text.RegularExpressions;
using PropertyGpsApi.Features.Properties;

namespace PropertyGpsApi.Tests;

/// <summary>
/// The Verification History screen's MONTHLY INSIGHTS figures.
///
/// These counters are the only number an officer has for "how much did I get
/// through this month", and BBMP will be asked about them. A code filed in the
/// wrong bucket is not a cosmetic error - it is a survey reported as approved
/// when it was rejected.
///
/// The SQL is a private const, so these read it directly rather than requiring a
/// database. That is the point: the buckets must be checkable without one, since
/// the server is behind a VPN that is not always up.
/// </summary>
public class VerificationSummaryTests
{
    private static string VerdictSums() =>
        (string)typeof(HistoryRepository)
            .GetField("VerdictSums", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    private static IReadOnlyList<int> CodesIn(string bucket)
    {
        var line = VerdictSums()
            .Split('\n')
            .Single(l => l.TrimStart().StartsWith(bucket, StringComparison.Ordinal));

        // Only the CASE condition: everything after THEN is the "1 ELSE 0"
        // counter, not a status code.
        var condition = line[..line.IndexOf("THEN", StringComparison.Ordinal)];

        return Regex.Matches(condition, @"\b\d+\b").Select(m => int.Parse(m.Value)).ToList();
    }

    /// <summary>
    /// The buckets must agree with AppStatus.verdict in the Flutter app
    /// (lib/models/single_site/single_site_property.dart). If they drift, the
    /// badge on a card and the counter on the insights panel disagree about the
    /// same survey.
    /// </summary>
    [Fact]
    public void Pending_is_only_data_received_from_RI()
    {
        Assert.Equal([13], CodesIn("Pending"));
    }

    [Fact]
    public void Approved_covers_every_stage_past_QC()
    {
        // 14 QC approved, 30 approved by RI, 150 JC approved,
        // 200 Commissioner approved, 300 payment done.
        Assert.Equal([14, 30, 150, 200, 300], CodesIn("Approved"));
    }

    [Fact]
    public void Rejected_covers_every_rejection_and_nothing_else()
    {
        // 12 rejected QC, 25 rejected by RI, 110 JC rejected.
        Assert.Equal([12, 25, 110], CodesIn("Rejected"));
    }

    [Fact]
    public void Returned_is_its_own_bucket_not_a_rejection()
    {
        // A returned survey is work to redo, which is different from being told
        // no - counting it as rejected would tell an officer they had failed.
        Assert.Equal([400], CodesIn("Returned"));
    }

    [Fact]
    public void No_code_is_counted_in_two_buckets()
    {
        var all = new[] { "Pending", "Approved", "Rejected", "Returned" }
            .SelectMany(CodesIn)
            .ToList();

        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void Ten_is_in_no_bucket_because_the_master_table_defines_it_twice()
    {
        // masterDB_prod.dbo.Mst_AppStatus lists 10 as both "Application
        // Submitted" and "Data Sent to RI", with no column to tell them apart.
        // It still counts toward Total, so the figure is never silently lost.
        var all = new[] { "Pending", "Approved", "Rejected", "Returned" }
            .SelectMany(CodesIn)
            .ToList();

        Assert.DoesNotContain(10, all);
    }

    [Fact]
    public void Total_counts_every_row_not_just_the_bucketed_ones()
    {
        // So an unrecognised code shows up as a gap between Total and the sum of
        // the four, rather than being quietly filed as an approval.
        Assert.Contains("Total    = COUNT(*)", VerdictSums());
    }
}
