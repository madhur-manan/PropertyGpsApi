using Dapper;
using PropertyGpsApi.Models;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Common;

using PropertyGpsApi.Interfaces;

namespace PropertyGpsApi.Services;


/// <summary>
/// The surveys this officer has already submitted.
///
/// Not backed by USP_S_BtoA_GpsDashData_v1 at level 3, despite that being the branch
/// labelled "Rejected and completed". Two reasons, both verified against the server:
/// in UDD_KHATABTOA_TEST it names columns the rename removed and fails outright, and in
/// KhataBtoA_prod it runs but can never return a row, because its filter reads
///
///     where SD.Status_Id not in (11,200) and SD.Status_Id in (11,200)
///
/// which is unsatisfiable. Rather than rewrite a procedure shared with the QC and JC
/// screens, this is a plain read over the officer tables - scoped to one officer, so it
/// is ours to own.
/// </summary>
internal sealed class HistoryService(
    ISqlConnectionFactory connections,
    TimeProvider clock) : IHistoryService
{
    /// <summary>
    /// masterDB_prod.dbo.Mst_Roles: 116 Case Worker, 117 RI, 118 QC, 125 Joint
    /// Commissioner. BtoA_StatusDetail_Officer is a shared transition log that every
    /// role writes to, so the newest row on an application is usually the officer's
    /// own submit remark, not QC's.
    ///
    /// The yellow "QC Remarks" panel in the app must show what QC said. Reading the
    /// newest row of any role would print the officer their own note back under QC's
    /// name, which is worse than showing nothing - so the QC lookup is scoped to this
    /// role explicitly.
    /// </summary>
    private const int QcRoleId = 118;

    private const string PageSql = """
        SELECT
            ApplicationId = mao.Ofcr_ApplicationDisplayId,
            Epid          = mao.Ofcr_MotherEPID,
            SasId         = mao.Ofcr_SsaId,
            ZoneId        = md.MD_ZoneId,
            WardId        = md.MD_WardId,
            AppStatus     = ap.App_Status,
            SubmittedOn   = COALESCE(mao.UDte, mao.CDte),
            RoadCount     = (SELECT COUNT(*) FROM dbo.BtoA_SiteRoadDetails_Officer rd WITH (NOLOCK)
                             WHERE rd.Ofcr_ApplicationDisplayId = mao.Ofcr_ApplicationDisplayId),
            LastRemark    = sd.Status_Remark,
            LastStatusId  = sd.Status_Id,
            AppliedOn     = ap.App_Cdte,
            OwnerName     = own.Names,
            OwnerMobile   = own.Numbers,
            QcRemark      = qc.Status_Remark,
            QcOutcome     = qc.Status_Value,
            QcActedOn     = qc.CDte,
            -- The street the officer navigates by, taken from the roads they
            -- actually submitted. ActualRoadName is what they typed when the
            -- citizen's declaration was wrong, so it wins over RoadName.
            StreetName    = (SELECT TOP (1) COALESCE(NULLIF(LTRIM(RTRIM(rn.Ofcr_ActualRoadName)), N''),
                                                     NULLIF(LTRIM(RTRIM(rn.Ofcr_RoadName)), N''))
                             FROM dbo.BtoA_SiteRoadDetails_Officer rn WITH (NOLOCK)
                             WHERE rn.Ofcr_ApplicationDisplayId = mao.Ofcr_ApplicationDisplayId
                               AND COALESCE(NULLIF(LTRIM(RTRIM(rn.Ofcr_ActualRoadName)), N''),
                                            NULLIF(LTRIM(RTRIM(rn.Ofcr_RoadName)), N'')) IS NOT NULL
                             ORDER BY rn.Ofcr_SiteRoadRowID)
        FROM dbo.BtoA_MainApp_Officer mao WITH (NOLOCK)
        LEFT JOIN dbo.BtoAMainApp ap WITH (NOLOCK)
               ON ap.App_DisplayId = mao.Ofcr_ApplicationDisplayId
        LEFT JOIN dbo.BtoA_EPIDMetaData md WITH (NOLOCK)
               ON md.MD_APP_ID = ap.App_Id AND md.MD_MotherEPID = ap.App_MotherEPID
        OUTER APPLY (
            SELECT TOP (1) s.Status_Remark, s.Status_Id
            FROM dbo.BtoA_StatusDetail_Officer s WITH (NOLOCK)
            WHERE s.Ofcr_ApplicationDisplayId = mao.Ofcr_ApplicationDisplayId
              AND s.Status_Active = 1
            ORDER BY s.CDte DESC
        ) sd
        OUTER APPLY (
            SELECT TOP (1) q.Status_Remark, q.Status_Value, q.Status_Id, q.CDte
            FROM dbo.BtoA_StatusDetail_Officer q WITH (NOLOCK)
            WHERE q.Ofcr_ApplicationDisplayId = mao.Ofcr_ApplicationDisplayId
              AND q.Status_Active = 1
              AND q.CRole = @qcRole
            ORDER BY q.CDte DESC
        ) qc
        OUTER APPLY (
            SELECT Names   = STRING_AGG(NULLIF(LTRIM(RTRIM(o.Own_OwnerName)), N''), N', '),
                   Numbers = STRING_AGG(NULLIF(LTRIM(RTRIM(o.Own_Mobile)), N''), N', ')
            FROM dbo.BtoA_OwnerDetails o WITH (NOLOCK)
            WHERE o.Own_App_Id = ap.App_Id AND ISNULL(o.own_active, 1) = 1
        ) own
        WHERE mao.CBy = @officerId
        ORDER BY COALESCE(mao.UDte, mao.CDte) DESC
        OFFSET @start ROWS FETCH NEXT @range ROWS ONLY;
        """;

    private const string CountSql = """
        SELECT COUNT(*) FROM dbo.BtoA_MainApp_Officer WITH (NOLOCK) WHERE CBy = @officerId;
        """;

    public async Task<HistoryPage> PageForOfficerAsync(
        long officerId, int start, int range, CancellationToken ct)
    {
        // Clamped here, not at the edge, so every caller gets the same bounds.
        // The upper bound exists because the page is unfiltered - an officer
        // with a thousand surveys asking for all of them would hold a
        // connection open building one response nothing renders.
        if (start < 0) start = 0;
        range = Math.Clamp(range, 1, 200);

        // One connection for the page and its total. They used to be two calls
        // on two connections, which left a window where a survey submitted
        // between them produced a page whose count did not match its own total.
        await using var connection = await connections.OpenAsync(DbTarget.B2A, ct);

        var rows = (await connection.QueryAsync<HistoryEntryDto>(
            new CommandDefinition(PageSql, new { officerId, start, range, qcRole = QcRoleId },
                commandTimeout: 60, cancellationToken: ct))).AsList();

        var total = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(CountSql, new { officerId }, cancellationToken: ct));

        return new HistoryPage(rows, total, start, range);
    }

    /// <summary>
    /// The verdict buckets — the one place the SQL side defines them.
    ///
    /// Held as data rather than only as SQL text so the tests can assert on the
    /// codes themselves. They previously read the generated SQL back by
    /// reflection and regexed it, which made re-indenting the query a test
    /// failure and let a rename become a NullReferenceException.
    ///
    /// These MUST agree with AppStatus.verdict in the Flutter app
    /// (lib/models/single_site/single_site_property.dart), or a card's badge and
    /// the counter above it disagree about the same survey.
    ///
    /// 10 is deliberately in no bucket: masterDB_prod.dbo.Mst_AppStatus defines
    /// it twice, with no column to tell the two meanings apart. A code in no
    /// bucket still counts toward Total, so the four can sum to less than the
    /// total rather than a stray code being quietly filed as "approved".
    /// </summary>
    internal static readonly int[] PendingCodes = [13];

    /// 14 QC approved, 30 approved by RI, 150 JC approved,
    /// 200 Commissioner approved, 300 payment done.
    internal static readonly int[] ApprovedCodes = [14, 30, 150, 200, 300];

    /// 12 rejected QC, 25 rejected by RI, 110 JC rejected.
    internal static readonly int[] RejectedCodes = [12, 25, 110];

    /// Returned for re-verification. Its own bucket, not a rejection — the
    /// officer has work to redo, which is different from being told no.
    internal static readonly int[] ReturnedCodes = [400];

    /// <summary>
    /// Total is COUNT(*), not the sum of the four buckets, so an unrecognised
    /// code shows up as a gap between them rather than being absorbed.
    /// </summary>
    internal static readonly string VerdictSums = string.Join(",\n", [
        "        Total    = COUNT(*)",
        $"        Pending  = {Bucket(PendingCodes)}",
        $"        Approved = {Bucket(ApprovedCodes)}",
        $"        Rejected = {Bucket(RejectedCodes)}",
        $"        Returned = {Bucket(ReturnedCodes)}",
    ]);

    /// <summary>
    /// Codes are inlined rather than parameterised on purpose: they are a fixed
    /// part of the query's shape, not user input, so inlining keeps one cached
    /// plan instead of one per bucket size.
    /// </summary>
    private static string Bucket(int[] codes) =>
        codes.Length == 1
            ? $"SUM(CASE WHEN ap.App_Status = {codes[0]} THEN 1 ELSE 0 END)"
            : $"SUM(CASE WHEN ap.App_Status IN ({string.Join(", ", codes)}) THEN 1 ELSE 0 END)";

    private static readonly string DailySql = $"""
        SELECT
                Day = DAY(COALESCE(mao.UDte, mao.CDte)),
        {VerdictSums}
        FROM dbo.BtoA_MainApp_Officer mao WITH (NOLOCK)
        LEFT JOIN dbo.BtoAMainApp ap WITH (NOLOCK)
               ON ap.App_DisplayId = mao.Ofcr_ApplicationDisplayId
        WHERE mao.CBy = @officerId
          AND COALESCE(mao.UDte, mao.CDte) >= @from
          AND COALESCE(mao.UDte, mao.CDte) <  @to
        GROUP BY DAY(COALESCE(mao.UDte, mao.CDte))
        ORDER BY 1;
        """;

    /// <summary>
    /// Newest first. Built from the same submitted-on date the daily breakdown
    /// groups by, so the picker can never offer a month that then reads zero.
    /// </summary>
    private const string MonthsSql = """
        SELECT DISTINCT
            Ym = FORMAT(COALESCE(mao.UDte, mao.CDte), 'yyyy-MM')
        FROM dbo.BtoA_MainApp_Officer mao WITH (NOLOCK)
        WHERE mao.CBy = @officerId
          AND COALESCE(mao.UDte, mao.CDte) IS NOT NULL
        ORDER BY 1 DESC;
        """;

    public async Task<VerificationSummaryDto> SummaryForOfficerAsync(
        long officerId, int? year, int? month, CancellationToken ct)
    {
        // The server's clock, not the caller's. A phone with a wrong date would
        // otherwise ask for the wrong month and get a confidently empty answer.
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var y = year ?? today.Year;
        var m = month ?? today.Month;

        // Rejected rather than clamped. A client asking for month 13 has a bug,
        // and silently answering for December would hide it behind plausible
        // figures an officer might act on.
        if (m < 1 || m > 12)
            throw ApiException.BadRequest("'month' must be between 1 and 12.");
        if (y < 2000 || y > today.Year + 1)
            throw ApiException.BadRequest("'year' is out of range.");

        var from = new DateTime(y, m, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var to = from.AddMonths(1);

        await using var connection = await connections.OpenAsync(DbTarget.B2A, ct);

        var days = (await connection.QueryAsync<DailyRow>(
            new CommandDefinition(DailySql, new { officerId, from, to },
                commandTimeout: 60, cancellationToken: ct))).AsList();

        var months = (await connection.QueryAsync<string>(
            new CommandDefinition(MonthsSql, new { officerId },
                commandTimeout: 60, cancellationToken: ct))).AsList();

        return new VerificationSummaryDto
        {
            Year = y,
            Month = m,
            Total = days.Sum(d => d.Total),
            Pending = days.Sum(d => d.Pending),
            Approved = days.Sum(d => d.Approved),
            Rejected = days.Sum(d => d.Rejected),
            Returned = days.Sum(d => d.Returned),
            Daily = days.Select(d => new DailyVerificationDto
            {
                Day = d.Day,
                Date = new DateTime(y, m, d.Day).ToString("yyyy-MM-dd"),
                Total = d.Total,
                Pending = d.Pending,
                Approved = d.Approved,
                Rejected = d.Rejected,
                Returned = d.Returned,
            }).ToList(),
            MonthsWithWork = months,
        };
    }

    private sealed class DailyRow
    {
        public int Day { get; init; }
        public int Total { get; init; }
        public int Pending { get; init; }
        public int Approved { get; init; }
        public int Rejected { get; init; }
        public int Returned { get; init; }
    }
}
