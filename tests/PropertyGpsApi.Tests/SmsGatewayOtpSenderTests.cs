using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Infrastructure.Options;
using PropertyGpsApi.Infrastructure.Security;

namespace PropertyGpsApi.Tests;

/// <summary>
/// The request the SMS gateway receives, in each protocol. SmsMobile is checked against
/// B2A_WEBAPI's clSendSMS, the code already sending OTPs through smsmobile1.karnataka.gov.in;
/// Mgov against LayoutKhataAPI's clsSendOtp. Every credential here is invented.
/// </summary>
public class SmsGatewayOtpSenderTests
{
    private const string ServiceKey = "11111111-2222-3333-4444-555555555555";

    /// <summary>
    /// The key for <see cref="ServiceKey"/>, computed by .NET Framework's
    /// DESCryptoServiceProvider exactly as B2A_WEBAPI's clSendSMS.Encrypt does - not by the
    /// code under test.
    /// </summary>
    private const string ServiceKeyEncrypted = "31EzH31puR7yD3VThtLbwU00sfwWS2WyjHY9SGSoBlVOZVxyGd7Aog";

    private const string Message =
        "Your OTP for logging in to the BToA Application is 123456. Please do not share this OTP with anyone. Regards - BBMP";

    private static SmsOptions Options(string protocol) => new()
    {
        Protocol = protocol,
        ApiUrl = "http://sms.example.test/index.php/sendmsg",
        Username = "user-1",
        Password = "pass word&1",
        SenderId = "BBMPPT",
        SecretKey = ServiceKey,
        ServiceType = "unicodemsg",
        TemplateId = "1107000000000000000",
    };

    private sealed class Gateway(HttpStatusCode status, string reply) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public string? ContentType { get; private set; }
        public Uri? Url { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Url = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(ct);
            ContentType = request.Content.Headers.ContentType?.ToString();
            return new HttpResponseMessage(status) { Content = new StringContent(reply) };
        }
    }

    private sealed class RecordingLogger : ILogger<SmsGatewayOtpSender>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }

    private static async Task<(Gateway Gateway, RecordingLogger Log)> Send(
        string protocol, HttpStatusCode status = HttpStatusCode.OK, string reply = "")
    {
        var gateway = new Gateway(status, reply);
        var log = new RecordingLogger();
        await new SmsGatewayOtpSender(new HttpClient(gateway),
                Microsoft.Extensions.Options.Options.Create(Options(protocol)), log)
            .SendAsync("9876543210", "123456", CancellationToken.None);
        return (gateway, log);
    }

    // ---- SmsMobile ------------------------------------------------------------------------

    [Fact]
    public void SmsMobile_key_matches_what_B2A_WEBAPI_sends() =>
        Assert.Equal(ServiceKeyEncrypted, SmsGatewayOtpSender.SmsMobileKey(ServiceKey));

    [Fact]
    public async Task SmsMobile_sends_the_fields_as_B2A_WEBAPI_builds_them()
    {
        var (gateway, _) = await Send(SmsOptions.SmsMobile, reply: "402,MsgID = 1");

        Assert.Equal(
            "username=user-1"
            + "&password=pass%20word%261"
            + "&smsservicetype=unicodemsg"
            + "&content=" + Uri.EscapeDataString(Message)
            + "&mobileno=9876543210"
            + "&senderid=BBMPPT"
            + "&key=" + ServiceKeyEncrypted
            + "&templateid=1107000000000000000",
            gateway.Body);
        Assert.Equal("application/x-www-form-urlencoded; charset=utf-8", gateway.ContentType);
        Assert.Equal("http://sms.example.test/index.php/sendmsg", gateway.Url!.ToString());
    }

    /// <summary>The reply seen on the test server on 2026-10-12 (ids invented).</summary>
    [Fact]
    public async Task SmsMobile_a_402_reply_is_sent()
    {
        var (_, log) = await Send(SmsOptions.SmsMobile,
            reply: "402, MsgID = 1508202312345678Mobile_1-BBMPPT,9876543210");

        Assert.Equal(["OTP dispatched to 3210 via the SMS gateway"], log.Lines);
    }

    /// <summary>
    /// B2A_WEBAPI counts any reply as sent, so an officer waits for an SMS that never comes.
    /// A refusal is an error here, and its reply is logged - with no number or code readable.
    /// </summary>
    [Fact]
    public async Task SmsMobile_any_other_reply_is_a_failure_logged_with_digits_masked()
    {
        var gateway = new Gateway(HttpStatusCode.OK, "401, Invalid key for 9876543210");
        var log = new RecordingLogger();
        var sender = new SmsGatewayOtpSender(new HttpClient(gateway),
            Microsoft.Extensions.Options.Options.Create(Options(SmsOptions.SmsMobile)), log);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sender.SendAsync("9876543210", "123456", CancellationToken.None));

        Assert.Contains("'401'", ex.Message);
        var line = Assert.Single(log.Lines);
        Assert.StartsWith("SMS gateway refused the OTP for 3210: 401, Invalid key for ##########", line);
        Assert.DoesNotContain("123456", line);
    }

    [Fact]
    public async Task SmsMobile_an_HTTP_error_is_a_failure() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Send(SmsOptions.SmsMobile, HttpStatusCode.InternalServerError));

    // ---- Mgov -----------------------------------------------------------------------------

    [Fact]
    public async Task Mgov_hashes_the_password_and_signs_the_message()
    {
        var (gateway, _) = await Send(SmsOptions.Mgov, reply: "402,MsgID = 1");
        var form = gateway.Body!.Split('&')
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));

        Assert.Equal(Hex(SHA1.HashData(Encoding.UTF8.GetBytes("pass word&1"))), form["password"]);
        Assert.Equal(Hex(SHA512.HashData(Encoding.UTF8.GetBytes("user-1" + "BBMPPT" + Message + ServiceKey))), form["key"]);
        Assert.Equal(Message, form["content"]);
    }

    [Fact]
    public async Task Mgov_a_reply_other_than_402_is_a_failure()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Send(SmsOptions.Mgov, reply: "401,Invalid credentials"));
        Assert.Contains("'401'", ex.Message);
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
