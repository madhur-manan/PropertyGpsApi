using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Infrastructure.Security;
using PropertyGpsApi.Services;

namespace PropertyGpsApi.Infrastructure;

/// <summary>
/// Writes one dbo.GpsApiLog row (db/46) for each RI fetch, submit, allot / unassign and sign-in
/// (user, 2026-10-30). Registered once for every action and picks its calls by route, so no
/// controller carries an attribute for it. Runs after model binding, which is after the request
/// body was decrypted, so the logged JSON is readable. ApiLog:Enabled turns it off.
/// </summary>
internal sealed class ApiCallLogFilter(IApiCallLogWriter writer, IOptions<ApiLogOptions> options) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var action = options.Value.Enabled
            ? ApiCallLogBuilder.ActionFor(context.ActionDescriptor.AttributeRouteInfo?.Template)
            : null;
        if (action is null)
        {
            await next();
            return;
        }

        var http = context.HttpContext;
        var entry = new ApiCallLogEntry
        {
            Action = action,
            Endpoint = http.Request.Path.Value ?? "",
            OfficerId = http.User.OptionalLong(GpsClaims.UserId),
            MobileNumber = http.User.FindFirst(GpsClaims.Mobile)?.Value,
            TraceId = http.TraceIdentifier,
            AppVersion = http.Request.Headers["X-App-Version"].FirstOrDefault(),
        };

        try
        {
            ApiCallLogBuilder.Request(entry, context.ActionArguments, Files(http));
        }
        catch (Exception)
        {
            entry.RequestBody = "{\"unreadable\":true}";
        }

        var clock = Stopwatch.StartNew();
        var executed = await next();
        entry.DurationMs = (int)Math.Min(int.MaxValue, clock.ElapsedMilliseconds);

        try
        {
            if (executed.Exception is { } exception && !executed.ExceptionHandled)
                ApiCallLogBuilder.Failure(entry, exception);
            else if (executed.Result is ObjectResult result)
                ApiCallLogBuilder.Response(entry, result.StatusCode ?? StatusCodes.Status200OK, result.Value);
            else
                ApiCallLogBuilder.Response(entry, (executed.Result as IStatusCodeActionResult)?.StatusCode ?? http.Response.StatusCode, null);
        }
        catch (Exception)
        {
            entry.ResponseBody = "{\"unreadable\":true}";
        }

        await writer.WriteAsync(entry);
    }

    /// <summary>A submit's photos, by name and size only - never their bytes.</summary>
    private static IEnumerable<(string Name, long Length)> Files(HttpContext http) =>
        http.Request.HasFormContentType
            ? http.Request.Form.Files.Select(f => (f.FileName, f.Length)).ToList()
            : [];
}
