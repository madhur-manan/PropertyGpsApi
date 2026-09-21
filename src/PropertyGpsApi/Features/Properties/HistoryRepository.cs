using Dapper;
using PropertyGpsApi.Features.Properties.Dtos;
using PropertyGpsApi.Infrastructure.Data;

namespace PropertyGpsApi.Features.Properties;

public interface IHistoryRepository
{
    Task<IReadOnlyList<HistoryEntryDto>> ForOfficerAsync(
        long officerId, int start, int range, CancellationToken ct);

    Task<int> CountForOfficerAsync(long officerId, CancellationToken ct);
}

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
internal sealed class HistoryRepository(ISqlConnectionFactory connections) : IHistoryRepository
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
            QcActedOn     = qc.CDte
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

    public async Task<IReadOnlyList<HistoryEntryDto>> ForOfficerAsync(
        long officerId, int start, int range, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(DbTarget.B2A, ct);
        var rows = await connection.QueryAsync<HistoryEntryDto>(
            new CommandDefinition(PageSql, new { officerId, start, range, qcRole = QcRoleId },
                commandTimeout: 60, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<int> CountForOfficerAsync(long officerId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(DbTarget.B2A, ct);
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(CountSql, new { officerId }, cancellationToken: ct));
    }
}
