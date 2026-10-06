using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PropertyGpsApi.Common;
using PropertyGpsApi.Infrastructure.Options;
using PropertyGpsApi.Infrastructure.Security.BodyEncryption;
using static PropertyGpsApi.Tests.FilterHarness;

namespace PropertyGpsApi.Tests;

/// <summary>
/// The filter that opens pgps-body/1 request bodies before model binding. Every row of the
/// header/marker decision table in every mode, the form path through ReadFormAsync, the
/// failure mapping, and what may and may not appear in a log.
/// </summary>
public class RequestBodyDecryptionFilterTests
{
    private const string WardSyncJson =
        """{"corporationId":1,"zoneId":102,"wardId":54,"localCount":3,"knownAppIds":[101,102,103],"batchSize":100}""";

    /// <summary>Distinctive enough that finding it anywhere in a log is proof of a leak.</summary>
    private const string SecretPlaintext =
        """{"remarks":"ENCTEST-ಕನ್ನಡ-7f3a9c","appId":424242,"mobile":"9000000001"}""";

    private static async Task<(bool Reached, DefaultHttpContext Http, CapturingLogger<RequestBodyDecryptionFilter> Log)> Run(
        DefaultHttpContext http, RequestEncryptionOptions options, TimeProvider? clock = null,
        params Microsoft.AspNetCore.Mvc.Abstractions.ParameterDescriptor[] parameters)
    {
        var log = new CapturingLogger<RequestBodyDecryptionFilter>();
        var filter = Filter(options, log, clock);
        var reached = await RunAsync(filter, Context(http, parameters.Length == 0 ? [BodyParameter()] : parameters));
        return (reached, http, log);
    }

    private static async Task<(ApiException Error, CapturingLogger<RequestBodyDecryptionFilter> Log)> Refused(
        DefaultHttpContext http, RequestEncryptionOptions options, TimeProvider? clock = null,
        params Microsoft.AspNetCore.Mvc.Abstractions.ParameterDescriptor[] parameters)
    {
        var log = new CapturingLogger<RequestBodyDecryptionFilter>();
        var filter = Filter(options, log, clock);
        var reached = false;
        var error = await Assert.ThrowsAsync<ApiException>(async () =>
            reached = await RunAsync(filter, Context(http, parameters.Length == 0 ? [BodyParameter()] : parameters)));
        Assert.False(reached);
        Assert.Null(error.InnerException);
        return (error, log);
    }

    private static SealedBody SealWardSync(string plaintext = WardSyncJson, string path = WardSyncPath,
        string method = "POST", string? token = Token) =>
        TestSealer.Seal(plaintext, method, path, "body", token);

    // ---------------------------------------------------------------------------------------
    public class TheDecisionTable
    {
        [Theory]
        [InlineData(RequestEncryptionMode.Optional)]
        [InlineData(RequestEncryptionMode.Required)]
        public async Task Header_and_marker_open_the_body_for_the_binder(RequestEncryptionMode mode)
        {
            var sealedBody = SealWardSync();
            var (reached, http, log) = await Run(JsonRequest(sealedBody.Envelope), Options(mode));

            Assert.True(reached);
            Assert.Equal(WardSyncJson, await ReadBodyAsync(http.Request));
            Assert.Equal(Encoding.UTF8.GetByteCount(WardSyncJson), http.Request.ContentLength);
            Assert.Equal("application/json", http.Request.ContentType);
            Assert.DoesNotContain(log.Lines, l => l.Level >= LogLevel.Information);
        }

        [Theory]
        [InlineData(RequestEncryptionMode.Off)]
        [InlineData(RequestEncryptionMode.Optional)]
        [InlineData(RequestEncryptionMode.Required)]
        public async Task Header_without_marker_is_refused_in_every_mode(RequestEncryptionMode mode)
        {
            var (error, log) = await Refused(JsonRequest(WardSyncJson, header: true), Options(mode));

            Assert.Equal(ApiErrorCodes.BodyDecryptFailed, error.Code);
            Assert.Equal(400, error.StatusCode);
            Assert.True(error.Retryable);
            Assert.Contains(log.Lines, l => l.Level == LogLevel.Information && l.Message.Contains("X-Body-Enc is set"));
        }

        [Theory]
        [InlineData(RequestEncryptionMode.Optional)]
        [InlineData(RequestEncryptionMode.Required)]
        public async Task Marker_without_header_still_opens_and_says_the_header_was_stripped(RequestEncryptionMode mode)
        {
            var sealedBody = SealWardSync();
            var (reached, http, log) = await Run(JsonRequest(sealedBody.Envelope, header: false), Options(mode));

            Assert.True(reached);
            Assert.Equal(WardSyncJson, await ReadBodyAsync(http.Request));
            var line = Assert.Single(log.Lines, l => l.Level == LogLevel.Information);
            Assert.Contains("without the X-Body-Enc header", line.Message);
            Assert.Contains(WardSyncPath, line.Message);
            Assert.Contains(sealedBody.Kid, line.Message);
        }

        [Fact]
        public async Task Plain_body_in_Optional_passes_untouched_and_is_counted_in_the_log()
        {
            var (reached, http, log) = await Run(JsonRequest(WardSyncJson, header: false), Options(RequestEncryptionMode.Optional));

            Assert.True(reached);
            Assert.Equal(WardSyncJson, await ReadBodyAsync(http.Request));
            var line = Assert.Single(log.Lines);
            Assert.Equal(LogLevel.Information, line.Level);
            Assert.Contains("Plain request body accepted", line.Message);
            Assert.Contains(WardSyncPath, line.Message);
        }

        [Fact]
        public async Task Plain_body_in_Off_passes_silently()
        {
            var (reached, http, log) = await Run(JsonRequest(WardSyncJson, header: false), Options(RequestEncryptionMode.Off));

            Assert.True(reached);
            Assert.Equal(WardSyncJson, await ReadBodyAsync(http.Request));
            Assert.Empty(log.Lines);
        }

