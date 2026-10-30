using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Common;
using PropertyGpsApi.Infrastructure;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Models;
using PropertyGpsApi.Services;
using Xunit;

namespace PropertyGpsApi.Tests;

/// <summary>
/// dbo.GpsApiLog (db/46): one row per RI fetch, submit, allot / unassign and sign-in, with the
/// request and response JSON - the OTP and the token blanked, photos by name and size, a fetch
/// summarised (user, 2026-10-30).
/// </summary>
public sealed class ApiCallLogTests
{
    private const string Base = "v1/api/gbagps/singlesite/";

    [Theory]
    [InlineData(Base + "auth/otp/send", "OTP_SEND")]
    [InlineData(Base + "auth/otp/verify", "OTP_VERIFY")]
    [InlineData(Base + "propertyinfo/assign", "ALLOT")]
    [InlineData(Base + "propertyinfo/unassign", "UNASSIGN")]
    [InlineData(Base + "propertyinfo/add-new", "SUBMIT")]
    [InlineData(Base + "propertyinfo/ward-sync", "FETCH")]
    [InlineData(Base + "propertyinfo/fetch", "FETCH")]
    [InlineData(Base + "propertyinfo/ward-sync/ack", null)]
    [InlineData(Base + "propertyinfo/ward-sync/holders", null)]
    [InlineData(Base + "auth/me", null)]
    [InlineData(null, null)]
    public void Logs_exactly_the_six_calls(string? template, string? action) =>
        Assert.Equal(action, ApiCallLogBuilder.ActionFor(template));

    [Fact]
    public void The_otp_and_the_token_are_never_stored()
    {
        var entry = new ApiCallLogEntry { Action = "OTP_VERIFY" };
        ApiCallLogBuilder.Request(entry,
            new Dictionary<string, object?> { ["request"] = new VerifyOtpRequest { Mobile = "9000000001", Otp = "482913", DeviceId = "a:phone" } },
            []);
        ApiCallLogBuilder.Response(entry, 200,
            ApiResponse<VerifyOtpResponse>.Ok(new VerifyOtpResponse { Token = "eyJ.secret.token", UserId = 14 }));

        Assert.DoesNotContain("482913", entry.RequestBody);
        Assert.DoesNotContain("eyJ.secret.token", entry.ResponseBody);
        Assert.Contains(ApiCallLogBuilder.Blanked, entry.RequestBody);
        Assert.Contains(ApiCallLogBuilder.Blanked, entry.ResponseBody);
        Assert.Equal("9000000001", entry.MobileNumber);
        Assert.Equal("a:phone", entry.DeviceId);
        Assert.Equal(14, entry.OfficerId);
        Assert.True(entry.IsSuccess);
    }

