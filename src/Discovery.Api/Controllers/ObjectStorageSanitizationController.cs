using System.Threading;
using System.Threading.Tasks;
using Discovery.Api.Filters;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Manutenção/sanitização do object storage: mede o que está publicado no bucket
/// (total de objetos e bytes) e remove os assets da loja que ficaram sem vínculo
/// com nenhum pacote. Nada fora de app-store/custom/ é apagado.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/admin/object-storage-sanitization")]
public class ObjectStorageSanitizationController(IObjectStorageSanitizationService sanitizationService) : ControllerBase
{
    /// <summary>Lista as verificações disponíveis (chave, título e descrição).</summary>
    [HttpGet("checks")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public IActionResult GetChecks() => Ok(ObjectStorageSanitizationChecks.All);

    /// <summary>
    /// Varre o bucket. <c>dryRun=true</c> (padrão) apenas informa o que existe e o
    /// que seria removido; <c>minAgeHours</c> define a idade mínima do órfão.
    /// </summary>
    [HttpPost("run")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Execute)]
    public async Task<IActionResult> Run(
        [FromBody] ObjectStorageSanitizationRequest? request,
        CancellationToken ct)
    {
        var req = request ?? new ObjectStorageSanitizationRequest(DryRun: null, Checks: null, MinAgeHours: null);
        // Segurança: ausente/ambíguo = dry-run. Apagar exige a flag explícita.
        var dryRun = req.DryRun ?? true;

        try
        {
            var report = await sanitizationService.RunAsync(dryRun, req.Checks, req.MinAgeHours, ct);
            return Ok(report);
        }
        catch (ObjectStorageSanitizationBusyException ex)
        {
            // Outra execução em andamento: conflito, não erro do cliente.
            return Conflict(new { error = ex.Message });
        }
    }
}
