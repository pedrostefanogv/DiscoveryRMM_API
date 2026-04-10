using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;

namespace Discovery.Infrastructure.Services;

public class AutomationTaskService : IAutomationTaskService
{
    private const int LabelFilterMaxScan = 5000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IAutomationTaskRepository _taskRepository;
    private readonly IAutomationTaskAuditRepository _auditRepository;
    private readonly IAutomationScriptRepository _scriptRepository;
    private readonly IAgentRepository _agentRepository;
    private readonly ISiteRepository _siteRepository;
    private readonly IAgentLabelRepository _agentLabelRepository;
    private readonly ILoggingService _loggingService;

    public AutomationTaskService(
        IAutomationTaskRepository taskRepository,
        IAutomationTaskAuditRepository auditRepository,
        IAutomationScriptRepository scriptRepository,
        IAgentRepository agentRepository,
        ISiteRepository siteRepository,
        IAgentLabelRepository agentLabelRepository,
        ILoggingService loggingService)
    {
        _taskRepository = taskRepository;
        _auditRepository = auditRepository;
        _scriptRepository = scriptRepository;
        _agentRepository = agentRepository;
        _siteRepository = siteRepository;
        _agentLabelRepository = agentLabelRepository;
        _loggingService = loggingService;
    }

    public async Task<AutomationTaskPageDto> GetListAsync(
        AppApprovalScopeType? scopeType,
        Guid? scopeId,
        bool activeOnly,
        bool deletedOnly,
        bool includeDeleted,
        string? search,
        Guid? clientId,
        Guid? siteId,
        Guid? agentId,
        IReadOnlyList<AppApprovalScopeType>? scopeTypes,
        IReadOnlyList<AutomationTaskActionType>? actionTypes,
        IReadOnlyList<string>? labels,
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var safeLimit = Math.Clamp(limit, 1, 200);
        var safeOffset = Math.Max(0, offset);

        var normalizedLabels = labels?
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<AutomationTaskDefinition> items;
        int total;

        if (normalizedLabels is { Count: > 0 })
        {
            // Evita filtros de string em colunas jsonb no SQL; aplica labels em memória.
            var candidates = await _taskRepository.GetListAsync(
                scopeType,
                scopeId,
                activeOnly,
                deletedOnly,
                includeDeleted,
                search,
                clientId,
                siteId,
                agentId,
                scopeTypes,
                actionTypes,
                limit: LabelFilterMaxScan,
                offset: 0);

            var filtered = candidates
                .Where(task => MatchesTaskLabels(task, normalizedLabels))
                .ToList();

            total = filtered.Count;
            items = filtered
                .Skip(safeOffset)
                .Take(safeLimit)
                .ToList();
        }
        else
        {
            items = (await _taskRepository.GetListAsync(
                scopeType,
                scopeId,
                activeOnly,
                deletedOnly,
                includeDeleted,
                search,
                clientId,
                siteId,
                agentId,
                scopeTypes,
                actionTypes,
                safeLimit,
                safeOffset)).ToList();

            total = await _taskRepository.CountAsync(
                scopeType,
                scopeId,
                activeOnly,
                deletedOnly,
                includeDeleted,
                search,
                clientId,
                siteId,
                agentId,
                scopeTypes,
                actionTypes);
        }

        return new AutomationTaskPageDto
        {
            Items = items.Select(ToSummaryDto).ToList(),
            Count = items.Count,
            Total = total,
            Limit = safeLimit,
            Offset = safeOffset
        };
    }

    public async Task<AutomationTaskDetailDto?> GetByIdAsync(Guid id, bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var task = await _taskRepository.GetByIdIncludingDeletedAsync(id, includeInactive);
        return task is null ? null : ToDetailDto(task);
    }

