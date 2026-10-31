using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Discovery.Api.DependencyInjection;

/// <summary>
/// Health check de schema: reporta Unhealthy quando alguma tabela mapeada no EF
/// não existe no banco. Não derruba a API (decisão de 2026-10-31) — apenas deixa
/// a divergência visível no /health e no log, em vez de aparecer como HTTP 500
/// em um endpoint específico (incidente winget_packages).
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
            if (missing.Count == 0)
                return HealthCheckResult.Healthy("Schema em sincronia com o modelo EF");

            var detail = string.Join(", ", missing);
            _logger.LogCritical(
                "Schema divergente: {Count} tabela(s) mapeada(s) no EF ausente(s) no banco: {Tables}",
                missing.Count,
                detail);

            var data = new Dictionary<string, object> { ["missingTables"] = missing };
            return HealthCheckResult.Unhealthy(
                $"Schema divergente: {missing.Count} tabela(s) ausente(s): {detail}", data: data);
        }
        catch (Exception ex)
        {
            // Falha ao inspecionar não deve mascarar o health do banco.
            _logger.LogWarning(ex, "Falha ao verificar divergência de schema");
            return HealthCheckResult.Degraded("Não foi possível verificar o schema", ex);
        }
    }
}
