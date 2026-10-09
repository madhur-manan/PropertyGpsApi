using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Common;
using PropertyGpsApi.Infrastructure.Options;
using PropertyGpsApi.Infrastructure.Storage;
using PropertyGpsApi.Models;
using PropertyGpsApi.Services;

namespace PropertyGpsApi.Tests;

/// <summary>
/// Road photographs and the served notice: which bytes each road slot accepts, which
/// uploaded file binds to which road, and what the stored file is called.
///
/// A road has three slots - "private" (road in front of the property), "public" (nearest
/// public road) and "notice" (the served notice, camera or gallery). Every refusal here is
/// a 422 the device parks, so a false refusal costs a completed survey.
/// </summary>
public sealed class RoadMediaTests : IDisposable
{
    private static readonly string[] RoadSlots = ["private", "public", "notice"];

    // ---- bytes --------------------------------------------------------------------------

    private static byte[] Jpeg(bool complete = true, int trailingNulls = 0)
    {
        var body = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        body.AddRange(new byte[256]);
        if (complete) body.AddRange([0xFF, 0xD9]);
        body.AddRange(new byte[trailingNulls]);
        return body.ToArray();
    }

    private static byte[] Png() =>
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.Concat(new byte[64]).ToArray();

    private static byte[] Pdf() => "%PDF-1.7\n"u8.ToArray().Concat(new byte[64]).ToArray();

    /// <summary>An ISO-BMFF HEIC header, as a gallery pick that was NOT re-encoded arrives.</summary>
    private static byte[] Heic() =>
        new byte[] { 0x00, 0x00, 0x00, 0x18 }.Concat("ftypheic"u8.ToArray()).Concat(new byte[64]).ToArray();

    private static byte[] Webp() =>
        "RIFF"u8.ToArray().Concat(new byte[] { 0x24, 0, 0, 0 }).Concat("WEBPVP8 "u8.ToArray()).Concat(new byte[64]).ToArray();

    private static string Code(Action resolve) => Assert.Throws<ApiException>(resolve).Code;

    // ---- MediaContentPolicy per road slot -----------------------------------------------

    public static TheoryData<string> Slots() => new(RoadSlots);

    /// <summary>S16 (a, b, e): camera shots and re-encoded gallery picks, named by content as .jpg.</summary>
    [Theory]
    [MemberData(nameof(Slots))]
    public void A_complete_jpeg_is_accepted_in_every_road_slot(string slot) =>
        Assert.Equal(MediaContentPolicy.Jpeg,
            MediaContentPolicy.Resolve($"{slot}_0b5e.jpg", "image/jpeg", Jpeg(), slot));

    /// <summary>S16 (c): a PNG from the gallery is kept as PNG.</summary>
    [Theory]
    [MemberData(nameof(Slots))]
    public void A_png_is_accepted_in_every_road_slot(string slot) =>
        Assert.Equal(MediaContentPolicy.Png,
            MediaContentPolicy.Resolve($"{slot}_0b5e.png", "image/png", Png(), slot));

    [Theory]
    [InlineData("notice_0b5e.jpeg")]
    [InlineData("notice_0b5e.JPG")]
    [InlineData("NOTICE_0B5E.Jpeg")]
    public void Jpeg_extensions_are_matched_without_regard_to_case(string name) =>
        Assert.Equal(MediaContentPolicy.Jpeg, MediaContentPolicy.Resolve(name, "image/jpeg", Jpeg(), "notice"));

    [Fact]
    public void A_jpeg_padded_with_nulls_after_its_end_marker_is_complete() =>
        Assert.Equal(MediaContentPolicy.Jpeg,
            MediaContentPolicy.Resolve("road_A.jpg", "image/jpeg", Jpeg(trailingNulls: 40), "private"));

    /// <summary>Dart's http package sends this when no type is set; it is silence, not a lie.</summary>
    [Theory]
    [MemberData(nameof(Slots))]
    public void An_undeclared_content_type_is_judged_by_the_bytes(string slot) =>
        Assert.Equal(MediaContentPolicy.Jpeg,
            MediaContentPolicy.Resolve("road_A.jpg", "application/octet-stream", Jpeg(), slot));

