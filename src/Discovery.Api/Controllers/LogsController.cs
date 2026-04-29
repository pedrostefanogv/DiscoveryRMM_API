using System.Text.Json;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using CoreLogLevel = Discovery.Core.Enums.LogLevel;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
public class LogsController : ControllerBase
{
    private readonly ILogRepository _logRepo;
    private readonly IAgentRepository _agentRepo;
    private readonly ISiteRepository _siteRepo;

    public LogsController(ILogRepository logRepo, IAgentRepository agentRepo, ISiteRepository siteRepo)
    {
        _logRepo = logRepo;
        _agentRepo = agentRepo;
        _siteRepo = siteRepo;
    }

    [HttpGet]
    public async Task<IActionResult> Query(
        [FromQuery] Guid? clientId,
        [FromQuery] Guid? siteId,
        [FromQuery] Guid? agentId,
        [FromQuery] LogType? type,
        [FromQuery] CoreLogLevel? level,
        [FromQuery] LogSource? source,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0)
    {
        var query = new LogQuery
        {
            ClientId = clientId,
            SiteId = siteId,
            AgentId = agentId,
            Type = type,
            Level = level,
            Source = source,
            From = from,
            To = to,
            Limit = limit,
            Offset = offset
        };

        var logs = await _logRepo.QueryAsync(query);
        return Ok(logs);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateLogRequest request)
    {
        if (request.AgentId is null && request.SiteId is null && request.ClientId is null)
            return BadRequest("clientId, siteId or agentId is required.");

        Guid? clientId = request.ClientId;
        Guid? siteId = request.SiteId;
        Guid? agentId = request.AgentId;

        if (agentId.HasValue)
        {
            var agent = await _agentRepo.GetByIdAsync(agentId.Value);
            if (agent is null) return NotFound("Agent not found.");

            siteId = agent.SiteId;
            var site = await _siteRepo.GetByIdAsync(agent.SiteId);
            if (site is null) return NotFound("Site not found for agent.");
            clientId = site.ClientId;
        }
        else if (siteId.HasValue && !clientId.HasValue)
        {
            var site = await _siteRepo.GetByIdAsync(siteId.Value);
            if (site is null) return NotFound("Site not found.");
            clientId = site.ClientId;
        }

        string? dataJson = null;
        if (request.DataJson.HasValue && request.DataJson.Value.ValueKind != JsonValueKind.Null)
            dataJson = request.DataJson.Value.GetRawText();

        var entry = new LogEntry
        {
            ClientId = clientId,
            SiteId = siteId,
            AgentId = agentId,
            Type = request.Type,
            Level = request.Level,
            Source = request.Source,
            Message = request.Message,
            DataJson = dataJson
        };

        var created = await _logRepo.CreateAsync(entry);
        return Ok(created);
    }
}

public record CreateLogRequest(
    Guid? ClientId,
    Guid? SiteId,
    Guid? AgentId,
    LogType Type,
    CoreLogLevel Level,
    LogSource Source,
    string Message,
    JsonElement? DataJson);