        [Fact]
        public async Task Plain_body_in_Required_is_refused()
        {
            var (error, log) = await Refused(JsonRequest(WardSyncJson, header: false), Options(RequestEncryptionMode.Required));

            Assert.Equal(ApiErrorCodes.BodyEncryptionRequired, error.Code);
            Assert.Equal(400, error.StatusCode);
            Assert.True(error.Retryable);
            Assert.Contains(log.Lines, l => l.Level == LogLevel.Information && l.Message.Contains(WardSyncPath));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Off_refuses_a_sealed_body(bool header)
        {
            var (error, _) = await Refused(JsonRequest(SealWardSync().Envelope, header: header), Options(RequestEncryptionMode.Off));

            Assert.Equal(ApiErrorCodes.BodyKeyUnknown, error.Code);
            Assert.True(error.Retryable);
        }

        [Theory]
        [InlineData(WardSyncPath)]
        [InlineData("/V1/API/GBAGPS/SINGLESITE/PROPERTYINFO/WARD-SYNC/")]
        public async Task Required_lets_a_plain_body_through_on_a_plain_allowed_path(string allowed)
        {
            var (reached, http, _) = await Run(JsonRequest(WardSyncJson, header: false),
                Options(RequestEncryptionMode.Required, plainAllowedPaths: [allowed]));

            Assert.True(reached);
            Assert.Equal(WardSyncJson, await ReadBodyAsync(http.Request));
        }

        [Fact]
        public async Task A_sealed_body_on_a_plain_allowed_path_is_still_opened()
        {
            var (reached, http, _) = await Run(JsonRequest(SealWardSync().Envelope),
                Options(RequestEncryptionMode.Required, plainAllowedPaths: [WardSyncPath]));

            Assert.True(reached);
            Assert.Equal(WardSyncJson, await ReadBodyAsync(http.Request));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Required_lets_an_empty_body_through_to_model_validation(bool declareLength)
        {
            var (reached, _, _) = await Run(JsonRequest("", header: false, declareLength: declareLength),
                Options(RequestEncryptionMode.Required));

            Assert.True(reached);
        }

        [Fact]
        public async Task Leading_whitespace_before_the_marker_is_still_a_sealed_body()
        {
            var (reached, http, _) = await Run(JsonRequest(" \r\n\t" + SealWardSync().Envelope), Options(RequestEncryptionMode.Required));

            Assert.True(reached);
            Assert.Equal(WardSyncJson, await ReadBodyAsync(http.Request));
        }
    }

    // ---------------------------------------------------------------------------------------
    public class ContentTypes
    {
        [Theory]
        [InlineData("text/json")]
        [InlineData("application/json; charset=utf-8")]
        [InlineData("application/vnd.pgps+json")]
        [InlineData("APPLICATION/JSON")]
        public async Task Every_json_media_type_the_formatter_accepts_is_opened(string contentType)
        {
            var (reached, http, _) = await Run(JsonRequest(SealWardSync().Envelope, contentType: contentType),
                Options(RequestEncryptionMode.Required));

            Assert.True(reached);
            Assert.Equal(WardSyncJson, await ReadBodyAsync(http.Request));
        }

        [Theory]
        [InlineData("text/json")]
        [InlineData("application/problem+json")]
        [InlineData("text/plain")]
        [InlineData("application/octet-stream")]
        [InlineData(null)]
        public async Task Required_refuses_a_plain_body_whatever_its_content_type(string? contentType)
        {
            var (error, _) = await Refused(JsonRequest(WardSyncJson, header: false, contentType: contentType),
                Options(RequestEncryptionMode.Required));

            Assert.Equal(ApiErrorCodes.BodyEncryptionRequired, error.Code);
        }

        [Fact]
        public async Task A_header_on_a_content_type_that_cannot_be_sealed_is_refused()
        {
            var (error, _) = await Refused(JsonRequest(SealWardSync().Envelope, contentType: "text/plain"),
                Options(RequestEncryptionMode.Optional));

            Assert.Equal(ApiErrorCodes.BodyDecryptFailed, error.Code);
        }

        [Fact]
        public async Task Optional_passes_other_content_types_on_for_the_formatter_to_refuse()
        {
            var (reached, http, _) = await Run(JsonRequest("hello", header: false, contentType: "text/plain"),
                Options(RequestEncryptionMode.Optional));

            Assert.True(reached);
            Assert.Equal("hello", await ReadBodyAsync(http.Request));
        }
    }

    // ---------------------------------------------------------------------------------------
    public class WhichActions
    {
        [Theory]
        [InlineData("POST", "")]
        [InlineData("POST", "query")]
        [InlineData("POST", "special")]
        [InlineData("POST", "query,special")]
        [InlineData("GET", "body")]
        [InlineData("DELETE", "body")]
        public async Task Actions_that_bind_no_body_or_form_are_left_untouched(string method, string sources)
        {
            var parameters = sources.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s switch
            {
                "query" => QueryParameter(),
                "special" => UnboundParameter(),
                _ => BodyParameter()
            }).ToArray();

            // Required, a header and a plain body: every reason to refuse, if the filter looked.
            var http = JsonRequest(WardSyncJson, method: method, header: true);
            var original = http.Request.Body;
            var log = new CapturingLogger<RequestBodyDecryptionFilter>();
            var context = Context(http, parameters);

            var reached = await RunAsync(Filter(Options(RequestEncryptionMode.Required), log), context);

            Assert.True(reached);
            Assert.Same(original, http.Request.Body);
            Assert.Equal(0, original.Position);
            Assert.Empty(log.Lines);
        }

        [Theory]
        [InlineData("PUT")]
        [InlineData("PATCH")]
        [InlineData("post")]
        public async Task Put_and_patch_bodies_are_opened_too(string method)
        {
            var sealedBody = TestSealer.Seal(WardSyncJson, method, WardSyncPath, bearerToken: Token);
            var (reached, http, _) = await Run(JsonRequest(sealedBody.Envelope, method: method), Options(RequestEncryptionMode.Required));

            Assert.True(reached);
            Assert.Equal(WardSyncJson, await ReadBodyAsync(http.Request));
        }

        [Fact]
        public async Task A_form_bound_action_is_covered()
        {
            Assert.True(RequestBodyDecryptionFilter.AppliesTo("POST",
                new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor { Parameters = [FormParameter()] }));

            var (error, _) = await Refused(JsonRequest(WardSyncJson, header: false), Options(RequestEncryptionMode.Required),
                null, FormParameter());
            Assert.Equal(ApiErrorCodes.BodyEncryptionRequired, error.Code);
        }

        /// <summary>A large upload aimed at a [FromBody] action, carrying a field that would open.</summary>
        private static (DefaultHttpContext Http, MemoryStream Original, byte[] Body) FormSentToJsonAction(bool header)
        {
            var sealedField = TestSealer.Seal(WardSyncJson, "POST", WardSyncPath, "form:payload", Token).Envelope;
            var body = Multipart.Build([("payload", sealedField)], [("files", "big.jpg", new byte[512 * 1024])]);
            var original = new MemoryStream(body);
            return (Multipart.Request(body, path: WardSyncPath, header: header, stream: original), original, body);
        }

        [Theory]
        [InlineData(RequestEncryptionMode.Off)]
        [InlineData(RequestEncryptionMode.Optional)]
        public async Task A_form_sent_to_a_json_action_is_never_parsed_or_buffered(RequestEncryptionMode mode)
        {
            var (http, original, body) = FormSentToJsonAction(header: false);

            var (reached, _, log) = await Run(http, Options(mode));   // a [FromBody] action

            Assert.True(reached);
            Assert.Null(http.Features.Get<IFormFeature>()?.Form);
            Assert.True(original.Position <= 64 * 1024, $"read {original.Position} bytes of a {body.Length}-byte body");
            Assert.DoesNotContain(log.Lines, l => l.Message.Contains("Opened sealed"));

            // Every byte is still there, in order, for the framework to refuse with its 415.
            using var copy = new MemoryStream();
            await http.Request.Body.CopyToAsync(copy);
            Assert.Equal(body, copy.ToArray());
        }

        [Theory]
        [InlineData(false, RequestEncryptionMode.Required, ApiErrorCodes.BodyEncryptionRequired)]
        [InlineData(true, RequestEncryptionMode.Optional, ApiErrorCodes.BodyDecryptFailed)]
        public async Task A_form_sent_to_a_json_action_is_refused_without_being_parsed(bool header, RequestEncryptionMode mode, string code)
        {
            var (http, original, _) = FormSentToJsonAction(header);

            var (error, log) = await Refused(http, Options(mode));

            Assert.Equal(code, error.Code);
            Assert.Null(http.Features.Get<IFormFeature>()?.Form);
            Assert.True(original.Position <= 64 * 1024);
            Assert.DoesNotContain(log.Lines, l => l.Message.Contains("Opened sealed"));
        }

        [Fact]
        public async Task A_sealed_json_body_sent_to_a_form_action_is_not_opened()
        {
            var envelope = TestSealer.Seal(WardSyncJson, "POST", AddNewPath, bearerToken: Token).Envelope;

            var (reached, http, log) = await Run(JsonRequest(envelope, path: AddNewPath, header: false),
                Options(RequestEncryptionMode.Optional), null, FormParameter());
            Assert.True(reached);
            Assert.Equal(envelope, await ReadBodyAsync(http.Request));
            Assert.DoesNotContain(log.Lines, l => l.Message.Contains("Opened sealed"));

            var (error, _) = await Refused(JsonRequest(envelope, path: AddNewPath, header: true),
                Options(RequestEncryptionMode.Optional), null, FormParameter());
            Assert.Equal(ApiErrorCodes.BodyDecryptFailed, error.Code);
        }

        [Fact]
        public void BindingOf_reports_body_form_and_the_bound_form_field_names()
        {
            static RequestBodyDecryptionFilter.BodyBinding Of(params Microsoft.AspNetCore.Mvc.Abstractions.ParameterDescriptor[] p) =>
                RequestBodyDecryptionFilter.BindingOf(new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor { Parameters = p });

            var json = Of(BodyParameter(), UnboundParameter());
            Assert.True(json.Body);
            Assert.False(json.Form);
            Assert.Empty(json.FormFields);

            var survey = Of(FormParameter(), UnboundParameter());
            Assert.False(survey.Body);
            Assert.True(survey.Form);
            Assert.Equal(["payload"], survey.FormFields);
            Assert.Contains("PAYLOAD", survey.FormFields);

            Assert.Equal(["survey"], Of(FormParameter("json", boundName: "survey")).FormFields);

            var filesOnly = Of(FormFileParameter());
            Assert.True(filesOnly.Form);
            Assert.Empty(filesOnly.FormFields);

            Assert.False(Of(QueryParameter(), UnboundParameter()).Any);
        }
    }

