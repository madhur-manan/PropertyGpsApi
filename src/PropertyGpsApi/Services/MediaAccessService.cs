using Dapper;
using PropertyGpsApi.Infrastructure.Data;

using PropertyGpsApi.Models;

using PropertyGpsApi.Interfaces;

namespace PropertyGpsApi.Services;


/// <summary>
/// Answers "whose property is this" for the media endpoint.
///
/// Survey photographs are evidence in a tax assessment and carry the officer's GPS trail,
/// so reading one is a jurisdiction decision, not just an authentication one. The stored
/// URLs contain only an EPID and a timestamp, which makes them guessable - without this
/// check any signed-in officer in the state could walk another ward's evidence.
///
/// The scope is the application's ULB and ward number, as the ward sync matches them. Since
/// db/44 one EPID can have several applications on the phones; their files share the EPID's
/// folder, so the scope is the newest active application's - one answer, not whichever row
/// SQL Server happened to read first.
/// </summary>
internal sealed class MediaAccessService(ISqlConnectionFactory connections) : IMediaAccessService
{
    internal const string Sql = """
        SELECT TOP (1) UlbCode = TRY_CONVERT(int, md.MD_ULBCode),
               WardNumber = LTRIM(RTRIM(CONVERT(nvarchar(20), md.MD_WardId)))
        FROM dbo.BtoA_EPIDMetaData md WITH (NOLOCK)
        JOIN dbo.BtoAMainApp ap WITH (NOLOCK)
          ON ap.App_Id = md.MD_APP_ID AND ap.App_MotherEPID = md.MD_MotherEPID
        WHERE md.MD_MotherEPID = @epid AND ap.App_Active = 1
        ORDER BY ap.App_Id DESC;
        """;

    public async Task<MediaScope?> ScopeForAsync(string epid, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(DbTarget.B2A, ct);
        return await connection.QueryFirstOrDefaultAsync<MediaScope>(
            new CommandDefinition(Sql, new { epid }, cancellationToken: ct));
    }
}
