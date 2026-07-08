using System.Text.Json;
using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Api.Controllers;

// ── Request/Response DTOs compartilhados entre AgentsController e AgentAuthController ──

public record CreateAgentRequest(Guid SiteId, string Hostname, string? DisplayName, string? OperatingSystem, string? OsVersion, string? AgentVersion);
public record UpdateAgentRequest(Guid SiteId, string Hostname, string? DisplayName);
public record SendCommandRequest(CommandType CommandType, string Payload);
public record StartRemoteDebugRequest(string? LogLevel = "info", int? TtlMinutes = 20, string? PreferredTransport = null);
public record RemoteDebugStartResponse(Guid SessionId, Guid CommandId, Guid AgentId, string LogLevel, DateTime StartedAtUtc, DateTime ExpiresAtUtc, string PreferredTransport, string NatsSubject);
public record HardwareReportRequest(string? Hostname, string? DisplayName, string? MeshCentralNodeId, AgentStatus? Status, string? OperatingSystem, string? OsVersion, string? AgentVersion, string? LastIpAddress, string? MacAddress, AgentHardwareInfo? Hardware, HardwareComponentsPayload? Components, JsonElement? InventoryRaw, string? InventorySchemaVersion, DateTime? InventoryCollectedAt, int? MachineScore);
public record HardwareComponentsPayload(List<DiskInfo>? Disks, List<NetworkAdapterInfo>? NetworkAdapters, List<MemoryModuleInfo>? MemoryModules, List<PrinterInfo>? Printers, List<ListeningPortInfo>? ListeningPorts, List<OpenSocketInfo>? OpenSockets);
public record CreateTokenRequest(string? Description);
public record ForceAutomationSyncRequest(bool Policies = true, bool Inventory = false, bool Software = false, bool AppStore = false);
public record RefreshAgentDataRequest(bool ListeningPorts = false, bool OpenConnections = false, bool Software = false, bool Printers = false, bool Hardware = false);
public record SoftwareInventoryReportRequest(DateTime? CollectedAt, List<SoftwareInventoryItemRequest>? Software);
public record SoftwareInventoryItemRequest(string Name, string? Version, string? Publisher, string? InstallId, string? Serial, string? Source, string? InstallDate, string? InstallSource);
public record UpsertAgentCustomFieldValueRequest(JsonElement Value);
public record SendFanoutCommandRequest(CommandType CommandType, string Payload, DateTime? ExpiresAtUtc = null, string? IdempotencyKey = null, Guid? CommandId = null);
public record FanoutDispatchResponse(Guid DispatchId, string Subject, string TargetScope, Guid? TargetClientId, Guid? TargetSiteId, DateTime IssuedAtUtc, DateTime? ExpiresAtUtc, string IdempotencyKey);
public record RestartRequest(int DelaySeconds = 15, bool Force = false, string? Message = null);
public record ShutdownRequest(int DelaySeconds = 30, bool Force = false, string? Message = null);
public record WakeOnLanRequest(string? BroadcastAddress = null);
public record TransferAgentRequest(Guid TargetSiteId, string? Reason);
public record BulkTransferAgentsRequest(IReadOnlyList<Guid> AgentIds, Guid TargetSiteId, string? Reason);
public record SetAgentMaintenanceRequest(bool Enabled, string? Reason);
public record SetAgentMaintenanceResponse(Guid AgentId, bool MaintenanceEnabled, string EffectiveStatus, DateTime ChangedAtUtc, string? Reason);