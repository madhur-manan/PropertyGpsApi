using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using PropertyGpsApi.Common;
using PropertyGpsApi.Infrastructure.Data;

namespace PropertyGpsApi.Services;

/// <summary>ApiLog section of appsettings.json: whether RI calls are written to dbo.GpsApiLog.</summary>
public sealed class ApiLogOptions
{
    public const string Section = "ApiLog";

    public bool Enabled { get; init; } = true;
}

/// <summary>One row of dbo.GpsApiLog (db/46).</summary>
internal sealed class ApiCallLogEntry
{
    public long? AppId { get; set; }
    public string? ApplicationDisplayId { get; set; }
    public long? OfficerId { get; set; }
    public string? MobileNumber { get; set; }
    public string Action { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public int? UlbCode { get; set; }
    public string? WardNumber { get; set; }
    public string RequestBody { get; set; } = "{}";
    public string ResponseBody { get; set; } = "{}";
    public int HttpStatus { get; set; }
    public bool IsSuccess { get; set; }
    public string? ErrorCode { get; set; }
    public int? DurationMs { get; set; }
    public string? DeviceId { get; set; }
    public string? AppVersion { get; set; }
    public string? TraceId { get; set; }
    public string? AdditionalInfo { get; set; }
}

internal interface IApiCallLogWriter
{
    Task WriteAsync(ApiCallLogEntry entry);
}

/// <summary>
/// Writes the log row. It never fails the RI's call: a database without dbo.GpsApiLog (db/46
/// not run yet) or any other failure is logged once as a warning and otherwise ignored.
/// </summary>
internal sealed class ApiCallLogWriter(ISqlConnectionFactory connections, ILogger<ApiCallLogWriter> logger)
    : IApiCallLogWriter
{
    private static int warned;

    // A submit names its application by display id; the App_Id is looked up so every row
    // of one application can be found by GpsApiLog_AppId.
    internal const string InsertSql = """
        INSERT dbo.GpsApiLog
            (GpsApiLog_AppId, GpsApiLog_ApplicationDisplayId, GpsApiLog_OfficerId, GpsApiLog_MobileNumber,
             GpsApiLog_Action, GpsApiLog_Endpoint, GpsApiLog_UlbCode, GpsApiLog_WardNumber,
             GpsApiLog_RequestBody, GpsApiLog_ResponseBody, GpsApiLog_HttpStatus, GpsApiLog_IsSuccess,
             GpsApiLog_ErrorCode, GpsApiLog_DurationMs, GpsApiLog_DeviceId, GpsApiLog_AppVersion,
             GpsApiLog_TraceId, GpsApiLog_AdditionalInfo)
        VALUES
            (COALESCE(@AppId, (SELECT TOP (1) m.App_Id FROM dbo.BtoAMainApp m WITH (NOLOCK)
                                WHERE @ApplicationDisplayId IS NOT NULL AND m.App_DisplayId = @ApplicationDisplayId
                                ORDER BY m.App_Active DESC, m.App_Id DESC)),
             @ApplicationDisplayId, @OfficerId, @MobileNumber,
             @Action, @Endpoint, @UlbCode, @WardNumber,
             @RequestBody, @ResponseBody, @HttpStatus, @IsSuccess,
             @ErrorCode, @DurationMs, @DeviceId, @AppVersion,
             @TraceId, @AdditionalInfo);
        """;

    public async Task WriteAsync(ApiCallLogEntry entry)
    {
        try
        {
            await using var connection = await connections.OpenAsync(DbTarget.B2A, CancellationToken.None);
            await connection.ExecuteAsync(new CommandDefinition(InsertSql, Parameters(entry), commandTimeout: 15));
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref warned, 1) == 0)
                logger.LogWarning(ex, "Could not write dbo.GpsApiLog (is db/46 run?); RI calls go on unlogged");
        }
    }

    internal static DynamicParameters Parameters(ApiCallLogEntry e)
    {
        var p = new DynamicParameters();
        p.Add("@AppId", e.AppId, DbType.Int64);
        p.Add("@ApplicationDisplayId", Cut(e.ApplicationDisplayId, 50), DbType.String, size: 50);
        p.Add("@OfficerId", e.OfficerId, DbType.Int64);
        p.Add("@MobileNumber", Cut(e.MobileNumber, 15), DbType.AnsiString, size: 15);
        p.Add("@Action", Cut(e.Action, 50), DbType.AnsiString, size: 50);
        p.Add("@Endpoint", Cut(e.Endpoint, 200), DbType.AnsiString, size: 200);
        p.Add("@UlbCode", e.UlbCode, DbType.Int32);
        p.Add("@WardNumber", Cut(e.WardNumber, 20), DbType.String, size: 20);
        p.Add("@RequestBody", e.RequestBody, DbType.String, size: -1);
        p.Add("@ResponseBody", e.ResponseBody, DbType.String, size: -1);
        p.Add("@HttpStatus", e.HttpStatus, DbType.Int32);
        p.Add("@IsSuccess", e.IsSuccess, DbType.Boolean);
        p.Add("@ErrorCode", Cut(e.ErrorCode, 50), DbType.AnsiString, size: 50);
        p.Add("@DurationMs", e.DurationMs, DbType.Int32);
        p.Add("@DeviceId", Cut(e.DeviceId, 64), DbType.String, size: 64);
        p.Add("@AppVersion", Cut(e.AppVersion, 20), DbType.AnsiString, size: 20);
        p.Add("@TraceId", Cut(e.TraceId, 64), DbType.AnsiString, size: 64);
        p.Add("@AdditionalInfo", Cut(e.AdditionalInfo, 200), DbType.String, size: 200);
        return p;
    }

    private static string? Cut(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}

