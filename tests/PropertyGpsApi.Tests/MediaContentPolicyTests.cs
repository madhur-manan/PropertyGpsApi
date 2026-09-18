using PropertyGpsApi.Common;
using PropertyGpsApi.Infrastructure.Storage;

namespace PropertyGpsApi.Tests;

/// <summary>
/// What a captured file is allowed to be. These are the checks standing between a phone
/// and the filesystem of a government server, so each failure mode gets its own test.
/// </summary>
public class MediaContentPolicyTests
{
    private static byte[] Jpeg(bool complete = true)
    {
        var body = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 }
            .Concat(new byte[200]).ToList();
        if (complete) body.AddRange(new byte[] { 0xFF, 0xD9 });
        return body.ToArray();
    }

    private static byte[] Png() =>
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.Concat(new byte[64]).ToArray();

    private static byte[] Pdf() =>
        "%PDF-1.4\n"u8.ToArray().Concat(new byte[64]).ToArray();

    [Fact]
    public void Accepts_a_complete_jpeg()
        => Assert.Equal(MediaContentPolicy.Jpeg,
            MediaContentPolicy.Resolve("photo.jpg", "image/jpeg", Jpeg(), "property"));

    [Fact]
    public void Accepts_a_png()
        => Assert.Equal(MediaContentPolicy.Png,
            MediaContentPolicy.Resolve("map.png", "image/png", Png(), "map"));

    [Fact]
    public void Accepts_a_pdf_only_in_the_note_sheet_slot()
        => Assert.Equal(MediaContentPolicy.Pdf,
            MediaContentPolicy.Resolve("note.pdf", "application/pdf", Pdf(),
                MediaContentPolicy.NoteSheetSlot));

    [Fact]
    public void Rejects_a_pdf_in_a_camera_slot()
    {
        var ex = Assert.Throws<ApiException>(() =>
            MediaContentPolicy.Resolve("note.pdf", "application/pdf", Pdf(), "property"));
        Assert.Equal(ApiErrorCodes.MediaUnsupported, ex.Code);
        Assert.True(ex.Recoverable);
    }

    /// <summary>
    /// The failure mode that matters most: a connection dying mid-upload produces a JPEG
    /// that opens fine in some viewers and is unusable as evidence at QC.
    /// </summary>
    [Fact]
    public void Rejects_a_truncated_jpeg()
    {
        var ex = Assert.Throws<ApiException>(() =>
            MediaContentPolicy.Resolve("photo.jpg", "image/jpeg", Jpeg(complete: false), "property"));
        Assert.Equal(ApiErrorCodes.MediaTruncated, ex.Code);
    }

    [Fact]
    public void Rejects_content_that_does_not_match_its_extension()
    {
        var ex = Assert.Throws<ApiException>(() =>
            MediaContentPolicy.Resolve("photo.jpg", "image/jpeg", Png(), "property"));
        Assert.Equal(ApiErrorCodes.MediaMismatch, ex.Code);
    }

    [Fact]
    public void Rejects_something_that_is_not_an_image_or_pdf()
    {
        var ex = Assert.Throws<ApiException>(() =>
            MediaContentPolicy.Resolve("payload.jpg", "image/jpeg",
                "<svg onload=alert(1)>"u8.ToArray(), "property"));
        Assert.Equal(ApiErrorCodes.MediaUnsupported, ex.Code);
    }

    [Theory]
    [InlineData("shell.exe")]
    [InlineData("archive.zip")]
    [InlineData("noextension")]
    public void Rejects_disallowed_extensions(string name)
        => Assert.Throws<ApiException>(() =>
            MediaContentPolicy.Resolve(name, "image/jpeg", Jpeg(), "property"));
}