    [Theory]
    [MemberData(nameof(Slots))]
    public void A_truncated_jpeg_is_refused_in_every_road_slot(string slot) =>
        Assert.Equal(ApiErrorCodes.MediaTruncated,
            Code(() => MediaContentPolicy.Resolve("road_A.jpg", "image/jpeg", Jpeg(complete: false), slot)));

    [Fact]
    public void A_truncated_jpeg_tells_the_officer_to_capture_it_again()
    {
        var ex = Assert.Throws<ApiException>(() =>
            MediaContentPolicy.Resolve("notice_A.jpg", "image/jpeg", Jpeg(complete: false), "notice"));
        Assert.Equal("'notice_A.jpg' is incomplete. Please capture it again.", ex.Message);
        Assert.True(ex.Recoverable);
    }

    [Theory]
    [MemberData(nameof(Slots))]
    public void A_jpg_holding_a_png_is_a_mismatch(string slot) =>
        Assert.Equal(ApiErrorCodes.MediaMismatch,
            Code(() => MediaContentPolicy.Resolve("road_A.jpg", "image/jpeg", Png(), slot)));

    [Fact]
    public void A_png_holding_a_jpeg_is_a_mismatch() =>
        Assert.Equal(ApiErrorCodes.MediaMismatch,
            Code(() => MediaContentPolicy.Resolve("notice_A.png", "image/png", Jpeg(), "notice")));

    /// <summary>
    /// The app names gallery files by content (photo_store.dart), so a re-encoded HEIC
    /// arrives as .jpg. One still named .heic is a mismatch, not silently accepted.
    /// </summary>
    [Fact]
    public void Jpeg_bytes_under_a_heic_name_are_a_mismatch() =>
        Assert.Equal(ApiErrorCodes.MediaMismatch,
            Code(() => MediaContentPolicy.Resolve("IMG_1.heic", "image/heic", Jpeg(), "notice")));

    /// <summary>S16 (d): a raw HEIC or WebP from the gallery is not a usable notice photo.</summary>
    [Theory]
    [InlineData("IMG_1.jpg")]
    [InlineData("IMG_1.heic")]
    public void Raw_heic_bytes_are_unsupported(string name) =>
        Assert.Equal(ApiErrorCodes.MediaUnsupported,
            Code(() => MediaContentPolicy.Resolve(name, "image/heic", Heic(), "notice")));

    [Fact]
    public void Raw_webp_bytes_are_unsupported() =>
        Assert.Equal(ApiErrorCodes.MediaUnsupported,
            Code(() => MediaContentPolicy.Resolve("IMG_2.jpg", "image/webp", Webp(), "notice")));

    [Fact]
    public void An_empty_file_is_unsupported() =>
        Assert.Equal(ApiErrorCodes.MediaUnsupported,
            Code(() => MediaContentPolicy.Resolve("notice_A.jpg", "image/jpeg", [], "notice")));

    /// <summary>Only the note sheet may be a PDF; a served notice is photographed.</summary>
    [Theory]
    [MemberData(nameof(Slots))]
    public void A_pdf_is_refused_in_every_road_slot(string slot)
    {
        var ex = Assert.Throws<ApiException>(() =>
            MediaContentPolicy.Resolve("notice_A.pdf", "application/pdf", Pdf(), slot));
        Assert.Equal(ApiErrorCodes.MediaUnsupported, ex.Code);
        Assert.Equal("'notice_A.pdf' is a PDF, and only the note sheet may be a PDF.", ex.Message);
    }

    [Fact]
    public void A_specific_wrong_content_type_claim_is_a_mismatch() =>
        Assert.Equal(ApiErrorCodes.MediaMismatch,
            Code(() => MediaContentPolicy.Resolve("notice_A.jpg", "image/png", Jpeg(), "notice")));

    // ---- SubmitMediaService: which upload binds to which road ----------------------------

    private sealed class RecordingStore : IMediaStore
    {
        public List<MediaUpload> Saved { get; } = [];

        public Task<StoredMedia> SaveAsync(MediaUpload upload, CancellationToken ct)
        {
            Saved.Add(upload);
            return Task.FromResult(new StoredMedia(
                $"stored://{upload.Slot}/{upload.RoadOrdinal?.ToString() ?? "none"}/{upload.ClientFileName}",
                "k", "sha", upload.Content.LongLength, MediaContentPolicy.Jpeg));
        }