/// <summary>
/// What each logged call stores (user, 2026-10-30): the request and the response as JSON,
/// with the OTP and the sign-in token blanked, a submit's photos by name and size only, and
/// a fetch's reply summarised (counts and App_Ids) rather than 100 full applications.
/// </summary>
internal static class ApiCallLogBuilder
{
    internal const string Blanked = "******";

    /// <summary>The logged calls, by the end of their route template.</summary>
    private static readonly (string Suffix, string Action)[] Routes =
    [
        ("auth/otp/send", "OTP_SEND"),
        ("auth/otp/verify", "OTP_VERIFY"),
        ("propertyinfo/assign", "ALLOT"),
        ("propertyinfo/unassign", "UNASSIGN"),
        ("propertyinfo/add-new", "SUBMIT"),
        ("propertyinfo/ward-sync", "FETCH"),
        ("propertyinfo/fetch", "FETCH"),
    ];

    /// <summary>The action for a route template, or null when the call is not logged.</summary>
    internal static string? ActionFor(string? routeTemplate)
    {
        var template = (routeTemplate ?? "").Trim('/');
        foreach (var (suffix, action) in Routes)
            if (template.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return action;
        return null;
    }

    /// <summary>Fills the request side of <paramref name="entry"/> from the bound action arguments.</summary>
    internal static void Request(
        ApiCallLogEntry entry, IDictionary<string, object?> arguments, IEnumerable<(string Name, long Length)> files)
    {
        if (entry.Action == "SUBMIT")
        {
            // add-new binds one form field, "payload": the survey JSON as text.
            var payload = arguments.Values.OfType<string>().FirstOrDefault() ?? "";
            var survey = Parse(payload) ?? new JsonObject { ["unreadable"] = payload.Length > 2000 ? payload[..2000] : payload };
            var list = files.Select(f => new JsonObject { ["name"] = f.Name, ["bytes"] = f.Length }).ToArray<JsonNode?>();
            entry.RequestBody = new JsonObject { ["payload"] = survey, ["files"] = new JsonArray(list) }.ToJsonString(JsonOptions.Default);
            entry.ApplicationDisplayId = Text(survey["applicationId"]);
            entry.UlbCode = Int(survey["ulbCode"]);
            entry.WardNumber = Text(survey["wardNumber"]);
            var total = list.Length == 0 ? 0 : files.Sum(f => f.Length);
            entry.AdditionalInfo = $"{list.Length} file(s), {total / 1024.0:0.#} KB";
            return;
        }

        var body = arguments.Values.FirstOrDefault(v => v is not null and not CancellationToken);
        var node = body is null ? new JsonObject() : JsonSerializer.SerializeToNode(body, body.GetType(), JsonOptions.Default) ?? new JsonObject();
        if (node is JsonObject o)
        {
            if (entry.Action == "OTP_VERIFY" && o.ContainsKey("otp")) o["otp"] = Blanked;
            entry.MobileNumber ??= Text(o["mobile"]);
            entry.DeviceId = Text(o["deviceId"]);
            entry.UlbCode = Int(o["ulbCode"]);
            entry.WardNumber = Text(o["wardNumber"]);
            if (entry.Action is "ALLOT" or "UNASSIGN" && Int(o["appId"]) is { } appId) entry.AppId = appId;
        }
        entry.RequestBody = node.ToJsonString(JsonOptions.Default);
    }

    /// <summary>Fills the response side from the reply the action produced.</summary>
    internal static void Response(ApiCallLogEntry entry, int status, object? value)
    {
        entry.HttpStatus = status;
        var node = value is null ? null : JsonSerializer.SerializeToNode(value, value.GetType(), JsonOptions.Default);
        var envelope = node as JsonObject;
        entry.IsSuccess = status < 400 && (envelope is null || envelope["success"]?.GetValue<bool>() != false);
        entry.ErrorCode = entry.IsSuccess ? null : Text(envelope?["code"]);

        if (envelope?["data"] is JsonObject data)
        {
            if (data.ContainsKey("token")) data["token"] = Blanked;
            if (data.ContainsKey("devOtp")) data["devOtp"] = Blanked;
            if (entry.Action == "OTP_VERIFY" && Int(data["userId"]) is { } userId) entry.OfficerId ??= userId;
            if (entry.Action == "FETCH") envelope["data"] = FetchSummary(data);
        }
        else if (entry.Action == "FETCH" && envelope?["data"] is JsonArray rows)
        {
            envelope["data"] = new JsonObject { ["rows"] = rows.Count, ["appIds"] = AppIds(rows) };
        }
        entry.ResponseBody = node?.ToJsonString(JsonOptions.Default) ?? "{}";
    }

    /// <summary>A failure that left the action as an exception (handled later by the global handler).</summary>
    internal static void Failure(ApiCallLogEntry entry, Exception exception)
    {
        var (status, code, message) = exception is ApiException api
            ? (api.StatusCode, api.Code, api.Message)
            : (StatusCodes.Status500InternalServerError, "SERVER_ERROR", "Something went wrong at our end.");
        entry.HttpStatus = status;
        entry.IsSuccess = false;
        entry.ErrorCode = code;
        entry.ResponseBody = new JsonObject { ["success"] = false, ["code"] = code, ["message"] = message }
            .ToJsonString(JsonOptions.Default);
    }

    /// <summary>A ward-sync page: the counts and the App_Ids sent, not every field of every application.</summary>
    internal static JsonObject FetchSummary(JsonObject data)
    {
        var summary = new JsonObject();
        foreach (var key in new[] { "serverCount", "localCount", "missingCount", "returned",
                                    "minMixedExtentPercent", "maxCompletedOffline", "maxAllottedPerOfficer", "unassignOnSignOut" })
            if (data.ContainsKey(key)) summary[key] = data[key]?.DeepClone();
        if (data["items"] is JsonArray items) summary["appIds"] = AppIds(items);
        if (data["closed"] is JsonArray closed) summary["closed"] = closed.Count;
        if (data["holders"] is JsonArray holders) summary["holders"] = holders.Count;
        return summary;
    }

    private static JsonArray AppIds(JsonArray rows) =>
        new(rows.OfType<JsonObject>().Select(r => Int(r["appId"])).Where(id => id is not null).Distinct()
                .Select(id => (JsonNode?)JsonValue.Create(id!.Value)).ToArray());

    private static JsonObject? Parse(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? (string.IsNullOrWhiteSpace(s) ? null : s.Trim())
        : node is JsonValue n && n.TryGetValue<long>(out var l) ? l.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : null;

    private static int? Int(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<int>(out var i) ? i
        : node is JsonValue s && s.TryGetValue<string>(out var t) && int.TryParse(t, out var p) ? p
        : null;
}
