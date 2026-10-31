using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

public class AutomationTaskService : IAutomationTaskService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Tempo padrao (s) para a acao continuar quando o usuario nao responde ao prompt.</summary>
    public const int DefaultPromptTimeoutSeconds = 60;
    private const int MinPromptTimeoutSeconds = 5;
    private const int MaxPromptTimeoutSeconds = 3600;
    private const int MaxCloseProcesses = 20;
    private const int MaxCloseProcessNameLength = 120;

    private readonly IAutomationTaskRepository _taskRepository;
    private readonly IAutomationTaskAuditRepository _auditRepository;
    private readonly IAutomationScriptRepository _scriptRepository;
    private readonly IAgentRepository _agentRepository;
    private readonly ISiteRepository _siteRepository;
    private readonly IClientRepository _clientRepository;
    private readonly IAgentLabelRepository _agentLabelRepository;
    private readonly ILoggingService _loggingService;
    private readonly ICustomFieldService _customFieldService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AutomationTaskService> _logger;

    public AutomationTaskService(
        IAutomationTaskRepository taskRepository,
        IAutomationTaskAuditRepository auditRepository,
        IAutomationScriptRepository scriptRepository,
        IAgentRepository agentRepository,
        ISiteRepository siteRepository,
        IClientRepository clientRepository,
        IAgentLabelRepository agentLabelRepository,
        ILoggingService loggingService,
        ICustomFieldService customFieldService,
        IServiceScopeFactory scopeFactory,
        ILogger<AutomationTaskService> logger)
    {
        _taskRepository = taskRepository;
        _auditRepository = auditRepository;
        _scriptRepository = scriptRepository;
        _agentRepository = agentRepository;
        _siteRepository = siteRepository;
        _clientRepository = clientRepository;
        _agentLabelRepository = agentLabelRepository;
        _loggingService = loggingService;
        _customFieldService = customFieldService;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<CursorPageDto<AutomationTaskSummaryDto>> GetListPageAsync(
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
        IReadOnlyList<string>? triggers,
        bool? isActive,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var safeLimit = Math.Clamp(limit, 1, 200);

        // Quando labels estão presentes o filtro é aplicado em memória (pós-query).
        // Over-fetch para reduzir o risco de páginas menores que o limite e de
        // "pular" tarefas que casam com o filtro mas ficam fora da janela buscada.
        var fetchLimit = labels is { Count: > 0 }
            ? Math.Clamp(safeLimit * 5, safeLimit, 500)
            : safeLimit;

        var items = await _taskRepository.GetListPageAsync(
            scopeType, scopeId, activeOnly, deletedOnly, includeDeleted,
            search, clientId, siteId, agentId, scopeTypes, actionTypes, triggers, isActive, cursor, fetchLimit);

        var filteredItems = labels is { Count: > 0 }
            ? items.Where(task => MatchesTaskLabels(task, labels)).ToList()
            : (IReadOnlyList<AutomationTaskDefinition>)items;

        var slice = CursorPaginationHelper.SlicePage(filteredItems, safeLimit);
        var pageItems = slice.Page.Select(ToSummaryDto).ToList();
        await EnrichScopeNamesAsync(pageItems);

        return new CursorPageDto<AutomationTaskSummaryDto>(
            pageItems.AsReadOnly(),
            pageItems.Count,
            cursor,
            slice.HasMore && slice.LastItem is not null
                ? CursorPaginationHelper.EncodeCreatedAtCursor(slice.LastItem.UpdatedAt, slice.LastItem.Id)
                : null,
            slice.HasMore,
            safeLimit);
    }

    public async Task<AutomationTaskDetailDto?> GetByIdAsync(Guid id, bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var task = await _taskRepository.GetByIdIncludingDeletedAsync(id, includeInactive);
        if (task is null) return null;
        var dto = ToDetailDto(task);
        await EnrichScopeNamesAsync([dto]);
        return dto;
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

        // NotificationMode e a fonte de verdade; RequiresApproval fica derivado
        // (= Prompt) para compatibilidade com o contrato antigo de agents.
        var notificationMode = ResolveNotificationMode(request);

        // Policy de automação SOBREPÕE a App Store: a tarefa é aceita mesmo com
        // pacote fora da loja — mas o resultado da checagem fica persistido e
        // exposto na UI (packageAuthorized/packageWarning).
        var packageAuth = await ResolvePackageAuthorizationAsync(
            request.ActionType, request.InstallationType, request.PackageId,
            resolvedScope.clientId, resolvedScope.siteId, resolvedScope.agentId);

        var task = new AutomationTaskDefinition
        {
            Name = request.Name.Trim(),
            Description = request.Description,
            ActionType = request.ActionType,
            InstallationType = request.InstallationType,
            PackageId = request.PackageId?.Trim(),
            PackageAuthorized = packageAuth.Authorized,
            PackageWarning = packageAuth.Warning,
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
            RequiresApproval = notificationMode == AutomationNotificationMode.Prompt,
            NotificationMode = notificationMode,
            ToastTiming = request.ToastTiming ?? AutomationToastTiming.After,
            CollectorFieldDefinitionId = request.CollectorFieldDefinitionId,
            WriteOutputToNote = request.WriteOutputToNote,
            // 0–99 com default por tipo de ação quando o operador não informa.
            ExecutionOrder = AutomationTaskExecutionOrder.ResolveForCreate(request.ExecutionOrder, request.ActionType),
            Key = NormalizeTaskKey(request.Key, request.Name),
            AllowDefer = request.AllowDefer,
            CloseProcessesJson = SerializeTags(request.CloseProcesses),
            UserPromptTimeoutSeconds = NormalizePromptTimeout(request.PromptTimeoutSeconds),
            IsActive = request.IsActive
        };

        await ValidateCollectorFieldAsync(task);
        await ValidateTaskKeyAsync(task);
        ValidateTask(task);
        await ValidateScopeTargetExistsAsync(task);

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

        await LogInfoBestEffortAsync(
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
                created.PackageAuthorized,
                packageWarning = created.PackageWarning,
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

        // Reavalia a autorização do pacote com o escopo NOVO da edição.
        var updatePackageAuth = await ResolvePackageAuthorizationAsync(
            request.ActionType, request.InstallationType, request.PackageId,
            resolvedScope.clientId, resolvedScope.siteId, resolvedScope.agentId);

        var oldSnapshot = BuildAuditSnapshot(existing);
        var oldIsActive = existing.IsActive;
        // Escopo anterior: usado para NÃO revalidar o alvo quando o PUT não mexe no
        // escopo (tarefa antiga apontando para agente na lixeira continua editável).
        var previousScope = (existing.ScopeType, existing.ClientId, existing.SiteId, existing.AgentId);

        existing.Name = request.Name.Trim();
        existing.Description = request.Description;
        existing.ActionType = request.ActionType;
        existing.InstallationType = request.InstallationType;
        existing.PackageId = request.PackageId?.Trim();
        // Só sobrescreve quando a checagem realmente executou. Uma falha
        // transitória da checagem retornava (null,null) e APAGAVA a evidência de
        // autorização já persistida (revisão 2026-10-31, A06).
        if (updatePackageAuth.Authorized.HasValue)
        {
            existing.PackageAuthorized = updatePackageAuth.Authorized;
            existing.PackageWarning = updatePackageAuth.Warning;
        }
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
        var notificationMode = request.NotificationMode ?? existing.NotificationMode;
        existing.NotificationMode = notificationMode;
        existing.ToastTiming = request.ToastTiming ?? existing.ToastTiming;
        existing.CollectorFieldDefinitionId = request.CollectorFieldDefinitionId;
        existing.WriteOutputToNote = request.WriteOutputToNote;
        // Ausente preserva a ordem atual (não reseta para o default ao editar).
        if (request.ExecutionOrder.HasValue)
        {
            existing.ExecutionOrder = AutomationTaskExecutionOrder.ResolveForCreate(request.ExecutionOrder, request.ActionType);
        }
        existing.Key = NormalizeTaskKey(request.Key ?? existing.Key, existing.Name);
        // RequiresApproval derivado do modo (Prompt) — o handler de PUT ja
        // preserva o valor atual quando o campo nao vem no payload.
        existing.RequiresApproval = notificationMode == AutomationNotificationMode.Prompt;
        existing.AllowDefer = request.AllowDefer;
        existing.CloseProcessesJson = SerializeTags(request.CloseProcesses);
        existing.UserPromptTimeoutSeconds = NormalizePromptTimeout(request.PromptTimeoutSeconds);
        existing.IsActive = request.IsActive;

        await ValidateCollectorFieldAsync(existing);
        await ValidateTaskKeyAsync(existing);
        ValidateTask(existing);
        await ValidateScopeTargetExistsAsync(existing, previousScope);

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

        await LogInfoBestEffortAsync(
            LogType.Automation,
            LogSource.Api,
            "Automation task updated.",
            new { correlationId, taskId = updated.Id, taskName = updated.Name, updated.ActionType, updated.IsActive, updated.PackageAuthorized, packageWarning = updated.PackageWarning },
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

        // A chave da tarefa excluída pode ter sido reutilizada (unicidade por escopo
        // considera só tarefas ativas). Reativar sem checar estouraria 23505 no
        // índice parcial da M254 → 500.
        if (!string.IsNullOrWhiteSpace(existing.Key))
        {
            var conflict = await _taskRepository.ExistsKeyAsync(
                existing.Key, existing.ClientId, existing.SiteId, existing.AgentId, existing.Id);
            if (conflict)
                throw new InvalidOperationException(
                    "Já existe uma tarefa ativa com esta chave neste escopo; altere a chave da tarefa ativa ou a da tarefa excluída antes de reativar.");
        }

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

        await LogInfoBestEffortAsync(
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

#pragma warning disable CS0618 // TaskId is obsolete — still used in offset-based preview; will be migrated to cursor in v2
        return new AutomationTaskTargetPreviewPageDto
        {
            TaskId = task.Id,
#pragma warning restore CS0618
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

        var (agent, site, applicableTasks) = await ResolveApplicablePolicyTasksAsync(agentId);

        // Tarefas RunScript sem script entregável (inativo/excluído) saem da policy,
        // e os scripts restantes são carregados uma única vez: entram no fingerprint
        // (versão + hash do conteúdo) e são reutilizados na montagem dos DTOs.
        var (applicable, scriptsById) = await LoadDeliverablePolicyInputsAsync(applicableTasks);
        var policyKey = BuildPolicyKey(applicable, scriptsById);
        var fingerprint = ComputeHash(policyKey);
        var policyUpToDate = !string.IsNullOrWhiteSpace(request.KnownPolicyFingerprint)
            && string.Equals(request.KnownPolicyFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase);
        // Registra o que o agent recebeu (nos dois ramos): é o que permite à aba
        // "Políticas" dizer se ele está com a policy vigente.
        await RememberDeliveredPolicyAsync(agent, fingerprint);

        // O atalho "up-to-date" responde Tasks=[] e NÃO carrega conteúdo de script.
        // Quando o agent pede o conteúdo explicitamente (sync manual com
        // IncludeScriptContent), o atalho é ignorado: sem isso um agent que já
        // conhece o fingerprint nunca conseguiria obter o conteúdo, e a mensagem
        // "refaca o sync com IncludeScriptContent" seria impossível de cumprir.
        if (policyUpToDate && !request.IncludeScriptContent)
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
                Tasks = [],
                PreloadPackages = BuildPreloadPackages(applicable)
            };
        }

        var taskDtos = await BuildTaskPolicyDtosAsync(applicable, request.IncludeScriptContent, scriptsById);

        await LogInfoBestEffortAsync(
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
            // policyUpToDate reflete o fingerprint; as Tasks vêm populadas porque o
            // conteúdo foi pedido explicitamente (atalho up-to-date ignorado acima).
            UpToDate = policyUpToDate,
            PolicyFingerprint = fingerprint,
            GeneratedAt = DateTime.UtcNow,
            TaskCount = taskDtos.Count,
            Tasks = taskDtos,
            PreloadPackages = BuildPreloadPackages(applicable)
        };
    }

    /// <summary>
    /// Registra no agent o fingerprint da policy entregue. Só escreve quando o
    /// valor muda: o agent sincroniza a cada check-in e reescrever a linha
    /// sempre amplificaria writes sem ganho — o fingerprint é a fonte da
    /// verdade e o timestamp marca quando ESTA policy foi entregue.
    /// </summary>
    private async Task RememberDeliveredPolicyAsync(Agent agent, string fingerprint)
    {
        if (string.Equals(agent.LastPolicyFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            return;

        var syncedAt = DateTime.UtcNow;
        agent.LastPolicyFingerprint = fingerprint;
        agent.LastPolicySyncAt = syncedAt;
        // Update direcionado (2 colunas) em vez de reescrever a linha inteira do
        // agent — o sync acontece a cada check-in e não pode competir com outras
        // atualizações do cadastro.
        await _agentRepository.SetPolicySyncAsync(agent.Id, fingerprint, syncedAt);
    }

    /// <summary>
    /// Carrega numa única consulta todos os scripts referenciados pelas tasks
    /// RunScript. Compartilhado pelo cálculo do fingerprint (BuildPolicyKey) e
    /// pela montagem dos DTOs — ambos precisam do conteúdo/versão do script.
    /// </summary>
    /// <summary>
    /// Prepara a policy: filtra as tarefas RunScript cujo script não é entregável e
    /// carrega numa única consulta os scripts referenciados pelas demais.
    ///
    /// Uma tarefa RunScript cujo script foi desativado/excluído DEPOIS da criação
    /// continuava indo na policy com Script=null e o agent falhava a execução com
    /// exit 2 ("script nao resolvido para a tarefa") a cada gatilho. Criar/editar
    /// tarefa RunScript já exige script ativo (ValidateActionPayloadAsync), então
    /// este filtro cobre a desativação posterior. A tarefa continua no catálogo —
    /// só não é entregue enquanto o script não voltar a ficar ativo.
    /// </summary>
    private async Task<(List<AutomationTaskDefinition> Tasks, IReadOnlyDictionary<Guid, AutomationScriptDefinition> Scripts)>
        LoadDeliverablePolicyInputsAsync(IReadOnlyList<AutomationTaskDefinition> applicable)
    {
        var scriptsById = await LoadScriptsForTasksAsync(applicable);
        return (FilterDeliverableTasks(applicable, scriptsById), scriptsById);
    }

    /// <summary>
    /// Remove da policy as tarefas RunScript sem script disponível (id ausente ou
    /// script inativo/excluído). Visível para teste.
    /// </summary>
    internal static List<AutomationTaskDefinition> FilterDeliverableTasks(
        IReadOnlyList<AutomationTaskDefinition> tasks,
        IReadOnlyDictionary<Guid, AutomationScriptDefinition> scriptsById)
        => tasks
            .Where(task => !(task.ActionType == AutomationTaskActionType.RunScript
                && (!task.ScriptId.HasValue || !scriptsById.ContainsKey(task.ScriptId.Value))))
            .ToList();

    private async Task<IReadOnlyDictionary<Guid, AutomationScriptDefinition>> LoadScriptsForTasksAsync(
        IReadOnlyList<AutomationTaskDefinition> applicable)
    {
        var scriptIds = applicable
            .Where(task => task.ActionType == AutomationTaskActionType.RunScript && task.ScriptId.HasValue)
            .Select(task => task.ScriptId!.Value)
            .Distinct()
            .ToList();
        return scriptIds.Count > 0
            ? await _scriptRepository.GetByIdsAsync(scriptIds, includeInactive: false)
            : new Dictionary<Guid, AutomationScriptDefinition>();
    }

    /// <summary>
    /// Monta os DTOs que o agent recebe (e que a UI de verificação mostra). É o
    /// ponto ÚNICO de mapeamento: policy-sync e prévia usam exatamente a mesma
    /// lista, então o que o operador vê na aba "Políticas" é o que o agent recebe.
    /// </summary>
    private async Task<List<AgentAutomationTaskPolicyDto>> BuildTaskPolicyDtosAsync(
        IReadOnlyList<AutomationTaskDefinition> applicable,
        bool includeScriptContent,
        IReadOnlyDictionary<Guid, AutomationScriptDefinition>? preloadedScripts = null)
    {
        // Reutiliza os scripts já carregados pelo fingerprint quando possível:
        // antes o repositório era consultado duas vezes por policy-sync.
        var scriptsById = preloadedScripts ?? await LoadScriptsForTasksAsync(applicable);

        var taskDtos = new List<AgentAutomationTaskPolicyDto>(applicable.Count);
        foreach (var task in applicable)
        {
            AgentAutomationScriptRefDto? scriptRef = null;
            if (task.ActionType == AutomationTaskActionType.RunScript && task.ScriptId.HasValue)
            {
                scriptsById.TryGetValue(task.ScriptId.Value, out var script);
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
                        Content = includeScriptContent ? script.Content : null
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
                AllowDefer = task.AllowDefer,
                CloseProcesses = ParseTags(task.CloseProcessesJson),
                PromptTimeoutSeconds = NormalizePromptTimeout(task.UserPromptTimeoutSeconds),
                NotificationMode = task.NotificationMode,
                ToastTiming = task.ToastTiming,
                CollectorFieldDefinitionId = task.CollectorFieldDefinitionId,
                WriteOutputToNote = task.WriteOutputToNote,
                ExecutionOrder = task.ExecutionOrder,
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
        return taskDtos;
    }

    public async Task<AgentAutomationPolicyPreviewDto> GetApplicablePoliciesForAgentAsync(
        Guid agentId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var (agent, _, applicableTasks) = await ResolveApplicablePolicyTasksAsync(agentId);
        // A prévia usa o MESMO fingerprint do sync (mesma lista de tarefas
        // entregáveis): para isso precisa dos scripts (versão + hash entram na
        // chave) — aqui sem trafegar conteúdo ao browser.
        var (applicable, scriptsById) = await LoadDeliverablePolicyInputsAsync(applicableTasks);
        var taskDtos = await BuildTaskPolicyDtosAsync(applicable, includeScriptContent: false, scriptsById);
        var fingerprint = ComputeHash(BuildPolicyKey(applicable, scriptsById));

        return new AgentAutomationPolicyPreviewDto
        {
            AgentId = agent.Id,
            PolicyFingerprint = fingerprint,
            LastSyncedPolicyFingerprint = agent.LastPolicyFingerprint,
            LastPolicySyncAt = agent.LastPolicySyncAt,
            UpToDate = agent.LastPolicyFingerprint is not null
                && string.Equals(agent.LastPolicyFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase),
            GeneratedAt = DateTime.UtcNow,
            TaskCount = taskDtos.Count,
            Tasks = taskDtos,
            PreloadPackages = BuildPreloadPackages(applicable)
        };
    }

    /// <summary>
    /// Extrai os packages das tasks de instalação/atualização ativas do escopo
    /// do agent, para pré-carga no cache P2P (fetch proativo sem instalar).
    /// Deduplicado por packageId (mantém a ação mais abrangente: UpdateOrInstall
    /// > Update > Install) e ordenado — o fingerprint da policy NÃO muda por
    /// causa desta lista (precarregar não é um evento de política).
    /// </summary>
    private static IReadOnlyList<AgentPreloadPackageDto> BuildPreloadPackages(IEnumerable<AutomationTaskDefinition> tasks)
    {
        var byPackage = new Dictionary<string, AutomationTaskActionType>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            if (!task.IsActive || string.IsNullOrWhiteSpace(task.PackageId)) continue;
            if (task.ActionType is not (AutomationTaskActionType.InstallPackage
                or AutomationTaskActionType.UpdatePackage
                or AutomationTaskActionType.UpdateOrInstallPackage)) continue;

            var packageId = task.PackageId.Trim();
            if (byPackage.TryGetValue(packageId, out var existing))
            {
                // Mantém a ação mais abrangente para o mesmo pacote.
                byPackage[packageId] = (AutomationTaskActionType)Math.Max((int)existing, (int)task.ActionType);
            }
            else
            {
                byPackage[packageId] = task.ActionType;
            }
        }

        return byPackage
            .Select(kv => new AgentPreloadPackageDto { PackageId = kv.Key, ActionType = kv.Value })
            .OrderBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<string> GetPolicyFingerprintForAgentAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var (_, _, applicableTasks) = await ResolveApplicablePolicyTasksAsync(agentId);
        var (applicable, scriptsById) = await LoadDeliverablePolicyInputsAsync(applicableTasks);
        var policyKey = BuildPolicyKey(applicable, scriptsById);
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

        var globalTasks = await _taskRepository.GetListPageAsync(AppApprovalScopeType.Global, null, activeOnly: true, deletedOnly: false, includeDeleted: false, search: null, clientId: null, siteId: null, agentId: null, scopeTypes: null, actionTypes: null, triggers: null, isActive: null, cursor: null, limit: 1000);
        var clientTasks = await _taskRepository.GetListPageAsync(AppApprovalScopeType.Client, site.ClientId, activeOnly: true, deletedOnly: false, includeDeleted: false, search: null, clientId: null, siteId: null, agentId: null, scopeTypes: null, actionTypes: null, triggers: null, isActive: null, cursor: null, limit: 1000);
        var siteTasks = await _taskRepository.GetListPageAsync(AppApprovalScopeType.Site, site.Id, activeOnly: true, deletedOnly: false, includeDeleted: false, search: null, clientId: null, siteId: null, agentId: null, scopeTypes: null, actionTypes: null, triggers: null, isActive: null, cursor: null, limit: 1000);
        var agentTasks = await _taskRepository.GetListPageAsync(AppApprovalScopeType.Agent, agentId, activeOnly: true, deletedOnly: false, includeDeleted: false, search: null, clientId: null, siteId: null, agentId: null, scopeTypes: null, actionTypes: null, triggers: null, isActive: null, cursor: null, limit: 1000);

        var applicable = globalTasks
            .Concat(clientTasks)
            .Concat(siteTasks)
            .Concat(agentTasks)
            .GroupBy(task => task.Id)
            .Select(group => group.First())
            .Where(task => MatchesTagFilters(task, labelSet))
            // Entrega já na ordem de execução (o agent reordena por garantia).
            .OrderBy(task => task.ExecutionOrder)
            .ThenBy(task => task.Id)
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

    /// <summary>
    /// Avalia se o pacote da tarefa está autorizado: existência no catálogo
    /// (app_packages — a M055 removeu as tabelas legadas winget_packages/
    /// chocolatey_packages) e aprovação na App Store efetiva do escopo. A policy
    /// de automação SOBREPÕE a loja: a tarefa NÃO é bloqueada — o resultado é
    /// persistido (packageAuthorized/packageWarning) e exposto na UI.
    /// Retorna (null, null) para ações que não são de pacote.
    /// </summary>
    internal static async Task<(bool? Authorized, string? Warning)> ResolvePackageAuthorizationAsync(
        IAppStoreService appStoreService,
        AutomationTaskActionType actionType,
        AppInstallationType? installationType,
        string? packageId,
        Guid? clientId,
        Guid? siteId,
        Guid? agentId)
    {
        var isPackageAction = actionType is AutomationTaskActionType.InstallPackage
            or AutomationTaskActionType.UpdatePackage
            or AutomationTaskActionType.RemovePackage
            or AutomationTaskActionType.UpdateOrInstallPackage;
        if (!isPackageAction || installationType is null || string.IsNullOrWhiteSpace(packageId))
            return (null, null);

        var trimmed = packageId.Trim();
        // Winget/Chocolatey são gravados em lowercase no catálogo (app_packages).
        var normalizedId = installationType == AppInstallationType.Custom
            ? trimmed
            : trimmed.ToLowerInvariant();

        // Existência no catálogo: evita salvar id inexistente (ex.:
        // "mozilla.thunderbird.br" em vez de "Mozilla.Thunderbird.pt-BR"),
        // que falharia no agent com exit 13.
        // Winget/Chocolatey vêm de sync: catálogo vazio/indisponível é tratado como
        // "unknown" (não se afirma que o id não existe). Custom é cadastrado um a um,
        // então a existência é sempre verificada.
        bool inCatalog;
        if (installationType == AppInstallationType.Custom)
        {
            inCatalog = await appStoreService.GetCatalogPackageByIdAsync(installationType.Value, normalizedId) is not null;
        }
        else
        {
            var catalogSearch = await appStoreService.SearchCatalogAsync(installationType.Value, null, null, 1, null);
            inCatalog = catalogSearch.TotalPackagesInSource == 0
                || await appStoreService.GetCatalogPackageByIdAsync(installationType.Value, normalizedId) is not null;
        }

        var approved = await appStoreService.IsPackageApprovedAsync(clientId, siteId, agentId, installationType.Value, normalizedId);

        if (!inCatalog)
        {
            // A07: a mensagem citava "Winget" para qualquer tipo de instalação.
            var catalogName = installationType == AppInstallationType.Winget
                ? "Winget"
                : installationType.ToString();
            var example = installationType == AppInstallationType.Winget
                ? " Por exemplo: 'mozilla.thunderbird.br' não é válido; o id correto é 'Mozilla.Thunderbird.pt-BR'."
                : string.Empty;
            return (false,
                $"O PackageId '{trimmed}' não existe no catálogo {catalogName} — a instalação falhará no agent. " +
                $"Verifique o id (a comparação ignora maiúsculas/minúsculas).{example}");
        }

        if (!approved)
        {
            return (false,
                "Pacote não está na App Store; a policy de automação será aplicada e instalará/atualizará " +
                "por cima da loja. Para evitar o bloqueio do agent (exit 13), adicione o pacote à política da App Store.");
        }

        return (true, null);
    }

    /// <summary>
    /// Avalia a autorização do pacote em um ESCOPO/DB CONTEXT PRÓPRIO, fora da
    /// transação do comando.
    ///
    /// Motivo (incidente 2026-10-31, POST /api/v1/automation/tasks → HTTP 500):
    /// esta checagem consultava tabelas legadas dentro da transação aberta pelo
    /// TransactionBehavior. Qualquer erro SQL aqui (42P01 tabela ausente, 42501
    /// permissão negada) abortava a transação e o comando seguinte falhava com
    /// 25P02 — o catch antigo engolia a exceção mas NÃO salvava a transação, então
    /// a tarefa nunca era criada e o usuário via 500.
    ///
    /// Em um escopo separado (nova conexão, sem transação ambiente) a falha fica
    /// isolada: a tarefa é criada com packageAuthorized = null e o motivo real
    /// aparece no log como Warning, em vez de derrubar o comando.
    /// </summary>
    private async Task<(bool? Authorized, string? Warning)> ResolvePackageAuthorizationAsync(
        AutomationTaskActionType actionType,
        AppInstallationType? installationType,
        string? packageId,
        Guid? clientId,
        Guid? siteId,
        Guid? agentId)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var appStoreService = scope.ServiceProvider.GetRequiredService<IAppStoreService>();

            return await ResolvePackageAuthorizationAsync(
                appStoreService, actionType, installationType, packageId, clientId, siteId, agentId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: a checagem não pode impedir a criação/edição da tarefa.
            // Diferente do catch{} anterior, agora o motivo real fica no log.
            _logger.LogWarning(
                ex,
                "Falha ao avaliar autorização do pacote {PackageId} (actionType={ActionType}). " +
                "A tarefa seguirá sem evidência de autorização (packageAuthorized = null).",
                packageId,
                actionType);
            return (null, null);
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

    /// <summary>
    /// O alvo do escopo precisa existir de fato. Sem isso, um `scopeId` inválido
    /// (id digitado errado, cliente/site excluído, id de outra entidade) passava
    /// pela criação e explodia no INSERT como violação de FK (23503) →
    /// `DbUpdateException` → HTTP 500. Aqui a falha vira erro de validação (400)
    /// com mensagem clara.
    /// </summary>
    private async Task ValidateScopeTargetExistsAsync(
        AutomationTaskDefinition task,
        (AppApprovalScopeType ScopeType, Guid? ClientId, Guid? SiteId, Guid? AgentId)? previousScope = null)
    {
        // PUT que não altera o escopo não revalida o alvo: tarefas antigas podem
        // apontar para agente já na lixeira e ainda precisam ser editáveis
        // (desativar, trocar nome, mudar escopo para outro alvo válido).
        if (previousScope is { } previous
            && previous.ScopeType == task.ScopeType
            && previous.ClientId == task.ClientId
            && previous.SiteId == task.SiteId
            && previous.AgentId == task.AgentId)
        {
            return;
        }

        switch (task.ScopeType)
        {
            case AppApprovalScopeType.Client when task.ClientId is Guid clientId:
                if (await _clientRepository.GetByIdAsync(clientId) is null)
                    throw new InvalidOperationException($"Cliente {clientId} não existe (ou foi excluído).");
                break;

            case AppApprovalScopeType.Site when task.SiteId is Guid siteId:
                if (await _siteRepository.GetByIdAsync(siteId) is null)
                    throw new InvalidOperationException($"Site {siteId} não existe (ou foi excluído).");
                break;

            case AppApprovalScopeType.Agent when task.AgentId is Guid agentId:
                if (await _agentRepository.GetByIdAsync(agentId) is null)
                    throw new InvalidOperationException($"Agente {agentId} não existe (ou está na lixeira).");
                break;
        }
    }

    /// <summary>
    /// Tarefa coletora: o campo destino precisa existir, estar ativo, ser de
    /// escopo Agente e permitir gravação pelo agente (allowAgentWrite).
    /// </summary>
    private async Task ValidateCollectorFieldAsync(AutomationTaskDefinition task)
    {
        if (task.CollectorFieldDefinitionId is not Guid collectorId)
            return;

        // Tarefa coletora só faz sentido em execução de script.
        if (task.ActionType != AutomationTaskActionType.RunScript)
            throw new InvalidOperationException("Tarefa coletora só é permitida em tarefas do tipo 'Executar script'.");

        var definition = await _customFieldService.GetDefinitionByIdAsync(collectorId);
        if (definition is null || !definition.IsActive)
            throw new InvalidOperationException("O campo do coletor não existe ou está inativo.");

        if (definition.ScopeType != CustomFieldScopeType.Agent)
            throw new InvalidOperationException("Tarefa coletora só pode gravar em campo customizado de escopo Agente.");

        if (!definition.AllowAgentWrite)
            throw new InvalidOperationException("O campo do coletor precisa permitir gravação pelo agente.");
    }

    /// <summary>
    /// Normaliza a chave da tarefa no mesmo padrão das chaves de campo
    /// customizado: minúsculas, [a-z0-9_-], derivada do nome quando vazia.
    /// </summary>
    private static string? NormalizeTaskKey(string? key, string? name)
    {
        var candidate = string.IsNullOrWhiteSpace(key) ? name ?? string.Empty : key;
        var normalized = candidate.Trim().ToLowerInvariant();

        // Mantém apenas o alfabeto permitido; espaços e acentos viram hífen.
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, "[^a-z0-9_-]+", "-");
        normalized = normalized.Trim('-');

        if (normalized.Length == 0)
            return null;

        // Truncar sem hash fazia nomes longos distintos colidirem na MESMA chave
        // (o operador via "Já existe uma tarefa com esta chave" sem entender o
        // motivo). Acima de 100 chars a chave fica "<92 chars>-<hash8>" — as chaves
        // já gravadas não mudam, só as tarefas novas com nome muito longo.
        if (normalized.Length <= 100)
            return normalized;

        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return $"{normalized[..91]}-{hash[..8].ToLowerInvariant()}";
    }

    /// <summary>Chave padronizada precisa ser única entre as tarefas ATIVAS do MESMO escopo.</summary>
    private async Task ValidateTaskKeyAsync(AutomationTaskDefinition task)
    {
        if (string.IsNullOrWhiteSpace(task.Key))
            return;

        var duplicated = await _taskRepository.ExistsKeyAsync(
            task.Key,
            task.ClientId,
            task.SiteId,
            task.AgentId,
            task.Id == Guid.Empty ? null : task.Id);
        if (duplicated)
            throw new InvalidOperationException("Já existe uma tarefa com esta chave.");
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

        // Mesmo dialeto do agent (robfig/cron padrão: 5 campos, sem segundos).
        if (task.TriggerRecurring && !string.IsNullOrWhiteSpace(task.ScheduleCron)
            && !CronScheduleValidator.IsValid(task.ScheduleCron))
            throw new InvalidOperationException(
                $"ScheduleCron '{task.ScheduleCron}' is not a valid 5-field cron expression (minute hour day-of-month month day-of-week).");

        // Processos a fechar: limita quantidade/tamanho (o Welcome do PSADT recebe a
        // lista por linha de comando; nomes absurdos quebram o cmdlet).
        var closeProcesses = ParseTags(task.CloseProcessesJson);
        if (closeProcesses.Count > MaxCloseProcesses)
            throw new InvalidOperationException($"CloseProcesses must have up to {MaxCloseProcesses} process names.");

        foreach (var process in closeProcesses)
        {
            if (process.Length > MaxCloseProcessNameLength)
                throw new InvalidOperationException(
                    $"CloseProcesses entry '{process[..Math.Min(32, process.Length)]}...' exceeds {MaxCloseProcessNameLength} characters.");

            if (!CloseProcessNameRegex.IsMatch(process))
                throw new InvalidOperationException(
                    $"CloseProcesses entry '{process}' contains invalid characters (use letters, numbers, '.', '_', '-' or space).");
        }
    }

    /// <summary>
    /// Normaliza o timeout do prompt para a faixa suportada pelo Welcome do PSADT.
    /// 0 ou negativo = default de 60s (acao padrao continuar).
    /// </summary>
    public static int NormalizePromptTimeout(int seconds)
    {
        if (seconds <= 0)
            return DefaultPromptTimeoutSeconds;

        return Math.Clamp(seconds, MinPromptTimeoutSeconds, MaxPromptTimeoutSeconds);
    }

    /// <summary>
    /// Fonte de verdade do modo de notificacao no create. Quando o cliente nao
    /// envia o campo (contrato antigo), deriva do booleano RequiresApproval:
    /// true = Prompt (Welcome), false = Silent.
    /// </summary>
    public static AutomationNotificationMode ResolveNotificationMode(CreateAutomationTaskRequest request)
        => request.NotificationMode
           ?? (request.RequiresApproval ? AutomationNotificationMode.Prompt : AutomationNotificationMode.Silent);

    private static readonly System.Text.RegularExpressions.Regex CloseProcessNameRegex =
        new(@"^[A-Za-z0-9._ -]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

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

    /// <summary>
    /// Log de auditoria em banco é BEST-EFFORT. Essas chamadas acontecem DENTRO da
    /// transação do comando: uma falha na gravação do log (ex.: constraint nova na
    /// tabela logs) abortaria a transação e derrubaria a criação/edição da tarefa
    /// com 25P02 → HTTP 500. Aqui a falha só vira Warning no log do processo.
    /// </summary>
    private async Task LogInfoBestEffortAsync(
        LogType type,
        LogSource source,
        string message,
        object? data = null,
        string? clientId = null,
        string? siteId = null,
        string? agentId = null)
    {
        try
        {
            // Ordem posicional do contrato: (type, source, message, data, agentId, siteId, clientId).
            await _loggingService.LogInfoAsync(type, source, message, data, agentId, siteId, clientId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Falha ao gravar log de auditoria (best-effort): {Message}", message);
        }
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

    /// <summary>
    /// Chave semântica da policy: inclui todos os campos que afetam a execução no agent
    /// (triggers, cron, tags, payload, approval). LastUpdatedAt fica como garantia extra —
    /// qualquer update bumpa o timestamp, mas o fingerprint não deve DEPENDER disso.
    /// </summary>
    private static string BuildPolicyKey(
        IReadOnlyList<AutomationTaskDefinition> tasks,
        IReadOnlyDictionary<Guid, AutomationScriptDefinition>? scriptsById = null)
    {
        var builder = new StringBuilder();
        foreach (var task in tasks.OrderBy(task => task.Id))
        {
            builder
                .Append(task.Id).Append('|')
                .Append(task.LastUpdatedAt.ToUniversalTime().Ticks).Append('|')
                .Append(task.ActionType).Append('|')
                .Append(task.InstallationType).Append('|')
                .Append(task.ScriptId?.ToString() ?? string.Empty).Append('|')
                .Append(task.PackageId ?? string.Empty).Append('|')
                .Append(task.CommandPayload ?? string.Empty).Append('|')
                .Append(task.ScopeType).Append('|')
                .Append(task.ClientId?.ToString() ?? string.Empty).Append('|')
                .Append(task.SiteId?.ToString() ?? string.Empty).Append('|')
                .Append(task.AgentId?.ToString() ?? string.Empty).Append('|')
                .Append(task.IncludeTagsJson ?? string.Empty).Append('|')
                .Append(task.ExcludeTagsJson ?? string.Empty).Append('|')
                .Append(task.TriggerImmediate ? '1' : '0')
                .Append(task.TriggerRecurring ? '1' : '0')
                .Append(task.TriggerOnUserLogin ? '1' : '0')
                .Append(task.TriggerOnAgentCheckIn ? '1' : '0').Append('|')
                .Append(task.ScheduleCron ?? string.Empty).Append('|')
                // Separadores explícitos: sem eles "timeout=601,ativo=1" e
                // "timeout=60,ativo=11" colidiriam na chave.
                .Append(task.RequiresApproval ? '1' : '0').Append('|')
                .Append(task.AllowDefer ? '1' : '0').Append('|')
                .Append(task.CloseProcessesJson ?? string.Empty).Append('|')
                .Append(NormalizePromptTimeout(task.UserPromptTimeoutSeconds)).Append('|')
                .Append((int)task.NotificationMode).Append('|')
                .Append((int)task.ToastTiming).Append('|')
                // Ordem entra no fingerprint: mudar a ordem precisa re-sincronizar
                // os agents para que a fila sequencial use o valor novo.
                .Append(task.ExecutionOrder).Append('|')
                .Append(task.IsActive ? '1' : '0');
            // Conteúdo do script entra no fingerprint: sem isso, editar o conteúdo
            // de um script não alterava a policy entregue — os agentes continuavam
            // com conteúdo antigo (ou sem conteúdo algum) até uma mudança de task.
            if (scriptsById is not null
                && task.ActionType == AutomationTaskActionType.RunScript
                && task.ScriptId.HasValue
                && scriptsById.TryGetValue(task.ScriptId.Value, out var script))
            {
                builder
                    .Append('|').Append(script.Version)
                    .Append('|').Append(script.LastUpdatedAt.ToUniversalTime().Ticks)
                    .Append('|').Append(ComputeHash(script.Content));
            }
            builder.Append(';');
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

    /// <summary>
    /// Resolve os nomes de client/site/agent para exibição nos DTOs de tarefa.
    ///
    /// Faz o carregamento em 3 passos (agentes → sites → clientes) para que tarefas
    /// de escopo Agent tragam SiteName/ScopeSiteId/ScopeClientId SEM await dentro do
    /// loop (o N+1 anterior consultava site/cliente por item).
    /// </summary>
    private async Task EnrichScopeNamesAsync(IReadOnlyList<AutomationTaskSummaryDto> dtos)
    {
        if (dtos.Count == 0) return;

        var clientIds = new HashSet<Guid>();
        var siteIds = new HashSet<Guid>();
        var agentIds = new HashSet<Guid>();

        foreach (var dto in dtos)
        {
            if (dto.ScopeId is not { } scopeId) continue;
            switch (dto.ScopeType)
            {
                case AppApprovalScopeType.Client: clientIds.Add(scopeId); break;
                case AppApprovalScopeType.Site: siteIds.Add(scopeId); break;
                case AppApprovalScopeType.Agent: agentIds.Add(scopeId); break;
            }
        }

        var agents = agentIds.Count == 0
            ? new Dictionary<Guid, Agent>()
            : (await Task.WhenAll(agentIds.Select(async id => (id, agent: await _agentRepository.GetByIdAsync(id)))))
                .Where(x => x.agent is not null).ToDictionary(x => x.id, x => x.agent!);

        // Escopo Agent também precisa do site do agent (e do cliente do site).
        foreach (var agent in agents.Values)
            siteIds.Add(agent.SiteId);

        var sites = siteIds.Count == 0
            ? new Dictionary<Guid, Site>()
            : (await Task.WhenAll(siteIds.Select(async id => (id, site: await _siteRepository.GetByIdAsync(id)))))
                .Where(x => x.site is not null).ToDictionary(x => x.id, x => x.site!);

        foreach (var site in sites.Values)
            clientIds.Add(site.ClientId);

        var clients = clientIds.Count == 0
            ? new Dictionary<Guid, string>()
            : (await Task.WhenAll(clientIds.Select(async id => (id, client: await _clientRepository.GetByIdAsync(id)))))
                .Where(x => x.client is not null).ToDictionary(x => x.id, x => x.client!.Name);

        foreach (var dto in dtos)
        {
            if (dto.ScopeId is not { } scopeId) continue;
            switch (dto.ScopeType)
            {
                case AppApprovalScopeType.Client:
                    dto.ScopeName = clients.GetValueOrDefault(scopeId);
                    dto.ScopeClientId = scopeId;
                    dto.ClientName = dto.ScopeName;
                    break;

                case AppApprovalScopeType.Site:
                    if (sites.TryGetValue(scopeId, out var site))
                    {
                        dto.ScopeName = site.Name;
                        dto.SiteName = site.Name;
                        dto.ScopeSiteId = site.Id;
                        dto.ScopeClientId = site.ClientId;
                        dto.ClientName = clients.GetValueOrDefault(site.ClientId);
                    }
                    break;

                case AppApprovalScopeType.Agent:
                    if (agents.TryGetValue(scopeId, out var agent))
                    {
                        dto.ScopeName = agent.DisplayName ?? agent.Hostname;
                        dto.AgentName = agent.DisplayName ?? agent.Hostname;
                        if (sites.TryGetValue(agent.SiteId, out var agentSite))
                        {
                            dto.ScopeSiteId = agentSite.Id;
                            dto.ScopeClientId = agentSite.ClientId;
                            dto.SiteName = agentSite.Name;
                            dto.ClientName = clients.GetValueOrDefault(agentSite.ClientId);
                        }
                    }
                    break;
            }
        }
    }

    private static AutomationTaskSummaryDto ToSummaryDto(AutomationTaskDefinition task)
    {
        return new AutomationTaskSummaryDto
        {
            Id = task.Id,
            Name = task.Name,
            Description = task.Description,
            ActionType = task.ActionType,
            InstallationType = task.InstallationType,
            PackageId = task.PackageId,
            ScopeType = task.ScopeType,
            ScopeId = ResolveScopeId(task),
            IsActive = task.IsActive,
            RequiresApproval = task.RequiresApproval,
            AllowDefer = task.AllowDefer,
            CloseProcesses = ParseTags(task.CloseProcessesJson),
            PromptTimeoutSeconds = NormalizePromptTimeout(task.UserPromptTimeoutSeconds),
            NotificationMode = task.NotificationMode,
            ToastTiming = task.ToastTiming,
            CollectorFieldDefinitionId = task.CollectorFieldDefinitionId,
            WriteOutputToNote = task.WriteOutputToNote,
            ExecutionOrder = task.ExecutionOrder,
            Key = task.Key,
            PackageAuthorized = task.PackageAuthorized,
            PackageWarning = task.PackageWarning,
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
            AllowDefer = task.AllowDefer,
            CloseProcesses = ParseTags(task.CloseProcessesJson),
            PromptTimeoutSeconds = NormalizePromptTimeout(task.UserPromptTimeoutSeconds),
            NotificationMode = task.NotificationMode,
            ToastTiming = task.ToastTiming,
            CollectorFieldDefinitionId = task.CollectorFieldDefinitionId,
            WriteOutputToNote = task.WriteOutputToNote,
            ExecutionOrder = task.ExecutionOrder,
            Key = task.Key,
            PackageAuthorized = task.PackageAuthorized,
            PackageWarning = task.PackageWarning,
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
            task.NotificationMode,
            task.ToastTiming,
            task.CollectorFieldDefinitionId,
            task.WriteOutputToNote,
            task.ExecutionOrder,
            task.Key,
            task.AllowDefer,
            task.PackageAuthorized,
            task.PackageWarning,
            CloseProcesses = ParseTags(task.CloseProcessesJson),
            task.UserPromptTimeoutSeconds,
            task.IsActive,
            task.DeletedAt,
            task.LastUpdatedAt,
            task.CreatedAt,
            task.UpdatedAt
        };
    }
}
