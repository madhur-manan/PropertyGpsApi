using Microsoft.Extensions.Time.Testing;
using PropertyGpsApi.Common;
using PropertyGpsApi.Features.Properties;

namespace PropertyGpsApi.Tests;

/// <summary>
/// Which month the summary answers for, and which it refuses.
///
/// This was untestable until the clock was injected: the controller read
/// DateTime.Today directly, so "defaults to the current month" could only be
/// checked by waiting for the calendar. The rules also had to be reached
/// through HTTP.
///
/// No database is touched — every case here is rejected before the connection
/// opens, which is the point: a bad month never reaches SQL.
/// </summary>
public class HistorySummaryRangeTests
{
    private static readonly DateTimeOffset Sept2026 =
        new(2026, 9, 21, 10, 30, 0, TimeSpan.Zero);

    private static IHistoryRepository At(DateTimeOffset now) =>
        new HistoryRepository(new ThrowingConnections(), new FakeTimeProvider(now));

    private static async Task<ApiException> Refused(int? year, int? month)
        => await Assert.ThrowsAsync<ApiException>(
            () => At(Sept2026).SummaryForOfficerAsync(11320, year, month, default));

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    [InlineData(-1)]
    [InlineData(99)]
    public async Task An_impossible_month_is_refused_before_any_query(int month)
    {
        // Rejected rather than clamped: a client asking for month 13 has a bug,
        // and silently answering for December would hide it behind plausible
        // figures an officer might act on.
        var refused = await Refused(2026, month);

        Assert.Equal(400, refused.StatusCode);
        Assert.Contains("month", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1999)]
    [InlineData(1)]
    public async Task A_year_before_the_system_existed_is_refused(int year)
        => Assert.Equal(400, (await Refused(year, 9)).StatusCode);

    [Fact]
    public async Task A_year_further_ahead_than_next_year_is_refused()
        => Assert.Equal(400, (await Refused(2028, 9)).StatusCode);

    [Fact]
    public async Task Next_year_is_allowed_because_a_financial_year_straddles_January()
    {
        // Reaches the database, which is what ThrowingConnections proves.
        await Assert.ThrowsAsync<NotSupportedException>(
            () => At(Sept2026).SummaryForOfficerAsync(11320, 2027, 1, default));
    }

    [Fact]
    public async Task No_month_given_means_the_server_current_month_not_the_caller_clock()
    {
        // A phone with a wrong date would otherwise ask for the wrong month and
        // get a confidently empty answer. Passing nulls must be accepted and
        // resolved server-side — proven by it reaching the connection rather
        // than being refused as out of range.
        await Assert.ThrowsAsync<NotSupportedException>(
            () => At(Sept2026).SummaryForOfficerAsync(11320, null, null, default));
    }

    [Fact]
    public async Task A_clock_in_a_year_the_range_check_would_reject_still_works()
    {
        // Guards the interaction between the default and the bound: if "today"
        // is 2031 the default year must still pass its own validation.
        var future = new DateTimeOffset(2031, 3, 4, 0, 0, 0, TimeSpan.Zero);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => At(future).SummaryForOfficerAsync(11320, null, null, default));
    }

    /// <summary>
    /// Opening a connection here means validation let the call through. These
    /// tests never want a database, so reaching one is the signal.
    /// </summary>
    private sealed class ThrowingConnections : PropertyGpsApi.Infrastructure.Data.ISqlConnectionFactory
    {
        public Task<Microsoft.Data.SqlClient.SqlConnection> OpenAsync(
            PropertyGpsApi.Infrastructure.Data.DbTarget target, CancellationToken ct = default)
            => throw new NotSupportedException("validation passed; a query was attempted");
    }
}
