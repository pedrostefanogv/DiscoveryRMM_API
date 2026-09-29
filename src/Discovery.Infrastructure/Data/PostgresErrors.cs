using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Discovery.Infrastructure.Data;

/// <summary>
/// Classificacao de erros de escrita do Postgres usada pelos servicos/handlers de labels.
///
/// Centraliza o que antes era um helper privado por classe (P2pService,
/// AddAgentLabelCommandHandler, AgentAutoLabelingService), evitando divergencia.
/// </summary>
internal static class PostgresErrors
{
    /// <summary>Violacao de indice unico (corrida entre escritores).</summary>
    public static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation;
}
