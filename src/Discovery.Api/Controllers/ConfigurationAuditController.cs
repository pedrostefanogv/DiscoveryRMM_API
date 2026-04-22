using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/configuration-audit")]
public class ConfigurationAuditController : ControllerBase
{
    private readonly IConfigurationAuditService _service;

    public ConfigurationAuditController(IConfigurationAuditService service) => _service = service;

    /// <summary>
    /// Retorna mudanças recentes em configurações.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetRecent([FromQuery] int days = 30, [FromQuery] int limit = 200)
    {
        var items = await _service.GetRecentChangesAsync(days, limit);
        return Ok(items);
    }

    /// <summary>
    /// Retorna histórico de mudanças de uma entidade específica.
    /// </summary>
    [HttpGet("{entityType}/{entityId:guid}")]
    public async Task<IActionResult> GetEntityHistory(string entityType, Guid entityId, [FromQuery] int limit = 100)
    {
        var items = await _service.GetEntityHistoryAsync(entityType, entityId, limit);
        return Ok(items);
    }

    /// <summary>
    /// Retorna histórico de mudanças de um campo específico de uma entidade.
    /// </summary>
    [HttpGet("{entityType}/{entityId:guid}/field/{fieldName}")]
    public async Task<IActionResult> GetFieldHistory(string entityType, Guid entityId, string fieldName)
    {
        var items = await _service.GetFieldHistoryAsync(entityType, entityId, fieldName);
        return Ok(items);
    }

    /// <summary>
    /// Retorna histórico de mudanças feitas por um usuário.
    /// </summary>
    [HttpGet("by-user/{username}")]
    public async Task<IActionResult> GetByUser(string username, [FromQuery] int limit = 100)
    {
        var items = await _service.GetChangesByUserAsync(username, limit);
        return Ok(items);
    }

    /// <summary>
    /// Relatório de auditoria em período.
    /// </summary>
    [HttpGet("report")]
    public async Task<IActionResult> GetReport([FromQuery] DateTime startDate, [FromQuery] DateTime endDate)
    {
        var items = await _service.GetAuditReportAsync(startDate, endDate);
        return Ok(items);
    }
}