    // ---------------------------------------------------------------------------------------
    public class ReadingTheBody
    {
        [Fact]
        public async Task Peeking_never_loses_a_byte_of_a_plain_body_that_trickles_in()
        {
            // DefaultHttpContext has no native request pipe - the same as IIS in-process - so
            // BodyReader wraps Body here exactly as it does on the server.
            var body = WardSyncJson + new string(' ', 500) + "\n";
            var http = JsonRequest(body, header: false, declareLength: false, stream: b => new TrickleStream(b, 3));

            var (reached, _, _) = await Run(http, Options(RequestEncryptionMode.Optional));

            Assert.True(reached);
            Assert.Equal(body, await ReadBodyAsync(http.Request));
        }

        [Fact]
        public async Task A_plain_body_shorter_than_the_peek_window_arrives_whole()
        {
            var http = JsonRequest("{}", header: false, stream: b => new TrickleStream(b, 1));
            var (reached, _, _) = await Run(http, Options(RequestEncryptionMode.Optional));

            Assert.True(reached);
            Assert.Equal("{}", await ReadBodyAsync(http.Request));
        }

        [Fact]
        public async Task A_sealed_body_that_trickles_in_without_a_length_opens()
        {
            var sealedBody = SealWardSync();
            var http = JsonRequest(sealedBody.Envelope, declareLength: false, stream: b => new TrickleStream(b, 7));

            var (reached, _, _) = await Run(http, Options(RequestEncryptionMode.Required));

            Assert.True(reached);
            Assert.Equal(WardSyncJson, await ReadBodyAsync(http.Request));
        }

