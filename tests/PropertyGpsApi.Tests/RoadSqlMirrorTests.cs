using System.Text.RegularExpressions;
using PropertyGpsApi.Services;

namespace PropertyGpsApi.Tests;

/// <summary>
/// The SQL the submit and the history page send themselves cannot run without a database,
/// so db/27_udd_Check_RoadScenarios.sql runs it against UDD_KHATABTOA_TEST, word for word,
/// and checks what it does there: superseding a resubmission's earlier rows and documents
/// (D5 / D22), clearing a notice (D22), healing Ofcr_App_Id (D33), the road row check (N3),
/// the history line (D23). This holds the two copies in step: change a constant and this
/// fails until db/27 is changed and re-run.
///
/// Compared with all whitespace collapsed. The one rewrite: Dapper expands "IN @live" from
/// a list, which T-SQL writes as "IN (SELECT v FROM @live)".
/// </summary>
public sealed class RoadSqlMirrorTests
{
    private static readonly Lazy<string> Script = new(() => Normalise(File.ReadAllText(
        Path.Combine(RepositoryRoot(), "db", "27_udd_Check_RoadScenarios.sql"))));

    public static TheoryData<string, string> Statements => new()
    {
        { nameof(VerificationSubmitService.DeclaredRoadsSql), VerificationSubmitService.DeclaredRoadsSql },
        { nameof(VerificationSubmitService.HealAppIdSql), VerificationSubmitService.HealAppIdSql },
        { nameof(VerificationSubmitService.HeldSql), VerificationSubmitService.HeldSql },
        { nameof(VerificationSubmitService.ClearNoticeSql), VerificationSubmitService.ClearNoticeSql },
        { nameof(VerificationSubmitService.SupersedeRoadRowsSql), VerificationSubmitService.SupersedeRoadRowsSql },
        { nameof(VerificationSubmitService.SupersedeDocumentsSql), VerificationSubmitService.SupersedeDocumentsSql },
        { nameof(VerificationSubmitService.EnsureDocumentSql), VerificationSubmitService.EnsureDocumentSql },
        { nameof(HistoryService.RoadCountSql), HistoryService.RoadCountSql },
        { nameof(HistoryService.StreetNameSql), HistoryService.StreetNameSql },
    };

    [Theory]
    [MemberData(nameof(Statements))]
    public void Db27_runs_the_statement_word_for_word(string name, string sql)
    {
        var expected = Normalise(sql.Replace("IN @live", "IN (SELECT v FROM @live)", StringComparison.Ordinal));
        Assert.True(Script.Value.Contains(expected, StringComparison.Ordinal),
            $"db/27_udd_Check_RoadScenarios.sql no longer contains {name} as the API sends it. "
            + "Copy the new text into the script, re-run it on UDD_KHATABTOA_TEST, and update its expectations.");
    }

    /// <summary>The road-row supersede must not depend on the row id the procedure returns (B2 on live).</summary>
    [Fact]
    public void Superseding_keys_on_the_write_time_not_on_returned_row_ids()
    {
        Assert.Contains("ISNULL(UDte, CDte) < @startedAt", VerificationSubmitService.SupersedeRoadRowsSql);
        Assert.DoesNotContain("@written", VerificationSubmitService.SupersedeRoadRowsSql);
    }

    /// <summary>D23: the history counts only this officer's current, not-deleted roads.</summary>
    [Theory]
    [InlineData("rd.CBy = @officerId")]
    [InlineData("rd.isActive = 1")]
    [InlineData("ISNULL(rd.Ofcr_Correction_Type_Id, 0) <> 2")]
    public void History_road_count_reads_only_current_roads_of_the_officer(string predicate) =>
        Assert.Contains(predicate, HistoryService.RoadCountSql);

    private static string Normalise(string sql) => Regex.Replace(sql, @"\s+", " ").Trim();

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "db", "27_udd_Check_RoadScenarios.sql")))
                return dir.FullName;

        throw new InvalidOperationException("db/27_udd_Check_RoadScenarios.sql not found above the test output folder.");
    }
}
