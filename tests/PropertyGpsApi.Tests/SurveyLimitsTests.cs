using System.ComponentModel.DataAnnotations;
using PropertyGpsApi.Infrastructure.Options;
using PropertyGpsApi.Services;
using Xunit;

namespace PropertyGpsApi.Tests;

/// <summary>
/// How many completed surveys may wait on a phone, and how many applications one RI may hold,
/// are server settings (Survey:MaxCompletedOffline, Survey:MaxAllottedPerOfficer; user,
/// 2026-10-28). The allotment limit reaches USP_IU_Architect_AssignedApp as @MaxPending (db/45).
/// </summary>
public sealed class SurveyLimitsTests
{
    [Fact]
    public void Default_to_todays_limits()
    {
        var options = new SurveyOptions();
        Assert.Equal(5, options.MaxCompletedOffline);
        Assert.Equal(2, options.MaxAllottedPerOfficer);
        Assert.False(options.UnassignOnSignOut, "signing out unassigns nothing until it is switched on");
    }

    [Fact]
    public void The_switch_is_named_as_the_phone_reads_it()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new PropertyGpsApi.Models.WardSyncResponse { UnassignOnSignOut = true });
        Assert.Contains("\"unassignOnSignOut\":true", json);
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(50, 20, true)]
    [InlineData(0, 2, false)]
    [InlineData(51, 2, false)]
    [InlineData(5, 0, false)]
    [InlineData(5, 21, false)]
    public void Are_kept_in_range(int offline, int allotted, bool valid)
    {
        var options = new SurveyOptions { MaxCompletedOffline = offline, MaxAllottedPerOfficer = allotted };
        Assert.Equal(valid, Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true));
    }

    [Fact]
    public void The_procedure_gets_the_limit_when_it_takes_it()
    {
        var p = ApplicationService.AssignParameters(113, 7, 117, "RI", assign: true, maxPending: 3);
        Assert.Contains("MaxPending", p.ParameterNames);
        Assert.Equal(3, p.Get<int>("@MaxPending"));
    }

    /// <summary>Before db/45 the procedure has no @MaxPending, and an unknown parameter fails the call.</summary>
    [Fact]
    public void A_procedure_without_the_parameter_is_called_as_before()
    {
        var p = ApplicationService.AssignParameters(113, 7, 117, "RI", assign: true, maxPending: null);
        Assert.DoesNotContain("MaxPending", p.ParameterNames);
        Assert.Equal(6, p.ParameterNames.Count());
    }

    private static readonly Lazy<string> Script = new(() => File.ReadAllText(
        Path.Combine(RepositoryRoot(), "db", "45_udd_AssignLimitParameter.sql")));

    [Fact]
    public void Db45_adds_the_parameter_last_with_todays_default() =>
        Assert.Matches(@"@IsActive\s+BIT,[^@]*@MaxPending\s+INT\s*=\s*2\s*\)", Script.Value);

    [Fact]
    public void Db45_uses_the_parameter_in_both_counts()
    {
        Assert.Contains("Status='Pending' having count(*)>=@MaxPending)", Script.Value);
        Assert.Contains("Status ='Pending' having count(*)<=@MaxPending)", Script.Value);
    }

    /// <summary>AssignOutcome recognises the refusal by this phrase (QUEUE_FULL).</summary>
    [Fact]
    public void Db45_keeps_the_phrase_the_api_recognises() =>
        Assert.Contains("applications pending in your queue. Please complete them", Script.Value);

    [Fact]
    public void Db45_backs_up_before_it_changes_anything()
    {
        var backup = Script.Value.IndexOf("INSERT INTO dbo.ZZ_PropertyGpsApi_ProcBackup_45", StringComparison.Ordinal);
        var change = Script.Value.IndexOf("CREATE OR ALTER PROCEDURE [dbo].[USP_IU_Architect_AssignedApp]", StringComparison.Ordinal);
        Assert.True(backup > 0 && change > backup);
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "db", "45_udd_AssignLimitParameter.sql")))
                return dir.FullName;

        throw new InvalidOperationException("db/45_udd_AssignLimitParameter.sql not found above the test output folder.");
    }
}
