using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Discovery.Api.DependencyInjection;

/// <summary>
/// Health check de runtime obsoleto: detecta quando o runtime .NET foi atualizado
/// sob o processo em execução (ex.: apt upgrade removeu o diretório do framework
/// antigo). No Linux, /proc/self/maps lista as DLLs mapeadas com o sufixo
/// "(deleted)" quando o arquivo original foi removido — qualquer assembly carregada
/// sob demanda depois disso falha com FileNotFoundException.
///
/// Incidente de 2026-11-02: dotnet 10.0.13 → 10.0.14 com o Discovery.Api rodando;
/// a Fido2NetLib não conseguia carregar System.Runtime.Serialization.Primitives e
/// o login por MFA/FIDO2 quebrava com HTTP 400. Reiniciar o serviço resolve — este
/// check torna a condição visível no /health (e no log) antes do próximo incidente.
///
/// No-op em Windows (não existe /proc/self/maps): reporta Healthy.
/// </summary>
internal sealed class RuntimeStaleHealthCheck : IHealthCheck
{
    private readonly ILogger<RuntimeStaleHealthCheck> _logger;

    public RuntimeStaleHealthCheck(ILogger<RuntimeStaleHealthCheck> logger) => _logger = logger;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        const string mapsPath = "/proc/self/maps";
        if (!OperatingSystem.IsLinux() || !File.Exists(mapsPath))
            return Task.FromResult(HealthCheckResult.Healthy("Runtime check não aplicável a esta plataforma"));

        string maps;
        try
        {
            maps = File.ReadAllText(mapsPath);
        }
        catch (Exception ex)
        {
            // Falha ao inspecionar não deve derrubar o /health.
            _logger.LogWarning(ex, "Falha ao ler {MapsPath} para verificar runtime obsoleto", mapsPath);
            return Task.FromResult(HealthCheckResult.Healthy("Não foi possível inspecionar o runtime"));
        }

        var deleted = RuntimeStaleInspector.FindDeletedFrameworkLibraries(maps);
        if (deleted.Count == 0)
            return Task.FromResult(HealthCheckResult.Healthy("Runtime .NET consistente com o processo"));

        var files = deleted.Take(10).ToList();
        _logger.LogCritical(
            "Runtime .NET atualizado sob o processo em execução: {Count} DLL(s) do framework mapeada(s) como deleted. " +
            "Reinicie o serviço discovery-api. Exemplos: {Files}",
            deleted.Count,
            string.Join(", ", files));

        var data = new Dictionary<string, object> { ["deletedLibraries"] = files };
        return Task.FromResult(HealthCheckResult.Unhealthy(
            "Runtime .NET foi atualizado sob o processo (DLLs do framework marcadas como deleted). Reinicie o serviço discovery-api.",
            data: data));
    }
}

/// <summary>
/// Parser puro de /proc/self/maps, separado do health check para permitir testes
/// unitários sem arquivo real.
/// </summary>
internal static class RuntimeStaleInspector
{
    /// <summary>
    /// Retorna os caminhos das DLLs do framework (.NETCore.App / AspNetCore.App)
    /// mapeadas como "(deleted)" no conteúdo de /proc/self/maps.
    /// </summary>
    public static IReadOnlyList<string> FindDeletedFrameworkLibraries(string mapsContent)
    {
        var result = new List<string>();
        foreach (var line in mapsContent.Split('\n'))
        {
            // Formato: address perms offset dev inode pathname
            var trimmed = line.TrimEnd('\r');
            if (!trimmed.EndsWith("(deleted)", StringComparison.Ordinal))
                continue;

            // O caminho é o penúltimo campo da linha (o último token é "(deleted)").
            var fields = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 6)
                continue;
            var path = fields[^2];

            if (path.Contains("/Microsoft.NETCore.App/", StringComparison.Ordinal) ||
                path.Contains("/Microsoft.AspNetCore.App/", StringComparison.Ordinal))
            {
                if (!result.Contains(path))
                    result.Add(path);
            }
        }

        return result;
    }
}