        [Fact]
        public async Task Kannada_text_and_an_empty_object_round_trip()
        {
            foreach (var plaintext in new[] { SecretPlaintext, "{}" })
            {
                var (reached, http, _) = await Run(JsonRequest(SealWardSync(plaintext).Envelope), Options(RequestEncryptionMode.Required));
                Assert.True(reached);
                Assert.Equal(plaintext, await ReadBodyAsync(http.Request));
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    public class Cryptography
    {
        [Fact]
        public async Task A_failed_unwrap_and_a_failed_tag_are_indistinguishable()
        {
            // Same plaintext length, so the envelopes are the same length and the log lines can
            // be compared whole.
            var badUnwrap = TestSealer.Seal(WardSyncJson, "POST", WardSyncPath, bearerToken: Token,
                wrappedKey: new byte[384].Select((_, i) => (byte)(i * 7 + 1)).ToArray());
            var wrongLengthKey = TestSealer.Seal(WardSyncJson, "POST", WardSyncPath, bearerToken: Token,
                wrappedKey: TestSealer.WrapWrongLengthKey());
            var badTag = TestSealer.Seal(WardSyncJson, "POST", WardSyncPath, bearerToken: Token, flipTagBit: true);

            var outcomes = new List<(ApiException Error, string[] Log)>();
            foreach (var sealedBody in new[] { badUnwrap, wrongLengthKey, badTag })
            {
                var (error, log) = await Refused(JsonRequest(sealedBody.Envelope), Options(RequestEncryptionMode.Required));
                outcomes.Add((error, log.Lines.Select(l => $"{l.Level}|{l.Message}|{l.Exception}").ToArray()));
            }

            foreach (var (error, logLines) in outcomes.Skip(1))
            {
                Assert.Equal(outcomes[0].Error.Code, error.Code);
                Assert.Equal(outcomes[0].Error.Message, error.Message);
                Assert.Equal(outcomes[0].Error.StatusCode, error.StatusCode);
                Assert.Equal(outcomes[0].Error.Retryable, error.Retryable);
                Assert.Equal(outcomes[0].Log, logLines);
            }

            Assert.Equal(ApiErrorCodes.BodyDecryptFailed, outcomes[0].Error.Code);
        }

        public static TheoryData<string, string, string, string?> Misdirected => new()
        {
            // sealed for                                        sent as: method, path, token
            { "another endpoint",    "POST", "/v1/api/gbagps/singlesite/propertyinfo/assign", Token },
            { "another method",      "PUT",  WardSyncPath, Token },
            { "another officer",     "POST", WardSyncPath, Token + "x" },
            { "no token at all",     "POST", WardSyncPath, null },
        };

        [Theory]
        [MemberData(nameof(Misdirected))]
        public async Task An_envelope_only_opens_where_it_was_sealed_for(string why, string method, string path, string? token)
        {
            _ = why;
            var sealedBody = SealWardSync();
            var (error, _) = await Refused(JsonRequest(sealedBody.Envelope, path: path, method: method, token: token),
                Options(RequestEncryptionMode.Optional));

            Assert.Equal(ApiErrorCodes.BodyDecryptFailed, error.Code);
        }

        [Fact]
        public async Task An_envelope_sealed_without_a_token_does_not_open_with_one()
        {
            var sealedBody = SealWardSync(token: null);
            var (error, _) = await Refused(JsonRequest(sealedBody.Envelope, token: Token), Options(RequestEncryptionMode.Optional));
            Assert.Equal(ApiErrorCodes.BodyDecryptFailed, error.Code);

            var (reached, http, _) = await Run(JsonRequest(sealedBody.Envelope, token: null), Options(RequestEncryptionMode.Optional));
            Assert.True(reached);
            Assert.Equal(WardSyncJson, await ReadBodyAsync(http.Request));
        }

        [Fact]
        public async Task A_form_part_cannot_be_replayed_as_a_json_body()
        {
            var sealedBody = TestSealer.Seal(WardSyncJson, "POST", WardSyncPath, part: "form:payload", bearerToken: Token);
            var (error, _) = await Refused(JsonRequest(sealedBody.Envelope), Options(RequestEncryptionMode.Optional));
            Assert.Equal(ApiErrorCodes.BodyDecryptFailed, error.Code);
        }

        [Fact]
        public async Task Changing_ts_breaks_the_tag()
        {
            var sealedBody = SealWardSync();
            var tampered = sealedBody.Envelope.Replace($"\"ts\":{sealedBody.Ts}", $"\"ts\":{sealedBody.Ts + 1}");
            Assert.NotEqual(sealedBody.Envelope, tampered);

            var (error, _) = await Refused(JsonRequest(tampered), Options(RequestEncryptionMode.Optional));
            Assert.Equal(ApiErrorCodes.BodyDecryptFailed, error.Code);
        }

        [Fact]
        public async Task The_path_and_method_are_compared_case_insensitively()
        {
            var sealedBody = TestSealer.Seal(WardSyncJson, "post", WardSyncPath.ToUpperInvariant(), bearerToken: Token);
            var (reached, http, _) = await Run(JsonRequest(sealedBody.Envelope, path: WardSyncPath), Options(RequestEncryptionMode.Required));

            Assert.True(reached);
            Assert.Equal(WardSyncJson, await ReadBodyAsync(http.Request));
        }

        [Fact]
        public async Task A_key_this_server_does_not_hold_is_BODY_KEY_UNKNOWN()
        {
            var sealedBody = TestSealer.Seal(WardSyncJson, "POST", WardSyncPath, bearerToken: Token, publicKeyOf: TestKeys.Secondary);
            var (error, log) = await Refused(JsonRequest(sealedBody.Envelope), Options(RequestEncryptionMode.Optional));

            Assert.Equal(ApiErrorCodes.BodyKeyUnknown, error.Code);
            Assert.Equal(400, error.StatusCode);
            Assert.True(error.Retryable);
            Assert.Contains(log.Lines, l => l.Message.Contains(sealedBody.Kid) && l.Message.Contains("UnknownKey"));
        }

        [Fact]
        public async Task A_future_format_version_is_refused()
        {
            var sealedBody = TestSealer.Seal(WardSyncJson, "POST", WardSyncPath, bearerToken: Token, enc: "pgps-body/2");
            var (error, _) = await Refused(JsonRequest(sealedBody.Envelope), Options(RequestEncryptionMode.Optional));
            Assert.Equal(ApiErrorCodes.BodyDecryptFailed, error.Code);
        }

        [Fact]
        public async Task Plain_text_that_is_not_UTF8_is_refused()
        {
            var sealedBody = TestSealer.SealBytes([0x7B, 0xC3, 0x28, 0x7D], "POST", WardSyncPath, bearerToken: Token);
            var (error, log) = await Refused(JsonRequest(sealedBody.Envelope), Options(RequestEncryptionMode.Optional));

            Assert.Equal(ApiErrorCodes.BodyDecryptFailed, error.Code);
            Assert.Contains(log.Lines, l => l.Message.Contains("NotUtf8"));
        }

        [Fact]
        public async Task With_MaxAgeMinutes_set_a_stale_envelope_is_refused_and_a_fresh_one_opens()
        {
            var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(TestSealer.DefaultTs));
            var fresh = TestSealer.Seal(WardSyncJson, "POST", WardSyncPath, bearerToken: Token, ts: TestSealer.DefaultTs - 60);
            var stale = TestSealer.Seal(WardSyncJson, "POST", WardSyncPath, bearerToken: Token, ts: TestSealer.DefaultTs - 3600);

            var (reached, _, _) = await Run(JsonRequest(fresh.Envelope), Options(maxAgeMinutes: 5), clock);
            Assert.True(reached);

            var (error, log) = await Refused(JsonRequest(stale.Envelope), Options(maxAgeMinutes: 5), clock);
            Assert.Equal(ApiErrorCodes.BodyDecryptFailed, error.Code);
            Assert.Contains(log.Lines, l => l.Message.Contains("Stale"));

            // 0, the default, only logs ts.
            var (reachedAnyAge, _, _) = await Run(JsonRequest(stale.Envelope), Options(maxAgeMinutes: 0), clock);
            Assert.True(reachedAnyAge);
        }
    }

    // ---------------------------------------------------------------------------------------
    public class StrictParsing
    {
        private static readonly BodyAadContext Aad = new("POST", WardSyncPath, "body", BodyEncryptionFormat.TokenTag(Token));
        private static readonly RequestBodyOpener Opener = FilterHarness.Opener(Options());

        private static BodyOpenFailure Open(string envelope) =>
            Opener.TryOpen(Encoding.UTF8.GetBytes(envelope), Aad).Failure;

        private static (SealedBody Sealed, Dictionary<string, string> Raw) Parts()
        {
            var s = SealWardSync();
            return (s, new Dictionary<string, string>
            {
                ["enc"] = "\"pgps-body/1\"",
                ["kid"] = $"\"{s.Kid}\"",
                ["ts"] = s.Ts.ToString(),
                ["k"] = $"\"{s.K}\"",
                ["n"] = $"\"{s.N}\"",
                ["c"] = $"\"{s.C}\""
            });
        }

        private static string Join(IEnumerable<KeyValuePair<string, string>> fields) =>
            "{" + string.Join(",", fields.Select(f => $"\"{f.Key}\":{f.Value}")) + "}";

        [Fact]
        public void The_reference_envelope_opens_and_key_order_is_not_enforced()
        {
            var (s, raw) = Parts();
            Assert.Equal(BodyOpenFailure.None, Open(s.Envelope));
            Assert.Equal(BodyOpenFailure.None, Open(Join(raw.Reverse())));
            Assert.Equal(BodyOpenFailure.None, Open(" \n" + s.Envelope + " \r\n"));
        }

        [Theory]
        [InlineData("enc")]
        [InlineData("kid")]
        [InlineData("ts")]
        [InlineData("k")]
        [InlineData("n")]
        [InlineData("c")]
        public void Every_field_is_required(string missing)
        {
            var (_, raw) = Parts();
            raw.Remove(missing);
            Assert.Equal(BodyOpenFailure.Malformed, Open(Join(raw)));
        }

        public static TheoryData<string> Mutations => new()
        {
            "extra key", "duplicate key", "wrong case", "ts as string", "negative ts", "fractional ts",
            "upper-case kid", "short kid", "long nonce", "short k", "short c", "url-safe base64",
            "space inside base64", "trailing garbage", "array", "nested object", "comment", "truncated",
            "null value", "unpadded base64"
        };

        [Theory]
        [MemberData(nameof(Mutations))]
        public void Anything_looser_than_the_spec_is_malformed(string mutation)
        {
            var (s, raw) = Parts();
            string envelope;
            switch (mutation)
            {
                case "extra key": envelope = Join(raw.Append(new("x", "1"))); break;
                case "duplicate key": envelope = Join(raw.Append(new("n", raw["n"]))); break;
                case "wrong case": envelope = s.Envelope.Replace("\"kid\":", "\"Kid\":"); break;
                case "ts as string": raw["ts"] = $"\"{s.Ts}\""; envelope = Join(raw); break;
                case "negative ts": raw["ts"] = "-1"; envelope = Join(raw); break;
                case "fractional ts": raw["ts"] = s.Ts + ".0"; envelope = Join(raw); break;
                case "upper-case kid": raw["kid"] = $"\"{s.Kid.ToUpperInvariant()}\""; envelope = Join(raw); break;
                case "short kid": raw["kid"] = $"\"{s.Kid[..15]}\""; envelope = Join(raw); break;
                case "long nonce": raw["n"] = $"\"{Convert.ToBase64String(new byte[16])}\""; envelope = Join(raw); break;
                case "short k": raw["k"] = $"\"{Convert.ToBase64String(new byte[256])}\""; envelope = Join(raw); break;
                case "short c": raw["c"] = $"\"{Convert.ToBase64String(new byte[15])}\""; envelope = Join(raw); break;
                case "url-safe base64": raw["k"] = $"\"{s.K.Replace('+', '-').Replace('/', '_')}\""; envelope = Join(raw); break;
                case "space inside base64": raw["c"] = $"\"{s.C[..8]} {s.C[8..]}\""; envelope = Join(raw); break;
                case "trailing garbage": envelope = s.Envelope + "x"; break;
                case "array": envelope = "[" + s.Envelope + "]"; break;
                case "nested object": raw["c"] = "{\"c\":" + raw["c"] + "}"; envelope = Join(raw); break;
                case "comment": envelope = s.Envelope.Replace("{\"enc\"", "{/*x*/\"enc\""); break;
                case "truncated": envelope = s.Envelope[..^2]; break;
                case "null value": raw["kid"] = "null"; envelope = Join(raw); break;
                case "unpadded base64": raw["n"] = $"\"{Convert.ToBase64String(new byte[11]).TrimEnd('=')}\""; envelope = Join(raw); break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }

            Assert.NotEqual(s.Envelope, envelope);
            Assert.Equal(BodyOpenFailure.Malformed, Open(envelope));
        }

        [Fact]
        public void A_json_escape_inside_base64_is_the_same_string()
        {
            var (s, _) = Parts();
            var index = s.K.IndexOf('/');
            Assert.True(index >= 0, "a 512-character base64 string without '/' is vanishingly unlikely");
            var escaped = s.Envelope.Replace(s.K, s.K[..index] + "\\/" + s.K[(index + 1)..]);

            Assert.Equal(BodyOpenFailure.None, Open(escaped));
        }

        [Theory]
        [InlineData("QQ==", true)]
        [InlineData("QUI=", true)]
        [InlineData("QUJD", true)]
        [InlineData("QQ", false)]
        [InlineData("QQ=", false)]
        [InlineData("QR==", false)]
        [InlineData("Q===", false)]
        [InlineData("=QQ=", false)]
        [InlineData("QQ==QQ==", false)]
        [InlineData("Q Q=", false)]
        [InlineData("-_8=", false)]
        [InlineData("", false)]
        public void Base64_must_be_canonical_standard_and_padded(string text, bool valid) =>
            Assert.Equal(valid, SealedEnvelope.TryDecodeCanonicalBase64(Encoding.ASCII.GetBytes(text), out _));

        [Fact]
        public void Open_throws_the_fixed_errors()
        {
            var (s, _) = Parts();
            Assert.Equal(WardSyncJson, Opener.Open(s.Utf8, Aad));

            var unknown = TestSealer.Seal(WardSyncJson, "POST", WardSyncPath, bearerToken: Token, publicKeyOf: TestKeys.Secondary);
            Assert.Equal(ApiErrorCodes.BodyKeyUnknown, Assert.Throws<ApiException>(() => Opener.Open(unknown.Utf8, Aad)).Code);
            Assert.Equal(ApiErrorCodes.BodyDecryptFailed, Assert.Throws<ApiException>(() => Opener.Open("{}"u8, Aad)).Code);
        }
    }

    // ---------------------------------------------------------------------------------------
    public class Size
    {
        [Fact]
        public async Task A_sealed_body_declared_larger_than_MaxEnvelopeBytes_is_413()
        {
            var sealedBody = SealWardSync(WardSyncJson + new string(' ', 2000));
            var (error, _) = await Refused(JsonRequest(sealedBody.Envelope), Options(maxEnvelopeBytes: 1024));

            Assert.Equal(ApiErrorCodes.PayloadTooLarge, error.Code);
            Assert.Equal(413, error.StatusCode);
            Assert.False(error.Retryable);
        }

        [Fact]
        public async Task A_sealed_body_that_grows_past_MaxEnvelopeBytes_without_a_length_is_413()
        {
            var sealedBody = SealWardSync(WardSyncJson + new string(' ', 2000));
            var http = JsonRequest(sealedBody.Envelope, declareLength: false, stream: b => new TrickleStream(b, 100));

            var (error, _) = await Refused(http, Options(maxEnvelopeBytes: 1024));
            Assert.Equal(ApiErrorCodes.PayloadTooLarge, error.Code);
        }

        [Theory]
        [InlineData(8_388_000L, RequestEncryptionOptions.DefaultMaxEnvelopeBytes, RequestBodyDecryptionFilter.MaxInitialBufferBytes)]
        [InlineData(RequestEncryptionOptions.DefaultMaxEnvelopeBytes, RequestEncryptionOptions.DefaultMaxEnvelopeBytes, RequestBodyDecryptionFilter.MaxInitialBufferBytes)]
        [InlineData(1000L, RequestEncryptionOptions.DefaultMaxEnvelopeBytes, 1000)]
        [InlineData(100_000L, 4096L, 4096)]
        [InlineData(0L, RequestEncryptionOptions.DefaultMaxEnvelopeBytes, 0)]
        [InlineData(null, RequestEncryptionOptions.DefaultMaxEnvelopeBytes, 0)]
        public void A_declared_length_reserves_at_most_64_KiB_before_the_bytes_arrive(long? declared, long max, int expected)
        {
            // A client that declares 8 MB, sends a marker and then stalls must not be able to
            // hold 8 MB of server memory per connection.
            Assert.Equal(expected, RequestBodyDecryptionFilter.InitialBufferBytes(declared, max));
        }

        [Fact]
        public async Task A_sealed_body_larger_than_the_initial_reservation_still_opens_whole()
        {
            var plaintext = WardSyncJson + new string(' ', 3 * RequestBodyDecryptionFilter.MaxInitialBufferBytes);
            var sealedBody = SealWardSync(plaintext);

            foreach (var declareLength in new[] { true, false })
            {
                var http = JsonRequest(sealedBody.Envelope, declareLength: declareLength, stream: b => new TrickleStream(b, 4096));
                var (reached, _, _) = await Run(http, Options(RequestEncryptionMode.Required));

                Assert.True(reached);
                Assert.Equal(plaintext, await ReadBodyAsync(http.Request));
            }
        }

        [Fact]
        public async Task MaxEnvelopeBytes_does_not_apply_to_plain_bodies()
        {
            var body = WardSyncJson + new string(' ', 5000);
            var (reached, http, _) = await Run(JsonRequest(body, header: false), Options(maxEnvelopeBytes: 1024));

            Assert.True(reached);
            Assert.Equal(body, await ReadBodyAsync(http.Request));
        }

        [Fact]
        public async Task The_server_body_limit_surfacing_mid_read_is_413()
        {
            var sealedBody = SealWardSync();
            var prefix = Encoding.UTF8.GetBytes(sealedBody.Envelope[..100]);
            var http = JsonRequest(sealedBody.Envelope, declareLength: false,
                stream: _ => new FailingStream(new BadHttpRequestException("Request body too large.", 413), prefix));

            var (error, _) = await Refused(http, Options());
            Assert.Equal(ApiErrorCodes.PayloadTooLarge, error.Code);
        }

        [Fact]
        public async Task A_connection_failure_mid_body_is_a_retryable_400()
        {
            var http = JsonRequest(SealWardSync().Envelope, declareLength: false,
                stream: _ => new FailingStream(new IOException("connection reset")));

            var (error, log) = await Refused(http, Options());
            Assert.Equal(ApiErrorCodes.BadRequest, error.Code);
            Assert.Equal(400, error.StatusCode);
            Assert.True(error.Retryable);
            Assert.DoesNotContain("connection reset", log.AllText);
        }

        [Fact]
        public async Task A_client_that_walks_away_mid_body_is_an_ordinary_cancellation()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var http = JsonRequest(SealWardSync().Envelope, declareLength: false,
                stream: _ => new FailingStream(new IOException("The client disconnected.")));
            http.RequestAborted = cts.Token;

            var filter = Filter(Options(), new CapturingLogger<RequestBodyDecryptionFilter>());
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(filter, Context(http, BodyParameter())));
            Assert.Null(error.InnerException);
        }
    }