    [Fact]
    public void A_submit_stores_the_survey_json_and_its_photos_by_name_and_size()
    {
        var entry = new ApiCallLogEntry { Action = "SUBMIT" };
        ApiCallLogBuilder.Request(entry,
            new Dictionary<string, object?> { ["payload"] = """{"applicationId":"202610150000043","epid":"38-521-372","ulbCode":43,"wardNumber":"11"}""" },
            [("property_1.jpg", 204800), ("map_1.jpg", 102400)]);

        var body = JsonNode.Parse(entry.RequestBody)!;
        Assert.Equal("38-521-372", body["payload"]!["epid"]!.GetValue<string>());
        Assert.Equal("property_1.jpg", body["files"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(204800, body["files"]![0]!["bytes"]!.GetValue<long>());
        Assert.Equal("202610150000043", entry.ApplicationDisplayId);
        Assert.Equal(43, entry.UlbCode);
        Assert.Equal("11", entry.WardNumber);
        Assert.Equal("2 file(s), 300 KB", entry.AdditionalInfo);
    }

    [Fact]
    public void A_refused_submit_keeps_its_reasons()
    {
        var entry = new ApiCallLogEntry { Action = "SUBMIT" };
        ApiCallLogBuilder.Response(entry, 422, new ApiResponse<SubmitVerificationResponse>
        {
            Success = false,
            Code = "DUPLICATE_ROAD",
            Message = "Roads 1 and 2 are the same road.",
        });

        Assert.False(entry.IsSuccess);
        Assert.Equal(422, entry.HttpStatus);
        Assert.Equal("DUPLICATE_ROAD", entry.ErrorCode);
        Assert.Contains("Roads 1 and 2 are the same road.", entry.ResponseBody);
    }

    [Fact]
    public void An_allot_names_its_application()
    {
        var entry = new ApiCallLogEntry { Action = "ALLOT" };
        ApiCallLogBuilder.Request(entry, new Dictionary<string, object?> { ["request"] = new AssignRequest { AppId = 43 } }, []);
        Assert.Equal(43, entry.AppId);
    }

    [Fact]
    public void A_fetch_reply_is_summarised_not_stored_whole()
    {
        var entry = new ApiCallLogEntry { Action = "FETCH" };
        ApiCallLogBuilder.Request(entry,
            new Dictionary<string, object?> { ["request"] = new WardSyncRequest { UlbCode = 43, WardNumber = "11", LocalCount = 2 } },
            []);
        ApiCallLogBuilder.Response(entry, 200, ApiResponse<WardSyncResponse>.Ok(new WardSyncResponse
        {
            ServerCount = 13,
            LocalCount = 2,
            MissingCount = 11,
            Items = [new PropertyDto { AppId = 1, OwnerNames = "An owner" }, new PropertyDto { AppId = 3, OwnerNames = "Another" }],
            MinMixedExtentPercent = 10,
        }));

        Assert.Equal(43, entry.UlbCode);
        Assert.Equal("11", entry.WardNumber);
        var data = JsonNode.Parse(entry.ResponseBody)!["data"]!;
        Assert.Equal(13, data["serverCount"]!.GetValue<int>());
        Assert.Equal("[1,3]", data["appIds"]!.ToJsonString());
        Assert.DoesNotContain("An owner", entry.ResponseBody);
    }

    [Fact]
    public void A_call_that_failed_with_an_exception_is_logged_with_its_code()
    {
        var entry = new ApiCallLogEntry { Action = "ALLOT" };
        ApiCallLogBuilder.Failure(entry, ApiException.Unprocessable("Application is Already Assigned .", ApiErrorCodes.AlreadyAssigned));

        Assert.False(entry.IsSuccess);
        Assert.Equal(422, entry.HttpStatus);
        Assert.Equal(ApiErrorCodes.AlreadyAssigned, entry.ErrorCode);
    }

    // ---- the filter --------------------------------------------------------------------

    private sealed class RecordingWriter : IApiCallLogWriter
    {
        public List<ApiCallLogEntry> Written { get; } = [];
        public Task WriteAsync(ApiCallLogEntry entry) { Written.Add(entry); return Task.CompletedTask; }
    }

    private static ActionExecutingContext Executing(string template, Dictionary<string, object?> args)
    {
        var action = new ControllerActionDescriptor { AttributeRouteInfo = new AttributeRouteInfo { Template = template } };
        var context = new ActionContext(new DefaultHttpContext(), new RouteData(), action);
        return new ActionExecutingContext(context, [], args, controller: new object());
    }

    private static Task<ActionExecutedContext> Run(ActionExecutingContext executing, IActionResult result) =>
        Task.FromResult(new ActionExecutedContext(executing, [], controller: new object()) { Result = result });

    [Fact]
    public async Task The_filter_writes_one_row_for_a_logged_call()
    {
        var writer = new RecordingWriter();
        var filter = new ApiCallLogFilter(writer, Options.Create(new ApiLogOptions()));
        var executing = Executing(Base + "propertyinfo/assign", new() { ["request"] = new AssignRequest { AppId = 43 } });

        await filter.OnActionExecutionAsync(executing, () => Run(executing, new OkObjectResult(ApiResponse<bool>.Ok(true))));

        var row = Assert.Single(writer.Written);
        Assert.Equal("ALLOT", row.Action);
        Assert.Equal(43, row.AppId);
        Assert.True(row.IsSuccess);
        Assert.NotNull(row.DurationMs);
    }

    [Fact]
    public async Task Nothing_is_logged_when_switched_off_or_for_other_calls()
    {
        var writer = new RecordingWriter();
        var off = new ApiCallLogFilter(writer, Options.Create(new ApiLogOptions { Enabled = false }));
        var on = new ApiCallLogFilter(writer, Options.Create(new ApiLogOptions()));
        var assign = Executing(Base + "propertyinfo/assign", new());
        var me = Executing(Base + "auth/me", new());

        await off.OnActionExecutionAsync(assign, () => Run(assign, new OkResult()));
        await on.OnActionExecutionAsync(me, () => Run(me, new OkResult()));

        Assert.Empty(writer.Written);
    }

    private sealed class BrokenDatabase : ISqlConnectionFactory
    {
        public Task<SqlConnection> OpenAsync(DbTarget target, CancellationToken ct = default) =>
            throw new InvalidOperationException("Invalid object name 'dbo.GpsApiLog'.");
    }

    [Fact]
    public async Task A_log_that_cannot_be_written_never_fails_the_call()
    {
        var writer = new ApiCallLogWriter(new BrokenDatabase(), NullLogger<ApiCallLogWriter>.Instance);
        await writer.WriteAsync(new ApiCallLogEntry { Action = "FETCH" }); // no exception
    }

    [Fact]
    public void The_insert_finds_a_submits_app_id_from_its_display_id()
    {
        Assert.Contains("COALESCE(@AppId", ApiCallLogWriter.InsertSql);
        Assert.Contains("m.App_DisplayId = @ApplicationDisplayId", ApiCallLogWriter.InsertSql);
    }
}
