using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Data;

/// <summary>
/// Consulta de existência de tabela legada (Postgres) para código que deve
/// tolerar tabelas removidas por migração (ex.: winget_packages, dropada pela
/// M055). Usa to_regclass — NÃO lança 42P01 quando a tabela não existe — e
/// trata provedores não-Postgres (testes InMemory/SQLite) como "existe",
/// preservando o comportamento atual desses ambientes.
///
/// Importante: esta checagem deve rodar ANTES de qualquer consulta que toque a
/// tabela. Um 42P01 dentro da transação do comando (TransactionBehavior)
/// aborta a transação inteira — mesmo que a exceção seja engolida — e o
/// comando seguinte falha com 25P02 (vira HTTP 500).
/// </summary>
internal static class LegacyTableGuard
{
    public static async Task<bool> ExistsAsync(DiscoveryDbContext db, string tableName, CancellationToken cancellationToken = default)
    {
        // Provedores não relacionais/nao-Postgres (testes InMemory, SQLite):
        // to_regclass não existe — assume "existe" para não mudar semântica.
        if (!db.Database.IsNpgsql())
            return true;

        var exists = await db.Database
            .SqlQuery<bool>($"SELECT to_regclass({tableName}) IS NOT NULL AS \"Value\"")
            .SingleAsync(cancellationToken);
        return exists;
    }
}
