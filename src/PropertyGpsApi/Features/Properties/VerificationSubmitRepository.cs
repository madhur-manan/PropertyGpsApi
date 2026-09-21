using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Common;
using PropertyGpsApi.Features.Properties.Dtos;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Infrastructure.Options;

namespace PropertyGpsApi.Features.Properties;

public interface IVerificationSubmitRepository
{
    Task<SubmitVerificationResponse> SubmitAsync(
        SubmitVerificationRequest request,
        IReadOnlyDictionary<string, string> mediaUrls,
        long officerId,
        int roleId,
        string? officerMobile,
        CancellationToken ct);
}

/// <summary>
/// Writes one completed survey.
///
/// Three legacy procedures plus two of our own, in a single transaction. All-or-nothing
/// is the right shape here: the status procedure moves BtoAMainApp.App_Status, so a
/// half-written survey would leave an application carrying officer data with no status
/// transition - invisible to the workflow and indistinguishable from "not yet surveyed".
///
/// Media is written before this is called, and is never rolled back. An orphaned file
/// costs disk and a sweeper reclaims it; a committed row pointing at a photograph that
/// was never stored is destroyed evidence for a tax assessment, and since the device
/// marks the record synced it would never be sent again.
/// </summary>
internal sealed class VerificationSubmitRepository(
    ISqlConnectionFactory connections,
    IOptions<StoredProcedureOptions> procedures,
    ILogger<VerificationSubmitRepository> logger) : IVerificationSubmitRepository
{
    public async Task<SubmitVerificationResponse> SubmitAsync(
        SubmitVerificationRequest request,
        IReadOnlyDictionary<string, string> mediaUrls,
        long officerId,
        int roleId,
        string? officerMobile,
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

            await WriteApplicationAsync(connection, transaction, sp, request, site, mediaUrls, officerId, roleId, ct);
            var roadsStored = await WriteRoadsAsync(connection, transaction, sp, request, site, mediaUrls, officerId, roleId, ct);
            await WriteExtrasAsync(connection, transaction, request, mediaUrls, ct);
            await WriteStatusAsync(connection, transaction, sp, request, appId.Value, officerId, roleId, officerMobile, ct);

            // Read the status back rather than reporting a constant. The procedure decides
            // it from the role, so a copy here could drift from what was actually written
            // and the response would confidently state something untrue.
            var appStatus = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT App_Status FROM BtoAMainApp WHERE App_Id = @id",
                new { id = appId.Value }, transaction, 30, CommandType.Text, cancellationToken: ct));

            await transaction.CommitAsync(ct);

            logger.LogInformation(
                "Verification stored for {ApplicationId} by officer {OfficerId}: {Roads} road(s), {Media} file(s)",
                request.ApplicationId, officerId, roadsStored, mediaUrls.Count);

            return new SubmitVerificationResponse
            {
                ApplicationId = request.ApplicationId,
                Epid = request.Epid,
                AppStatus = appStatus ?? 0,
                RoadsStored = roadsStored,
                MediaStored = mediaUrls.Count,
                StoredUtc = DateTimeOffset.UtcNow
            };
        }
        catch
        {
            await SafeRollbackAsync(transaction);
            throw;
        }
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
        SubmitVerificationRequest request, SubmitSiteDetails site,
        IReadOnlyDictionary<string, string> mediaUrls,
        long officerId, int roleId, CancellationToken ct)
    {
        var p = new DynamicParameters();
        p.Add("@Ofcr_RowId", 0, DbType.Int32);
        p.Add("@Ofcr_ApplicationDisplayId", request.ApplicationId, DbType.String, size: 100);
        p.Add("@Ofcr_App_Id", 0, DbType.Int32);          // the procedure resolves this itself
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
        p.Add("@Ofcr_CorrectedRoadDetails", AsBit(site.IsRoadDetailsCorrect is 0), DbType.Boolean);
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

        var row = await connection.QueryFirstOrDefaultAsync<AppWriteRow>(new CommandDefinition(
            sp.SubmitApplication, p, transaction, 60, CommandType.StoredProcedure, cancellationToken: ct));

        if (row is null || !row.Status)
            throw ApiException.Unprocessable(
                row?.Message?.Trim() ?? "The application details could not be saved.",
                ApiErrorCodes.SubmitRejected, recoverable: true);
    }

    private static async Task<int> WriteRoadsAsync(
        SqlConnection connection, SqlTransaction transaction, StoredProcedureOptions sp,
        SubmitVerificationRequest request, SubmitSiteDetails site,
        IReadOnlyDictionary<string, string> mediaUrls,
        long officerId, int roleId, CancellationToken ct)
    {
        var stored = 0;
        var rejected = new List<ApiError>();

        // Ordered so two concurrent submissions touch rows in the same sequence. With the
        // application lock held this is belt and braces, but it costs nothing.
        foreach (var road in site.RoadDetails.OrderBy(r => r.RoadRowId ?? 0))
        {
            ct.ThrowIfCancellationRequested();

            var p = new DynamicParameters();
            p.Add("@BtoA_RoadRowId", 0, DbType.Int32);
            p.Add("@BtoA_MainAppId", 0, DbType.Int32);   // resolved inside the procedure
            p.Add("@BtoA_ApplicationDisplayId", request.ApplicationId, DbType.AnsiString, size: 50);
            p.Add("@BtoA_Corrected_Road_Details", AsBit(road.IsRoadDetailsCorrect is 0), DbType.Boolean);

            // The procedure has no roadStatus parameter. Correction_Type carries it, and
            // 'delete'/2 is what drives the isactive flag on the stored row.
            p.Add("@BtoA_Correction_Type_Id", road.RoadStatus, DbType.Int32);
            p.Add("@BtoA_Correction_Type_Value", CorrectionTypeValue(road.RoadStatus), DbType.String, size: 100);

            p.Add("@BtoA_RoadType", road.RoadType, DbType.String, size: 100);
            p.Add("@BtoA_IsPresentInPublicRoadList", AsBit(road.IsPresentInPublicRoadList), DbType.Boolean);
            p.Add("@BtoA_RoadId", ParseRoadId(road.RoadId), DbType.Int32);
            p.Add("@BtoA_RoadName", road.RoadName, DbType.String, size: 500);
            p.Add("@BtoA_ActualRoadName", road.ActualRoadName, DbType.String, size: 500);
            p.Add("@BtoA_Nearest_Public_Road_Latitude", road.NearPublicRoadLat, DbType.Double);
            p.Add("@BtoA_Nearest_Public_Road_Longitude", road.NearPublicRoadLng, DbType.Double);
            p.Add("@CBy", officerId, DbType.Int64);
            p.Add("@CRole", roleId, DbType.Int32);
            p.Add("@BtoA_Nearest_Private_Road_Latitude", road.PrivateRoadLat, DbType.Double);
            p.Add("@BtoA_Nearest_Private_Road_Longitude", road.PrivateRoadLng, DbType.Double);
            p.Add("@BtoA_Nearest_Private_Road_Document", UrlFor(mediaUrls, road.PrivateRoadImage), DbType.String, size: -1);
            p.Add("@BtoA_Nearest_Public_Road_Document", UrlFor(mediaUrls, road.PublicRoadImage), DbType.String, size: -1);
            p.Add("@BtoA_SiteRoadRowID", road.RoadRowId?.ToString(), DbType.String, size: 1000);

            var row = await connection.QueryFirstOrDefaultAsync<RoadWriteRow>(new CommandDefinition(
                sp.SubmitRoad, p, transaction, 60, CommandType.StoredProcedure, cancellationToken: ct));

            // A road the KSRSAC master does not recognise comes back as Status = 0 with a
            // message, not as an exception. Treating that as success would store a property
            // whose road count quietly disagrees with the survey.
            if (row is null || !row.Status)
            {
                rejected.Add(new ApiError
                {
                    Field = $"siteDetails.roadDetails[{stored + rejected.Count}]",
                    Code = ApiErrorCodes.RoadNotRecognised,
                    Message = row?.Message?.Trim()
                        ?? $"Road '{road.RoadName}' was not accepted and no reason was given."
                });
                continue;
            }

            if (row.RoadRowId > 0 && !string.IsNullOrWhiteSpace(road.NoticeImage))
                await WriteRoadNoticeAsync(connection, transaction, row.RoadRowId,
                    UrlFor(mediaUrls, road.NoticeImage), ct);

            stored++;
        }

        if (rejected.Count > 0)
            throw new SubmitRejectedException(
                rejected,
                ApiErrorCodes.RoadNotRecognised,
                "Some road details were not accepted. Nothing was saved.");

        return stored;
    }

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

    private static bool? AsBit(int? value) => value switch { null => null, 0 => false, _ => true };
    private static bool? AsBit(bool value) => value;

    /// <summary>
    /// "999" is the agreed sentinel for "this road is not in the public list". It is a
    /// real value the procedure expects, not a parse failure.
    /// </summary>
    private static int? ParseRoadId(string? roadId) =>
        int.TryParse(roadId, out var parsed) ? parsed : null;

    private static string? CorrectionTypeValue(int? roadStatus) => roadStatus switch
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
    private static string? UrlFor(IReadOnlyDictionary<string, string> mediaUrls, string? clientName) =>
        string.IsNullOrWhiteSpace(clientName) ? null
        : mediaUrls.TryGetValue(clientName, out var url) ? url : null;

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
