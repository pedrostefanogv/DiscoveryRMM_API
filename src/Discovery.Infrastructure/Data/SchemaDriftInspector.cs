using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Data;

/// <summary>
/// Detecta divergência entre o modelo EF e o schema real do PostgreSQL.
///
/// Motivo (incidente 2026-10-31): as tabelas legadas `winget_packages`/`chocolatey_packages`
/// foram removidas pela M055 mas continuavam mapeadas e consultadas — o primeiro
/// SELECT dentro de uma transação abortava o comando com 25P02 e o usuário via
/// HTTP 500 no endpoint de criação de policy. A divergência ficou invisível por
/// dias; este inspector a torna explícita no boot (log CRITICAL) e no /health.
/// (O mapeamento legado foi removido de vez na limpeza pós-incidente.)
///
/// Segunda lição do mesmo incidente: "tabela existe" não garante "tabela legível".
/// Quando as tabelas legadas foram recriadas por rollback sem GRANT para o role da
/// API, o guard de existência passou e o SELECT falhou com 42501 — o mesmo 500. Por
/// isso o inspector também reporta tabelas mapeadas que o role atual NÃO pode ler.
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
    /// Tabelas mapeadas no EF que NÃO existem no schema public. Vazio em provedores
    /// não-Postgres (testes InMemory/SQLite).
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
        return expected
            .Where(table => !existingSet.Contains(table))
            .ToList();
    }

    /// <summary>
    /// Tabelas mapeadas no EF que existem mas o role atual NÃO consegue ler (sem
    /// privilégio SELECT). Este é o caso que gerou 42501 → 25P02 → HTTP 500 no
    /// incidente de 2026-10-31. Vazio em provedores não-Postgres.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FindUnreadableTablesAsync(
        DiscoveryDbContext db,
        CancellationToken cancellationToken = default)
    {
        if (!db.Database.IsNpgsql())
            return [];

        var expected = ExpectedTables(db).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unreadable = await db.Database
            .SqlQueryRaw<string>(@"
                SELECT c.relname AS ""Value""
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public'
                  AND c.relkind IN ('r', 'v', 'm', 'p', 'f')
                  AND NOT has_table_privilege(c.oid, 'SELECT')
                ORDER BY c.relname")
            .ToListAsync(cancellationToken);

        return unreadable.Where(table => expected.Contains(table)).ToList();
    }
}
