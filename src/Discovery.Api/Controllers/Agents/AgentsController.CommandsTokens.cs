using System.Text.Json;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Agent command and token management endpoints.
/// </summary>
public partial class AgentsController
{
    [HttpGet("{id:guid}/commands")]
    public async Task<IActionResult> GetCommands(Guid id, [FromQuery] int limit = 50)
        => Ok(await _commandRepo.GetByAgentIdAsync(id, limit));

    [HttpPost("{id:guid}/commands")]
    public async Task<IActionResult> SendCommand(Guid id, [FromBody] SendCommandRequest request)
    {
        var agent = await _agentRepo.GetByIdAsync(id);
        if (agent is null) return NotFound();
        var command = new AgentCommand { AgentId = id, CommandType = request.CommandType, Payload = request.Payload };
        var created = await _commandDispatcher.DispatchAsync(command);
        return CreatedAtAction(nameof(GetCommands), new { id }, created);
    }

    [HttpGet("{id:guid}/tokens")]
    public async Task<IActionResult> GetTokens(Guid id) => Ok(await _authService.GetTokensByAgentIdAsync(id));

    [HttpPost("{id:guid}/tokens")]
    public async Task<IActionResult> CreateToken(Guid id, [FromBody] CreateTokenRequest request)
    {
        var agent = await _agentRepo.GetByIdAsync(id);
        if (agent is null) return NotFound();
        var (token, rawToken) = await _authService.CreateTokenAsync(id, request.Description);
        return Ok(new { Token = rawToken, Id = token.Id, ExpiresAt = token.ExpiresAt });
    }

    [HttpDelete("{id:guid}/tokens/{tokenId:guid}")]
    public async Task<IActionResult> RevokeToken(Guid id, Guid tokenId) { await _authService.RevokeTokenAsync(tokenId); return NoContent(); }

    [HttpDelete("{id:guid}/tokens")]
    public async Task<IActionResult> RevokeAllTokens(Guid id) { await _authService.RevokeAllTokensAsync(id); return NoContent(); }
}

// ── Request/Response DTOs (kept close to the controller that consumes them) ──

public record CreateAgentRequest(Guid SiteId, string Hostname, string? DisplayName, string? OperatingSystem, string? OsVersion, string? AgentVersion);
public record UpdateAgentRequest(Guid SiteId, string Hostname, string? DisplayName);
public record SendCommandRequest(CommandType CommandType, string Payload);
public record StartRemoteDebugRequest(string? LogLevel = "info", int? TtlMinutes = 20);
public record RemoteDebugStartResponse(Guid SessionId, Guid CommandId, Guid AgentId, string LogLevel, DateTime StartedAtUtc, DateTime ExpiresAtUtc, string PreferredTransport, string FallbackTransport, string NatsSubject, string SignalRMethod);
public record HardwareReportRequest(string? Hostname, string? DisplayName, string? MeshCentralNodeId, AgentStatus? Status, string? OperatingSystem, string? OsVersion, string? AgentVersion, string? LastIpAddress, string? MacAddress, AgentHardwareInfo? Hardware, HardwareComponentsPayload? Components, JsonElement? InventoryRaw, string? InventorySchemaVersion, DateTime? InventoryCollectedAt);
public record HardwareComponentsPayload(List<DiskInfo>? Disks, List<NetworkAdapterInfo>? NetworkAdapters, List<MemoryModuleInfo>? MemoryModules, List<PrinterInfo>? Printers, List<ListeningPortInfo>? ListeningPorts, List<OpenSocketInfo>? OpenSockets);
public record CreateTokenRequest(string? Description);
public record ForceAutomationSyncRequest(bool Policies = true, bool Inventory = false, bool Software = false, bool AppStore = false);
public record SoftwareInventoryReportRequest(DateTime? CollectedAt, List<SoftwareInventoryItemRequest>? Software);
public record SoftwareInventoryItemRequest(string Name, string? Version, string? Publisher, string? InstallId, string? Serial, string? Source);
public record UpsertAgentCustomFieldValueRequest(JsonElement Value);
