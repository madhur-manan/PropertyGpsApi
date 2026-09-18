using Dapper;
using PropertyGpsApi.Infrastructure.Data;

namespace PropertyGpsApi.Features.Properties;

public interface IMediaAccessReader
{
    Task<MediaScope?> ScopeForAsync(string epid, CancellationToken ct);
}

/// <summary>Which zone and ward an EPID belongs to.</summary>
public sealed record MediaScope(int ZoneId, int WardId);

/// <summary>
/// Answers "whose property is this" for the media endpoint.
///
/// Survey photographs are evidence in a tax assessment and carry the officer's GPS trail,
/// so reading one is a jurisdiction decision, not just an authentication one. The stored
/// URLs contain only an EPID and a timestamp, which makes them guessable - without this
/// check any signed-in officer in the state could walk another ward's evidence.
/// </summary>
internal sealed class MediaAccessReader(ISqlConnectionFactory connections) : IMediaAccessReader
{
    private const string Sql = """
        SELECT TOP (1) ZoneId = md.MD_ZoneId, WardId = md.MD_WardId
        FROM dbo.BtoA_EPIDMetaData md WITH (NOLOCK)
        JOIN dbo.BtoAMainApp ap WITH (NOLOCK)
          ON ap.App_Id = md.MD_APP_ID AND ap.App_MotherEPID = md.MD_MotherEPID
        WHERE md.MD_MotherEPID = @epid AND ap.App_Active = 1;
        """;

    public async Task<MediaScope?> ScopeForAsync(string epid, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(DbTarget.B2A, ct);
        return await connection.QueryFirstOrDefaultAsync<MediaScope>(
            new CommandDefinition(Sql, new { epid }, cancellationToken: ct));
    }
}
