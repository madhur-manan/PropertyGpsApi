using System.Text.Json.Serialization;
using Dapper;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Infrastructure.Storage;

namespace PropertyGpsApi.Services;

/// <summary>One citizen document of an application, as the app lists it.</summary>
public sealed class CitizenDocumentDto
{
    [JsonPropertyName("documentId")] public long DocumentId { get; init; }
    [JsonPropertyName("typeId")] public int TypeId { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";

    /// <summary>"pdf", "jpg", "png" or "" - so the app knows which viewer to open.</summary>
    [JsonPropertyName("fileType")] public string FileType { get; init; } = "";

    [JsonPropertyName("uploadedOn")] public DateTime? UploadedOn { get; init; }
}

public interface ICitizenDocumentService
{
    Task<string?> EpidOfAsync(long appId, CancellationToken ct);
    Task<IReadOnlyList<CitizenDocumentDto>> ListAsync(long appId, CancellationToken ct);
    Task<(long AppId, string Url)?> FindAsync(long documentId, CancellationToken ct);
    Task<StoredFile?> OpenAsync(string url, CancellationToken ct);
}

/// <summary>
/// The citizen's documents of an application (user, 2026-10-30): the active BtoA_DocumentTran
/// rows, named from UDD_MASTERDB.dbo.mst_docType. The RI's own captures are left out - types
/// 12 and 13, and every row created by a ward officer (roles 116 / 117). The files are read
/// from Scality by their reference key; an older row holding our own link is read from the disk.
/// </summary>
internal sealed class CitizenDocumentService(
    ISqlConnectionFactory connections,
    IScalityClient scality,
    LocalDiskMediaStore disk) : ICitizenDocumentService
{
    internal const string ListSql = """
        SELECT d.docTrn_Id AS DocumentId, d.docTrn_Mdoc_id AS TypeId, d.docTrn_url AS Url, d.docTrn_CDte AS UploadedOn
          FROM dbo.BtoA_DocumentTran d WITH (NOLOCK)
         WHERE d.docTrn_App_Id = @appId AND d.docTrn_Active = 1
           AND ISNULL(d.docTrn_Mdoc_id, 0) NOT IN (12, 13)
           AND ISNULL(d.docTrn_CRole, 0) NOT IN (116, 117)
           AND ISNULL(d.docTrn_url, N'') <> N''
         ORDER BY d.docTrn_Mdoc_id, d.docTrn_Id;
        """;

    internal const string OneSql = """
        SELECT d.docTrn_App_Id AS AppId, d.docTrn_url AS Url
          FROM dbo.BtoA_DocumentTran d WITH (NOLOCK)
         WHERE d.docTrn_Id = @documentId AND d.docTrn_Active = 1
           AND ISNULL(d.docTrn_Mdoc_id, 0) NOT IN (12, 13)
           AND ISNULL(d.docTrn_CRole, 0) NOT IN (116, 117);
        """;

    internal const string EpidSql = "SELECT TOP (1) App_MotherEPID FROM dbo.BtoAMainApp WITH (NOLOCK) WHERE App_Id = @appId";

    internal const string NamesSql = "SELECT doc_Id AS Id, doc_name AS Name FROM dbo.mst_docType WITH (NOLOCK)";

    /// <summary>The application's EPID (for the ward check), or null when there is no such application.</summary>
    public async Task<string?> EpidOfAsync(long appId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(DbTarget.B2A, ct);
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(EpidSql, new { appId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<CitizenDocumentDto>> ListAsync(long appId, CancellationToken ct)
    {
        List<DocRow> rows;
        await using (var b2a = await connections.OpenAsync(DbTarget.B2A, ct))
            rows = (await b2a.QueryAsync<DocRow>(new CommandDefinition(ListSql, new { appId }, cancellationToken: ct))).AsList();
        if (rows.Count == 0) return [];

        Dictionary<int, string> names;
        await using (var master = await connections.OpenAsync(DbTarget.Master, ct))
            names = (await master.QueryAsync<(int Id, string Name)>(new CommandDefinition(NamesSql, cancellationToken: ct)))
                .GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First().Name?.Trim() ?? "");

        return Distinct(rows).Select(r => new CitizenDocumentDto
        {
            DocumentId = r.DocumentId,
            TypeId = r.TypeId ?? 0,
            Name = names.TryGetValue(r.TypeId ?? 0, out var n) && n.Length > 0 ? n : "Document",
            FileType = FileTypeOf(r.Url),
            UploadedOn = r.UploadedOn,
        }).ToList();
    }

    /// <summary>
    /// One entry per file: the citizen side writes some documents twice (same type, same
    /// Scality key - e.g. rows 292 and 293 of application 119 on the test server).
    /// </summary>
    private static IEnumerable<DocRow> Distinct(IEnumerable<DocRow> rows) =>
        rows.GroupBy(r => (r.TypeId, Url: (r.Url ?? "").Trim())).Select(g => g.First());

    /// <summary>The document's application, for the ward check, and a way to read its file.</summary>
    public async Task<(long AppId, string Url)?> FindAsync(long documentId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(DbTarget.B2A, ct);
        var row = await connection.QueryFirstOrDefaultAsync<KeyRow>(
            new CommandDefinition(OneSql, new { documentId }, cancellationToken: ct));
        return row is not null && !string.IsNullOrWhiteSpace(row.Url) ? (row.AppId, row.Url) : null;
    }

    /// <summary>The file behind docTrn_url: a Scality key, or a link of ours to a photo on the disk.</summary>
    public async Task<StoredFile?> OpenAsync(string url, CancellationToken ct)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length >= 2 && !segments[^1].StartsWith(ScalityLinks.Prefix, StringComparison.Ordinal)
                ? await disk.OpenAsync(Uri.UnescapeDataString(segments[^2]), Uri.UnescapeDataString(segments[^1]), ct)
                : null;
        }
        return await scality.FetchAsync(url.Trim(), ct);
    }

    internal static string FileTypeOf(string? url)
    {
        var ext = Path.GetExtension((url ?? "").Split('?')[0]).TrimStart('.').ToLowerInvariant();
        return ext is "jpeg" ? "jpg" : ext is "pdf" or "jpg" or "png" ? ext : "";
    }

    private sealed class KeyRow
    {
        public long AppId { get; init; }
        public string? Url { get; init; }
    }

    private sealed class DocRow
    {
        public long DocumentId { get; init; }
        public int? TypeId { get; init; }
        public string? Url { get; init; }
        public DateTime? UploadedOn { get; init; }
    }
}