    // ---------------------------------------------------------------------------------------
    public class TheSurveyForm
    {
        private static readonly byte[] Photo1 = Enumerable.Range(0, 3000).Select(i => (byte)(i % 251)).ToArray();
        private static readonly byte[] Photo2 = Enumerable.Range(0, 1200).Select(i => (byte)(255 - i % 256)).ToArray();

        private static SealedBody SealPayload(string plaintext = SecretPlaintext, string part = "form:payload",
            string path = AddNewPath, string? token = Token) =>
            TestSealer.Seal(plaintext, "POST", path, part, token);

        private static byte[] SurveyBody(string payload, params (string Name, string Value)[] extra) =>
            Multipart.Build(
                new[] { ("payload", payload) }.Concat(extra),
                [("files", "site_1.jpg", Photo1), ("files", "road_1.jpg", Photo2)]);

        [Fact]
        public async Task The_binder_and_Request_Form_Files_see_the_plain_payload_and_the_same_file_objects()
        {
            var http = Multipart.Request(SurveyBody(SealPayload().Envelope));

            // Read once up front so the test holds the very IFormFile objects the server parsed.
            var original = await http.Request.ReadFormAsync();
            var originalFiles = original.Files;
            var firstFile = originalFiles[0];

            var (reached, _, log) = await Run(http, Options(RequestEncryptionMode.Required), null, FormParameter());

            Assert.True(reached);
            var form = await http.Request.ReadFormAsync();
            Assert.Equal(SecretPlaintext, form["payload"].ToString());
            Assert.Equal(SecretPlaintext, http.Request.Form["payload"].ToString());
            Assert.Same(originalFiles, form.Files);
            Assert.Same(firstFile, http.Request.Form.Files[0]);
            Assert.DoesNotContain(log.Lines, l => l.Level >= LogLevel.Information);
        }

