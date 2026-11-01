using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Resolve a intenção de atualização por pacote no escopo consultado, sem
/// executar nada. Regras (mesma ordem que a execução deverá seguir):
///
/// 1. task de automação ativa que atualiza o pacote (UpdatePackage /
///    UpdateOrInstallPackage) é DONA da atualização — a automação tem
///    precedência sobre a Loja (ver app.authorizeAutomationPackage no agent);
/// 2. sem task, `autoUpdateEnabled` efetivo liga o auto-update da Loja;
/// 3. caso contrário é manual (operador usa a aba Atualizações).
///
/// Conflito é sinalizado quando a Loja está com auto-update ligado E existe
/// task de automação para o mesmo pacote: os dois motores agiriam sobre ele.
/// </summary>
public class AppStoreUpdateIntentService : IAppStoreUpdateIntentService
{
    private const int MaxAutomationTasksPerScope = 500;

    private static readonly AutomationTaskActionType[] UpdateActionTypes =
    [
        AutomationTaskActionType.UpdatePackage,
        AutomationTaskActionType.UpdateOrInstallPackage
    ];

    private readonly IAppStoreService _appStoreService;
    private readonly IAutomationTaskRepository _automationTasks;
    private readonly IConfigurationResolver _configurationResolver;
    private readonly ISiteRepository? _siteRepository;
    private readonly IAgentRepository? _agentRepository;

    public AppStoreUpdateIntentService(
        IAppStoreService appStoreService,
        IAutomationTaskRepository automationTasks,
        IConfigurationResolver configurationResolver,
        ISiteRepository? siteRepository = null,
        IAgentRepository? agentRepository = null)
    {
        _appStoreService = appStoreService;
        _automationTasks = automationTasks;
        _configurationResolver = configurationResolver;
        _siteRepository = siteRepository;
        _agentRepository = agentRepository;
    }

