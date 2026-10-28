using PropertyGpsApi.Services;

namespace PropertyGpsApi.Tests;

/// <summary>
/// Since db/44 one EPID can carry several applications on the phones, each its own record.
/// What the API keys on must then be the application, never the EPID alone.
/// </summary>
public sealed class OneRecordPerApplicationTests
{
    [Theory]
    [InlineData("38-521-372", "38-521-372", true)]
    [InlineData(" 38-521-372 ", "38-521-372", true)]
    [InlineData("38-521-372-a", "38-521-372-A", true)]
    [InlineData("38-521-372", "38-521-373", false)]
    [InlineData("", "38-521-372", false)]
    [InlineData(null, "38-521-372", false)]
    public void A_survey_is_stored_only_under_its_own_applications_epid(string? sent, string stored, bool same) =>
        Assert.Equal(same, VerificationSubmitService.SameEpid(sent, stored));

    [Fact]
    public void The_submit_reads_the_applications_epid_with_its_id()
    {
        var source = File.ReadAllText(SourcePath("Services", "VerificationSubmitService.cs"));
        Assert.Contains("SELECT App_Id AS AppId, App_MotherEPID AS Epid FROM BtoAMainApp WHERE App_DisplayId = @d AND App_Active = 1", source);
        Assert.Contains("if (!SameEpid(request.Epid, app!.Epid))", source);
    }

    [Fact]
    public void A_history_entry_carries_its_application_id() =>
        Assert.Contains("AppId         = ap.App_Id,", HistoryService.PageSql);

    /// <summary>Several applications share an EPID's photo folder: one answer, the newest.</summary>
    [Fact]
    public void A_photos_ward_is_read_from_the_newest_active_application() =>
        Assert.EndsWith("ORDER BY ap.App_Id DESC;", MediaAccessService.Sql.Trim());

    private static string SourcePath(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine([dir.FullName, "src", "PropertyGpsApi", .. parts]);
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException("src/PropertyGpsApi not found above the test output folder.");
    }
}
