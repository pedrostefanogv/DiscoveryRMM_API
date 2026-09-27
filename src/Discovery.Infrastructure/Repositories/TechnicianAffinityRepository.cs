using System.Data;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Repositories;

/// <summary>
/// Afinidade entre o chamado atual e chamados semelhantes já resolvidos, usando
/// similaridade textual do pg_trgm (índice GIN em tickets.title/description).
/// Degrada para lista vazia quando a extensão não está disponível — a triagem
/// continua funcionando, apenas sem este sinal.
/// </summary>
public class TechnicianAffinityRepository(
    DiscoveryDbContext db,
    ILogger<TechnicianAffinityRepository> logger) : ITechnicianAffinityRepository
{
    private const string Sql = @"
        SELECT t.assigned_to_user_id AS user_id,
               t.id                  AS ticket_id,
               t.title               AS title,
               similarity(t.title || ' ' || coalesce(t.description, ''), @query) AS similarity
        FROM tickets t
        WHERE t.assigned_to_user_id IS NOT NULL
          AND t.closed_at IS NOT NULL
          AND t.deleted_at IS NULL
          AND (@client_id IS NULL OR t.client_id = @client_id)
          AND (t.title || ' ' || coalesce(t.description, '')) % @query
        ORDER BY similarity DESC
        LIMIT @limit;";

    public async Task<IReadOnlyList<TechnicianAffinityHit>> FindSimilarResolvedAsync(
        string query, Guid? clientId, int limit, CancellationToken ct = default)
    {
        var normalized = (query ?? string.Empty).Trim();
        if (normalized.Length == 0) return [];

        try
        {
            var connection = db.Database.GetDbConnection();
            var openedHere = connection.State != ConnectionState.Open;
            if (openedHere) await connection.OpenAsync(ct);

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = Sql;
                AddParameter(command, "query", normalized.Length > 1000 ? normalized[..1000] : normalized);
                AddParameter(command, "client_id", (object?)clientId ?? DBNull.Value);
                AddParameter(command, "limit", Math.Clamp(limit, 1, 50));

                var hits = new List<TechnicianAffinityHit>();
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    hits.Add(new TechnicianAffinityHit(
                        reader.GetGuid(0),
                        reader.GetGuid(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(3) ? 0 : reader.GetDouble(3)));
                }

                return hits;
            }
            finally
            {
                if (openedHere) await connection.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Afinidade por pg_trgm indisponível; seguindo sem o sinal de afinidade.");
            return [];
        }
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@" + name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
