using PropertyGpsApi.Services;
using Xunit;

namespace PropertyGpsApi.Tests;

/// <summary>
/// A survey belongs in the RI's history when they created the application's officer row OR
/// they have their own status row for it (user, 2026-10-30). USP_IU_BtoA_MainApp_Officer
/// updates an existing row and leaves CBy as its creator - a QC user bulk-created 106 rows on
/// the test server, so RI 14's submit of application 119 (and five others) vanished from
/// history. Checked read-only on the test server: officer 14 now gets 044, 091, 103, 104, 119.
/// </summary>
public sealed class HistoryOwnershipTests
{
    [Fact]
    public void An_ri_owns_a_survey_by_their_own_status_row_as_well_as_by_creating_the_row()
    {
        var rule = HistoryService.SurveyedByOfficerSql;
        Assert.StartsWith("mao.Ofcr_ApplicationDisplayId IN (", rule);
        Assert.Contains("WHERE own.CBy = @officerId", rule);
        Assert.Contains("UNION", rule);
        Assert.Contains("FROM dbo.BtoA_StatusDetail_Officer mine", rule);
        Assert.Contains("mine.CBy = @officerId AND mine.CRole IN (116, 117)", rule);
    }

    /// <summary>
    /// A per-row "OR EXISTS" against the status table timed out on the test server (no index
    /// on its application number; 40k x 63k rows) and Property History failed to load.
    /// </summary>
    [Fact]
    public void The_rule_gathers_the_officers_applications_first_instead_of_probing_per_row()
    {
        Assert.DoesNotContain("EXISTS", HistoryService.SurveyedByOfficerSql);
        Assert.DoesNotContain("= mao.", HistoryService.SurveyedByOfficerSql);
    }

    [Fact]
    public void The_list_the_count_the_days_and_the_months_all_use_the_same_rule()
    {
        foreach (var sql in new[] { HistoryService.PageSql, HistoryService.CountSql, HistoryService.DailySql, HistoryService.MonthsSql })
        {
            Assert.Contains(HistoryService.SurveyedByOfficerSql, sql);
            Assert.DoesNotContain("WHERE mao.CBy = @officerId", sql);
            Assert.DoesNotContain("WHERE CBy = @officerId", sql);
        }
    }

    /// <summary>The count's rule names the row "mao", so its FROM must give that alias.</summary>
    [Fact]
    public void The_count_aliases_its_table_for_the_rule() =>
        Assert.Contains("FROM dbo.BtoA_MainApp_Officer mao WITH (NOLOCK)", HistoryService.CountSql);
}
