using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using PropertyGpsApi.Infrastructure.Storage;
using PropertyGpsApi.Models;

namespace PropertyGpsApi.Services;

/// <summary>
/// The RI's own captures as BtoA_DocumentTran rows, with a BtoA_DocumentTran_Hist row for every
/// row a submit inserts or changes (user, 2026-10-30). Runs inside the submit's transaction,
/// after the survey and its roads are written.
///
/// Types (UDD_MASTERDB.dbo.mst_docType): 13 Property Image - RI Captured, 12 Map Image - RI
/// Captured, 9 Nearest Public Road Document. The private road document (4) is still written by
/// the road procedure and the private-road rules in VerificationSubmitService; it is only given
/// its Scality key here. The served-notice photo has no document type and gets no row.
///
/// docTrn_url holds the Scality reference key - as the citizen's own rows do - when the photo
/// is in Scality, and our link otherwise (Media:Store = LocalDisk).
///
/// History: the table's trigger (trg_BtoA_DocumentTran) is disabled on the test server and
/// reads a column the table no longer has (docTrn_KRS_Id), so it writes nothing. Here every
/// row of the application inserted or updated since the submit started is copied once, with
/// ActionType 'Insert' or 'Update' and its new values.
/// </summary>
public sealed class RiDocumentWriter(ScalityLinks links)
{
    public const int PropertyImage = 13;
    public const int MapImage = 12;
    public const int NearestPublicRoad = 9;

    /// <summary>Earlier RI captures of these types, by this officer, are replaced, not added to.</summary>
    internal const string DeactivateSql = """
        UPDATE dbo.BtoA_DocumentTran
           SET docTrn_Active = 0, docTrn_UBy = @cby, docTrn_UDte = GETDATE(), docTrn_URole = @crole
         WHERE docTrn_App_Id = @appId AND docTrn_Mdoc_id IN (9, 12, 13) AND docTrn_CBy = @cby
           AND docTrn_Active = 1 AND docTrn_CDte < @startedAt;
        """;

    internal const string InsertSql = """
        INSERT INTO dbo.BtoA_DocumentTran
               (docTrn_App_Id, docTrn_Mdoc_id, docTrn_Active, docTrn_url, docTrn_Remarks, docTrn_CBy, docTrn_CDte,
                docTrn_CRole, DigitalSketchUpload_flag, UniqueIdentifier, DocumentId)
        VALUES (@appId, @mdoc, 1, @url, N'Property GPS: RI capture', @cby, GETDATE(), @crole, 0, N'444', @documentId);
        """;

    /// <summary>Rows written this submit (by the road procedure too) still carrying our sc_ link.</summary>
    internal const string LinkedRowsSql = """
        SELECT docTrn_Id AS Id, docTrn_url AS Url FROM dbo.BtoA_DocumentTran
         WHERE docTrn_App_Id = @appId AND docTrn_CDte >= @startedAt AND docTrn_url LIKE N'%/sc[_]%';
        """;

    internal const string HistorySql = """
        INSERT INTO dbo.BtoA_DocumentTran_Hist
               (docTrn_Id, docTrn_KRS_Id, docTrn_Mdoc_id, docTrn_MSubdoc_id, docTrn_MPropType_id, docTrn_Selection,
                docTrn_url, docTrn_Active, docTrn_Remarks, docTrn_AdditionalInfo, docTrn_CBy, docTrn_CDte, docTrn_CRole,
                docTrn_UBy, docTrn_UDte, docTrn_URole, DocumentId, DocumentDate, UniqueIdentifier, ActionType, CDatetime)
        SELECT d.docTrn_Id, d.docTrn_App_Id, d.docTrn_Mdoc_id, d.docTrn_MSubdoc_id, d.docTrn_MPropType_id, d.docTrn_Selection,
               d.docTrn_url, d.docTrn_Active, d.docTrn_Remarks, d.docTrn_AdditionalInfo, d.docTrn_CBy, d.docTrn_CDte, d.docTrn_CRole,
               d.docTrn_UBy, d.docTrn_UDte, d.docTrn_URole, d.DocumentId, d.DocumentDate, d.UniqueIdentifier,
               CASE WHEN d.docTrn_CDte >= @startedAt THEN 'Insert' ELSE 'Update' END, SYSDATETIME()
          FROM dbo.BtoA_DocumentTran d
         WHERE d.docTrn_App_Id = @appId AND (d.docTrn_CDte >= @startedAt OR d.docTrn_UDte >= @startedAt);
        """;

    /// <summary>The rows to insert for one submit: type, link, and the road row for a road photo.</summary>
    internal static List<(int Mdoc, string Url, string? DocumentId)> Captures(
        SubmitVerificationRequest request, IReadOnlyDictionary<string, string> mediaUrls)
    {
        string? UrlOf(string? name) =>
            string.IsNullOrWhiteSpace(name) ? null
            : mediaUrls.TryGetValue(Path.GetFileName(name), out var url) ? url : null;

        var rows = new List<(int, string, string?)>();
        if (UrlOf(request.PropertyImage) is { } property) rows.Add((PropertyImage, property, null));
        if (UrlOf(request.MapImage) is { } map) rows.Add((MapImage, map, null));
        foreach (var road in request.SiteDetails.RoadDetails)
            if (road.RoadStatus != 2 && UrlOf(road.PublicRoadImage) is { } nearest)
                rows.Add((NearestPublicRoad, nearest, road.RoadRowId?.ToString()));
        return rows;
    }

    /// <summary>What docTrn_url holds: the Scality key for a photo in Scality, else our link.</summary>
    internal string DocumentUrl(string url) => links.KeyFromUrl(url) ?? url;

    public async Task WriteAsync(
        SqlConnection connection, SqlTransaction transaction, SubmitVerificationRequest request,
        IReadOnlyDictionary<string, string> mediaUrls, int appId, long officerId, int roleId,
        DateTime startedAt, CancellationToken ct)
    {
        var keys = new { appId, cby = officerId, crole = roleId, startedAt };

        await connection.ExecuteAsync(new CommandDefinition(DeactivateSql, keys, transaction, 30, CommandType.Text, cancellationToken: ct));

        foreach (var (mdoc, url, documentId) in Captures(request, mediaUrls))
            await connection.ExecuteAsync(new CommandDefinition(InsertSql,
                new { appId, mdoc, url = DocumentUrl(url), cby = officerId, crole = roleId, documentId },
                transaction, 30, CommandType.Text, cancellationToken: ct));

        var linked = await connection.QueryAsync<(long Id, string Url)>(new CommandDefinition(
            LinkedRowsSql, keys, transaction, 30, CommandType.Text, cancellationToken: ct));
        foreach (var (id, url) in linked)
            if (links.KeyFromUrl(url) is { } key)
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE dbo.BtoA_DocumentTran SET docTrn_url = @key WHERE docTrn_Id = @id",
                    new { key, id }, transaction, 30, CommandType.Text, cancellationToken: ct));

        await connection.ExecuteAsync(new CommandDefinition(HistorySql, keys, transaction, 30, CommandType.Text, cancellationToken: ct));
    }
}
