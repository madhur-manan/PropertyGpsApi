using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Common;
using PropertyGpsApi.Models;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Infrastructure.Options;
using PropertyGpsApi.Infrastructure.Storage;

using PropertyGpsApi.Interfaces;

namespace PropertyGpsApi.Services;


/// <summary>
/// Writes one completed survey.
///
/// Three legacy procedures plus two of our own, in a single transaction. All-or-nothing
/// is the right shape here: the status procedure moves BtoAMainApp.App_Status, so a
/// half-written survey would leave an application carrying officer data with no status
/// transition - invisible to the workflow and indistinguishable from "not yet surveyed".
///
/// Media is written before this is called. A survey that fails before its commit is
/// attempted discards the files stored for it: nothing can point at them, and every retry
/// of a refused survey used to leave another full set behind. Once the commit has been
/// attempted they are never removed - its outcome may be unknown, and a committed row
/// pointing at a photograph that was deleted is destroyed evidence for a tax assessment,
/// which the device, having marked the record synced, would never send again.
/// </summary>
internal sealed class VerificationSubmitService(
    ISqlConnectionFactory connections,
    IMediaStore media,
    IOptions<StoredProcedureOptions> procedures,
    ILogger<VerificationSubmitService> logger) : IVerificationSubmitService
{
    public async Task<SubmitVerificationResponse> SubmitAsync(
        SubmitVerificationRequest request,
        IReadOnlyDictionary<string, string> mediaUrls,
        long officerId,
        int roleId,
        string? officerMobile,
        CancellationToken ct)
    {
        var commit = new CommitTracker();
        try
        {
            return await WriteSurveyAsync(request, mediaUrls, officerId, roleId, officerMobile, commit, ct);
        }
        catch when (!commit.Attempted)
        {
            // Not CancellationToken ct: a phone that gave up mid-request cancels it, and
            // its files must go all the same.
            await media.DiscardAsync(mediaUrls.Values, CancellationToken.None);
            throw;
        }
    }

    /// <summary>Set just before the commit, so a failure can tell whether anything may have been saved.</summary>
    private sealed class CommitTracker
    {
        public bool Attempted { get; set; }
    }

    private async Task<SubmitVerificationResponse> WriteSurveyAsync(
        SubmitVerificationRequest request,
        IReadOnlyDictionary<string, string> mediaUrls,
        long officerId,
        int roleId,
        string? officerMobile,
        CommitTracker commit,
        CancellationToken ct)
    {
        var sp = procedures.Value;
        var site = request.SiteDetails;

        await using var connection = await connections.OpenAsync(DbTarget.B2A, ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);

        try
        {
            // Serialise every submit for this one application, across all API instances.
            // Two devices, or one device retrying twice in parallel, would otherwise
            // interleave the road writes.
            await TakeApplicationLockAsync(connection, transaction, request.ApplicationId, ct);

            // The procedures silently do nothing for an application they cannot find, and
            // USP_IU_BtoA_MainApp_Officer reports that only in its first result set. Reading
            // App_Id up front turns "unknown application" into one clear answer, and we need
            // the id anyway for the road and status calls.
            var appId = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT App_Id FROM BtoAMainApp WHERE App_DisplayId = @d AND App_Active = 1",
                new { d = request.ApplicationId }, transaction, 30, CommandType.Text, cancellationToken: ct));

            if (appId is null)
                throw ApiException.Unprocessable(
                    "This application is not on the server. Nothing was saved.",
                    ApiErrorCodes.ApplicationNotFound, recoverable: true);

            // Every road row id must be one of this application's own (defect N3). A row of
            // another application, or a Site_Id sent in its place, used to be filed as given.
            var declared = (await connection.QueryAsync<DeclaredRoadRow>(new CommandDefinition(
                DeclaredRoadsSql, new { appId = appId.Value }, transaction, 30, CommandType.Text,
                cancellationToken: ct))).AsList();
            var (rowErrors, rowWarnings) = CheckRoadRows(site.RoadDetails, declared);
            if (rowErrors.Count > 0)
                throw new SubmitRejectedException(
                    rowErrors, ApiErrorCodes.UnknownRoadRow,
                    "Some roads do not belong to this application. Fetch the property again. Nothing was saved.");

            await WriteApplicationAsync(connection, transaction, sp, request, site, appId.Value, officerId, roleId, ct);
            var (roadsStored, unverifiedRoads) = await WriteRoadsAsync(
                connection, transaction, sp, request, site, mediaUrls, appId.Value, officerId, roleId, ct);
            await WriteExtrasAsync(connection, transaction, request, mediaUrls, ct);
            await WriteStatusAsync(connection, transaction, sp, request, appId.Value, officerId, roleId, officerMobile, ct);
            var assignmentClosed = await CompleteAssignmentAsync(
                connection, transaction, appId.Value, officerId, roleId, ct);

            // Read the status back rather than reporting a constant. The procedure decides
            // it from the role, so a copy here could drift from what was actually written
            // and the response would confidently state something untrue.
            var appStatus = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT App_Status FROM BtoAMainApp WHERE App_Id = @id",
                new { id = appId.Value }, transaction, 30, CommandType.Text, cancellationToken: ct));

            commit.Attempted = true;
            await transaction.CommitAsync(ct);

            logger.LogInformation(
                "Verification stored for {ApplicationId} by officer {OfficerId}: {Roads} road(s) "
                + "({Unverified} unrecognised), {Media} file(s), assignment {Assignment}",
                request.ApplicationId, officerId, roadsStored, unverifiedRoads.Count, mediaUrls.Count,
                assignmentClosed ? "completed" : "not held by this officer");

            return new SubmitVerificationResponse
            {
                ApplicationId = request.ApplicationId,
                Epid = request.Epid,
                AppStatus = appStatus ?? 0,
                RoadsStored = roadsStored,
                RoadsUnverified = unverifiedRoads.Count,
                MediaStored = mediaUrls.Count,
                StoredUtc = DateTimeOffset.UtcNow,
                Warnings = [.. unverifiedRoads, .. rowWarnings]
            };
        }
        catch
        {
            await SafeRollbackAsync(transaction);
            throw;
        }
    }

    /// <summary>
    /// Closes the submitting officer's own assignment for this application.
    ///
    /// Nothing on the submit path did this, so the assignment stayed Pending forever and
    /// USP_IU_Architect_AssignedApp - which refuses a third pending assignment - blocked
    /// every officer after their second survey. Inside the transaction on purpose: a survey
    /// that rolls back must leave the officer still holding the application.
    ///
    /// Our own procedure (db/19_udd_GpsCompleteAssignment.sql), mirroring the update the
    /// legacy workflow performs, because USP_IU_Architect_AssignedApp can only unassign.
    /// Returns false when this officer held no pending assignment - a legitimate case (a
    /// survey completed by someone other than the holder), so it is logged, not an error.
    /// </summary>
    private static async Task<bool> CompleteAssignmentAsync(
        SqlConnection connection, SqlTransaction transaction, int appId, long officerId, int roleId,
        CancellationToken ct)
    {
        var p = new DynamicParameters();
        p.Add("@AppId", appId, DbType.Int32);
        p.Add("@OfficerId", officerId, DbType.Int64);
        p.Add("@RoleId", roleId, DbType.Int32);

        var rows = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "dbo.USP_U_BtoA_GpsCompleteAssignment", p, transaction, 30,
            CommandType.StoredProcedure, cancellationToken: ct));

        return rows > 0;
    }

    // App_Status after a submit is 13, "Data Received from RI", and the status procedure
    // derives that from the role rather than taking it from us. It is not duplicated here:
    // the value is read back above, so the response always reports what was really written.
    //
    // Mst_AppStatus also defines 30 "Approved By RI" and 25 "Rejected By RI". BBMP have
    // confirmed those are reserved for a future flow and nothing writes them today, so the
    // officer recommendation is carried on the status row instead - see VerdictFor.

    private static async Task TakeApplicationLockAsync(
        SqlConnection connection, SqlTransaction transaction, string applicationId, CancellationToken ct)
    {
        var p = new DynamicParameters();
        p.Add("@Resource", "gps:submit:" + applicationId, DbType.String, size: 255);
        p.Add("@LockMode", "Exclusive", DbType.String, size: 32);
        p.Add("@LockOwner", "Transaction", DbType.String, size: 32);
        p.Add("@LockTimeout", 5000, DbType.Int32);
        p.Add("@Result", dbType: DbType.Int32, direction: ParameterDirection.ReturnValue);

        await connection.ExecuteAsync(new CommandDefinition(
            "sp_getapplock", p, transaction, 30, CommandType.StoredProcedure, cancellationToken: ct));

        // 0 and 1 are "granted"; anything negative is a timeout or a deadlock. Retryable,
        // because the other submission will finish and release it.
        if (p.Get<int>("@Result") < 0)
            throw new ApiException(
                StatusCodes.Status409Conflict, ApiErrorCodes.SubmitInFlight,
                "This property is already being submitted. Please try again shortly.",
                retryable: true, recoverable: true);
    }

    private static async Task WriteApplicationAsync(
        SqlConnection connection, SqlTransaction transaction, StoredProcedureOptions sp,
        SubmitVerificationRequest request, SubmitSiteDetails site, int appId,
        long officerId, int roleId, CancellationToken ct)
    {
        // Rows this API wrote before D33 was fixed carry Ofcr_App_Id 0. The procedure's UPDATE
        // branch matches on display id AND App_Id, so a resubmission of one of them would match
        // nothing and still report success. Healed here, inside the submit transaction.
        await connection.ExecuteAsync(new CommandDefinition(
            HealAppIdSql, new { appId, id = request.ApplicationId },
            transaction, 30, CommandType.Text, cancellationToken: ct));

        var p = ApplicationParameters(request, site, appId, officerId, roleId);

        var row = await connection.QueryFirstOrDefaultAsync<AppWriteRow>(new CommandDefinition(
            sp.SubmitApplication, p, transaction, 60, CommandType.StoredProcedure, cancellationToken: ct));

        if (row is null || !row.Status)
            throw ApiException.Unprocessable(
                row?.Message?.Trim() ?? "The application details could not be saved.",
                ApiErrorCodes.SubmitRejected, recoverable: true);

        // The procedure reports Status 1 even when its UPDATE matched no row (an existing
        // officer row under a different App_Id). Saying "stored" then would lose every
        // application answer while the roads and the status move on.
        var held = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            HeldSql, new { appId, id = request.ApplicationId },
            transaction, 30, CommandType.Text, cancellationToken: ct));
        if (held == 0)
            throw ApiException.Unprocessable(
                "The application answers could not be saved against this application. Nothing was saved.",
                ApiErrorCodes.SubmitRejected, recoverable: true);
    }

    internal const string HealAppIdSql = """
        UPDATE dbo.BtoA_MainApp_Officer SET Ofcr_App_Id = @appId
         WHERE Ofcr_ApplicationDisplayId = @id AND Ofcr_App_Id = 0;
        """;

    internal const string HeldSql = """
        SELECT COUNT(*) FROM dbo.BtoA_MainApp_Officer
         WHERE Ofcr_ApplicationDisplayId = @id AND Ofcr_App_Id = @appId;
        """;

    /// <summary>
    /// The parameters of dbo.USP_IU_BtoA_MainApp_Officer. Separate from the call so every
    /// value can be asserted without a database.
    /// </summary>
    internal static DynamicParameters ApplicationParameters(
        SubmitVerificationRequest request, SubmitSiteDetails site, int appId, long officerId, int roleId)
    {
        var p = new DynamicParameters();
        p.Add("@Ofcr_RowId", 0, DbType.Int32);
        p.Add("@Ofcr_ApplicationDisplayId", request.ApplicationId, DbType.String, size: 100);
        // The resolved App_Id. It used to be 0 "because the procedure resolves it" - it does
        // not: it stores the value as given and keys its UPDATE on it (defect D33).
        p.Add("@Ofcr_App_Id", appId, DbType.Int32);
        p.Add("@Ofcr_SsaId", request.SasId, DbType.String, size: 100);
        p.Add("@Ofcr_MotherEPID", request.Epid, DbType.String, size: 100);
        p.Add("@Ofcr_ApplicationDate", request.VerifiedDate.UtcDateTime, DbType.DateTime);
        p.Add("@Ofcr_PropertyLandExistOnSpot", AsBit(request.PropertyLandExists), DbType.Boolean);
        p.Add("@Ofcr_IsAllBhoomiSurveyNosCorrect", AsBit(request.IsAllBhoomiSurveyNosCorrect), DbType.Boolean);

        // The column is bit, so only 0/1 fit. The full answer - including 2, "refer to
        // surveyor", and "not answered" - is written to Ofcr_IsGovtProperty_Value by
        // USP_U_BtoA_GpsSubmitExtras_App. Anything other than a plain yes lands as 0 here.
        p.Add("@Ofcr_IsGovtProperty", request.IsGovtProperty == 1, DbType.Boolean);

        p.Add("@Ofcr_CorrectedGpsLocation", AsBit(site.IsGpsLocationCorrect is 0), DbType.Boolean);
        p.Add("@Ofcr_CorrectedLatitude", site.CorrectedLat?.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture), DbType.String, size: 100);
        p.Add("@Ofcr_CorrectedLongitude", site.CorrectedLng?.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture), DbType.String, size: 100);
        p.Add("@Ofcr_CorrectedPropertyUseType", AsBit(site.IsPropertyUseTypeCorrect is 0), DbType.Boolean);
        p.Add("@Ofcr_CorrectedPropertyUseTypeId", site.PropertyUseTypeId, DbType.Int32);
        p.Add("@Ofcr_CorrectedPropertyUseTypeValue", site.PropertyUseType, DbType.String, size: 200);
        p.Add("@Ofcr_CorrectedCommercialArea", site.CommercialArea, DbType.Double);
        p.Add("@Ofcr_CorrectedResidentialArea", site.ResidentialArea, DbType.Double);
        p.Add("@Ofcr_CorrectedIndustrialArea", site.IndustrialArea, DbType.Double);
        p.Add("@Ofcr_CorrectedRoadDetails", RoadDetailsCorrected(site), DbType.Boolean);
        p.Add("@Ofcr_IsCornerPlotOrMoreThanTwoSidesRoadFacing", AsBit(site.IsCornerPlot), DbType.Boolean);
        p.Add("@Ofcr_RoadFacingSides", site.RoadFacingSides, DbType.Int32);

        // Both columns are named ...InSqft / ...InSqmt, but a road width is a length. The
        // value is metres, and the feet figure is a straight linear conversion - NOT the
        // 10.7639 area factor the legacy client applied here.
        p.Add("@Ofcr_RoadWidthInFrontOfPropertyInSqmt", request.ActualRoadWidth, DbType.Double);
        p.Add("@Ofcr_RoadWidthInFrontOfPropertyInSqft", request.ActualRoadWidth * 3.28084, DbType.Double);

        p.Add("@Ofcr_nearest_public_Road_Latitude", request.NearestPublicRoadLat?.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture), DbType.String, size: 100);
        p.Add("@Ofcr_nearest_public_Road_Longitude", request.NearestPublicRoadLng?.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture), DbType.String, size: 100);
        p.Add("@Ofcr_gpsStatusFullWorkflowJSON", (string?)null, DbType.String, size: -1);
        p.Add("@Ofcr_Aditional", BuildAdditionalInfo(request), DbType.String, size: -1);
        p.Add("@CBy", officerId, DbType.Int64);
        p.Add("@CRole", roleId, DbType.Int32);
        p.Add("@matchedSurveyNo", request.MatchedSurveyNo, DbType.Int32);
        p.Add("@surveyRemark", request.SurveyRemark ?? request.Remark, DbType.String, size: -1);
        p.Add("@Ofcr_IsBuildingExists", AsBit(request.BuildingExists), DbType.Boolean);
        p.Add("@mstPlanPropertyUseType", site.MstPlanPropertyUseType, DbType.String, size: 100);
        return p;
    }

    /// <summary>
    /// Ofcr_CorrectedRoadDetails: whether the officer changed the road declaration. The app
    /// sends siteDetails.isRoadDetailsCorrect; older installs sent null, which was stored as
    /// "not corrected" whatever the roads said (defect D9). Then any road that is not
    /// unchanged (status other than 0) means corrected.
    /// </summary>
    internal static bool RoadDetailsCorrected(SubmitSiteDetails site) => site.IsRoadDetailsCorrect switch
    {
        0 => true,
        1 => false,
        _ => (site.RoadDetails ?? []).Any(r => r is not null && r.RoadStatus is not 0)
    };

    /// <summary>
    /// Ofcr_Corrected_Road_Details for one road, by the same rule: the officer's answer when
    /// given, else whether the road was changed. An added road and a road marked not found
    /// carry no answer (the app leaves isRoadDetailsCorrect null on them) and were stored as
    /// "not corrected".
    /// </summary>
    internal static bool RoadCorrected(SubmitRoadDetail road) => road.IsRoadDetailsCorrect switch
    {
        0 => true,
        1 => false,
        _ => road.RoadStatus is not 0
    };

    /// <summary>
    /// Writes each road, and reports which of them the KSRSAC master did not recognise.
    /// </summary>
    /// <remarks>
    /// An unrecognised road used to abort the whole submit. Because every write shares one
    /// transaction, that discarded the application row, the roads that did match and the
    /// move to status 13 - and it made one legitimate answer impossible to send at all:
    /// "not in the list" travels as road id 999, which exists once in the master, in an
    /// unrelated ward. No answer the officer could give would save.
    ///
    /// The road is now stored either way and an unrecognised one is a warning. A genuine
    /// write failure (Status = 0, meaning the row is NOT in the database) still aborts,
    /// because reporting success for a road nobody stored would be a lie.
    /// </remarks>
    private async Task<(int Stored, List<ApiError> Unverified)> WriteRoadsAsync(
        SqlConnection connection, SqlTransaction transaction, StoredProcedureOptions sp,
        SubmitVerificationRequest request, SubmitSiteDetails site,
        IReadOnlyDictionary<string, string> mediaUrls,
        int appId, long officerId, int roleId, CancellationToken ct)
    {
        var stored = 0;
        var rejected = new List<ApiError>();
        var unverified = new List<ApiError>();
        var livePrivate = new List<(string RowId, string? Url)>();

        // The server clock before the first road write, and the private-road documents that
        // exist before it: every road row this submit inserts or updates is stamped at or
        // after the one, every document it inserts has an id above the other (see
        // SupersedeEarlierRowsAsync). Neither relies on the RoadRowId the procedure returns,
        // which on a server without the B2 fix is a document id for a private road.
        var startedAt = await connection.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            "SELECT GETDATE()", null, transaction, 30, CommandType.Text, cancellationToken: ct));
        var maxDocBefore = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT ISNULL(MAX(docTrn_Id), 0) FROM dbo.BtoA_DocumentTran "
            + "WHERE docTrn_App_Id = @appId AND docTrn_Mdoc_id = 4 AND UniqueIdentifier = N'444'",
            new { appId }, transaction, 30, CommandType.Text, cancellationToken: ct));

        // Ordered so two concurrent submissions touch rows in the same sequence. With the
        // application lock held this is belt and braces, but it costs nothing. The payload
        // index travels with each road so an error names the card the officer filled in.
        foreach (var (road, index) in site.RoadDetails.Select((r, i) => (r, i)).OrderBy(x => x.r.RoadRowId ?? 0))
        {
            ct.ThrowIfCancellationRequested();

            var p = RoadParameters(request.ApplicationId, road, mediaUrls, officerId, roleId);

            var row = await connection.QueryFirstOrDefaultAsync<RoadWriteRow>(new CommandDefinition(
                sp.SubmitRoad, p, transaction, 60, CommandType.StoredProcedure, cancellationToken: ct));

            // The row was not written. Reporting the survey as stored would be untrue, and
            // the officer would never be asked for it again.
            if (row is null || !row.Status)
            {
                rejected.Add(RoadError(index,
                    row?.Message?.Trim()
                        ?? $"Road '{NameOf(road)}' was not accepted and no reason was given."));
                continue;
            }

            // Stored, but the master does not know this road. Named individually rather
            // than counted: this is the only place it is visible until QC opens the record.
            if (!row.KsracMatched)
            {
                unverified.Add(RoadError(index,
                    $"Road '{NameOf(road)}' is not in the KSRSAC master for this ward. "
                    + "It has been stored as the officer entered it."));

                logger.LogWarning(
                    "Road not recognised on {ApplicationId} from officer {OfficerId}: "
                    + "id {RoadId}, name {RoadName}. Stored unverified.",
                    request.ApplicationId, officerId, road.RoadId, road.RoadName);
            }

            if (row.RoadRowId > 0)
            {
                if (!string.IsNullOrWhiteSpace(road.NoticeImage))
                    await WriteRoadNoticeAsync(connection, transaction, row.RoadRowId,
                        UrlFor(mediaUrls, road.NoticeImage), ct);
                else
                    // A road that is no longer private keeps no notice. The notice procedure
                    // COALESCEs, so a resubmission could never clear the old one (D22).
                    // Scoped to this application and officer, so a wrong id handed back by
                    // the procedure (B2) can never clear somebody else's notice.
                    await connection.ExecuteAsync(new CommandDefinition(
                        ClearNoticeSql, new { roadRowId = row.RoadRowId, appId, cby = officerId },
                        transaction, 30, CommandType.Text, cancellationToken: ct));
            }

            if (road.RoadStatus != RoadDeleted && IsPrivate(road) && road.RoadRowId is { } srid)
                livePrivate.Add((srid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    UrlFor(mediaUrls, road.PrivateRoadImage)));

            stored++;
        }

        if (rejected.Count > 0)
            throw new SubmitRejectedException(
                rejected,
                ApiErrorCodes.RoadNotRecognised,
                "Some road details could not be saved. Nothing was saved.");

        await SupersedeEarlierRowsAsync(connection, transaction, request.ApplicationId, appId,
            startedAt, livePrivate, maxDocBefore, officerId, roleId, ct);

        return (stored, unverified);
    }

    internal const string ClearNoticeSql = """
        UPDATE dbo.BtoA_SiteRoadDetails_Officer
           SET Ofcr_Notice_Document = NULL
         WHERE Ofcr_RowId = @roadRowId AND Ofcr_App_Id = @appId AND CBy = @cby
           AND Ofcr_Notice_Document IS NOT NULL;
        """;

    /// <summary>
    /// A resubmission (QC return, a retry after a lost reply) replaces this officer's earlier
    /// answer instead of adding to it (defects D5, D22).
    ///
    /// The road procedure upserts on App_Id + RoadId + SiteRoadRowID + CBy + CRole with '=',
    /// so an added road (SiteRoadRowID NULL), a road whose id changed, or a road no longer in
    /// the survey left its earlier row active beside the new one, and every insert of a
    /// private road added a BtoA_DocumentTran row. After the new rows are written, inside the
    /// same transaction:
    ///  - this officer's road rows for the application that this submit did not write (not
    ///    inserted or updated since <paramref name="startedAt"/>) are made inactive;
    ///  - this officer's private-road documents (Mdoc 4 / 444) stay active only for the live
    ///    private roads of this submit: the newest per declared road row, and those inserted
    ///    by this submit for added roads. Every other one is made inactive;
    ///  - a live private declared road left with no active document of this officer gets one,
    ///    as the procedure's insert branch would have written it. Its update branch never
    ///    inserts one, so a road that was public at the last submit and is private now had
    ///    none (D22).
    /// Rows of other officers are left alone (whether a second officer replaces the first is
    /// decision D35). The procedure-side fix is requested in docs/DBA_Request.md (F2).
    /// </summary>
    private static async Task SupersedeEarlierRowsAsync(
        SqlConnection connection, SqlTransaction transaction, string applicationId, int appId,
        DateTime startedAt, IReadOnlyList<(string RowId, string? Url)> livePrivate, long maxDocBefore,
        long officerId, int roleId, CancellationToken ct)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            SupersedeRoadRowsSql,
            new { id = applicationId, cby = officerId, crole = roleId, startedAt },
            transaction, 30, CommandType.Text, cancellationToken: ct));

        await connection.ExecuteAsync(new CommandDefinition(
            SupersedeDocumentsSql,
            new { appId, cby = officerId, crole = roleId, live = livePrivate.Select(r => r.RowId).ToList(), maxDocBefore },
            transaction, 30, CommandType.Text, cancellationToken: ct));

        foreach (var (rowId, url) in livePrivate)
            await connection.ExecuteAsync(new CommandDefinition(
                EnsureDocumentSql,
                new { appId, cby = officerId, crole = roleId, srid = rowId, url },
                transaction, 30, CommandType.Text, cancellationToken: ct));
    }

    // Kept as constants so db/27_udd_Check_RoadScenarios.sql can mirror them word for word
    // (RoadSupersedeSqlTests holds the two in step).
    internal const string SupersedeRoadRowsSql = """
        UPDATE dbo.BtoA_SiteRoadDetails_Officer
           SET isActive = 0, UBy = @cby, UDte = GETDATE(), URole = @crole
         WHERE Ofcr_ApplicationDisplayId = @id AND CBy = @cby AND CRole = @crole
           AND isActive = 1 AND ISNULL(UDte, CDte) < @startedAt;
        """;

    internal const string EnsureDocumentSql = """
        IF NOT EXISTS (SELECT 1 FROM dbo.BtoA_DocumentTran
                        WHERE docTrn_App_Id = @appId AND docTrn_Mdoc_id = 4 AND UniqueIdentifier = N'444'
                          AND DocumentId = @srid AND docTrn_CBy = @cby AND docTrn_Active = 1)
            INSERT INTO dbo.BtoA_DocumentTran
                   (docTrn_App_Id, docTrn_Mdoc_id, docTrn_Active, docTrn_url, docTrn_CBy, docTrn_CDte,
                    docTrn_CRole, DigitalSketchUpload_flag, UniqueIdentifier, DocumentId)
            VALUES (@appId, 4, 1, @url, @cby, GETDATE(), @crole, 1, N'444', @srid);
        """;

    internal const string SupersedeDocumentsSql = """
        UPDATE d
           SET docTrn_Active = 0, docTrn_UBy = @cby, docTrn_UDte = GETDATE(), docTrn_URole = @crole
          FROM dbo.BtoA_DocumentTran d
         WHERE d.docTrn_App_Id = @appId AND d.docTrn_Mdoc_id = 4 AND d.UniqueIdentifier = N'444'
           AND d.docTrn_CBy = @cby AND d.docTrn_Active = 1
           AND NOT (
                (d.DocumentId IS NULL AND d.docTrn_Id > @maxDocBefore)
             OR (d.DocumentId IS NOT NULL AND d.DocumentId IN @live
                 AND d.docTrn_Id = (SELECT MAX(x.docTrn_Id) FROM dbo.BtoA_DocumentTran x
                                     WHERE x.docTrn_App_Id = d.docTrn_App_Id AND x.docTrn_Mdoc_id = 4
                                       AND x.UniqueIdentifier = N'444' AND x.DocumentId = d.DocumentId
                                       AND x.docTrn_CBy = @cby)));
        """;

    /// <summary>
    /// The parameters of dbo.USP_IU_BtoA_SiteRoadDetails_Officer for one road. Separate from
    /// the call so every value can be asserted without a database.
    /// </summary>
    internal static DynamicParameters RoadParameters(
        string applicationId, SubmitRoadDetail road, IReadOnlyDictionary<string, string> mediaUrls,
        long officerId, int roleId)
    {
        var p = new DynamicParameters();
        p.Add("@BtoA_RoadRowId", 0, DbType.Int32);
        // The road procedure, unlike the application one, does look App_Id up from the
        // display id and overwrites this parameter.
        p.Add("@BtoA_MainAppId", 0, DbType.Int32);
        p.Add("@BtoA_ApplicationDisplayId", applicationId, DbType.AnsiString, size: 50);
        p.Add("@BtoA_Corrected_Road_Details", RoadCorrected(road), DbType.Boolean);

        // The procedure has no roadStatus parameter. Correction_Type carries it, and
        // 'delete'/2 is what drives the isactive flag on the stored row.
        p.Add("@BtoA_Correction_Type_Id", road.RoadStatus, DbType.Int32);
        p.Add("@BtoA_Correction_Type_Value", CorrectionTypeValue(road.RoadStatus), DbType.String, size: 50);

        // Sizes match the procedure's own parameters (NVARCHAR(50) / NVARCHAR(250)); longer
        // values are refused by Validate rather than silently truncated (D24).
        p.Add("@BtoA_RoadType", road.RoadType, DbType.String, size: 50);
        p.Add("@BtoA_IsPresentInPublicRoadList", AsBit(road.IsPresentInPublicRoadList), DbType.Boolean);
        p.Add("@BtoA_RoadId", ParseRoadId(road.RoadId), DbType.Int32);
        p.Add("@BtoA_RoadName", road.RoadName, DbType.String, size: MaxRoadNameLength);
        p.Add("@BtoA_ActualRoadName", road.ActualRoadName, DbType.String, size: MaxRoadNameLength);
        p.Add("@BtoA_Nearest_Public_Road_Latitude", road.NearPublicRoadLat, DbType.Double);
        p.Add("@BtoA_Nearest_Public_Road_Longitude", road.NearPublicRoadLng, DbType.Double);
        p.Add("@CBy", officerId, DbType.Int64);
        p.Add("@CRole", roleId, DbType.Int32);
        p.Add("@BtoA_Nearest_Private_Road_Latitude", road.PrivateRoadLat, DbType.Double);
        p.Add("@BtoA_Nearest_Private_Road_Longitude", road.PrivateRoadLng, DbType.Double);
        p.Add("@BtoA_Nearest_Private_Road_Document", UrlFor(mediaUrls, road.PrivateRoadImage), DbType.String, size: -1);
        p.Add("@BtoA_Nearest_Public_Road_Document", UrlFor(mediaUrls, road.PublicRoadImage), DbType.String, size: -1);
        p.Add("@BtoA_SiteRoadRowID", road.RoadRowId?.ToString(System.Globalization.CultureInfo.InvariantCulture), DbType.String, size: 1000);
        return p;
    }

    private static bool IsPrivate(SubmitRoadDetail road) =>
        string.Equals(road.RoadType?.Trim(), "private", StringComparison.OrdinalIgnoreCase);

    /// <summary>The road as the officer named it: the typed name, else the master name.</summary>
    internal static string NameOf(SubmitRoadDetail road) =>
        (string.IsNullOrWhiteSpace(road.ActualRoadName) ? road.RoadName : road.ActualRoadName)?.Trim() ?? "";

    /// <summary>
    /// Names the road by its position in the payload - the card the officer filled in - not
    /// by its id: two roads may share an id (999, or a public road named twice), and the
    /// write loop runs in RoadRowId order, so neither an id nor a loop counter identifies
    /// the card (defect A8).
    /// </summary>
    internal static ApiError RoadError(int index, string message) => new()
    {
        Field = $"siteDetails.roadDetails[{index}]",
        Code = ApiErrorCodes.RoadNotRecognised,
        Message = message
    };

    private static async Task WriteRoadNoticeAsync(
        SqlConnection connection, SqlTransaction transaction, int roadRowId, string? noticeUrl,
        CancellationToken ct)
    {
        var p = new DynamicParameters();
        p.Add("@RoadRowId", roadRowId, DbType.Int32);
        p.Add("@NoticeDocument", noticeUrl, DbType.String, size: -1);

        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.USP_U_BtoA_GpsRoadNoticeDocument", p, transaction, 30,
            CommandType.StoredProcedure, cancellationToken: ct));
    }

    private static async Task WriteExtrasAsync(
        SqlConnection connection, SqlTransaction transaction,
        SubmitVerificationRequest request, IReadOnlyDictionary<string, string> mediaUrls,
        CancellationToken ct)
    {
        var p = new DynamicParameters();
        p.Add("@Ofcr_ApplicationDisplayId", request.ApplicationId, DbType.String, size: 100);
        p.Add("@PropertyDocument", UrlFor(mediaUrls, request.PropertyImage), DbType.String, size: -1);
        p.Add("@MapDocument", UrlFor(mediaUrls, request.MapImage), DbType.String, size: -1);
        p.Add("@NoteSheetDocument", UrlFor(mediaUrls, request.NoteSheetFile), DbType.String, size: -1);
        p.Add("@GovtPropertyDetails", request.GovtPropertyDetails, DbType.String, size: 1000);
        p.Add("@IsGovtPropertyValue", request.IsGovtProperty, DbType.Int32);

        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.USP_U_BtoA_GpsSubmitExtras_App", p, transaction, 30,
            CommandType.StoredProcedure, cancellationToken: ct));
    }

    private static async Task WriteStatusAsync(
        SqlConnection connection, SqlTransaction transaction, StoredProcedureOptions sp,
        SubmitVerificationRequest request, int appId, long officerId, int roleId,
        string? officerMobile, CancellationToken ct)
    {
        var p = new DynamicParameters();
        p.Add("@Ofcr_StatusRowId", 0, DbType.Int32);
        p.Add("@Ofcr_App_Id", appId, DbType.Int32);
        p.Add("@Ofcr_ApplicationDisplayId", request.ApplicationId, DbType.String, size: 50);
        p.Add("@Ofcr_Mobile_Number", officerMobile, DbType.String, size: 15);
        p.Add("@Status_Date", request.VerifiedDate.UtcDateTime, DbType.DateTime);
        // Status_Id here is a VERDICT code in this procedure vocabulary, not an App_Status:
        // live rows show 10 = APPROVED, 11 = REJECTED, 12 = RETURN_TO_RI. Hardcoding 10
        // recorded every submission as an approval, including one where the officer had
        // recommended rejection.
        var (statusId, statusValue) = VerdictFor(request.KhataRecommendation);
        p.Add("@Status_Id", statusId, DbType.Int32);
        p.Add("@Status_Remark", request.Remark, DbType.String, size: -1);
        p.Add("@Status_Rejected_Reason", (string?)null, DbType.String, size: -1);
        p.Add("@Status_Value", statusValue, DbType.String, size: 100);
        p.Add("@CBy", officerId, DbType.Int64);
        p.Add("@CRole", roleId, DbType.Int32);
        p.Add("@isAutoEscalated", false, DbType.Boolean);
        p.Add("@autoEscalatedDate", (DateTime?)null, DbType.DateTime);

        // This is the call that moves BtoAMainApp.App_Status to 13. Before the fix in
        // db/11 it set the status to NULL for role 116 and the application vanished; it
        // now raises instead if a role is ever unmapped, which reaches the client as a
        // retryable server error rather than silent loss.
        await connection.ExecuteAsync(new CommandDefinition(
            sp.SubmitStatus, p, transaction, 60, CommandType.StoredProcedure, cancellationToken: ct));
    }

    private static async Task SafeRollbackAsync(SqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync();
        }
        catch (Exception)
        {
            // A transaction already doomed by XACT_ABORT throws on rollback. The work is
            // undone either way, and masking the original failure would be worse.
        }
    }

    /// <summary>
    /// Maps the officer recommendation onto the vocabulary already in use in
    /// BtoA_StatusDetail_Officer, rather than inventing one: APPROVED and REJECTED, with
    /// the past tense that QC and JC rows use, and the matching 10/11 code.
    ///
    /// A khata recommendation only applies to a SinglePlotApproval; an ordinary BtoAKhata
    /// survey is a verification, not a decision, so it is recorded with no verdict rather
    /// than a default one. Either way App_Status becomes 13 - our role branch does not read
    /// these - but the stored row is what QC and JC actually look at.
    ///
    /// Mst_AppStatus also defines 30 "Approved By RI" and 25 "Rejected By RI". Nothing in
    /// the database writes either, so they are left alone until BBMP say what should.
    /// </summary>
    // internal, not private, so the tests can call it directly. Reaching it by
    // reflection meant a rename turned into a NullReferenceException at runtime
    // instead of a compile error - on the one mapping that decides whether an
    // officer is recorded as having approved or rejected a khata.
    internal static (int StatusId, string? StatusValue) VerdictFor(string? recommendation)
    {
        var value = recommendation?.Trim();
        if (string.IsNullOrEmpty(value)) return (ApproveCode, null);

        return value.StartsWith("Reject", StringComparison.OrdinalIgnoreCase)
            ? (RejectCode, "REJECTED")
            : (ApproveCode, "APPROVED");
    }

    private const int ApproveCode = 10;
    private const int RejectCode = 11;

    // The value helpers below are internal, not private, so RoadWriteMappingTests calls them
    // directly rather than by reflection (see the note on VerdictFor).

    /// <summary>Any non-zero value is a set bit; Validate refuses anything but 0/1 first (A4).</summary>
    internal static bool? AsBit(int? value) => value switch { null => null, 0 => false, _ => true };
    private static bool? AsBit(bool value) => value;

    /// <summary>
    /// "999" is the agreed sentinel for "this road is not in the public list". It is a
    /// real value the procedure expects, not a parse failure. Anything that is not an int
    /// is refused by Validate (A3), so null here means the road has no id at all.
    /// </summary>
    internal static int? ParseRoadId(string? roadId) =>
        int.TryParse(roadId, out var parsed) ? parsed : null;

    internal static string? CorrectionTypeValue(int? roadStatus) => roadStatus switch
    {
        0 => "unchanged",
        1 => "updated",
        2 => "delete",
        3 => "added",
        _ => null
    };

    /// <summary>
    /// The payload names a file by its basename; the media store returns where it actually
    /// went. A slot naming a file that was never uploaded is caught before this point.
    /// </summary>
    /// <remarks>
    /// SubmitMediaService stores each file under Path.GetFileName of the payload value, so a
    /// payload naming "field_photos/road_A.jpg" stored the photo as "road_A.jpg" and this
    /// lookup, by the raw value, then wrote NULL into the document column (defect A2).
    /// </remarks>
    internal static string? UrlFor(IReadOnlyDictionary<string, string> mediaUrls, string? clientName) =>
        string.IsNullOrWhiteSpace(clientName) ? null
        : mediaUrls.TryGetValue(clientName, out var url) ? url
        : mediaUrls.TryGetValue(Path.GetFileName(clientName.Trim()), out var byBaseName) ? byBaseName
        : null;

    /// <summary>
    /// Ofcr_Aditional is the only free-text column on the application row that nothing else
    /// reads, so the answers with no column of their own are kept here rather than dropped.
    /// </summary>
    private static string BuildAdditionalInfo(SubmitVerificationRequest r) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            r.IsLandUntraceable,
            r.IsLandLocationUpdated,
            r.KhataRecommendation,
            r.KhataComments,
            r.SingleSiteId,
            r.ApplicationType,
            siteArea = r.SiteDetails.SiteArea,
            eastWest = r.SiteDetails.EastWest,
            northSouth = r.SiteDetails.NorthSouth,
            isDeclaredRoadFacingSidesCorrect = r.SiteDetails.IsDeclaredRoadFacingSidesCorrect
        });

    public void Validate(SubmitVerificationRequest request, int roleId, long? officerWardId)
    {
        JurisdictionRules.RequireOwnWard(
            roleId, officerWardId, request.WardId,
            "You can only submit surveys for your own ward.");

        var errors = new List<ApiError>();

        if (string.IsNullOrWhiteSpace(request.ApplicationId))
            errors.Add(new ApiError { Field = "applicationId", Code = "REQUIRED", Message = "The application id is required." });

        if (string.IsNullOrWhiteSpace(request.Epid))
            errors.Add(new ApiError { Field = "epid", Code = "REQUIRED", Message = "The EPID is required." });

        // The justification is the whole evidentiary value of asserting government land.
        if (request.IsGovtProperty == 1 && string.IsNullOrWhiteSpace(request.GovtPropertyDetails))
            errors.Add(new ApiError
            {
                Field = "govtPropertyDetails",
                Code = "REQUIRED",
                Message = "Please describe why this is government property."
            });

        // These land in bit/int NOT NULL columns on the legacy officer tables. Left
        // unanswered they reach SQL as NULL and the insert fails - which surfaced as a 500,
        // and the client treats 5xx as retryable, so a survey missing one answer would have
        // been resent forever. Naming them here turns that into one clear rejection the
        // officer can act on.
        Require(errors, "propertyLandExists", request.PropertyLandExists);
        Require(errors, "isAllBhoomiSurveyNosCorrect", request.IsAllBhoomiSurveyNosCorrect);

        // "siteDetails": null or "roadDetails": null in the JSON used to throw a
        // NullReferenceException below - a 500, which the device treats as retryable, so
        // the same broken survey was resent on every sync forever.
        var site = request.SiteDetails;
        if (site is null)
        {
            errors.Add(new ApiError
            {
                Field = "siteDetails",
                Code = "REQUIRED",
                Message = "The site and road answers are missing from this survey."
            });
            throw new SubmitRejectedException(errors);
        }
        var roads = site.RoadDetails ?? [];

        // "roadDetails": [null] deserialises to a null element, and every rule below would
        // dereference it - a NullReferenceException, a retryable 500, and the device resending
        // the same survey forever (defect A1).
        var nullRoads = roads.Select((r, i) => (r, i)).Where(x => x.r is null).Select(x => x.i).ToList();
        if (nullRoads.Count > 0)
        {
            errors.AddRange(nullRoads.Select(i => new ApiError
            {
                Field = $"siteDetails.roadDetails[{i}]",
                Code = "INVALID",
                Message = $"Road {i + 1} is empty."
            }));
            throw new SubmitRejectedException(errors);
        }

        Require(errors, "siteDetails.isCornerPlot", site.IsCornerPlot);

        // Answers stored in bit columns. AsBit stored any non-zero value as 1, so a 2 on the
        // corner-plot answer skipped both corner rules and was then filed as "yes" (A4).
        RequireBit(errors, "siteDetails.isCornerPlot", site.IsCornerPlot);
        RequireBit(errors, "siteDetails.isRoadDetailsCorrect", site.IsRoadDetailsCorrect);
        RequireBit(errors, "siteDetails.isDeclaredRoadFacingSidesCorrect", site.IsDeclaredRoadFacingSidesCorrect);

        foreach (var (road, i) in roads.Select((r, i) => (r, i)))
        {
            Require(errors, $"siteDetails.roadDetails[{i}].roadStatus", road.RoadStatus);
            Require(errors, $"siteDetails.roadDetails[{i}].isPresentInPublicRoadList",
                road.IsPresentInPublicRoadList);
            RequireBit(errors, $"siteDetails.roadDetails[{i}].isPresentInPublicRoadList", road.IsPresentInPublicRoadList);
            RequireBit(errors, $"siteDetails.roadDetails[{i}].isRoadDetailsCorrect", road.IsRoadDetailsCorrect);
        }

        if (roads.Count == 0)
            errors.Add(new ApiError { Field = "siteDetails.roadDetails", Code = "REQUIRED", Message = "At least one road is required." });
        else
            ValidateRoadSet(errors, site, roads);

        // 0,0 is in the Gulf of Guinea. It is what a device reports when it never got a
        // fix, and it is the commonest real GPS failure - worth catching here rather than
        // storing a survey that places a Bengaluru property in the Atlantic.
        RejectNullIsland(errors, "siteDetails.correctedLat", site.CorrectedLat, site.CorrectedLng);
        foreach (var (road, i) in roads.Select((r, i) => (r, i)))
        {
            RejectNullIsland(errors, $"siteDetails.roadDetails[{i}].privateRoadLat", road.PrivateRoadLat, road.PrivateRoadLng);
            RejectNullIsland(errors, $"siteDetails.roadDetails[{i}].nearPublicRoadLat", road.NearPublicRoadLat, road.NearPublicRoadLng);
        }

        // A point is both coordinates or neither, and on the globe (A4).
        RequireCoordinatePair(errors, "nearestPublicRoadLat", request.NearestPublicRoadLat, request.NearestPublicRoadLng);
        RequireCoordinatePair(errors, "siteDetails.correctedLat", site.CorrectedLat, site.CorrectedLng);
        foreach (var (road, i) in roads.Select((r, i) => (r, i)))
        {
            RequireCoordinatePair(errors, $"siteDetails.roadDetails[{i}].privateRoadLat", road.PrivateRoadLat, road.PrivateRoadLng);
            RequireCoordinatePair(errors, $"siteDetails.roadDetails[{i}].nearPublicRoadLat", road.NearPublicRoadLat, road.NearPublicRoadLng);
        }

        if (errors.Count > 0)
            throw new SubmitRejectedException(errors);
    }

    /// <summary>The procedure's NVARCHAR(250) road name parameters (and columns).</summary>
    internal const int MaxRoadNameLength = 250;

    /// <summary>The procedure's NVARCHAR(50) road type parameter.</summary>
    internal const int MaxRoadTypeLength = 50;

    private static void RequireBit(List<ApiError> errors, string field, int? value)
    {
        if (value is null or 0 or 1) return;
        errors.Add(new ApiError
        {
            Field = field,
            Code = "INVALID",
            Message = $"This answer must be 0 or 1, not {value}."
        });
    }

    private static void RequireCoordinatePair(List<ApiError> errors, string field, double? lat, double? lng)
    {
        if (lat is null && lng is null) return;
        if (lat is null || lng is null)
        {
            errors.Add(new ApiError
            {
                Field = field,
                Code = "INVALID",
                Message = "A location needs both a latitude and a longitude. Please capture it again."
            });
            return;
        }
        if (double.IsNaN(lat.Value) || double.IsNaN(lng.Value)
            || lat.Value is < -90 or > 90 || lng.Value is < -180 or > 180)
            errors.Add(new ApiError
            {
                Field = field,
                Code = "INVALID",
                Message = "This location is not a valid latitude and longitude. Please capture it again."
            });
    }

    // ---- Road rows belong to the application (N3) ---------------------------------------

    internal const string DeclaredRoadsSql = """
        SELECT RowId = Rd_RoadRow_ID,
               Active = CAST(CASE WHEN ISNULL(Rd_RoadActive, 1) = 1 THEN 1 ELSE 0 END AS bit),
               RoadId = CAST(Rd_RoadId AS nvarchar(50)), RoadName = Rd_RoadName,
               EnteredRoadName = Rd_EnteredRoadName, PrivateRoadName = Rd_PrivateRoadName,
               PrivateRoadText = Rd_PrivateRoadText
        FROM dbo.BtoA_SiteRoadDetails
        WHERE Rd_App_Id = @appId;
        """;

    /// <summary>
    /// Every roadRowId must be a road row of this application. A Site_Id sent in its place
    /// (D25), another application's row, or an id the citizen's set no longer holds after a
    /// positional restore (D6) used to be filed as given - as Ofcr_SiteRoadRowID, and as the
    /// DocumentId of a private road's document. Those are refused.
    ///
    /// An active declared road the survey says nothing about is reported as a warning, not
    /// refused: an officer-added road may legitimately stand in for it in the count, and a
    /// blank row (no road, no name: PropertyMapper.IsBlankRoad) is never shown as a card.
    /// </summary>
    internal static (List<ApiError> Errors, List<ApiError> Warnings) CheckRoadRows(
        IReadOnlyList<SubmitRoadDetail> roads, IReadOnlyCollection<DeclaredRoadRow> declared)
    {
        var errors = new List<ApiError>();
        var warnings = new List<ApiError>();
        var known = declared.Select(d => d.RowId).ToHashSet();

        foreach (var (road, i) in roads.Select((r, i) => (r, i)))
        {
            if (road.RoadRowId is { } id && !known.Contains(id))
                errors.Add(new ApiError
                {
                    Field = $"siteDetails.roadDetails[{i}].roadRowId",
                    Code = ApiErrorCodes.UnknownRoadRow,
                    Message = $"Road {i + 1} is not one of this application's declared roads. "
                              + "Fetch the property again and redo this road."
                });
        }

        var answered = roads.Where(r => r.RoadRowId is not null).Select(r => r.RoadRowId!.Value).ToHashSet();
        foreach (var row in declared.Where(d => d.Active && !d.IsBlank && !answered.Contains(d.RowId))
                                    .OrderBy(d => d.RowId))
            warnings.Add(new ApiError
            {
                Field = "siteDetails.roadDetails",
                Code = ApiErrorCodes.DeclaredRoadNotAnswered,
                Message = $"Declared road row {row.RowId} has no answer in this survey."
            });

        return (errors, warnings);
    }

    /// <summary>The most roads a plot can face; the app enforces the same figure.</summary>
    internal const int MaxRoads = 5;

    private const int RoadDeleted = 2;
    private const string NotInListRoadId = "999";

    /// <summary>
    /// The roads as a set: how many, whether that agrees with the corner-plot answer, and
    /// whether any road appears twice.
    ///
    /// These used to be accepted. A field survey - not a corner plot, one declared road,
    /// two submitted - would have been stored with 200 and an extra road row. Unlike an
    /// unrecognised KSRSAC road (a warning: the officer reporting reality), these are
    /// surveys that contradict themselves, so they are refused and the officer corrects
    /// them on the device. The app applies the same rules before sending; this is the
    /// backstop for older installs.
    /// </summary>
    private static void ValidateRoadSet(
        List<ApiError> errors, SubmitSiteDetails site, IReadOnlyList<SubmitRoadDetail> roads)
    {
        foreach (var (road, i) in roads.Select((r, i) => (r, i)))
        {
            // [Range(0, 3)] on the model never runs: the payload is deserialised by hand.
            if (road.RoadStatus is { } status and (< 0 or > 3))
                errors.Add(new ApiError
                {
                    Field = $"siteDetails.roadDetails[{i}].roadStatus",
                    Code = "INVALID",
                    Message = $"Road {i + 1} has an unknown status ({status})."
                });

            // A road id that is not a number reached the procedure as NULL, which also
            // defeats its upsert key (A3).
            if (!string.IsNullOrWhiteSpace(road.RoadId) && ParseRoadId(road.RoadId) is null)
                errors.Add(new ApiError
                {
                    Field = $"siteDetails.roadDetails[{i}].roadId",
                    Code = "INVALID",
                    Message = $"Road {i + 1} has a road id that is not a number ({road.RoadId.Trim()})."
                });

            // A row id of 0 or below names no road row, and makes media file names the
            // media endpoint refuses to serve (A6).
            if (road.RoadRowId is <= 0)
                errors.Add(new ApiError
                {
                    Field = $"siteDetails.roadDetails[{i}].roadRowId",
                    Code = "INVALID",
                    Message = $"Road {i + 1} has an invalid road row id ({road.RoadRowId})."
                });

            // The procedure's parameters are NVARCHAR(250) / NVARCHAR(50): longer values were
            // silently cut (D24).
            TooLong(errors, $"siteDetails.roadDetails[{i}].roadName", road.RoadName, MaxRoadNameLength, i);
            TooLong(errors, $"siteDetails.roadDetails[{i}].actualRoadName", road.ActualRoadName, MaxRoadNameLength, i);
            TooLong(errors, $"siteDetails.roadDetails[{i}].roadType", road.RoadType, MaxRoadTypeLength, i);

            // A live private road is stored with its evidence or not at all: both points, both
            // photographs and the served notice. Without this a private road arrived with no
            // evidence and the procedure filed a private-road document with no file (D15).
            if (road.RoadStatus != RoadDeleted && IsPrivate(road))
            {
                RequireEvidence(errors, i, "privateRoadLat", road.PrivateRoadLat is not null && road.PrivateRoadLng is not null,
                    "the location in front of the property");
                RequireEvidence(errors, i, "privateRoadImage", !string.IsNullOrWhiteSpace(road.PrivateRoadImage),
                    "the photograph of the road in front of the property");
                RequireEvidence(errors, i, "nearPublicRoadLat", road.NearPublicRoadLat is not null && road.NearPublicRoadLng is not null,
                    "the location on the nearest public road");
                RequireEvidence(errors, i, "publicRoadImage", !string.IsNullOrWhiteSpace(road.PublicRoadImage),
                    "the photograph of the nearest public road");
                RequireEvidence(errors, i, "noticeImage", !string.IsNullOrWhiteSpace(road.NoticeImage),
                    "the photograph of the served notice");
            }
        }

        // Two entries for one road row - including one marked deleted beside one kept, which
        // filed two officer rows for one citizen road (A7). Checked over every road, not only
        // the live ones.
        foreach (var group in roads.Select((r, i) => (Road: r, Index: i))
                                   .Where(x => x.Road.RoadRowId is not null)
                                   .GroupBy(x => x.Road.RoadRowId)
                                   .Where(g => g.Count() > 1))
        {
            var second = group.Skip(1).First();
            errors.Add(new ApiError
            {
                Field = $"siteDetails.roadDetails[{second.Index}].roadRowId",
                Code = "DUPLICATE_ROAD",
                Message = $"Roads {string.Join(" and ", group.Select(x => x.Index + 1))} " +
                          "are the same declared road."
            });
        }

        // A road the officer marked "not found" is being deleted; it is not a side.
        var live = roads.Select((r, i) => (Road: r, Index: i))
                        .Where(x => x.Road.RoadStatus != RoadDeleted)
                        .ToList();

        if (live.Count == 0)
        {
            errors.Add(new ApiError
            {
                Field = "siteDetails.roadDetails",
                Code = "REQUIRED",
                Message = "Every road was marked not found. Record the road the plot actually faces."
            });
            return;
        }

        if (live.Count > MaxRoads)
            errors.Add(new ApiError
            {
                Field = "siteDetails.roadDetails",
                Code = "TOO_MANY_ROADS",
                Message = $"A plot can face at most {MaxRoads} roads, but {live.Count} were submitted."
            });

        // The count the officer asserted - the declared one if they agreed with it, their
        // own if they corrected it. The app always sends it.
        if (site.RoadFacingSides is { } sides && live.Count != sides)
            errors.Add(new ApiError
            {
                Field = "siteDetails.roadDetails",
                Code = "ROAD_COUNT_MISMATCH",
                Message = $"The plot faces {sides} {Roads(sides)}, but {live.Count} " +
                          $"{(live.Count == 1 ? "was" : "were")} submitted. " +
                          "Remove the extra road or correct the number of roads."
            });

        var asserted = site.RoadFacingSides ?? live.Count;
        if (site.IsCornerPlot == 0 && asserted != 1)
            errors.Add(new ApiError
            {
                Field = "siteDetails.isCornerPlot",
                Code = "CORNER_PLOT_MISMATCH",
                Message = $"A plot that is not a corner plot faces one road, but {asserted} were given."
            });
        if (site.IsCornerPlot == 1 && asserted < 2)
            errors.Add(new ApiError
            {
                Field = "siteDetails.isCornerPlot",
                Code = "CORNER_PLOT_MISMATCH",
                Message = "A corner plot faces at least two roads, but only one was given."
            });

        // The same public road named twice. A pair of roads confirmed unchanged is never
        // reported: two declared rows may legitimately name the same master road (a corner
        // plot whose citizen declared two lanes off one road). At least one of the pair
        // must have been chosen by the officer (updated or added). 999 is the shared
        // "not in the list" sentinel and identifies nothing.
        foreach (var group in live.Where(x => x.Road.IsPresentInPublicRoadList == 1
                                              && !string.IsNullOrWhiteSpace(x.Road.RoadId)
                                              && x.Road.RoadId!.Trim() != NotInListRoadId)
                                  .GroupBy(x => x.Road.RoadId!.Trim())
                                  .Where(g => g.Count() > 1
                                              && g.Any(x => x.Road.RoadStatus is 1 or 3)))
        {
            var second = group.Skip(1).First();
            var name = group.Select(x => x.Road.ActualRoadName ?? x.Road.RoadName)
                            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            errors.Add(new ApiError
            {
                Field = $"siteDetails.roadDetails[{second.Index}].roadId",
                Code = "DUPLICATE_ROAD",
                Message = $"Roads {string.Join(" and ", group.Select(x => x.Index + 1))} are " +
                          $"the same road{(name is null ? "" : $" ({name})")}. " +
                          "Each road facing the plot should appear once."
            });
        }
    }

    private static string Roads(int n) => n == 1 ? "road" : "roads";

    private static void TooLong(List<ApiError> errors, string field, string? value, int max, int index)
    {
        if (value is null || value.Length <= max) return;
        errors.Add(new ApiError
        {
            Field = field,
            Code = "TOO_LONG",
            Message = $"Road {index + 1}: this text is {value.Length} characters; at most {max} can be stored."
        });
    }

    private static void RequireEvidence(List<ApiError> errors, int index, string field, bool present, string what)
    {
        if (present) return;
        errors.Add(new ApiError
        {
            Field = $"siteDetails.roadDetails[{index}].{field}",
            Code = "REQUIRED",
            Message = $"Road {index + 1} is a private road and needs {what}."
        });
    }

    private static void Require(List<ApiError> errors, string field, int? value)
    {
        if (value is null)
            errors.Add(new ApiError
            {
                Field = field,
                Code = "REQUIRED",
                Message = "This answer is needed before the survey can be submitted."
            });
    }

    private static void RejectNullIsland(List<ApiError> errors, string field, double? lat, double? lng)
    {
        if (lat is null || lng is null) return;
        if (Math.Abs(lat.Value) < 0.0001 && Math.Abs(lng.Value) < 0.0001)
            errors.Add(new ApiError
            {
                Field = field,
                Code = "NO_GPS_FIX",
                Message = "The device recorded no GPS fix for this point. Please capture it again."
            });
    }
}

/// <summary>
/// Raised when one or more roads were rejected by the procedure. Carries every rejection
/// so the officer is told about all of them at once rather than one per attempt.
/// </summary>
internal sealed class SubmitRejectedException(
    IReadOnlyList<ApiError> errors,
    string code = ApiErrorCodes.ValidationFailed,
    string summary = "Some answers were not accepted. Nothing was saved.")
    : Exception(summary)
{
    public IReadOnlyList<ApiError> Errors { get; } = errors;
    public string Code { get; } = code;
    public string Summary { get; } = summary;
}
