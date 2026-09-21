using PropertyGpsApi.Features.Properties;

namespace PropertyGpsApi.Tests;

/// <summary>
/// The Verification History screen's MONTHLY INSIGHTS figures.
///
/// These counters are the only number an officer has for "how much did I get
/// through this month", and BBMP will be asked about them. A code in the wrong
/// bucket is not a cosmetic error — it is a survey reported as approved when it
/// was rejected.
///
/// These assert on the bucket arrays directly. The first version pulled the
/// generated SQL out of a private const by reflection and regexed it, which
/// meant a rename became a NullReferenceException and re-indenting the query
/// became a test failure. The buckets are now data and the SQL is built from
/// them, so there is one source of truth and nothing here parses text.
/// </summary>
public class VerificationSummaryTests
{
    /// <summary>
    /// Must agree with AppStatus.verdict in the Flutter app
    /// (lib/models/single_site/single_site_property.dart). If these drift, the
    /// badge on a card and the counter on the insights panel disagree about the
    /// same survey.
    /// </summary>
    [Fact]
    public void Pending_is_only_data_received_from_RI()
        => Assert.Equal([13], HistoryRepository.PendingCodes);

    [Fact]
    public void Approved_covers_every_stage_past_QC()
    {
        // 14 QC approved, 30 approved by RI, 150 JC approved,
        // 200 Commissioner approved, 300 payment done.
        Assert.Equal([14, 30, 150, 200, 300], HistoryRepository.ApprovedCodes);
    }

    [Fact]
    public void Rejected_covers_every_rejection_and_nothing_else()
    {
        // 12 rejected QC, 25 rejected by RI, 110 JC rejected.
        Assert.Equal([12, 25, 110], HistoryRepository.RejectedCodes);
    }

    [Fact]
    public void Returned_is_its_own_bucket_not_a_rejection()
    {
        // A returned survey is work to redo, which is different from being told
        // no — counting it as rejected would tell an officer they had failed.
        Assert.Equal([400], HistoryRepository.ReturnedCodes);
    }

    private static int[] AllBucketedCodes() =>
    [
        .. HistoryRepository.PendingCodes,
        .. HistoryRepository.ApprovedCodes,
        .. HistoryRepository.RejectedCodes,
        .. HistoryRepository.ReturnedCodes,
    ];

    [Fact]
    public void No_code_is_counted_in_two_buckets()
    {
        var all = AllBucketedCodes();
        Assert.Equal(all.Length, all.Distinct().Count());
    }

    [Fact]
    public void Ten_is_in_no_bucket_because_the_master_table_defines_it_twice()
    {
        // masterDB_prod.dbo.Mst_AppStatus lists 10 as both "Application
        // Submitted" and "Data Sent to RI", with no column to tell them apart.
        // It still counts toward Total, so the figure is never silently lost.
        Assert.DoesNotContain(10, AllBucketedCodes());
    }

    /// <summary>
    /// Total must be COUNT(*), never the sum of the four buckets — otherwise an
    /// unrecognised code (App_Status 10, above) would be absorbed instead of
    /// showing up as a gap the officer can see.
    ///
    /// Checked as a substring rather than by matching the SQL's layout: the
    /// original version asserted "Total    = COUNT(*)" including four spaces of
    /// alignment, so reformatting the query failed the test.
    /// </summary>
    [Fact]
    public void Total_counts_every_row_not_just_the_bucketed_ones()
    {
        Assert.Contains("COUNT(*)", HistoryRepository.VerdictSums);
        Assert.Contains("Total", HistoryRepository.VerdictSums);
    }
}
