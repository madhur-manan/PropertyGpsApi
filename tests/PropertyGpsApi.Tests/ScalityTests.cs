using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PropertyGpsApi.Common;
using PropertyGpsApi.Infrastructure.Options;
using PropertyGpsApi.Infrastructure.Storage;
using PropertyGpsApi.Models;
using PropertyGpsApi.Services;
using Xunit;

namespace PropertyGpsApi.Tests;

/// <summary>
/// BBMP's Scality document store (user, 2026-10-30): the RI's photos are stored there, the
/// citizen's documents are read from there, and every RI capture gets a BtoA_DocumentTran row
/// with a history row.
/// </summary>
[Collection("Scality token")]
public sealed class ScalityTests
{
    private const string Key = "bbmpit_udd_b2a/1cef093b-187a-4576-bbaf-be1080667511.jpg";

    /// <summary>A Scality that answers token, upload and fetch, and records what it was sent.</summary>
    private sealed class FakeScality : HttpMessageHandler
    {
        public int Tokens;
        public readonly List<string> Uploads = [];
        public HttpStatusCode? NextStatus;
        public bool RefuseUpload;
        public bool Down;
        public byte[] Stored = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Down) throw new HttpRequestException("connection refused");
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/Auth/token"))
            {
                Tokens++;
                return Json(new { access_token = $"token-{Tokens}", token_type = "Bearer", expires_in = 1800 });
            }
            if (NextStatus is { } status)
            {
                NextStatus = null;
                return new HttpResponseMessage(status);
            }
            if (path.EndsWith("/api/Scality/upload-base64"))
            {
                Uploads.Add(await request.Content!.ReadAsStringAsync(ct));
                return RefuseUpload
                    ? Json(new { success = false, referenceKey = "", errorMessage = "bucket full" })
                    : Json(new { success = true, referenceKey = Key, errorMessage = "" });
            }
            if (path.EndsWith("/api/Scality/fetch-base64"))
                return Json(new { success = true, base64Data = Convert.ToBase64String(Stored), contentType = "application/octet-stream" });
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }

    private static ScalityClient Client(FakeScality server, FakeTimeProvider? clock = null, bool configured = true)
    {
        ScalityClient.ForgetToken();
        return new ScalityClient(new HttpClient(server),
            Options.Create(new ScalityOptions { ClientId = configured ? "Admin" : "", ClientSecret = configured ? "secret" : "" }),
            clock ?? new FakeTimeProvider(DateTimeOffset.UtcNow), NullLogger<ScalityClient>.Instance);
    }

    private static ScalityLinks Links() =>
        new(Options.Create(new JwtOptions { Key = "a-test-signing-key-that-is-long-enough-1234567890", Issuer = "i", Audience = "a" }));

    // ---- the client ----------------------------------------------------------------------------

    [Fact]
    public async Task Uploads_the_photo_as_base64_with_the_table_and_bucket_and_returns_the_key()
    {
        var server = new FakeScality();
        var key = await Client(server).UploadAsync([1, 2, 3], "38521372-0-property-20261030_101500", "jpg", default);

        Assert.Equal(Key, key);
        var sent = JsonDocument.Parse(server.Uploads.Single()).RootElement;
        Assert.Equal("AQID", sent.GetProperty("base64String").GetString());
        Assert.Equal("jpg", sent.GetProperty("extension").GetString());
        Assert.Equal("udd_b2a", sent.GetProperty("tableName").GetString());
        Assert.Equal("bbmpit", sent.GetProperty("bucketName").GetString());
    }

    [Fact]
    public async Task The_token_is_reused_then_renewed_near_expiry_and_after_a_401()
    {
        var server = new FakeScality();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var client = Client(server, clock);

        await client.UploadAsync([1], "a", "jpg", default);
        await client.UploadAsync([1], "b", "jpg", default);
        Assert.Equal(1, server.Tokens);

        clock.Advance(TimeSpan.FromMinutes(29)); // within two minutes of the 30-minute expiry
        await client.UploadAsync([1], "c", "jpg", default);
        Assert.Equal(2, server.Tokens);

        server.NextStatus = HttpStatusCode.Unauthorized;
        await client.UploadAsync([1], "d", "jpg", default);
        Assert.Equal(3, server.Tokens);
    }

    [Fact]
    public async Task A_refused_or_unreachable_store_fails_the_submit_as_retryable()
    {
        var refused = new FakeScality { RefuseUpload = true };
        var ex = await Assert.ThrowsAsync<ApiException>(() => Client(refused).UploadAsync([1], "a", "jpg", default));
        Assert.True(ex.Retryable);
        Assert.Equal(503, ex.StatusCode);

        var down = new FakeScality { Down = true };
        Assert.True((await Assert.ThrowsAsync<ApiException>(() => Client(down).UploadAsync([1], "a", "jpg", default))).Retryable);
    }

    [Fact]
    public async Task Without_credentials_nothing_is_sent()
    {
        var server = new FakeScality();
        var ex = await Assert.ThrowsAsync<ApiException>(() => Client(server, configured: false).UploadAsync([1], "a", "jpg", default));
        Assert.Equal("DOCUMENT_STORE_NOT_CONFIGURED", ex.Code);
        Assert.Equal(0, server.Tokens);
    }

    [Fact]
    public async Task Fetches_a_file_and_names_its_type_from_the_key_or_the_bytes()
    {
        var file = await Client(new FakeScality()).FetchAsync(Key, default);
        Assert.NotNull(file);
        Assert.Equal("image/jpeg", file.ContentType);

        Assert.Equal("application/pdf", ScalityClient.ContentTypeFor("x.bin", Encoding.ASCII.GetBytes("%PDF-1.7")));
    }

    // ---- links ---------------------------------------------------------------------------------

    [Fact]
    public void A_link_keeps_todays_shape_and_carries_its_key_signed_to_its_epid()
    {
        var links = Links();
        var url = links.Url("https://testapps.bbmpgov.in/propertygps", "38-521-372", Key);

        Assert.StartsWith("https://testapps.bbmpgov.in/propertygps/v1/api/gbagps/singlesite/propertyinfo/file/view/38-521-372/sc_", url);
        Assert.Equal(Key, links.KeyFromUrl(url));
        Assert.Matches(@"/sc_[A-Za-z0-9_-]+\.[A-Za-z0-9_-]{16}$", url);
    }

    [Fact]
    public void A_link_cannot_be_moved_to_another_epid_or_edited()
    {
        var links = Links();
        var name = links.Name("38-521-372", Key);

        Assert.Equal(Key, links.KeyFor("38-521-372", name));
        Assert.Null(links.KeyFor("38-521-999", name));
        Assert.Null(links.KeyFor("38-521-372", name[..^1] + (name[^1] == 'A' ? 'B' : 'A')));
        Assert.Null(links.KeyFor("38-521-372", "sc_../../etc.passwd"));
        Assert.Null(links.KeyFromUrl("https://x/propertyinfo/file/view/38-521-372/38521372-0-property-20261030_101500-abcdef12.jpg"));
    }

    // ---- the RI's document rows ----------------------------------------------------------------

    [Fact]
    public void Each_capture_gets_its_document_type()
    {
        var request = new SubmitVerificationRequest
        {
            PropertyImage = "p.jpg",
            MapImage = "m.png",
            SiteDetails = new SubmitSiteDetails
            {
                RoadDetails =
                [
                    new SubmitRoadDetail { RoadRowId = 11, RoadStatus = 0, PublicRoadImage = "near.jpg", NoticeImage = "n.jpg" },
                    new SubmitRoadDetail { RoadRowId = 12, RoadStatus = 2, PublicRoadImage = "gone.jpg" },
                ]
            }
        };
        var urls = new Dictionary<string, string> { ["p.jpg"] = "u-p", ["m.png"] = "u-m", ["near.jpg"] = "u-near", ["n.jpg"] = "u-n", ["gone.jpg"] = "u-g" };

        var rows = RiDocumentWriter.Captures(request, urls);

        Assert.Equal(
            [(13, "u-p", null), (12, "u-m", null), (9, "u-near", "11")],
            rows.Select(r => (r.Mdoc, r.Url, r.DocumentId)).ToList());
    }

    [Fact]
    public void A_photo_in_scality_is_filed_by_its_key_and_one_on_disk_by_its_link()
    {
        var links = Links();
        var writer = new RiDocumentWriter(links);
        var scalityUrl = links.Url("https://t/propertygps", "38-521-372", Key);

        Assert.Equal(Key, writer.DocumentUrl(scalityUrl));
        Assert.Equal("https://t/x/file/view/e/a.jpg", writer.DocumentUrl("https://t/x/file/view/e/a.jpg"));
    }

    [Fact]
    public void Earlier_captures_are_replaced_and_every_change_gets_a_history_row()
    {
        Assert.Contains("docTrn_Mdoc_id IN (9, 12, 13) AND docTrn_CBy = @cby", RiDocumentWriter.DeactivateSql);
        Assert.Contains("docTrn_CDte < @startedAt", RiDocumentWriter.DeactivateSql);
        Assert.Contains("INSERT INTO dbo.BtoA_DocumentTran_Hist", RiDocumentWriter.HistorySql);
        Assert.Contains("d.docTrn_App_Id,", RiDocumentWriter.HistorySql);   // into docTrn_KRS_Id
        Assert.Contains("CASE WHEN d.docTrn_CDte >= @startedAt THEN 'Insert' ELSE 'Update' END", RiDocumentWriter.HistorySql);
        Assert.Contains("(d.docTrn_CDte >= @startedAt OR d.docTrn_UDte >= @startedAt)", RiDocumentWriter.HistorySql);
    }

    // ---- the citizen's documents ---------------------------------------------------------------

    [Fact]
    public void The_list_leaves_out_the_ris_own_captures()
    {
        Assert.Contains("NOT IN (12, 13)", CitizenDocumentService.ListSql);
        Assert.Contains("NOT IN (116, 117)", CitizenDocumentService.ListSql);
        Assert.Contains("docTrn_Active = 1", CitizenDocumentService.ListSql);
        Assert.Contains("NOT IN (116, 117)", CitizenDocumentService.OneSql);
    }

    [Theory]
    [InlineData("bbmpit_udd_b2a/x.pdf", "pdf")]
    [InlineData("bbmpit_udd_b2a/x.JPEG", "jpg")]
    [InlineData("bbmpit_udd_b2a/x.png", "png")]
    [InlineData("bbmpit_udd_b2a/x", "")]
    public void The_file_type_comes_from_the_key(string url, string type) =>
        Assert.Equal(type, CitizenDocumentService.FileTypeOf(url));
}

[CollectionDefinition("Scality token", DisableParallelization = true)]
public sealed class ScalityTokenCollection;