    public async Task<AppStoreUpdateIntentPageDto> GetUpdateIntentAsync(
        AppApprovalScopeType scopeType,
        Guid? scopeId,
        AppInstallationType installationType,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        // Regras efetivas do escopo (Allow/Deny já resolvidos com herança).
        var resolved = await _appStoreService.GetResolvedRulesAsync(
            scopeType, scopeId, installationType, search, cancellationToken);

        var chain = await ResolveScopeChainAsync(scopeType, scopeId, cancellationToken);
        var scopedTasks = await LoadUpdateTasksAsync(chain, cancellationToken);
        var maintenanceWindow = await LoadMaintenanceWindowAsync(cancellationToken);

        var items = new List<AppStoreUpdateIntentItemDto>();
        foreach (var rule in resolved.Items)
        {
            // Só pacotes permitidos têm intenção de atualização: negado nunca atualiza.
            if (rule.Action != AppApprovalActionType.Allow)
                continue;

            var task = PickAutomationTask(scopedTasks, rule.PackageId, installationType);
            var conflict = task is not null && rule.AutoUpdateEnabled;

            items.Add(new AppStoreUpdateIntentItemDto
            {
                PackageId = rule.PackageId,
                PackageName = rule.PackageName,
                Action = rule.Action,
                AutoUpdateEnabled = rule.AutoUpdateEnabled,
                Owner = task is not null
                    ? AppUpdateOwner.Automation
                    : rule.AutoUpdateEnabled
                        ? AppUpdateOwner.AutoUpdate
                        : AppUpdateOwner.Manual,
                AutomationTaskId = task?.Task.Id,
                AutomationTaskName = task?.Task.Name,
                AutomationTriggerSummary = task is null ? null : DescribeTriggers(task.Task),
                Conflict = conflict,
                ConflictReason = conflict
                    ? $"Auto-atualização ligada, mas a automação \"{task!.Task.Name}\" já atualiza este pacote."
                    : null
            });
        }

        // Consistência: o mesmo pacote não aparece duas vezes (a regra resolvida já
        // é uma linha por pacote, mas o catálogo pode ter ids com caixa diferente).
        var deduped = items
            .GroupBy(item => item.PackageId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AppStoreUpdateIntentPageDto
        {
            ScopeType = scopeType,
            ScopeId = scopeId,
            InstallationType = installationType,
            Search = resolved.Search,
            Policy = resolved.Policy,
            MaintenanceWindow = maintenanceWindow,
            Count = deduped.Count,
            Items = deduped
        };
    }

    // ── Cadeia de escopo ────────────────────────────────────────────────

    private sealed record ScopeChain(Guid? ClientId, Guid? SiteId, Guid? AgentId);

    private async Task<ScopeChain> ResolveScopeChainAsync(
        AppApprovalScopeType scopeType,
        Guid? scopeId,
        CancellationToken cancellationToken)
    {
        switch (scopeType)
        {
            case AppApprovalScopeType.Client:
                return new ScopeChain(scopeId, null, null);

            case AppApprovalScopeType.Site:
            {
                var site = scopeId is null || _siteRepository is null
                    ? null
                    : await _siteRepository.GetByIdAsync(scopeId.Value);
                return new ScopeChain(site?.ClientId, scopeId, null);
            }

            case AppApprovalScopeType.Agent:
            {
                var agent = scopeId is null || _agentRepository is null
                    ? null
                    : await _agentRepository.GetByIdAsync(scopeId.Value);
                if (agent is null)
                    return new ScopeChain(null, null, scopeId);

                var site = _siteRepository is null
                    ? null
                    : await _siteRepository.GetByIdAsync(agent.SiteId);
                return new ScopeChain(site?.ClientId, agent.SiteId, scopeId);
            }

            default:
                return new ScopeChain(null, null, null);
        }
    }

    private static IReadOnlyList<AppApprovalScopeType> ApplicableScopes(AppApprovalScopeType scopeType) =>
        scopeType switch
        {
            AppApprovalScopeType.Global => [AppApprovalScopeType.Global],
            AppApprovalScopeType.Client => [AppApprovalScopeType.Global, AppApprovalScopeType.Client],
            AppApprovalScopeType.Site => [AppApprovalScopeType.Global, AppApprovalScopeType.Client, AppApprovalScopeType.Site],
            _ => [AppApprovalScopeType.Global, AppApprovalScopeType.Client, AppApprovalScopeType.Site, AppApprovalScopeType.Agent]
        };

    private sealed record ScopedTask(AutomationTaskDefinition Task, int ScopeRank);

    private async Task<IReadOnlyList<ScopedTask>> LoadUpdateTasksAsync(
        ScopeChain chain,
        CancellationToken cancellationToken)
    {
        var loaded = new List<ScopedTask>();

        foreach (var scope in ApplicableScopes(chain.AgentId is not null
                     ? AppApprovalScopeType.Agent
                     : chain.SiteId is not null
                         ? AppApprovalScopeType.Site
                         : chain.ClientId is not null
                             ? AppApprovalScopeType.Client
                             : AppApprovalScopeType.Global))
        {
            var scopeId = scope switch
            {
                AppApprovalScopeType.Client => chain.ClientId,
                AppApprovalScopeType.Site => chain.SiteId,
                AppApprovalScopeType.Agent => chain.AgentId,
                _ => null
            };

            // Escopos intermediários nulos não têm tasks próprias para carregar.
            if (scope != AppApprovalScopeType.Global && scopeId is null)
                continue;

            var tasks = await _automationTasks.GetListPageAsync(
                scopeType: scope,
                scopeId: scopeId,
                activeOnly: true,
                deletedOnly: false,
                includeDeleted: false,
                search: null,
                clientId: null,
                siteId: null,
                agentId: null,
                scopeTypes: null,
                actionTypes: UpdateActionTypes,
                triggers: null,
                isActive: true,
                cursor: null,
                limit: MaxAutomationTasksPerScope);

            foreach (var task in tasks)
            {
                if (task.DeletedAt is not null)
                    continue;

                loaded.Add(new ScopedTask(task, (int)scope));
            }
        }

        return loaded;
    }

    private static ScopedTask? PickAutomationTask(
        IReadOnlyList<ScopedTask> tasks,
        string packageId,
        AppInstallationType installationType)
    {
        if (string.IsNullOrWhiteSpace(packageId))
            return null;

        return tasks
            .Where(entry => string.Equals(entry.Task.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
            // Task sem InstallationType herda Winget (mesmo default do executor).
            .Where(entry => (entry.Task.InstallationType ?? AppInstallationType.Winget) == installationType)
            .OrderByDescending(entry => entry.ScopeRank)
            .ThenByDescending(entry => entry.Task.UpdatedAt)
            .FirstOrDefault();
    }

    private static string DescribeTriggers(AutomationTaskDefinition task)
    {
        var parts = new List<string>();
        if (task.TriggerRecurring)
        {
            parts.Add(string.IsNullOrWhiteSpace(task.ScheduleCron)
                ? "Recorrente"
                : $"Recorrente (cron: {task.ScheduleCron.Trim()})");
        }
        if (task.TriggerImmediate)
            parts.Add("Imediata");
        if (task.TriggerOnUserLogin)
            parts.Add("No login do usuário");
        if (task.TriggerOnAgentCheckIn)
            parts.Add("No check-in do agente");

        return parts.Count > 0 ? string.Join(" + ", parts) : "Manual";
    }

    // ── Janela de manutenção ────────────────────────────────────────────

    private async Task<AppMaintenanceWindowInfoDto> LoadMaintenanceWindowAsync(CancellationToken cancellationToken)
    {
        var info = new AppMaintenanceWindowInfoDto { UsesUtc = true };
        if (_configurationResolver is null)
            return info;

        ServerConfiguration? server;
        try
        {
            server = await _configurationResolver.GetServerAsync();
        }
        catch (Exception)
        {
            // Resolver indisponível não deve derrubar a simulação.
            return info;
        }

        var json = server?.MaintenanceWindowJson;
        if (string.IsNullOrWhiteSpace(json))
            return info;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return info;

            var timeZone = GetString(root, "timeZone") ?? "UTC";
            var startTime = GetString(root, "startTime") ?? GetString(root, "startUtc") ?? string.Empty;

            info.Enabled = root.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True;
            info.TimeZone = string.IsNullOrWhiteSpace(timeZone) ? "UTC" : timeZone;
            info.StartTime = startTime;
            info.StartUtc = GetString(root, "startUtc") ?? startTime;
            info.DurationMinutes = GetInt(root, "durationMinutes") ?? 60;
            info.RebootGraceSeconds = GetInt(root, "rebootGraceSeconds") ?? 60;
            info.UsesUtc = string.IsNullOrWhiteSpace(timeZone)
                || string.Equals(info.TimeZone, "UTC", StringComparison.OrdinalIgnoreCase);

            if (root.TryGetProperty("daysOfWeek", out var days) && days.ValueKind == JsonValueKind.Array)
            {
                var list = new List<int>();
                foreach (var day in days.EnumerateArray())
                {
                    if (day.ValueKind == JsonValueKind.Number && day.TryGetInt32(out var value) && value is >= 1 and <= 7)
                        list.Add(value);
                }
                info.DaysOfWeek = list;
            }
        }
        catch (JsonException)
        {
            // JSON inválido no servidor: devolve defaults (a UI mostra como desligada).
        }

        return info;
    }

    private static string? GetString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        return value.GetString();
    }

    private static int? GetInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
            return null;
        return value.TryGetInt32(out var number) ? number : null;
    }
}