    public async Task<AutomationTaskDetailDto> CreateAsync(
        CreateAutomationTaskRequest request,
        string? changedBy,
        string? ipAddress,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var resolvedScope = ResolveScope(request.ScopeType, request.ScopeId);
        await ValidateActionPayloadAsync(request.ActionType, request.InstallationType, request.PackageId, request.ScriptId, request.CommandPayload);

        var task = new AutomationTaskDefinition
        {
            Name = request.Name.Trim(),
            Description = request.Description,
            ActionType = request.ActionType,
            InstallationType = request.InstallationType,
            PackageId = request.PackageId?.Trim(),
            ScriptId = request.ScriptId,
            CommandPayload = request.CommandPayload,
            ScopeType = request.ScopeType,
            ClientId = resolvedScope.clientId,
            SiteId = resolvedScope.siteId,
            AgentId = resolvedScope.agentId,
            IncludeTagsJson = SerializeTags(request.IncludeTags),
            ExcludeTagsJson = SerializeTags(request.ExcludeTags),
            TriggerImmediate = request.TriggerImmediate,
            TriggerRecurring = request.TriggerRecurring,
            TriggerOnUserLogin = request.TriggerOnUserLogin,
            TriggerOnAgentCheckIn = request.TriggerOnAgentCheckIn,
            ScheduleCron = request.ScheduleCron,
            RequiresApproval = request.RequiresApproval,
            IsActive = request.IsActive
        };

        ValidateTask(task);

        var created = await _taskRepository.CreateAsync(task);
        var newSnapshot = BuildAuditSnapshot(created);

        await _auditRepository.CreateAsync(new AutomationTaskAudit
        {
            TaskId = created.Id,
            ChangeType = AutomationTaskChangeType.Created,
            NewValueJson = JsonSerializer.Serialize(newSnapshot, JsonOptions),
            ChangedBy = changedBy,
            IpAddress = ipAddress,
            CorrelationId = correlationId,
            Reason = "created"
        });

        await _loggingService.LogInfoAsync(
            LogType.Automation,
            LogSource.Api,
            "Automation task created.",
            new
            {
                correlationId,
                taskId = created.Id,
                taskName = created.Name,
                created.ActionType,
                created.ScopeType,
                created.TriggerImmediate,
                created.TriggerRecurring,
                created.TriggerOnUserLogin,
                created.TriggerOnAgentCheckIn
            },
            clientId: created.ClientId?.ToString(),
            siteId: created.SiteId?.ToString(),
            agentId: created.AgentId?.ToString());

        return ToDetailDto(created);
    }

