using Discovery.Api.Filters;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Automation.Commands;
using Discovery.Core.Enums.Identity;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Operações de automação em massa — cliente ou site INTEIRO.
///
/// Antes só existia o disparo por agente (AgentsController). Aqui o escopo vai na
/// ROTA (clients/{clientId} ou clients/{clientId}/sites/{siteId}) para que o
/// filtro <see cref="RequirePermissionAttribute"/> com <c>ScopeSource.FromRoute</c>
/// valide a permissão no nível correto: um usuário com papel por cliente só
/// opera os clientes dele, e um com papel por site só os sites dele.
///
/// O disparo é por agente (um comando + um report por máquina), então o
/// histórico de operações continua mostrando o resultado individual. Agentes
/// offline são contabilizados e não recebem comando (NATS core não retém
/// mensagens para agentes desconectados).
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/automation/operations")]
public class AutomationOperationsController(IMediator mediator) : ControllerBase
{
    /// <summary>Correlation id do cliente (header X-Correlation-Id) — identifica o lote no histórico.</summary>
    private string? RequestCorrelationId()
        => Request.Headers.TryGetValue("X-Correlation-Id", out var values)
            ? values.FirstOrDefault()
            : null;

    // ── Cliente inteiro ───────────────────────────────────────────────────

    [HttpPost("clients/{clientId:guid}/tasks/{taskId:guid}/run-now")]
    [RequirePermission(ResourceType.Automation, ActionType.Execute, ScopeSource.FromRoute)]
    public Task<IActionResult> RunTaskForClient(Guid clientId, Guid taskId, CancellationToken ct = default)
        => SendAsync(new RunAutomationTaskForScopeCommand(taskId, clientId, null, RequestCorrelationId()), ct);

    [HttpPost("clients/{clientId:guid}/scripts/{scriptId:guid}/run-now")]
    [RequirePermission(ResourceType.Automation, ActionType.Execute, ScopeSource.FromRoute)]
    public Task<IActionResult> RunScriptForClient(Guid clientId, Guid scriptId, CancellationToken ct = default)
        => SendAsync(new RunAutomationScriptForScopeCommand(scriptId, clientId, null, RequestCorrelationId()), ct);

    [HttpPost("clients/{clientId:guid}/force-sync")]
    [RequirePermission(ResourceType.Automation, ActionType.Execute, ScopeSource.FromRoute)]
    public Task<IActionResult> ForceSyncForClient(
        Guid clientId,
        [FromBody] ForceAutomationSyncScopeRequest? request = null,
        CancellationToken ct = default)
        => SendAsync(new ForceAutomationSyncForScopeCommand(
            clientId, null, request?.Policies, request?.Inventory, request?.Software, request?.AppStore, RequestCorrelationId()), ct);

    // ── Site inteiro ──────────────────────────────────────────────────────

    [HttpPost("clients/{clientId:guid}/sites/{siteId:guid}/tasks/{taskId:guid}/run-now")]
    [RequirePermission(ResourceType.Automation, ActionType.Execute, ScopeSource.FromRoute)]
    public Task<IActionResult> RunTaskForSite(Guid clientId, Guid siteId, Guid taskId, CancellationToken ct = default)
        => SendAsync(new RunAutomationTaskForScopeCommand(taskId, clientId, siteId, RequestCorrelationId()), ct);

    [HttpPost("clients/{clientId:guid}/sites/{siteId:guid}/scripts/{scriptId:guid}/run-now")]
    [RequirePermission(ResourceType.Automation, ActionType.Execute, ScopeSource.FromRoute)]
    public Task<IActionResult> RunScriptForSite(Guid clientId, Guid siteId, Guid scriptId, CancellationToken ct = default)
        => SendAsync(new RunAutomationScriptForScopeCommand(scriptId, clientId, siteId, RequestCorrelationId()), ct);

    [HttpPost("clients/{clientId:guid}/sites/{siteId:guid}/force-sync")]
    [RequirePermission(ResourceType.Automation, ActionType.Execute, ScopeSource.FromRoute)]
    public Task<IActionResult> ForceSyncForSite(
        Guid clientId,
        Guid siteId,
        [FromBody] ForceAutomationSyncScopeRequest? request = null,
        CancellationToken ct = default)
        => SendAsync(new ForceAutomationSyncForScopeCommand(
            clientId, siteId, request?.Policies, request?.Inventory, request?.Software, request?.AppStore, RequestCorrelationId()), ct);

    private async Task<IActionResult> SendAsync(IRequest<Result<AutomationScopeDispatchResultDto>> command, CancellationToken ct)
    {
        var result = await mediator.Send(command, ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code switch
            {
                "NotFound" => NotFound(new { error = errors[0].Message }),
                "Conflict" => Conflict(new { error = errors[0].Message }),
                _ => BadRequest(new { error = errors[0].Message })
            });
    }
}
