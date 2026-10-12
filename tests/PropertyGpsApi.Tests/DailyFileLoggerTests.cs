using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using PropertyGpsApi.Infrastructure.Logging;

namespace PropertyGpsApi.Tests;

/// <summary>
/// The daily log file: a line is in the file the moment it is logged (the IIS stdout log held
/// output back until the site stopped), one file per day, old files pruned, and nothing here
/// can ever throw into the request that is logging.
/// </summary>
public sealed class DailyFileLoggerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pgps-log-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 12, 13, 59, 10, TimeSpan.Zero));

    public DailyFileLoggerTests() => _clock.SetLocalTimeZone(TimeZoneInfo.Utc);

    private DailyFileLoggerProvider Provider(int retainDays = 30) =>
        new(new FileLogOptions { Path = _folder, RetainDays = retainDays }, _clock);

    /// <summary>Read the way an operator does, while the API still has the file open.</summary>
    private string Read(string name)
    {
        using var stream = new FileStream(Path.Combine(_folder, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(stream).ReadToEnd();
    }

    [Fact]
    public void A_line_is_in_the_file_as_soon_as_it_is_logged()
    {
        using var provider = Provider();
        provider.CreateLogger("PropertyGpsApi.Infrastructure.Security.SmsGatewayOtpSender")
            .LogInformation("OTP dispatched to {MobileSuffix} via the SMS gateway", "2755");

        Assert.Equal(
            "2026-10-12 13:59:10 +00:00 info: PropertyGpsApi.Infrastructure.Security.SmsGatewayOtpSender[0] "
            + "OTP dispatched to 2755 via the SMS gateway" + Environment.NewLine,
            Read("app-20261012.log"));
    }

    [Fact]
    public void A_new_day_starts_a_new_file()
    {
        using var provider = Provider();
        var log = provider.CreateLogger("Test");

        log.LogWarning("before midnight");
        _clock.Advance(TimeSpan.FromHours(11));
        log.LogWarning("after midnight");

        Assert.Contains("before midnight", Read("app-20261012.log"));
        Assert.Contains("after midnight", Read("app-20261013.log"));
        Assert.DoesNotContain("after midnight", Read("app-20261012.log"));
    }

    /// <summary>One entry per line, so searching for a word returns the whole entry.</summary>
    [Fact]
    public void A_multi_line_message_and_its_exception_stay_on_one_line()
    {
        using var provider = Provider();
        provider.CreateLogger("Test").LogError(new InvalidOperationException("boom"), "first\r\nsecond");

        var text = Read("app-20261012.log");
        Assert.Single(text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("fail: Test[0] first | second System.InvalidOperationException: boom", text);
    }

    [Fact]
    public void Old_log_files_are_pruned_and_everything_else_is_left_alone()
    {
        Directory.CreateDirectory(_folder);
        var old = _clock.GetLocalNow().LocalDateTime.AddDays(-31);
        var recent = _clock.GetLocalNow().LocalDateTime.AddDays(-29);
        foreach (var (name, when) in new[]
                 {
                     ("app-20260910.log", old), ("stdout_20260910000000_1.log", old),
                     ("app-20260913.log", recent), ("stdout_20260913000000_2.log", recent),
                     ("notes.txt", old),
                 })
        {
            File.WriteAllText(Path.Combine(_folder, name), "x");
            File.SetLastWriteTime(Path.Combine(_folder, name), when);
        }

        using var provider = Provider();
        provider.CreateLogger("Test").LogInformation("start");

        Assert.Equal(
            ["app-20260913.log", "app-20261012.log", "notes.txt", "stdout_20260913000000_2.log"],
            Directory.GetFiles(_folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    /// <summary>A folder that cannot be created (a file is in its way) costs the log, never the request.</summary>
    [Fact]
    public void A_folder_that_cannot_be_written_never_throws()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_folder)!);
        File.WriteAllText(_folder, "a file where the folder should be");
        try
        {
            using var provider = Provider();
            var log = provider.CreateLogger("Test");
            log.LogError("first");
            log.LogError("second");
        }
        finally
        {
            File.Delete(_folder);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}
