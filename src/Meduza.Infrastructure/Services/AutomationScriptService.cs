using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meduza.Core.DTOs;
using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Interfaces;

namespace Meduza.Infrastructure.Services;

public class AutomationScriptService : IAutomationScriptService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IAutomationScriptRepository _scriptRepository;
    private readonly IAutomationScriptAuditRepository _auditRepository;
    private readonly ILoggingService _loggingService;

    public AutomationScriptService(
        IAutomationScriptRepository scriptRepository,
        IAutomationScriptAuditRepository auditRepository,
        ILoggingService loggingService)
    {
        _scriptRepository = scriptRepository;
        _auditRepository = auditRepository;
        _loggingService = loggingService;
    }

    public async Task<AutomationScriptPageDto> GetListAsync(
        Guid? clientId,
        bool activeOnly,
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var safeLimit = Math.Clamp(limit, 1, 200);
        var safeOffset = Math.Max(0, offset);

        var items = await _scriptRepository.GetListAsync(clientId, activeOnly, safeLimit, safeOffset);
        var total = await _scriptRepository.CountAsync(clientId, activeOnly);

        return new AutomationScriptPageDto
        {
            Items = items.Select(ToSummaryDto).ToList(),
            Count = items.Count,
            Total = total,
            Limit = safeLimit,
            Offset = safeOffset
        };
    }

    public async Task<AutomationScriptDetailDto?> GetByIdAsync(
        Guid id,
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var script = await _scriptRepository.GetByIdAsync(id, includeInactive);
        return script is null ? null : ToDetailDto(script);
    }

    public async Task<AutomationScriptDetailDto> CreateAsync(
        CreateAutomationScriptRequest request,
        string? changedBy,
        string? ipAddress,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        ValidateRequest(request.Name, request.Summary, request.Content, request.TriggerModes);

        var script = new AutomationScriptDefinition
        {
            ClientId = request.ClientId,
            Name = request.Name.Trim(),
            Summary = request.Summary.Trim(),
            ScriptType = request.ScriptType,
            Version = string.IsNullOrWhiteSpace(request.Version) ? "1.0.0" : request.Version.Trim(),
            ExecutionFrequency = string.IsNullOrWhiteSpace(request.ExecutionFrequency) ? "manual" : request.ExecutionFrequency.Trim(),
            TriggerModesJson = SerializeTriggerModes(request.TriggerModes),
            Content = request.Content,
            ParametersSchemaJson = request.ParametersSchemaJson,
            MetadataJson = request.MetadataJson,
            IsActive = request.IsActive
        };

        var created = await _scriptRepository.CreateAsync(script);
        var newSnapshot = BuildAuditSnapshot(created);

        await _auditRepository.CreateAsync(new AutomationScriptAudit
        {
            ScriptId = created.Id,
            ChangeType = AutomationScriptChangeType.Created,
            NewValueJson = JsonSerializer.Serialize(newSnapshot, JsonOptions),
            ChangedBy = changedBy,
            IpAddress = ipAddress,
            CorrelationId = correlationId,
            Reason = "created"
        });

        await _loggingService.LogInfoAsync(
            LogType.Automation,
            LogSource.Api,
            "Automation script created.",
            new
            {
                correlationId,
                scriptId = created.Id,
                scriptName = created.Name,
                scriptType = created.ScriptType.ToString(),
                version = created.Version,
                triggerModes = ParseTriggerModes(created.TriggerModesJson),
                executionFrequency = created.ExecutionFrequency
            },
            clientId: created.ClientId?.ToString());

        return ToDetailDto(created);
    }

    public async Task<AutomationScriptDetailDto?> UpdateAsync(
        Guid id,
        UpdateAutomationScriptRequest request,
        string? changedBy,
        string? ipAddress,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        ValidateRequest(request.Name, request.Summary, request.Content, request.TriggerModes);

        var existing = await _scriptRepository.GetByIdAsync(id, includeInactive: true);
        if (existing is null)
            return null;

        var oldSnapshot = BuildAuditSnapshot(existing);
        var oldIsActive = existing.IsActive;

        existing.Name = request.Name.Trim();
        existing.Summary = request.Summary.Trim();
        existing.ScriptType = request.ScriptType;
        existing.Version = string.IsNullOrWhiteSpace(request.Version) ? existing.Version : request.Version.Trim();
        existing.ExecutionFrequency = string.IsNullOrWhiteSpace(request.ExecutionFrequency) ? "manual" : request.ExecutionFrequency.Trim();
        existing.TriggerModesJson = SerializeTriggerModes(request.TriggerModes);
        existing.Content = request.Content;
        existing.ParametersSchemaJson = request.ParametersSchemaJson;
        existing.MetadataJson = request.MetadataJson;
        existing.IsActive = request.IsActive;

        await _scriptRepository.UpdateAsync(existing);

        var updated = await _scriptRepository.GetByIdAsync(id, includeInactive: true) ?? existing;
        var newSnapshot = BuildAuditSnapshot(updated);

        await _auditRepository.CreateAsync(new AutomationScriptAudit
        {
            ScriptId = updated.Id,
            ChangeType = oldIsActive != updated.IsActive
                ? (updated.IsActive ? AutomationScriptChangeType.Activated : AutomationScriptChangeType.Deactivated)
                : AutomationScriptChangeType.Updated,
            OldValueJson = JsonSerializer.Serialize(oldSnapshot, JsonOptions),
            NewValueJson = JsonSerializer.Serialize(newSnapshot, JsonOptions),
            ChangedBy = changedBy,
            IpAddress = ipAddress,
            CorrelationId = correlationId,
            Reason = request.Reason
        });

        await _loggingService.LogInfoAsync(
            LogType.Automation,
            LogSource.Api,
            "Automation script updated.",
            new
            {
                correlationId,
                scriptId = updated.Id,
                scriptName = updated.Name,
                scriptType = updated.ScriptType.ToString(),
                version = updated.Version,
                active = updated.IsActive,
                executionFrequency = updated.ExecutionFrequency
            },
            clientId: updated.ClientId?.ToString());

        return ToDetailDto(updated);
    }

    public async Task<bool> DeleteAsync(
        Guid id,
        string? changedBy,
        string? ipAddress,
        string correlationId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var existing = await _scriptRepository.GetByIdAsync(id, includeInactive: true);
        if (existing is null)
            return false;

        var oldSnapshot = BuildAuditSnapshot(existing);
        await _scriptRepository.DeleteAsync(id);

        await _auditRepository.CreateAsync(new AutomationScriptAudit
        {
            ScriptId = id,
            ChangeType = AutomationScriptChangeType.Deleted,
            OldValueJson = JsonSerializer.Serialize(oldSnapshot, JsonOptions),
            ChangedBy = changedBy,
            IpAddress = ipAddress,
            CorrelationId = correlationId,
            Reason = reason
        });

        await _loggingService.LogWarningAsync(
            LogType.Automation,
            LogSource.Api,
            "Automation script deleted.",
            new { correlationId, scriptId = id, scriptName = existing.Name, reason },
            clientId: existing.ClientId?.ToString());

        return true;
    }

    public async Task<IReadOnlyList<AutomationScriptAuditDto>> GetAuditAsync(
        Guid id,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var entries = await _auditRepository.GetByScriptIdAsync(id, limit);
        return entries
            .Select(entry => new AutomationScriptAuditDto
            {
                Id = entry.Id,
                ScriptId = entry.ScriptId,
                ChangeType = entry.ChangeType.ToString(),
                Reason = entry.Reason,
                OldValueJson = entry.OldValueJson,
                NewValueJson = entry.NewValueJson,
                ChangedBy = entry.ChangedBy,
                IpAddress = entry.IpAddress,
                CorrelationId = entry.CorrelationId,
                ChangedAt = entry.ChangedAt
            })
            .ToList();
    }

    public async Task<AutomationScriptConsumeDto?> GetConsumePayloadAsync(
        Guid id,
        string? changedBy,
        string? ipAddress,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var script = await _scriptRepository.GetByIdAsync(id, includeInactive: false);
        if (script is null)
            return null;

        var payload = new AutomationScriptConsumeDto
        {
            ScriptId = script.Id,
            Name = script.Name,
            Version = script.Version,
            ScriptType = script.ScriptType,
            ExecutionFrequency = script.ExecutionFrequency,
            TriggerModes = ParseTriggerModes(script.TriggerModesJson),
            Summary = script.Summary,
            LastUpdatedAt = script.LastUpdatedAt,
            Content = script.Content,
            ContentHashSha256 = ComputeHash(script.Content),
            ParametersSchemaJson = script.ParametersSchemaJson,
            MetadataJson = script.MetadataJson
        };

        await _auditRepository.CreateAsync(new AutomationScriptAudit
        {
            ScriptId = script.Id,
            ChangeType = AutomationScriptChangeType.Consumed,
            NewValueJson = JsonSerializer.Serialize(new
            {
                script.Id,
                script.Name,
                script.Version,
                script.ScriptType,
                script.ExecutionFrequency
            }, JsonOptions),
            ChangedBy = changedBy,
            IpAddress = ipAddress,
            CorrelationId = correlationId,
            Reason = "consume-payload"
        });

        await _loggingService.LogInfoAsync(
            LogType.Automation,
            LogSource.Api,
            "Automation script payload consumed.",
            new
            {
                correlationId,
                scriptId = script.Id,
                scriptName = script.Name,
                version = script.Version,
                triggerModes = payload.TriggerModes
            },
            clientId: script.ClientId?.ToString());

        return payload;
    }

    private static void ValidateRequest(string name, string summary, string content, IReadOnlyList<string> triggerModes)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Name is required.");

        if (name.Length > 200)
            throw new InvalidOperationException("Name must have up to 200 characters.");

        if (string.IsNullOrWhiteSpace(summary))
            throw new InvalidOperationException("Summary is required.");

        if (summary.Length > 2000)
            throw new InvalidOperationException("Summary must have up to 2000 characters.");

        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("Content is required.");

        if (content.Length > 200000)
            throw new InvalidOperationException("Content size limit exceeded (200000 chars).");

        if (triggerModes is null || triggerModes.Count == 0)
            throw new InvalidOperationException("At least one trigger mode must be provided.");
    }

    private static string SerializeTriggerModes(IReadOnlyList<string> triggerModes)
    {
        var normalized = triggerModes
            .Where(mode => !string.IsNullOrWhiteSpace(mode))
            .Select(mode => mode.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return JsonSerializer.Serialize(normalized, JsonOptions);
    }

    private static IReadOnlyList<string> ParseTriggerModes(string? triggerModesJson)
    {
        if (string.IsNullOrWhiteSpace(triggerModesJson))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(triggerModesJson, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string ComputeHash(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static AutomationScriptSummaryDto ToSummaryDto(AutomationScriptDefinition script)
    {
        return new AutomationScriptSummaryDto
        {
            Id = script.Id,
            ClientId = script.ClientId,
            Name = script.Name,
            Summary = script.Summary,
            ScriptType = script.ScriptType,
            Version = script.Version,
            ExecutionFrequency = script.ExecutionFrequency,
            TriggerModes = ParseTriggerModes(script.TriggerModesJson),
            IsActive = script.IsActive,
            LastUpdatedAt = script.LastUpdatedAt,
            CreatedAt = script.CreatedAt
        };
    }

    private static AutomationScriptDetailDto ToDetailDto(AutomationScriptDefinition script)
    {
        return new AutomationScriptDetailDto
        {
            Id = script.Id,
            ClientId = script.ClientId,
            Name = script.Name,
            Summary = script.Summary,
            ScriptType = script.ScriptType,
            Version = script.Version,
            ExecutionFrequency = script.ExecutionFrequency,
            TriggerModes = ParseTriggerModes(script.TriggerModesJson),
            IsActive = script.IsActive,
            LastUpdatedAt = script.LastUpdatedAt,
            CreatedAt = script.CreatedAt,
            Content = script.Content,
            ContentHashSha256 = ComputeHash(script.Content),
            ParametersSchemaJson = script.ParametersSchemaJson,
            MetadataJson = script.MetadataJson,
            UpdatedAt = script.UpdatedAt
        };
    }

    private static object BuildAuditSnapshot(AutomationScriptDefinition script)
    {
        return new
        {
            script.Id,
            script.ClientId,
            script.Name,
            script.Summary,
            script.ScriptType,
            script.Version,
            script.ExecutionFrequency,
            TriggerModes = ParseTriggerModes(script.TriggerModesJson),
            script.IsActive,
            script.LastUpdatedAt,
            script.CreatedAt,
            script.UpdatedAt,
            ContentHashSha256 = ComputeHash(script.Content),
            script.ParametersSchemaJson,
            script.MetadataJson
        };
    }
}