    public async Task<AutomationTaskDetailDto?> UpdateAsync(
        Guid id,
        UpdateAutomationTaskRequest request,
        string? changedBy,
        string? ipAddress,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var existing = await _taskRepository.GetByIdAsync(id, includeInactive: true);
        if (existing is null)
            return null;

        var resolvedScope = ResolveScope(request.ScopeType, request.ScopeId);
        await ValidateActionPayloadAsync(request.ActionType, request.InstallationType, request.PackageId, request.ScriptId, request.CommandPayload);

        var oldSnapshot = BuildAuditSnapshot(existing);
        var oldIsActive = existing.IsActive;

        existing.Name = request.Name.Trim();
        existing.Description = request.Description;
        existing.ActionType = request.ActionType;
        existing.InstallationType = request.InstallationType;
        existing.PackageId = request.PackageId?.Trim();
        existing.ScriptId = request.ScriptId;
        existing.CommandPayload = request.CommandPayload;
        existing.ScopeType = request.ScopeType;
        existing.ClientId = resolvedScope.clientId;
        existing.SiteId = resolvedScope.siteId;
        existing.AgentId = resolvedScope.agentId;
        existing.IncludeTagsJson = SerializeTags(request.IncludeTags);
        existing.ExcludeTagsJson = SerializeTags(request.ExcludeTags);
        existing.TriggerImmediate = request.TriggerImmediate;
        existing.TriggerRecurring = request.TriggerRecurring;
        existing.TriggerOnUserLogin = request.TriggerOnUserLogin;
        existing.TriggerOnAgentCheckIn = request.TriggerOnAgentCheckIn;
        existing.ScheduleCron = request.ScheduleCron;
        existing.RequiresApproval = request.RequiresApproval;
        existing.IsActive = request.IsActive;

        ValidateTask(existing);

        await _taskRepository.UpdateAsync(existing);

        var updated = await _taskRepository.GetByIdAsync(id, includeInactive: true) ?? existing;
        var newSnapshot = BuildAuditSnapshot(updated);

        await _auditRepository.CreateAsync(new AutomationTaskAudit
        {
            TaskId = updated.Id,
            ChangeType = oldIsActive != updated.IsActive
                ? (updated.IsActive ? AutomationTaskChangeType.Activated : AutomationTaskChangeType.Deactivated)
                : AutomationTaskChangeType.Updated,
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
            "Automation task updated.",
            new { correlationId, taskId = updated.Id, taskName = updated.Name, updated.ActionType, updated.IsActive },
            clientId: updated.ClientId?.ToString(),
            siteId: updated.SiteId?.ToString(),
            agentId: updated.AgentId?.ToString());

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

        var existing = await _taskRepository.GetByIdAsync(id, includeInactive: true);
        if (existing is null)
            return false;

        var oldSnapshot = BuildAuditSnapshot(existing);
        await _taskRepository.DeleteAsync(id);

        await _auditRepository.CreateAsync(new AutomationTaskAudit
        {
            TaskId = id,
            ChangeType = AutomationTaskChangeType.Deleted,
            OldValueJson = JsonSerializer.Serialize(oldSnapshot, JsonOptions),
            ChangedBy = changedBy,
            IpAddress = ipAddress,
            CorrelationId = correlationId,
            Reason = reason
        });

        await _loggingService.LogWarningAsync(
            LogType.Automation,
            LogSource.Api,
            "Automation task deleted.",
            new { correlationId, taskId = id, taskName = existing.Name, reason },
            clientId: existing.ClientId?.ToString(),
            siteId: existing.SiteId?.ToString(),
            agentId: existing.AgentId?.ToString());

        return true;
    }

    public async Task<AutomationTaskDetailDto?> RestoreAsync(
        Guid id,
        string? changedBy,
        string? ipAddress,
        string correlationId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var existing = await _taskRepository.GetByIdIncludingDeletedAsync(id, includeInactive: true);
        if (existing is null || existing.DeletedAt is null)
            return null;

        var oldSnapshot = BuildAuditSnapshot(existing);
        var restored = await _taskRepository.RestoreAsync(id);
        if (restored is null)
            return null;

        var newSnapshot = BuildAuditSnapshot(restored);

        await _auditRepository.CreateAsync(new AutomationTaskAudit
        {
            TaskId = restored.Id,
            ChangeType = AutomationTaskChangeType.Activated,
            OldValueJson = JsonSerializer.Serialize(oldSnapshot, JsonOptions),
            NewValueJson = JsonSerializer.Serialize(newSnapshot, JsonOptions),
            ChangedBy = changedBy,
            IpAddress = ipAddress,
            CorrelationId = correlationId,
            Reason = reason ?? "restored"
        });

        await _loggingService.LogInfoAsync(
            LogType.Automation,
            LogSource.Api,
            "Automation task restored.",
            new { correlationId, taskId = restored.Id, taskName = restored.Name, reason },
            clientId: restored.ClientId?.ToString(),
            siteId: restored.SiteId?.ToString(),
            agentId: restored.AgentId?.ToString());

        return ToDetailDto(restored);
    }

    public async Task<IReadOnlyList<AutomationTaskAuditDto>> GetAuditAsync(Guid id, int limit = 100, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var entries = await _auditRepository.GetByTaskIdAsync(id, limit);
        return entries.Select(entry => new AutomationTaskAuditDto
        {
            Id = entry.Id,
            TaskId = entry.TaskId,
            ChangeType = entry.ChangeType.ToString(),
            Reason = entry.Reason,
            OldValueJson = entry.OldValueJson,
            NewValueJson = entry.NewValueJson,
            ChangedBy = entry.ChangedBy,
            IpAddress = entry.IpAddress,
            CorrelationId = entry.CorrelationId,
            ChangedAt = entry.ChangedAt
        }).ToList();
    }

    public async Task<AutomationTaskTargetPreviewPageDto?> PreviewTargetAgentsAsync(
        Guid taskId,
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var task = await _taskRepository.GetByIdIncludingDeletedAsync(taskId, includeInactive: true);
        if (task is null)
            return null;

        var safeLimit = Math.Clamp(limit, 1, 200);
        var safeOffset = Math.Max(0, offset);

        var candidates = (await ResolveScopeAgentsAsync(task))
            .GroupBy(agent => agent.Id)
            .Select(group => group.First())
            .OrderBy(agent => agent.Hostname)
            .ToList();

        var includeTags = ParseTags(task.IncludeTagsJson);
        var excludeTags = ParseTags(task.ExcludeTagsJson);
        var hasTagFilters = includeTags.Count > 0 || excludeTags.Count > 0;

        var labels = hasTagFilters
            ? await _agentLabelRepository.GetByAgentIdsAsync(candidates.Select(agent => agent.Id).ToList())
            : [];

        var labelsByAgent = labels
            .GroupBy(label => label.AgentId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(label => label.Label)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(label => label)
                    .ToList() as IReadOnlyList<string>);

        var applicable = candidates
            .Where(agent => MatchesTagFilters(includeTags, excludeTags, labelsByAgent.TryGetValue(agent.Id, out var agentTags) ? agentTags : []))
            .ToList();

        var page = applicable
            .Skip(safeOffset)
            .Take(safeLimit)
            .Select(agent => new AutomationTaskTargetPreviewItemDto
            {
                AgentId = agent.Id,
                SiteId = agent.SiteId,
                Hostname = agent.Hostname,
                DisplayName = agent.DisplayName,
                Status = agent.Status,
                AgentTags = labelsByAgent.TryGetValue(agent.Id, out var agentTags) ? agentTags : []
            })
            .ToList();

        return new AutomationTaskTargetPreviewPageDto
        {
            TaskId = task.Id,
            TaskName = task.Name,
            ScopeType = task.ScopeType,
            IncludeTags = includeTags,
            ExcludeTags = excludeTags,
            Items = page,
            Count = page.Count,
            Total = applicable.Count,
            Limit = safeLimit,
            Offset = safeOffset
        };
    }

    public async Task<AgentAutomationPolicySyncResponse> SyncPolicyForAgentAsync(
        Guid agentId,
        AgentAutomationPolicySyncRequest request,
        string? changedBy,
        string? ipAddress,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var (agent, site, applicable) = await ResolveApplicablePolicyTasksAsync(agentId);

        var policyKey = BuildPolicyKey(applicable);
        var fingerprint = ComputeHash(policyKey);

        if (!string.IsNullOrWhiteSpace(request.KnownPolicyFingerprint)
            && string.Equals(request.KnownPolicyFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            await _loggingService.LogDebugAsync(
                LogType.Automation,
                LogSource.Api,
                "Automation policy sync is up to date.",
                new { correlationId, agentId, fingerprint, taskCount = applicable.Count },
                agentId: agentId.ToString(),
                siteId: site.Id.ToString(),
                clientId: site.ClientId.ToString());

            return new AgentAutomationPolicySyncResponse
            {
                UpToDate = true,
                PolicyFingerprint = fingerprint,
                GeneratedAt = DateTime.UtcNow,
                TaskCount = applicable.Count,
                Tasks = []
            };
        }

        var taskDtos = new List<AgentAutomationTaskPolicyDto>(applicable.Count);
        foreach (var task in applicable)
        {
            AgentAutomationScriptRefDto? scriptRef = null;
            if (task.ActionType == AutomationTaskActionType.RunScript && task.ScriptId.HasValue)
            {
                var script = await _scriptRepository.GetByIdAsync(task.ScriptId.Value, includeInactive: false);
                if (script is not null)
                {
                    scriptRef = new AgentAutomationScriptRefDto
                    {
                        ScriptId = script.Id,
                        Name = script.Name,
                        Version = script.Version,
                        Summary = script.Summary,
                        ScriptType = script.ScriptType,
                        LastUpdatedAt = script.LastUpdatedAt,
                        ContentHashSha256 = ComputeHash(script.Content),
                        Content = request.IncludeScriptContent ? script.Content : null,
                        ParametersSchemaJson = script.ParametersSchemaJson,
                        MetadataJson = script.MetadataJson
                    };
                }
            }

            taskDtos.Add(new AgentAutomationTaskPolicyDto
            {
                TaskId = task.Id,
                Name = task.Name,
                Description = task.Description,
                ActionType = task.ActionType,
                InstallationType = task.InstallationType,
                PackageId = task.PackageId,
                ScriptId = task.ScriptId,
                CommandPayload = task.CommandPayload,
                ScopeType = task.ScopeType,
                RequiresApproval = task.RequiresApproval,
                TriggerImmediate = task.TriggerImmediate,
                TriggerRecurring = task.TriggerRecurring,
                TriggerOnUserLogin = task.TriggerOnUserLogin,
                TriggerOnAgentCheckIn = task.TriggerOnAgentCheckIn,
                ScheduleCron = task.ScheduleCron,
                IncludeTags = ParseTags(task.IncludeTagsJson),
                ExcludeTags = ParseTags(task.ExcludeTagsJson),
                LastUpdatedAt = task.LastUpdatedAt,
                Script = scriptRef
            });
        }

        await _loggingService.LogInfoAsync(
            LogType.Automation,
            LogSource.Api,
            "Automation policy sync delivered to agent.",
            new
            {
                correlationId,
                agentId,
                fingerprint,
                taskCount = taskDtos.Count,
                includeScriptContent = request.IncludeScriptContent
            },
            agentId: agentId.ToString(),
            siteId: site.Id.ToString(),
            clientId: site.ClientId.ToString());

        return new AgentAutomationPolicySyncResponse
        {
            UpToDate = false,
            PolicyFingerprint = fingerprint,
            GeneratedAt = DateTime.UtcNow,
            TaskCount = taskDtos.Count,
            Tasks = taskDtos
        };
    }

    public async Task<string> GetPolicyFingerprintForAgentAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var (_, _, applicable) = await ResolveApplicablePolicyTasksAsync(agentId);
        var policyKey = BuildPolicyKey(applicable);
        return ComputeHash(policyKey);
    }

    private async Task<(Agent Agent, Site Site, List<AutomationTaskDefinition> ApplicableTasks)> ResolveApplicablePolicyTasksAsync(Guid agentId)
    {
        var agent = await _agentRepository.GetByIdAsync(agentId)
            ?? throw new InvalidOperationException("Agent not found.");
        var site = await _siteRepository.GetByIdAsync(agent.SiteId)
            ?? throw new InvalidOperationException("Site not found for agent.");

        var labels = await _agentLabelRepository.GetByAgentIdAsync(agentId);
        var labelSet = labels
            .Select(label => label.Label)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var globalTasks = await _taskRepository.GetListAsync(AppApprovalScopeType.Global, null, activeOnly: true, deletedOnly: false, includeDeleted: false, search: null, clientId: null, siteId: null, agentId: null, scopeTypes: null, actionTypes: null, limit: 1000, offset: 0);
        var clientTasks = await _taskRepository.GetListAsync(AppApprovalScopeType.Client, site.ClientId, activeOnly: true, deletedOnly: false, includeDeleted: false, search: null, clientId: null, siteId: null, agentId: null, scopeTypes: null, actionTypes: null, limit: 1000, offset: 0);
        var siteTasks = await _taskRepository.GetListAsync(AppApprovalScopeType.Site, site.Id, activeOnly: true, deletedOnly: false, includeDeleted: false, search: null, clientId: null, siteId: null, agentId: null, scopeTypes: null, actionTypes: null, limit: 1000, offset: 0);
        var agentTasks = await _taskRepository.GetListAsync(AppApprovalScopeType.Agent, agentId, activeOnly: true, deletedOnly: false, includeDeleted: false, search: null, clientId: null, siteId: null, agentId: null, scopeTypes: null, actionTypes: null, limit: 1000, offset: 0);

        var applicable = globalTasks
            .Concat(clientTasks)
            .Concat(siteTasks)
            .Concat(agentTasks)
            .GroupBy(task => task.Id)
            .Select(group => group.First())
            .Where(task => MatchesTagFilters(task, labelSet))
            .OrderBy(task => task.Id)
            .ToList();

        return (agent, site, applicable);
    }

    private async Task ValidateActionPayloadAsync(AutomationTaskActionType actionType, AppInstallationType? installationType, string? packageId, Guid? scriptId, string? commandPayload)
    {
        switch (actionType)
        {
            case AutomationTaskActionType.InstallPackage:
            case AutomationTaskActionType.UpdatePackage:
            case AutomationTaskActionType.RemovePackage:
            case AutomationTaskActionType.UpdateOrInstallPackage:
                if (!installationType.HasValue)
                    throw new InvalidOperationException("InstallationType is required for package actions.");
                if (string.IsNullOrWhiteSpace(packageId))
                    throw new InvalidOperationException("PackageId is required for package actions.");
                break;

            case AutomationTaskActionType.RunScript:
                if (!scriptId.HasValue)
                    throw new InvalidOperationException("ScriptId is required for RunScript action.");
                var script = await _scriptRepository.GetByIdAsync(scriptId.Value, includeInactive: false);
                if (script is null)
                    throw new InvalidOperationException("ScriptId not found or inactive.");
                break;

            case AutomationTaskActionType.CustomCommand:
                if (string.IsNullOrWhiteSpace(commandPayload))
                    throw new InvalidOperationException("CommandPayload is required for CustomCommand action.");
                break;
        }
    }

    private async Task<IReadOnlyList<Agent>> ResolveScopeAgentsAsync(AutomationTaskDefinition task)
    {
        return task.ScopeType switch
        {
            AppApprovalScopeType.Global => (await _agentRepository.GetAllAsync()).ToList(),
            AppApprovalScopeType.Client when task.ClientId.HasValue => (await _agentRepository.GetByClientIdAsync(task.ClientId.Value)).ToList(),
            AppApprovalScopeType.Site when task.SiteId.HasValue => (await _agentRepository.GetBySiteIdAsync(task.SiteId.Value)).ToList(),
            AppApprovalScopeType.Agent when task.AgentId.HasValue => await ResolveSingleAgentAsync(task.AgentId.Value),
            _ => []
        };
    }

    private async Task<IReadOnlyList<Agent>> ResolveSingleAgentAsync(Guid agentId)
    {
        var agent = await _agentRepository.GetByIdAsync(agentId);
        return agent is null ? [] : [agent];
    }

    private static bool MatchesTagFilters(IReadOnlyList<string> include, IReadOnlyList<string> exclude, IReadOnlyList<string> labels)
    {
        var labelSet = labels.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (include.Count > 0 && !include.Any(labelSet.Contains))
            return false;

        if (exclude.Count > 0 && exclude.Any(labelSet.Contains))
            return false;

        return true;
    }

    private static void ValidateTask(AutomationTaskDefinition task)
    {
        if (string.IsNullOrWhiteSpace(task.Name))
            throw new InvalidOperationException("Name is required.");

        if (task.Name.Length > 200)
            throw new InvalidOperationException("Name must have up to 200 characters.");

        if (!task.TriggerImmediate && !task.TriggerRecurring && !task.TriggerOnUserLogin && !task.TriggerOnAgentCheckIn)
            throw new InvalidOperationException("At least one trigger must be enabled.");

        if (task.TriggerRecurring && string.IsNullOrWhiteSpace(task.ScheduleCron))
            throw new InvalidOperationException("ScheduleCron is required when TriggerRecurring is enabled.");
    }

    private static (Guid? clientId, Guid? siteId, Guid? agentId) ResolveScope(AppApprovalScopeType scopeType, Guid? scopeId)
    {
        return scopeType switch
        {
            AppApprovalScopeType.Global => (null, null, null),
            AppApprovalScopeType.Client => (scopeId ?? throw new InvalidOperationException("ScopeId is required for Client scope."), null, null),
            AppApprovalScopeType.Site => (null, scopeId ?? throw new InvalidOperationException("ScopeId is required for Site scope."), null),
            AppApprovalScopeType.Agent => (null, null, scopeId ?? throw new InvalidOperationException("ScopeId is required for Agent scope.")),
            _ => (null, null, null)
        };
    }

    private static string? SerializeTags(IReadOnlyList<string> tags)
    {
        if (tags is null || tags.Count == 0)
            return null;

        var normalized = tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count == 0)
            return null;

        return JsonSerializer.Serialize(normalized, JsonOptions);
    }

