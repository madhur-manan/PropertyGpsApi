namespace PropertyGpsApi.Tests;

/// <summary>
/// db/44: the ward sync offers every workable application (one per App_Id) when the phone sends
/// @OnePerEpid = 0, and keeps the newest-per-EPID rule for phones that do not (app 1.2.0). The
/// procedure cannot run without a database, so its text is held to that here.
/// </summary>
public sealed class WardSyncEveryApplicationScriptTests
{
    private static readonly Lazy<string> Script = new(() => File.ReadAllText(
        Path.Combine(RepositoryRoot(), "db", "44_udd_WardSyncEveryApplication.sql")));

    [Fact]
    public void The_new_parameter_is_last_and_defaults_to_one_per_epid() =>
        Assert.Matches(@"@RefreshAppIds\s+nvarchar\(max\)\s*=\s*NULL,[^@]*@OnePerEpid\s+bit\s*=\s*1\s+AS", Script.Value);

    [Fact]
    public void The_epid_rule_applies_only_when_asked_for() =>
        Assert.Contains("WHERE @OnePerEpid = 0 OR x.rn = 1;", Script.Value);

    /// <summary>A JOIN could give one application two rows in #ward, whose key is App_Id.</summary>
    [Fact]
    public void The_ward_list_matches_the_metadata_with_exists() =>
        Assert.Contains("AND EXISTS (SELECT 1 FROM dbo.BtoA_EPIDMetaData md WITH (NOLOCK)", Script.Value);

    [Fact]
    public void Only_waiting_and_returned_applications_are_offered() =>
        Assert.Contains("AND ap.App_Status IN (10, 400)", Script.Value);

    [Fact]
    public void It_backs_up_before_it_changes_anything()
    {
        var backup = Script.Value.IndexOf("INSERT INTO dbo.ZZ_PropertyGpsApi_ProcBackup_44", StringComparison.Ordinal);
        var change = Script.Value.IndexOf("CREATE OR ALTER PROCEDURE [dbo].[USP_S_GpsWardSync]", StringComparison.Ordinal);
        Assert.True(backup > 0 && change > backup);
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "db", "44_udd_WardSyncEveryApplication.sql")))
                return dir.FullName;

        throw new InvalidOperationException("db/44_udd_WardSyncEveryApplication.sql not found above the test output folder.");
    }
}
