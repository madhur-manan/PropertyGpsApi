using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Infrastructure.Options;
using PropertyGpsApi.Infrastructure.Storage;
using PropertyGpsApi.Models;
using PropertyGpsApi.Services;

namespace PropertyGpsApi.Tests;

/// <summary>
/// A survey that fails before its commit leaves no files behind. On 2026-10-09 the test
/// server refused one survey ten times (a procedure there had been replaced) and kept all
/// ten sets of four photographs - 36 orphans for one application.
/// </summary>
public class SubmitMediaCleanupTests
{
    private sealed class UnreachableDatabase : ISqlConnectionFactory
    {
        public Task<SqlConnection> OpenAsync(DbTarget target, CancellationToken ct = default) =>
            throw new InvalidOperationException("database unreachable");
    }

    private sealed class RecordingStore : IMediaStore
    {
        public List<string> Discarded { get; } = [];

        public Task<StoredMedia> SaveAsync(MediaUpload upload, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<StoredFile?> OpenAsync(string epid, string fileName, CancellationToken ct) =>
            Task.FromResult<StoredFile?>(null);

        public Task DiscardAsync(IEnumerable<string> urls, CancellationToken ct)
        {
            Discarded.AddRange(urls);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_submit_that_fails_before_its_commit_discards_the_surveys_files()
    {
        var store = new RecordingStore();
        var service = new VerificationSubmitService(
            new UnreachableDatabase(), store,
            Options.Create(new StoredProcedureOptions()),
            NullLogger<VerificationSubmitService>.Instance);

        var urls = new Dictionary<string, string>
        {
            ["property.jpg"] = "https://example.test/v/9990000101/a.jpg",
            ["notice.jpg"] = "https://example.test/v/9990000101/b.jpg",
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SubmitAsync(
            new SubmitVerificationRequest
            {
                ApplicationId = "TESTAPP0001",
                Epid = "9990000101",
                SiteDetails = new SubmitSiteDetails { RoadDetails = [] },
            },
            urls, officerId: 11320, roleId: 116, officerMobile: null, CancellationToken.None));

        // The survey's own error reaches the caller unchanged.
        Assert.Equal("database unreachable", ex.Message);
        Assert.Equal(urls.Values, store.Discarded);
    }
}