        public Task<StoredFile?> OpenAsync(string epid, string fileName, CancellationToken ct) =>
            Task.FromResult<StoredFile?>(null);

        public List<string> Discarded { get; } = [];

        public Task DiscardAsync(IEnumerable<string> urls, CancellationToken ct)
        {
            Discarded.AddRange(urls);
            return Task.CompletedTask;
        }
    }

    private static IFormFileCollection Files(params string[] names)
    {
        var files = new FormFileCollection();
        foreach (var name in names)
        {
            var bytes = Jpeg();
            files.Add(new FormFile(new MemoryStream(bytes), 0, bytes.Length, "files", name)
            {
                Headers = new HeaderDictionary(),
                ContentType = "image/jpeg",
            });
        }
        return files;
    }

    private static SubmitRoadDetail Road(int? rowId, string? road, string? pub, string? notice) => new()
    {
        RoadRowId = rowId,
        RoadStatus = rowId is null ? 3 : 0,
        IsPresentInPublicRoadList = 0,
        PrivateRoadImage = road,
        PublicRoadImage = pub,
        NoticeImage = notice,
    };

    private static SubmitVerificationRequest Survey(params SubmitRoadDetail[] roads) => new()
    {
        ApplicationId = "TESTAPP0001",
        Epid = "9990000001",
        SiteDetails = new SubmitSiteDetails { RoadDetails = roads },
    };

    private static async Task<(RecordingStore Store, IReadOnlyDictionary<string, string> Urls)> StoreAsync(
        SubmitVerificationRequest request, IFormFileCollection files)
    {
        var store = new RecordingStore();
        var urls = await new SubmitMediaService(store, NullLogger<SubmitMediaService>.Instance)
            .StoreAsync(request, files, CancellationToken.None);
        return (store, urls);
    }

    /// <summary>S2: each of a declared road's three files goes to its own slot under that road's row id.</summary>
    [Fact]
    public async Task S2_a_declared_roads_files_are_stored_under_its_row_id_and_slot()
    {
        var (store, urls) = await StoreAsync(
            Survey(Road(91011, "road_A.jpg", "public_road_A.jpg", "notice_A.jpg")),
            Files("road_A.jpg", "public_road_A.jpg", "notice_A.jpg"));

        Assert.Equal(
            [("private", 91011, "road_A.jpg"), ("public", 91011, "public_road_A.jpg"), ("notice", 91011, "notice_A.jpg")],
            store.Saved.Select(u => (u.Slot, u.RoadOrdinal, u.ClientFileName)));
        Assert.All(store.Saved, u => Assert.Equal("9990000001", u.Epid));
        Assert.Equal("stored://notice/91011/notice_A.jpg", urls["notice_A.jpg"]);
    }

    /// <summary>S7 / S15: an officer-added road has no row id, so its files carry no ordinal (named -0-).</summary>
    [Fact]
    public async Task S7_an_added_roads_files_are_stored_without_a_road_ordinal()
    {
        var (store, _) = await StoreAsync(
            Survey(Road(91081, "road_A.jpg", "public_road_A.jpg", "notice_A.jpg"),
                   Road(null, "road_B.jpg", "public_road_B.jpg", "notice_B.jpg")),
            Files("road_A.jpg", "public_road_A.jpg", "notice_A.jpg", "road_B.jpg", "public_road_B.jpg", "notice_B.jpg"));

        Assert.Equal([91081, 91081, 91081, null, null, null], store.Saved.Select(u => u.RoadOrdinal));
    }

    [Fact]
    public async Task S1_a_public_road_with_no_captures_stores_nothing()
    {
        var (store, urls) = await StoreAsync(Survey(Road(91001, null, null, null)), Files());

        Assert.Empty(store.Saved);
        Assert.Empty(urls);
    }

