using System.Text.RegularExpressions;
using Discovery.Api.Filters;
using Discovery.Core.DTOs;
using Discovery.Core.Exceptions;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Governança das MCP tools (servidor + agente) por escopo.
///
/// Herança: ausência de política no escopo = herdar do nível acima.
/// `locked` no nível superior impede sobrescrita nos níveis mais específicos —
/// mesma semântica dos "campos bloqueados para herança" das configurações.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/mcp-tools")]
public class McpToolsController(IMcpToolGovernance governance) : ControllerBase
{
    private static readonly Regex ToolNamePattern =
        new("^[a-zA-Z0-9_.-]{1,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static bool IsValidToolName(string? name) =>
        !string.IsNullOrEmpty(name) && ToolNamePattern.IsMatch(name) && !name.Contains("..", StringComparison.Ordinal);

    [HttpGet]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View, ScopeSource.Global)]
    public async Task<IActionResult> GetCatalog(
        [FromQuery] Guid? clientId, [FromQuery] Guid? siteId, [FromQuery] Guid? agentId,
        CancellationToken ct)
    {
        var catalog = await governance.GetCatalogAsync(NormalizeScope(clientId, siteId, agentId), ct);
        return Ok(catalog);
    }

    [HttpPut("{toolName}")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit, ScopeSource.Global)]
    public async Task<IActionResult> Save(
        string toolName, [FromBody] SaveMcpToolPolicyRequest request, CancellationToken ct)
    {
        if (!IsValidToolName(toolName))
            return BadRequest(new { errors = new[] { new { Code = "InvalidToolName", Message = "Nome de tool inválido." } } });

        var scope = NormalizeScope(request.ClientId, request.SiteId, request.AgentId);
        var normalized = request with
        {
            ClientId = scope.ClientId,
            SiteId = scope.SiteId,
            AgentId = scope.AgentId,
            MaxCallsPerMinute = request.MaxCallsPerMinute is > 0 ? Math.Min(request.MaxCallsPerMinute.Value, 600) : null,
            TimeoutSeconds = request.TimeoutSeconds is > 0 ? Math.Min(request.TimeoutSeconds.Value, 3600) : null,
        };

        try
        {
            var catalog = await governance.SavePolicyAsync(toolName!, normalized, ct);
            return Ok(catalog);
        }
        catch (McpToolPolicyLockedException ex)
        {
            // Propriedades minúsculas: o cliente do site lê { errors: [{ message }] }.
            return Conflict(new
            {
                errors = new[] { new { code = "PolicyLocked", message = ex.Message } },
            });
        }
    }

    [HttpDelete("{toolName}")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit, ScopeSource.Global)]
    public async Task<IActionResult> Reset(
        string toolName, [FromQuery] Guid? clientId, [FromQuery] Guid? siteId, [FromQuery] Guid? agentId,
        CancellationToken ct)
    {
        if (!IsValidToolName(toolName))
            return BadRequest(new { errors = new[] { new { Code = "InvalidToolName", Message = "Nome de ferramenta inválido." } } });

        var removed = await governance.ResetPolicyAsync(toolName, NormalizeScope(clientId, siteId, agentId), ct);
        if (!removed)
            return NotFound(new { errors = new[] { new { Code = "NotOverridden", Message = "Não há sobrescrita neste escopo." } } });

        var catalog = await governance.GetCatalogAsync(NormalizeScope(clientId, siteId, agentId), ct);
        return Ok(catalog);
    }

    [HttpGet("{toolName}/impact")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View, ScopeSource.Global)]
    public async Task<IActionResult> Impact(
        string toolName, [FromQuery] Guid? clientId, [FromQuery] Guid? siteId, [FromQuery] Guid? agentId,
        CancellationToken ct)
    {
        if (!IsValidToolName(toolName))
            return BadRequest(new { errors = new[] { new { Code = "InvalidToolName", Message = "Nome de ferramenta inválido." } } });

        var count = await governance.GetImpactAsync(toolName, NormalizeScope(clientId, siteId, agentId), ct);
        return Ok(new McpToolPolicyImpact(count));
    }

    [HttpGet("audit")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View, ScopeSource.Global)]
    public async Task<IActionResult> Audit(
        [FromServices] Discovery.Core.Interfaces.IMcpToolAuditService auditService,
        [FromQuery] Guid? clientId = null,
        [FromQuery] Guid? agentId = null,
        [FromQuery] string? toolName = null,
        [FromQuery] string? status = null,
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var result = await auditService.QueryAsync(clientId, agentId, toolName, status, fromUtc, toUtc, page, pageSize, ct);
        return Ok(result);
    }

    /// <summary>
    /// Normaliza para a convenção da tabela: no máximo um nível preenchido
    /// (agent &gt; site &gt; client &gt; global).
    /// </summary>
    private static McpToolScope NormalizeScope(Guid? clientId, Guid? siteId, Guid? agentId)
    {
        if (agentId.HasValue) return new McpToolScope(null, null, agentId);
        if (siteId.HasValue) return new McpToolScope(null, siteId, null);
        if (clientId.HasValue) return new McpToolScope(clientId, null, null);
        return new McpToolScope(null, null, null);
    }
}