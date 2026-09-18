using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Common;
using PropertyGpsApi.Infrastructure.Options;
using PropertyGpsApi.Infrastructure.Storage;

namespace PropertyGpsApi.Tests;

public class LocalDiskMediaStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pgps-tests-" + Guid.NewGuid().ToString("N"));

    private LocalDiskMediaStore Store(long maxBytes = 1024 * 1024) =>
        new(Options.Create(new MediaOptions
        {
            Store = "LocalDisk",
            RootPath = _root,
            PublicBaseUrl = "https://example.test",
            MaxFileBytes = maxBytes
        }), NullLogger<LocalDiskMediaStore>.Instance);

    private static byte[] Jpeg() =>
        new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 }
            .Concat(new byte[128]).Concat(new byte[] { 0xFF, 0xD9 }).ToArray();

    private static MediaUpload Upload(string name = "photo.jpg", byte[]? content = null) =>
        new("property", "1234567890", 7, name, "image/jpeg", content ?? Jpeg());

    [Fact]
    public async Task Stores_a_file_and_reads_it_back_unchanged()
    {
        var store = Store();
        var content = Jpeg();

        var saved = await store.SaveAsync(Upload(content: content), CancellationToken.None);
        var name = saved.Url.Split('/').Last();
        var read = await store.OpenAsync("1234567890", name, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(content, read!.Content);
        Assert.Equal(MediaContentPolicy.Jpeg, read.ContentType);
    }

    /// <summary>
    /// The client's filename only ever decides which slot a part belongs to. What lands on
    /// disk is named by the server, which is what makes traversal and reserved device names
    /// impossible here rather than merely filtered.
    /// </summary>
    [Theory]
    [InlineData("../../../etc/passwd.jpg")]
    [InlineData("CON.jpg")]
    [InlineData("photo.jpg:alternate.jpg")]
    public async Task A_hostile_client_filename_never_reaches_the_filesystem(string hostile)
    {
        var store = Store();
        var saved = await store.SaveAsync(Upload(name: hostile), CancellationToken.None);
        var name = saved.Url.Split('/').Last();

        Assert.DoesNotContain("..", name);
        Assert.DoesNotContain(":", name);
        Assert.StartsWith("1234567890-7-property-", name);

        var written = Directory.GetFiles(_root, "*", SearchOption.AllDirectories);
        Assert.Single(written);
        Assert.StartsWith(Path.GetFullPath(_root), Path.GetFullPath(written[0]));
    }

    [Theory]
    [InlineData("../../../Windows/win.ini")]
    [InlineData("not-a-generated-name.jpg")]
    [InlineData("1234567890-7-property-20260101_000000-deadbeef.exe")]
    public async Task Reading_rejects_anything_the_server_did_not_name(string name)
        => Assert.Null(await Store().OpenAsync("1234567890", name, CancellationToken.None));

    [Fact]
    public async Task Reading_a_missing_file_returns_null_rather_than_throwing()
        => Assert.Null(await Store().OpenAsync(
            "1234567890", "1234567890-0-property-20260101_000000-deadbeef.jpg", CancellationToken.None));

    [Fact]
    public async Task Rejects_a_file_over_the_configured_limit()
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            Store(maxBytes: 64).SaveAsync(Upload(), CancellationToken.None));
        Assert.Equal(ApiErrorCodes.MediaTooLarge, ex.Code);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
