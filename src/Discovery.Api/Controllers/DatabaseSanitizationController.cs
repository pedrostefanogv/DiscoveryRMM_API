using System.Threading;
using System.Threading.Tasks;
using Discovery.Api.Filters;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Manutenção/sanitização do banco de dados: detecta e corrige inconsistências
/// de dados que impedem a operação (ex.: chamado sem estado de workflow).
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/admin/database-sanitization")]
public class DatabaseSanitizationController(IDatabaseSanitizationService sanitizationService) : ControllerBase
{
    /// <summary>Lista as verificações disponíveis (chave, título e descrição).</summary>
    [HttpGet("checks")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public IActionResult GetChecks() => Ok(DatabaseSanitizationChecks.All);

    /// <summary>
    /// Executa a sanitização. <c>dryRun=true</c> apenas pré-visualiza (nada é
    /// persistido); <c>checks</c> nulo/vazio roda todas as verificações.
    /// </summary>
    [HttpPost("run")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Execute)]
    public async Task<IActionResult> Run([FromBody] SanitizationRequest? request, CancellationToken ct)
    {
        var req = request ?? new SanitizationRequest(DryRun: null, Checks: null);
        // Segurança: ausente/ambíguo = dry-run. Aplicar exige a flag explícita.
        var dryRun = req.DryRun ?? true;
        var report = await sanitizationService.RunAsync(dryRun, req.Checks, ct);
        return Ok(report);
    }
}
