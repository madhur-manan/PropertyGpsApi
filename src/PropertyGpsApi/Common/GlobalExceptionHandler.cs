using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Data.SqlClient;
using PropertyGpsApi.Infrastructure.Data;

namespace PropertyGpsApi.Common;

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext http, Exception exception, CancellationToken cancellationToken)
    {
        switch (exception)
        {
            case ApiException api:
                logger.LogWarning("Handled API failure {Code} on {Path}", api.Code, http.Request.Path);
                await ErrorEnvelopeWriter.WriteAsync(
                    http, api.StatusCode, api.Code, api.Message, api.Retryable, api.Recoverable);
                return true;

            case OperationCanceledException when http.RequestAborted.IsCancellationRequested:
                // The officer walked out of coverage. Nothing to say, and nobody to say it to.
                logger.LogInformation("Request aborted by client: {Path}", http.Request.Path);
                return true;

            case SqlException sql:
                var transient = SqlTransience.IsTransient(sql);
                logger.LogError(sql, "SQL failure {Number} on {Path} (transient={Transient})",
                    sql.Number, http.Request.Path, transient);
                await ErrorEnvelopeWriter.WriteAsync(
                    http,
                    transient ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError,
                    transient ? ApiErrorCodes.Upstream : ApiErrorCodes.ServerError,
                    transient
                        ? "The records service is busy. Please try again shortly."
                        : "Something went wrong at our end.",
                    // Even non-transient SQL errors are reported retryable: losing a day of
                    // survey work to a server-side bug is far worse than a wasted retry.
                    retryable: true);
                return true;

            default:
                logger.LogError(exception, "Unhandled exception on {Path}", http.Request.Path);
                await ErrorEnvelopeWriter.WriteAsync(
                    http, StatusCodes.Status500InternalServerError, ApiErrorCodes.ServerError,
                    "Something went wrong at our end.", retryable: true);
                return true;
        }
    }
}
