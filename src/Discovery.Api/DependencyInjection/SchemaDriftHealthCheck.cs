using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Discovery.Api.DependencyInjection;

/// <summary>
/// Health check de schema: reporta Unhealthy quando alguma tabela mapeada no EF
/// não existe no banco ou existe mas o role da API NÃO tem privilégio de leitura —
/// as duas causas do incidente de 2026-10-31 (42P01 e 42501 dentro da transação do
/// comando → 25P02 → 500).
///
/// Não derruba a API (decisão de 2026-10-31) — apenas deixa a divergência visível
/// no /health e no log, em vez de aparecer como HTTP 500 em um endpoint específico.
/// </summary>
internal sealed class SchemaDriftHealthCheck : IHealthCheck
{
    private readonly DiscoveryDbContext _db;
    private readonly ILogger<SchemaDriftHealthCheck> _logger;

    public SchemaDriftHealthCheck(DiscoveryDbContext db, ILogger<SchemaDriftHealthCheck> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var missing = await SchemaDriftInspector.FindMissingTablesAsync(_db, cancellationToken);
            var unreadable = await SchemaDriftInspector.FindUnreadableTablesAsync(_db, cancellationToken);

            if (missing.Count == 0 && unreadable.Count == 0)
                return HealthCheckResult.Healthy("Schema em sincronia com o modelo EF");

            var data = new Dictionary<string, object>();
            var problems = new List<string>();

            if (missing.Count > 0)
            {
                problems.Add($"{missing.Count} tabela(s) ausente(s): {string.Join(", ", missing)}");
                data["missingTables"] = missing;
                _logger.LogCritical(
                    "Schema divergente: {Count} tabela(s) mapeada(s) no EF ausente(s) no banco: {Tables}",
                    missing.Count,
                    string.Join(", ", missing));
            }

            if (unreadable.Count > 0)
            {
                problems.Add($"{unreadable.Count} tabela(s) sem privilégio de leitura para o role da API: {string.Join(", ", unreadable)}");
                data["unreadableTables"] = unreadable;
                _logger.LogCritical(
                    "Schema divergente: {Count} tabela(s) mapeada(s) no EF sem privilégio SELECT para o role atual: {Tables}. " +
                    "Qualquer consulta a elas aborta a transação do comando (42501 → 25P02 → HTTP 500).",
                    unreadable.Count,
                    string.Join(", ", unreadable));
            }

            return HealthCheckResult.Unhealthy(
                $"Schema divergente — {string.Join("; ", problems)}", data: data);
        }
        catch (Exception ex)
        {
            // Falha ao inspecionar não deve mascarar o health do banco.
            _logger.LogWarning(ex, "Falha ao verificar divergência de schema");
            return HealthCheckResult.Degraded("Não foi possível verificar o schema", ex);
        }
    }
}
