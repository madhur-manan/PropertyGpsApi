using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Features.Properties.Dtos;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Infrastructure.Options;

namespace PropertyGpsApi.Features.Properties;

public interface IPushStatusRepository
{
    Task<PushStatusResponse> RecordAsync(
        PushStatusRequest request, long officerId, int roleId, CancellationToken ct);
}

internal sealed class PushStatusRepository(
    ISqlConnectionFactory connections,
    IOptions<StoredProcedureOptions> procedures,
    ILogger<PushStatusRepository> logger) : IPushStatusRepository
{
    // USP_U_GpsPushedDetails branches on exactly these two values. Anything else falls
    // through every branch and returns NO result set at all, which would look like a silent
    // failure - so the mapping is closed here rather than passed through from the client.
    private const int GpsSuccess = 200;
    private const int GpsFailure = 500;

    public async Task<PushStatusResponse> RecordAsync(
        PushStatusRequest request, long officerId, int roleId, CancellationToken ct)
    {
        var sp = procedures.Value;
        var results = new List<PushStatusResult>(request.Items.Count);

        await using var connection = await connections.OpenAsync(DbTarget.B2A, ct);

        foreach (var item in request.Items)
        {
            ct.ThrowIfCancellationRequested();

            var statusCode = item.Success ? GpsSuccess : GpsFailure;
            var message = string.IsNullOrWhiteSpace(item.Message)
                ? (item.Success ? "Stored on device via PropertyGpsApi" : "Device reported a failure")
                : item.Message.Trim();

            var update = new DynamicParameters();
            update.Add("@App_DisplayId", item.ApplicationId, DbType.AnsiString, size: 50);
            update.Add("@EPID", item.Epid, DbType.AnsiString, size: 50);
            update.Add("@StatusCode", statusCode, DbType.Int32);
            update.Add("@Message", message, DbType.String, size: -1);

            var row = await connection.QueryFirstOrDefaultAsync<PushStatusRow>(
                Sp.Call(sp.UpdatePushedDetails, update, ct));

            var accepted = row?.Status == 1;
            results.Add(new PushStatusResult
            {
                ApplicationId = item.ApplicationId,
                Epid = item.Epid,
                Accepted = accepted,
                Message = row?.Message?.Trim() ?? "The status procedure returned no result."
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