        [Fact]
        public async Task Without_a_prior_read_the_files_arrive_byte_for_byte()
        {
            var http = Multipart.Request(SurveyBody(SealPayload().Envelope));

            var (reached, _, _) = await Run(http, Options(RequestEncryptionMode.Required), null, FormParameter());

            Assert.True(reached);
            var form = await http.Request.ReadFormAsync();
            Assert.Equal(SecretPlaintext, form["payload"].ToString());
            Assert.Equal(2, form.Files.Count);
            Assert.Equal(["site_1.jpg", "road_1.jpg"], form.Files.Select(f => f.FileName));

            await using var copy = new MemoryStream();
            await form.Files[0].CopyToAsync(copy);
            Assert.Equal(Photo1, copy.ToArray());
        }

        [Fact]
        public async Task A_sealed_field_without_the_header_opens_and_says_so()
        {
            var http = Multipart.Request(SurveyBody(SealPayload().Envelope), header: false);
            var (reached, _, log) = await Run(http, Options(RequestEncryptionMode.Required), null, FormParameter());

            Assert.True(reached);
            Assert.Equal(SecretPlaintext, (await http.Request.ReadFormAsync())["payload"].ToString());
            Assert.Contains(log.Lines, l => l.Message.Contains("without the X-Body-Enc header") && l.Message.Contains("form:payload"));
        }

        [Fact]
        public async Task A_plain_payload_passes_in_Optional_with_a_log_line()
        {
            const string plain = """{"applicationId":1}""";
            var http = Multipart.Request(SurveyBody(plain), header: false);
            var (reached, _, log) = await Run(http, Options(RequestEncryptionMode.Optional), null, FormParameter());

            Assert.True(reached);
            Assert.Equal(plain, (await http.Request.ReadFormAsync())["payload"].ToString());
            Assert.Contains(log.Lines, l => l.Level == LogLevel.Information && l.Message.Contains("Plain request body accepted"));
        }

        [Fact]
        public async Task A_plain_payload_passes_in_Off()
        {
            var http = Multipart.Request(SurveyBody("""{"applicationId":1}"""), header: false);
            var (reached, _, _) = await Run(http, Options(RequestEncryptionMode.Off), null, FormParameter());
            Assert.True(reached);
        }

        [Fact]
        public async Task A_plain_payload_is_refused_in_Required()
        {
            var http = Multipart.Request(SurveyBody("""{"applicationId":1}"""), header: false);
            var (error, _) = await Refused(http, Options(RequestEncryptionMode.Required), null, FormParameter());
            Assert.Equal(ApiErrorCodes.BodyEncryptionRequired, error.Code);
        }

        [Fact]
        public async Task Files_alone_are_refused_in_Required_and_an_empty_form_is_not()
        {
            var filesOnly = Multipart.Request(Multipart.Build([], [("files", "a.jpg", Photo1)]), header: false);
            var (error, _) = await Refused(filesOnly, Options(RequestEncryptionMode.Required), null, FormParameter());
            Assert.Equal(ApiErrorCodes.BodyEncryptionRequired, error.Code);

            var empty = Multipart.Request(Multipart.Build([]), header: false);
            var (reached, _, _) = await Run(empty, Options(RequestEncryptionMode.Required), null, FormParameter());
            Assert.True(reached);
        }

        [Fact]
        public async Task The_header_on_a_form_with_no_sealed_field_is_refused()
        {
            var http = Multipart.Request(SurveyBody("""{"applicationId":1}"""), header: true);
            var (error, _) = await Refused(http, Options(RequestEncryptionMode.Optional), null, FormParameter());
            Assert.Equal(ApiErrorCodes.BodyDecryptFailed, error.Code);
        }

        [Fact]
        public async Task Off_refuses_a_sealed_field()
        {
            var http = Multipart.Request(SurveyBody(SealPayload().Envelope));
            var (error, _) = await Refused(http, Options(RequestEncryptionMode.Off), null, FormParameter());
            Assert.Equal(ApiErrorCodes.BodyKeyUnknown, error.Code);
        }