    private static IReadOnlyList<string> ParseTags(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(tagsJson, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static bool MatchesTagFilters(AutomationTaskDefinition task, IReadOnlySet<string> labels)
    {
        var include = ParseTags(task.IncludeTagsJson);
        var exclude = ParseTags(task.ExcludeTagsJson);

        if (include.Count > 0 && !include.Any(labels.Contains))
            return false;

        if (exclude.Count > 0 && exclude.Any(labels.Contains))
            return false;

        return true;
    }

    private static bool MatchesTaskLabels(AutomationTaskDefinition task, IReadOnlyList<string> labels)
    {
        var requested = labels.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requested.Count == 0)
            return true;

        var include = ParseTags(task.IncludeTagsJson);
        var exclude = ParseTags(task.ExcludeTagsJson);

        return include.Any(requested.Contains) || exclude.Any(requested.Contains);
    }

    private static string BuildPolicyKey(IReadOnlyList<AutomationTaskDefinition> tasks)
    {
        var builder = new StringBuilder();
        foreach (var task in tasks.OrderBy(task => task.Id))
        {
            builder
                .Append(task.Id).Append('|')
                .Append(task.LastUpdatedAt.ToUniversalTime().Ticks).Append('|')
                .Append(task.ActionType).Append('|')
                .Append(task.ScriptId?.ToString() ?? string.Empty).Append('|')
                .Append(task.PackageId ?? string.Empty).Append(';');
        }

        return builder.ToString();
    }

    private static string ComputeHash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static Guid? ResolveScopeId(AutomationTaskDefinition task)
    {
        return task.ScopeType switch
        {
            AppApprovalScopeType.Client => task.ClientId,
            AppApprovalScopeType.Site => task.SiteId,
            AppApprovalScopeType.Agent => task.AgentId,
            _ => null
        };
    }

    private static AutomationTaskSummaryDto ToSummaryDto(AutomationTaskDefinition task)
    {
        return new AutomationTaskSummaryDto
        {
            Id = task.Id,
            Name = task.Name,
            Description = task.Description,
            ActionType = task.ActionType,
            ScopeType = task.ScopeType,
            ScopeId = ResolveScopeId(task),
            IsActive = task.IsActive,
            RequiresApproval = task.RequiresApproval,
            LastUpdatedAt = task.LastUpdatedAt,
            IsDeleted = task.DeletedAt.HasValue,
            DeletedAt = task.DeletedAt
        };
    }

    private static AutomationTaskDetailDto ToDetailDto(AutomationTaskDefinition task)
    {
        return new AutomationTaskDetailDto
        {
            Id = task.Id,
            Name = task.Name,
            Description = task.Description,
            ActionType = task.ActionType,
            ScopeType = task.ScopeType,
            ScopeId = ResolveScopeId(task),
            IsActive = task.IsActive,
            RequiresApproval = task.RequiresApproval,
            LastUpdatedAt = task.LastUpdatedAt,
            IsDeleted = task.DeletedAt.HasValue,
            DeletedAt = task.DeletedAt,
            InstallationType = task.InstallationType,
            PackageId = task.PackageId,
            ScriptId = task.ScriptId,
            CommandPayload = task.CommandPayload,
            IncludeTags = ParseTags(task.IncludeTagsJson),
            ExcludeTags = ParseTags(task.ExcludeTagsJson),
            TriggerImmediate = task.TriggerImmediate,
            TriggerRecurring = task.TriggerRecurring,
            TriggerOnUserLogin = task.TriggerOnUserLogin,
            TriggerOnAgentCheckIn = task.TriggerOnAgentCheckIn,
            ScheduleCron = task.ScheduleCron,
            CreatedAt = task.CreatedAt,
            UpdatedAt = task.UpdatedAt
        };
    }

    private static object BuildAuditSnapshot(AutomationTaskDefinition task)
    {
        return new
        {
            task.Id,
            task.Name,
            task.Description,
            task.ActionType,
            task.InstallationType,
            task.PackageId,
            task.ScriptId,
            task.CommandPayload,
            task.ScopeType,
            task.ClientId,
            task.SiteId,
            task.AgentId,
            IncludeTags = ParseTags(task.IncludeTagsJson),
            ExcludeTags = ParseTags(task.ExcludeTagsJson),
            task.TriggerImmediate,
            task.TriggerRecurring,
            task.TriggerOnUserLogin,
            task.TriggerOnAgentCheckIn,
            task.ScheduleCron,
            task.RequiresApproval,
            task.IsActive,
            task.DeletedAt,
            task.LastUpdatedAt,
            task.CreatedAt,
            task.UpdatedAt
        };
    }
}