    [Fact]
    public async Task S16f_a_notice_named_in_the_survey_but_not_uploaded_is_refused()
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => StoreAsync(
            Survey(Road(91011, "road_A.jpg", "public_road_A.jpg", "notice_A.jpg")),
            Files("road_A.jpg", "public_road_A.jpg")));

        Assert.Equal(ApiErrorCodes.MediaMissing, ex.Code);
        Assert.Equal("The survey refers to 'notice_A.jpg' but no such file was uploaded.", ex.Message);
    }

    /// <summary>
    /// The road's other two photographs were written before the missing notice was found.
    /// The survey goes no further, so they are removed rather than left behind on every retry.
    /// </summary>
    [Fact]
    public async Task Files_stored_before_a_refusal_are_discarded()
    {
        var store = new RecordingStore();
        await Assert.ThrowsAsync<ApiException>(() =>
            new SubmitMediaService(store, NullLogger<SubmitMediaService>.Instance).StoreAsync(
                Survey(Road(91011, "road_A.jpg", "public_road_A.jpg", "notice_A.jpg")),
                Files("road_A.jpg", "public_road_A.jpg"), CancellationToken.None));

        Assert.Equal(
            ["stored://private/91011/road_A.jpg", "stored://public/91011/public_road_A.jpg"],
            store.Discarded);
    }

    [Fact]
    public async Task A_survey_whose_files_all_store_discards_nothing()
    {
        var (store, _) = await StoreAsync(
            Survey(Road(91011, "road_A.jpg", null, "notice_A.jpg")),
            Files("road_A.jpg", "notice_A.jpg"));

        Assert.Empty(store.Discarded);
    }

    [Fact]
    public async Task An_upload_is_bound_by_basename_and_without_regard_to_case()
    {
        var (store, urls) = await StoreAsync(
            Survey(Road(91011, "road_A.jpg", null, "notice_A.jpg")),
            Files("field_photos/ROAD_A.JPG", "notice_A.jpg"));

        Assert.Equal(2, store.Saved.Count);
        Assert.True(urls.ContainsKey("road_A.jpg"));
    }

    [Fact]
    public async Task One_file_named_in_two_road_slots_is_stored_once()
    {
        var (store, _) = await StoreAsync(
            Survey(Road(91081, "road_A.jpg", null, "notice_A.jpg"), Road(91082, "road_A.jpg", null, "notice_B.jpg")),
            Files("road_A.jpg", "notice_A.jpg", "notice_B.jpg"));

        Assert.Equal(["road_A.jpg", "notice_A.jpg", "notice_B.jpg"], store.Saved.Select(u => u.ClientFileName));
    }

    // ---- LocalDiskMediaStore: the stored name of a road file -----------------------------

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pgps-road-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private LocalDiskMediaStore DiskStore() => new(Options.Create(new MediaOptions
    {
        Store = "LocalDisk",
        RootPath = _root,
        PublicBaseUrl = "https://example.test",
        MaxFileBytes = 1024 * 1024,
    }), NullLogger<LocalDiskMediaStore>.Instance);

    [Theory]
    [InlineData("private", 91011, "road_A.jpg", "jpg")]
    [InlineData("public", 91011, "public_road_A.jpg", "jpg")]
    [InlineData("notice", 91011, "notice_A.png", "png")]
    [InlineData("notice", null, "notice_B.jpg", "jpg")]
    public async Task A_road_file_is_named_epid_row_slot_stamp_and_reads_back(
        string slot, int? rowId, string clientName, string extension)
    {
        var content = extension == "png" ? Png() : Jpeg();
        var store = DiskStore();

        var saved = await store.SaveAsync(new MediaUpload(
            slot, "9990000001", rowId, clientName, extension == "png" ? "image/png" : "image/jpeg", content),
            CancellationToken.None);

        var name = saved.Url.Split('/').Last();
        Assert.Matches(
            new Regex($@"^9990000001-{rowId?.ToString() ?? "0"}-{slot}-[0-9]{{8}}_[0-9]{{6}}-[a-f0-9]{{8}}\.{extension}$"),
            name);
        Assert.StartsWith($"https://example.test/{ApiRoutes.Base}/propertyinfo/file/view/9990000001/", saved.Url);

        var read = await store.OpenAsync("9990000001", name, CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal(content, read!.Content);
    }

    [Fact]
    public async Task A_road_file_over_the_size_limit_is_refused()
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => DiskStore().SaveAsync(
            new MediaUpload("notice", "9990000001", 91011, "notice_A.jpg", "image/jpeg", new byte[1024 * 1024 + 1]),
            CancellationToken.None));

        Assert.Equal(ApiErrorCodes.MediaTooLarge, ex.Code);
    }
}