        [Fact]
        public async Task A_sealed_field_must_carry_a_single_value()
        {
            var sealedValue = SealPayload().Envelope;
            var body = Multipart.Build([("payload", sealedValue), ("payload", sealedValue)]);
            var (error, _) = await Refused(Multipart.Request(body), Options(), null, FormParameter());
            Assert.Equal(ApiErrorCodes.BodyDecryptFailed, error.Code);
        }

        [Theory]
        [InlineData("body", "payload")]
        [InlineData("form:payload", "other")]
        [InlineData("form:Payload", "payload")]
        public async Task A_sealed_field_only_opens_under_the_part_it_was_sealed_for(string sealedPart, string fieldName)
        {
            var body = Multipart.Build([(fieldName, SealPayload(part: sealedPart).Envelope)]);
            var (error, _) = await Refused(Multipart.Request(body), Options(), null, FormParameter());
            Assert.Equal(ApiErrorCodes.BodyDecryptFailed, error.Code);
        }

        [Fact]
        public async Task A_plain_field_beside_a_sealed_one_is_refused_in_Required_and_logged_in_Optional()
        {
            var body = SurveyBody(SealPayload().Envelope, ("note", "plain"));

            var (error, _) = await Refused(Multipart.Request(body), Options(RequestEncryptionMode.Required), null, FormParameter());
            Assert.Equal(ApiErrorCodes.BodyEncryptionRequired, error.Code);

            var http = Multipart.Request(body);
            var (reached, _, log) = await Run(http, Options(RequestEncryptionMode.Optional), null, FormParameter());
            Assert.True(reached);
            var form = await http.Request.ReadFormAsync();
            Assert.Equal(SecretPlaintext, form["payload"].ToString());
            Assert.Equal("plain", form["note"].ToString());
            Assert.Contains(log.Lines, l => l.Message.Contains("Plain form field(s) accepted"));
        }

        [Fact]
        public async Task Sealed_fields_the_action_does_not_bind_are_refused_before_any_is_opened()
        {
            // Every field here would open on its own - each is sealed for its own part, this
            // path and this token - so only the bound-name rule stands between one anonymous
            // form and an RSA private-key operation per field.
            var extra = Enumerable.Range(0, 40).Select(i => ($"f{i}", SealPayload(part: $"form:f{i}").Envelope));
            var body = Multipart.Build(new[] { ("payload", SealPayload().Envelope) }.Concat(extra));

            var (error, log) = await Refused(Multipart.Request(body), Options(RequestEncryptionMode.Optional), null, FormParameter());

            Assert.Equal(ApiErrorCodes.BodyDecryptFailed, error.Code);
            Assert.True(error.Retryable);
            Assert.Contains(log.Lines, l => l.Message.Contains("does not bind"));
            Assert.DoesNotContain(log.Lines, l => l.Message.Contains("Opened sealed"));
        }

        [Fact]
        public async Task A_sealed_field_is_opened_under_the_name_the_action_binds()
        {
            // [FromForm(Name = "survey")] string json: the bound name counts, not the C# name.
            var bound = FormParameter("json", boundName: "survey");

            var http = Multipart.Request(Multipart.Build([("survey", SealPayload(part: "form:survey").Envelope)]));
            var (reached, _, _) = await Run(http, Options(RequestEncryptionMode.Required), null, bound);
            Assert.True(reached);
            Assert.Equal(SecretPlaintext, (await http.Request.ReadFormAsync())["survey"].ToString());

            var byParameterName = Multipart.Build([("json", SealPayload(part: "form:json").Envelope)]);
            var (error, _) = await Refused(Multipart.Request(byParameterName), Options(), null, bound);
            Assert.Equal(ApiErrorCodes.BodyDecryptFailed, error.Code);
        }

        [Fact]
        public async Task An_action_that_binds_only_files_opens_no_sealed_field()
        {
            var (error, log) = await Refused(Multipart.Request(SurveyBody(SealPayload().Envelope)), Options(), null,
                FormFileParameter());

            Assert.Equal(ApiErrorCodes.BodyDecryptFailed, error.Code);
            Assert.DoesNotContain(log.Lines, l => l.Message.Contains("Opened sealed"));
        }

        [Fact]
        public async Task A_sealed_field_larger_than_MaxEnvelopeBytes_is_413()
        {
            var body = SurveyBody(SealPayload(SecretPlaintext + new string(' ', 2000)).Envelope);
            var (error, _) = await Refused(Multipart.Request(body), Options(maxEnvelopeBytes: 1024), null, FormParameter());
            Assert.Equal(ApiErrorCodes.PayloadTooLarge, error.Code);
        }

        [Fact]
        public async Task A_url_encoded_form_field_is_opened_too()
        {
            var sealedValue = SealPayload().Envelope;
            var bodyText = "payload=" + Uri.EscapeDataString(sealedValue);
            var http = Multipart.Request(Encoding.ASCII.GetBytes(bodyText), contentType: "application/x-www-form-urlencoded");

            var (reached, _, _) = await Run(http, Options(RequestEncryptionMode.Required), null, FormParameter());

            Assert.True(reached);
            Assert.Equal(SecretPlaintext, (await http.Request.ReadFormAsync())["payload"].ToString());
        }
    }

    // ---------------------------------------------------------------------------------------
    public class FormReadErrors
    {
        [Fact]
        public async Task A_form_value_over_the_length_limit_is_413()
        {
            // ValueLengthLimit is enforced on url-encoded forms; multipart sections are capped
            // by MultipartBodyLengthLimit (next test).
            var body = Encoding.ASCII.GetBytes("payload=" + new string('x', 500));
            var http = Multipart.Request(body, contentType: "application/x-www-form-urlencoded");
            http.Features.Set<IFormFeature>(new FormFeature(http.Request, new FormOptions { ValueLengthLimit = 100 }));

            var (error, _) = await Refused(http, Options(), null, FormParameter());
            Assert.Equal(ApiErrorCodes.PayloadTooLarge, error.Code);
            Assert.Equal(413, error.StatusCode);
            Assert.False(error.Retryable);
        }

        [Fact]
        public async Task Too_many_form_values_is_413()
        {
            var body = Multipart.Build([("payload", "{}"), ("other", "x"), ("third", "y")]);
            var http = Multipart.Request(body);
            http.Features.Set<IFormFeature>(new FormFeature(http.Request, new FormOptions { ValueCountLimit = 2 }));

            var (error, _) = await Refused(http, Options(), null, FormParameter());
            Assert.Equal(ApiErrorCodes.PayloadTooLarge, error.Code);
        }

        [Fact]
        public async Task A_multipart_body_over_the_limit_is_413()
        {
            var body = Multipart.Build([("payload", "{}")], [("files", "a.jpg", new byte[5000])]);
            var http = Multipart.Request(body);
            http.Features.Set<IFormFeature>(new FormFeature(http.Request, new FormOptions { MultipartBodyLengthLimit = 1024 }));

            var (error, _) = await Refused(http, Options(), null, FormParameter());
            Assert.Equal(ApiErrorCodes.PayloadTooLarge, error.Code);
        }

