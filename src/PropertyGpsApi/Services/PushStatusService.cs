using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Models;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Infrastructure.Options;

using PropertyGpsApi.Interfaces;

namespace PropertyGpsApi.Services;


internal sealed class PushStatusService(
    ISqlConnectionFactory connections,
    IOptions<StoredProcedureOptions> procedures,
    ILogger<PushStatusService> logger) : IPushStatusService
{
    // USP_U_GpsPushedDetails branches on exactly these two values. Anything else falls
    // through every branch and returns NO result set at all, which would look like a silent
    // failure - so the mapping is closed here rather than passed through from the client.
    internal const int GpsSuccess = 200;
    internal const int GpsFailure = 500;

    /// <summary>
    /// The device's verdict as one of the only two codes the procedure understands.
    /// </summary>
    /// <remarks>
    /// Closed on purpose. The client sends a bool, never a status code: if this ever widened
    /// to pass a number through, an unrecognised value would take no branch in the procedure,
    /// return no row, and be reported as "not accepted" while the application quietly kept
    /// IsPushedToGps = 0 - a ward that downloads forever with no error anywhere.
    /// </remarks>
    internal static int StatusCodeFor(bool success) => success ? GpsSuccess : GpsFailure;

    /// <summary>
    /// What is recorded against the application: the device's own words when it sent any,
    /// and otherwise a sentence saying which side the record ended up on.
    /// </summary>
    internal static string RemarkFor(PushStatusItem item) =>
        string.IsNullOrWhiteSpace(item.Message)
            ? (item.Success ? "Stored on device via PropertyGpsApi" : "Device reported a failure")
            : item.Message.Trim();

    public async Task<PushStatusResponse> RecordAsync(
        PushStatusRequest request, long officerId, int roleId, CancellationToken ct)
    {
        var sp = procedures.Value;
        var results = new List<PushStatusResult>(request.Items.Count);

        await using var connection = await connections.OpenAsync(DbTarget.B2A, ct);

        foreach (var item in request.Items)
        {
            ct.ThrowIfCancellationRequested();

            var statusCode = StatusCodeFor(item.Success);
            var message = RemarkFor(item);

            // The device saying it could not store a record. The flag stays 0 and the ward
            // fetch will offer it again, which is the correct outcome - but it is also what a
            // device quietly failing to save anything looks like, so it is said out loud.
            if (!item.Success)
                logger.LogWarning(
                    "Device reported it did NOT store application {ApplicationId} (EPID {Epid}): {Reason}",
                    item.ApplicationId, item.Epid, message);

            var update = new DynamicParameters();
            update.Add("@App_DisplayId", item.ApplicationId, DbType.AnsiString, size: 50);
            update.Add("@EPID", item.Epid, DbType.AnsiString, size: 50);
            update.Add("@StatusCode", statusCode, DbType.Int32);
            update.Add("@Message", message, DbType.String, size: -1);

            var row = await connection.QueryFirstOrDefaultAsync<PushStatusRow>(
                Sp.Call(sp.UpdatePushedDetails, update, ct));

            var accepted = row?.Status == 1;
            var verdict = row?.Message?.Trim() ?? "The status procedure returned no result.";

            // Named per item, not merely counted. The device cannot fail this call loudly - a
            // ward that downloaded and saved must not be discarded because the acknowledgement
            // did not land - so this log is the only place a rejected acknowledgement is
            // visible at all. Without it, an application the procedure never matched reads
            // exactly like one it marked pushed, and simply turns up again on the next fetch.
            if (!accepted)
                logger.LogWarning(
                    "Push status NOT accepted for application {ApplicationId} (EPID {Epid}) " +
                    "from officer {OfficerId}: {Verdict}",
                    item.ApplicationId, item.Epid, officerId, verdict);

            results.Add(new PushStatusResult
            {
                ApplicationId = item.ApplicationId,
                Epid = item.Epid,
                Accepted = accepted,
                Message = verdict
            });

            // Audit trail for the exchange, per BBMP's own convention for this endpoint.
            await LogExchangeAsync(connection, item, statusCode, row, accepted, ct);

            // A device-side failure is also recorded application-wise, so a record that never
            // reaches the field is visible to somebody without trawling the logs.
            if (!item.Success)
                await LogNotPushedAsync(connection, item, statusCode, message, officerId, roleId, ct);
        }

        var acceptedCount = results.Count(r => r.Accepted);

        logger.LogInformation(
            "Push status from officer {OfficerId}: {Accepted} accepted, {Rejected} rejected of {Total}",
            officerId, acceptedCount, results.Count - acceptedCount, results.Count);

        return new PushStatusResponse
        {
            Accepted = acceptedCount,
            Rejected = results.Count - acceptedCount,
            Results = results
        };
    }

    private async Task LogExchangeAsync(
        Microsoft.Data.SqlClient.SqlConnection connection,
        PushStatusItem item, int statusCode, PushStatusRow? row, bool accepted, CancellationToken ct)
    {
        var p = new DynamicParameters();
        p.Add("@ApplicationDisplayId", item.ApplicationId, DbType.AnsiString, size: 50);
        p.Add("@PropertyID", item.Epid, DbType.AnsiString, size: 50);
        p.Add("@RequestJson", JsonSerializer.Serialize(item), DbType.String, size: -1);
        p.Add("@ResponseJson", JsonSerializer.Serialize(row), DbType.String, size: -1);
        p.Add("@ResponseCode", statusCode, DbType.Int32);
        p.Add("@ResponseStatus", accepted, DbType.Boolean);

        try
        {
            await connection.ExecuteAsync(Sp.Call(procedures.Value.InsertPushExchange, p, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Losing the audit row must not fail the acknowledgement: the application flag is
            // already set, and reporting failure would make the device send it all again.
            logger.LogWarning(ex, "Could not write the push exchange log for {ApplicationId}",
                item.ApplicationId);
        }
    }

    private async Task LogNotPushedAsync(
        Microsoft.Data.SqlClient.SqlConnection connection,
        PushStatusItem item, int statusCode, string message, long officerId, int roleId,
        CancellationToken ct)
    {
        var p = new DynamicParameters();
        p.Add("@applicationDisplayId", item.ApplicationId, DbType.String, size: 50);
        p.Add("@propertyId", item.Epid, DbType.String, size: 50);
        p.Add("@StatusCode", statusCode, DbType.Int32);
        p.Add("@Message", message, DbType.String, size: -1);
        p.Add("@Crole", roleId, DbType.Int32);
        p.Add("@Cby", officerId, DbType.Int32);
        p.Add("@Active", 1, DbType.Int32);

        try
        {
            await connection.ExecuteAsync(Sp.Call(procedures.Value.InsertNotPushed, p, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not write the not-pushed log for {ApplicationId}",
                item.ApplicationId);
        }
    }
}
