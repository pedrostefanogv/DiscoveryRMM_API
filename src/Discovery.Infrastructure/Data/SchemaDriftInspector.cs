using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Data;

/// <summary>
/// Detecta divergência entre o modelo EF e o schema real do PostgreSQL.
///
/// Motivo (incidente 2026-10-31): as tabelas legadas \`winget_packages\`/\`chocolatey_packages\`
/// foram removidas pela M055 mas continuavam mapeadas e consultadas — o primeiro
/// SELECT dentro de uma transação abortava o comando com 25P02 e o usuário via
/// HTTP 500 no endpoint de criação de policy. A divergência ficou invisível por
/// dias; este inspector a torna explícita no boot (log CRITICAL) e no /health.
/// </summary>
public static class SchemaDriftInspector
{
    /// <summary>Tabelas que o modelo EF espera no banco (ignora owned types e views).</summary>
    public static IReadOnlyList<string> ExpectedTables(DiscoveryDbContext db)
    {
        return db.Model.GetEntityTypes()
            .Where(entity => !entity.IsOwned())
            .Select(entity => entity.GetTableName())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Tabelas mapeadas no EF que NÃO existem no schema public. Vazio em
    /// provedores não-Postgres (testes InMemory/SQLite).
    /// </summary>
    public static async Task<IReadOnlyList<string>> FindMissingTablesAsync(
        DiscoveryDbContext db,
        CancellationToken cancellationToken = default)
    {
        if (!db.Database.IsNpgsql())
            return [];

        var expected = ExpectedTables(db);

        var existing = await db.Database
            .SqlQueryRaw<string>("SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname = 'public'")
            .ToListAsync(cancellationToken);

        var existingSet = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return expected.Where(table => !existingSet.Contains(table)).ToList();
    }
}