        [Theory]
        [InlineData("multipart/form-data")]                                   // no boundary at all
        [InlineData("multipart/form-data; boundary=" + LongBoundary)]          // over FormOptions' 128-character limit
        public async Task A_malformed_multipart_request_is_a_permanent_400(string contentType)
        {
            var body = Multipart.Build([("payload", "{}")]);

            var (error, log) = await Refused(Multipart.Request(body, contentType: contentType), Options(), null, FormParameter());
            Assert.Equal(ApiErrorCodes.BadRequest, error.Code);
            Assert.Equal(400, error.StatusCode);
            Assert.False(error.Retryable);
            Assert.All(log.Lines, l => Assert.Null(l.Exception));
        }

        private const string LongBoundary =
            "0123456789012345678901234567890123456789012345678901234567890123456789" +
            "0123456789012345678901234567890123456789012345678901234567890123456789";

        [Fact]
        public async Task A_multipart_body_cut_short_is_a_retryable_400()
        {
            // MultipartReader reports a body that ends early as an IOException: the connection
            // dropped, and sending again is the right answer.
            var body = Multipart.Build([("payload", "{}")], [("files", "a.jpg", new byte[100])], close: false);
            var truncated = body[..(body.Length - 40)];

            var (error, log) = await Refused(Multipart.Request(truncated), Options(), null, FormParameter());
            Assert.Equal(ApiErrorCodes.BadRequest, error.Code);
            Assert.Equal(400, error.StatusCode);
            Assert.True(error.Retryable);
            Assert.All(log.Lines, l => Assert.Null(l.Exception));
        }

        [Fact]
        public async Task A_connection_failure_while_reading_the_form_is_a_retryable_400()
        {
            var body = Multipart.Build([("payload", "{}")]);
            var http = Multipart.Request(body, stream: new FailingStream(new IOException("reset"), body[..20]));

            var (error, _) = await Refused(http, Options(), null, FormParameter());
            Assert.Equal(ApiErrorCodes.BadRequest, error.Code);
            Assert.True(error.Retryable);
        }

        [Fact]
        public async Task The_server_body_limit_while_reading_the_form_is_413()
        {
            var body = Multipart.Build([("payload", "{}")]);
            var http = Multipart.Request(body, stream: new FailingStream(new BadHttpRequestException("Request body too large.", 413), body[..20]));

            var (error, _) = await Refused(http, Options(), null, FormParameter());
            Assert.Equal(ApiErrorCodes.PayloadTooLarge, error.Code);
        }

        [Fact]
        public async Task A_client_that_aborts_the_form_upload_is_an_ordinary_cancellation()
        {
            using var cts = new CancellationTokenSource();
            var body = Multipart.Build([("payload", "{}")]);
            var http = Multipart.Request(body, stream: new FailingStream(new IOException("aborted"), body[..20]));
            http.RequestAborted = cts.Token;
            cts.Cancel();

            var filter = Filter(Options(), new CapturingLogger<RequestBodyDecryptionFilter>());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(filter, Context(http, FormParameter())));
        }
    }

    // ---------------------------------------------------------------------------------------
    public class WhatReachesTheLog
    {
        [Fact]
        public async Task No_plain_text_k_n_or_c_and_no_exception_is_ever_logged()
        {
            var log = new CapturingLogger<RequestBodyDecryptionFilter>();
            var filter = Filter(Options(RequestEncryptionMode.Required), log);
            var sealedBodies = new List<SealedBody>();

            async Task Attempt(SealedBody sealedBody, string? path = null, bool header = true)
            {
                sealedBodies.Add(sealedBody);
                try
                {
                    await RunAsync(filter, Context(JsonRequest(sealedBody.Envelope, path: path ?? WardSyncPath, header: header), BodyParameter()));
                }
                catch (ApiException e)
                {
                    Assert.Null(e.InnerException);
                    Assert.DoesNotContain("ENCTEST", e.Message);
                }
            }

            await Attempt(SealWardSync(SecretPlaintext));                                   // opens
            await Attempt(SealWardSync(SecretPlaintext), header: false);                    // opens, header stripped
            await Attempt(SealWardSync(SecretPlaintext), path: "/elsewhere");               // AAD mismatch
            await Attempt(TestSealer.Seal(SecretPlaintext, "POST", WardSyncPath, bearerToken: Token, flipTagBit: true));
            await Attempt(TestSealer.Seal(SecretPlaintext, "POST", WardSyncPath, bearerToken: Token, wrappedKey: new byte[384]));
            await Attempt(TestSealer.Seal(SecretPlaintext, "POST", WardSyncPath, bearerToken: Token, publicKeyOf: TestKeys.Secondary));
            await Attempt(TestSealer.SealBytes([0xFF, 0xFE, 0x45, 0x4E, 0x43, 0x54, 0x45, 0x53, 0x54], "POST", WardSyncPath, bearerToken: Token));

            var text = log.AllText;
            Assert.NotEmpty(log.Lines);
            Assert.DoesNotContain("ENCTEST", text);
            Assert.DoesNotContain("9000000001", text);
            Assert.DoesNotContain(Token, text);
            foreach (var sealedBody in sealedBodies)
            {
                foreach (var part in new[] { sealedBody.K, sealedBody.N, sealedBody.C })
                    Assert.DoesNotContain(part[..Math.Min(16, part.Length)], text);
            }
            Assert.All(log.Lines, l => Assert.Null(l.Exception));
            Assert.All(log.Lines, l => Assert.True(l.Level <= LogLevel.Information));
        }

        [Fact]
        public async Task The_global_handler_returns_the_envelope_with_retryable_true()
        {
            var http = new DefaultHttpContext();
            http.Request.Path = WardSyncPath;
            http.Response.Body = new MemoryStream();
            var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);

            await handler.TryHandleAsync(http, BodyEncryptionErrors.DecryptFailed(), default);

            http.Response.Body.Position = 0;
            using var json = await JsonDocument.ParseAsync(http.Response.Body);
            Assert.Equal(400, http.Response.StatusCode);
            Assert.Equal("BODY_DECRYPT_FAILED", json.RootElement.GetProperty("code").GetString());
            Assert.True(json.RootElement.GetProperty("retryable").GetBoolean());
            Assert.Equal(BodyEncryptionErrors.DecryptFailedMessage, json.RootElement.GetProperty("message").GetString());
        }

        [Fact]
        public void Each_code_has_its_status_retryable_flag_and_fixed_message()
        {
            var table = new (ApiException Error, string Code, int Status, bool Retryable, string Message)[]
            {
                (BodyEncryptionErrors.Required(), "BODY_ENCRYPTION_REQUIRED", 400, true, BodyEncryptionErrors.RequiredMessage),
                (BodyEncryptionErrors.KeyUnknown(), "BODY_KEY_UNKNOWN", 400, true, BodyEncryptionErrors.KeyUnknownMessage),
                (BodyEncryptionErrors.DecryptFailed(), "BODY_DECRYPT_FAILED", 400, true, BodyEncryptionErrors.DecryptFailedMessage),
                (BodyEncryptionErrors.TooLarge(), "PAYLOAD_TOO_LARGE", 413, false, BodyEncryptionErrors.TooLargeMessage),
            };

            foreach (var (error, code, status, retryable, message) in table)
            {
                Assert.Equal(code, error.Code);
                Assert.Equal(status, error.StatusCode);
                Assert.Equal(retryable, error.Retryable);
                Assert.Equal(message, error.Message);
                Assert.Null(error.InnerException);
            }
        }
    }
}
