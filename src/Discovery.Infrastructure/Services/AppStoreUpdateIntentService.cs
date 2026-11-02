using System.Globalization;
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
/// Política efetiva (resolved.Policy):
///  - Disabled: a Loja bloqueia tudo — a lista de itens é vazia;
///  - All: a lista vem do catálogo efetivo (GetEffectiveAppsAsync), não das
///    regras de aprovação; itens sem regra explícita têm autoUpdate=false;
///  - PreApproved (ou não resolvida): a lista vem das regras resolvidas.
///
/// Conflito é sinalizado quando dois motores agiriam sobre o pacote
/// (auto-update da Loja + task de automação) ou quando a task escolhida usa
/// filtros de tags (risco de furo: nem todo agente do escopo é coberto).
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
    private readonly IClientRepository? _clientRepository;
    private readonly IConfigurationService? _configurationService;

    public AppStoreUpdateIntentService(
        IAppStoreService appStoreService,
        IAutomationTaskRepository automationTasks,
        IConfigurationResolver configurationResolver,
        ISiteRepository? siteRepository = null,
        IAgentRepository? agentRepository = null,
        IClientRepository? clientRepository = null,
        IConfigurationService? configurationService = null)
    {
        _appStoreService = appStoreService;
        _automationTasks = automationTasks;
        _configurationResolver = configurationResolver;
        _siteRepository = siteRepository;
        _agentRepository = agentRepository;
        _clientRepository = clientRepository;
        _configurationService = configurationService;
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
        var maintenanceWindow = await LoadMaintenanceWindowAsync(chain, cancellationToken);

        var page = new AppStoreUpdateIntentPageDto
        {
            ScopeType = scopeType,
            ScopeId = scopeId,
            InstallationType = installationType,
            Search = resolved.Search,
            Policy = resolved.Policy,
            MaintenanceWindow = maintenanceWindow
        };

        // Política Disabled: a Loja bloqueia tudo — ninguém atualiza por catálogo.
        if (resolved.Policy == AppStorePolicyType.Disabled)
        {
            page.Count = 0;
            page.Items = [];
            return page;
        }

        var (scopedTasks, truncated) = await LoadUpdateTasksAsync(chain, cancellationToken);
        page.Truncated = truncated;

        // Política All: a base é o catálogo efetivo do escopo (o agent instala
        // tudo aprovado), não as regras de aprovação — elas só informam campos
        // explícitos (autoUpdate) para pacotes que as têm.
        var items = resolved.Policy == AppStorePolicyType.All
            ? await BuildItemsFromEffectiveAppsAsync(chain, installationType, resolved, scopedTasks, cancellationToken)
            : BuildItemsFromResolvedRulesAsync(resolved, scopedTasks, installationType);

        // Consistência: o mesmo pacote não aparece duas vezes (a regra resolvida já
        // é uma linha por pacote, mas o catálogo pode ter ids com caixa diferente).
        var deduped = items
            .GroupBy(item => item.PackageId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        page.Count = deduped.Count;
        page.Items = deduped;
        return page;
    }

    // ── Itens: regras resolvidas (PreApproved / não resolvida) ─────────

    private static List<AppStoreUpdateIntentItemDto> BuildItemsFromResolvedRulesAsync(
        AppApprovalResolvedPageDto resolved,
        IReadOnlyList<ScopedTask> scopedTasks,
        AppInstallationType installationType)
    {
        var items = new List<AppStoreUpdateIntentItemDto>();
        foreach (var rule in resolved.Items)
        {
            // Só pacotes permitidos têm intenção de atualização: negado nunca atualiza.
            if (rule.Action != AppApprovalActionType.Allow)
                continue;

            items.Add(BuildItem(rule.PackageId, rule.PackageName, rule.Action, rule.AutoUpdateEnabled, scopedTasks, installationType));
        }

        return items;
    }

    // ── Itens: catálogo efetivo (política All) ─────────────────────────

    private async Task<List<AppStoreUpdateIntentItemDto>> BuildItemsFromEffectiveAppsAsync(
        ScopeChain chain,
        AppInstallationType installationType,
        AppApprovalResolvedPageDto resolved,
        IReadOnlyList<ScopedTask> scopedTasks,
        CancellationToken cancellationToken)
    {
        var apps = await _appStoreService.GetEffectiveAppsAsync(
            chain.ClientId, chain.SiteId, chain.AgentId, installationType, cancellationToken);

        // Regras explícitas do escopo continuam informando autoUpdate quando existem.
        var ruleByPackage = resolved.Items
            .GroupBy(rule => rule.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var search = resolved.Search?.Trim();
        var items = new List<AppStoreUpdateIntentItemDto>();
        foreach (var app in apps)
        {
            if (search is { Length: > 0 } &&
                !app.PackageId.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !app.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ruleByPackage.TryGetValue(app.PackageId, out var rule);
            items.Add(BuildItem(
                app.PackageId,
                string.IsNullOrWhiteSpace(app.Name) ? app.PackageId : app.Name,
                rule?.Action ?? AppApprovalActionType.Allow,
                // Item sem regra explícita NÃO tem auto-atualização da Loja.
                rule?.AutoUpdateEnabled ?? false,
                scopedTasks,
                installationType));
        }

        return items;
    }

    private static AppStoreUpdateIntentItemDto BuildItem(
        string packageId,
        string? packageName,
        AppApprovalActionType action,
        bool autoUpdateEnabled,
        IReadOnlyList<ScopedTask> scopedTasks,
        AppInstallationType installationType)
    {
        var task = PickAutomationTask(scopedTasks, packageId, installationType);

        var reasons = new List<string>();
        if (task is not null && UsesTagFilters(task.Task))
        {
            reasons.Add($"A task \"{task.Task.Name}\" usa filtros de tags — nem todos os agentes do escopo serão cobertos.");
        }
        if (task is not null && autoUpdateEnabled)
        {
            reasons.Add($"Auto-atualização ligada, mas a automação \"{task.Task.Name}\" já atualiza este pacote.");
        }

        return new AppStoreUpdateIntentItemDto
        {
            PackageId = packageId,
            PackageName = packageName,
            Action = action,
            AutoUpdateEnabled = autoUpdateEnabled,
            Owner = task is not null
                ? AppUpdateOwner.Automation
                : autoUpdateEnabled
                    ? AppUpdateOwner.AutoUpdate
                    : AppUpdateOwner.Manual,
            AutomationTaskId = task?.Task.Id,
            AutomationTaskName = task?.Task.Name,
            AutomationTriggerSummary = task is null ? null : DescribeTriggers(task.Task),
            Conflict = reasons.Count > 0,
            ConflictReason = reasons.Count == 0 ? null : string.Join(" ", reasons)
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

    private async Task<(IReadOnlyList<ScopedTask> Tasks, bool Truncated)> LoadUpdateTasksAsync(
        ScopeChain chain,
        CancellationToken cancellationToken)
    {
        var truncated = false;
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

            // Bateu o limite por escopo: provavelmente há mais tasks do que as
            // exibidas — sinaliza Truncated em vez de truncar em silêncio.
            if (tasks.Count >= MaxAutomationTasksPerScope)
                truncated = true;

            foreach (var task in tasks)
            {
                if (task.DeletedAt is not null)
                    continue;

                loaded.Add(new ScopedTask(task, (int)scope));
            }
        }

        return (loaded, truncated);
    }

    /// <summary>
    /// Task com filtros de tags não cobre todo o escopo: sinaliza conflito
    /// (risco de furo na atualização). Array vazio "[]" não conta como filtro.
    /// </summary>
    private static bool UsesTagFilters(AutomationTaskDefinition task)
    {
        foreach (var json in new[] { task.IncludeTagsJson, task.ExcludeTagsJson })
        {
            if (string.IsNullOrWhiteSpace(json))
                continue;
            if (json.Trim().Equals("[]", StringComparison.Ordinal))
                continue;
            return true;
        }

        return false;
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

    private async Task<AppMaintenanceWindowInfoDto> LoadMaintenanceWindowAsync(
        ScopeChain chain,
        CancellationToken cancellationToken)
    {
        var info = new AppMaintenanceWindowInfoDto { UsesUtc = true };
        if (_configurationResolver is null)
            return info;

        try
        {
            // Mesma cascata crua que o agent recebe
            // (site ?? client ?? server — string.Empty num nível de cima BLOQUEIA
            // o nível de baixo, pois não é null). Null no nível = herda.
            var serverJson = (await _configurationResolver.GetServerAsync())?.MaintenanceWindowJson;
            string? clientJson = null;
            string? siteJson = null;
            if (_configurationService is not null && chain.ClientId is not null)
                clientJson = (await _configurationService.GetClientConfigAsync(chain.ClientId.Value))?.MaintenanceWindowJson;
            if (_configurationService is not null && chain.SiteId is not null)
                siteJson = (await _configurationService.GetSiteConfigAsync(chain.SiteId.Value))?.MaintenanceWindowJson;

            var json = siteJson ?? clientJson ?? serverJson;
            if (string.IsNullOrWhiteSpace(json))
                return info;

            FillFromJson(info, json);
            return info;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Resolver indisponível não deve derrubar a simulação. OCE propaga.
            return info;
        }
    }

    private static void FillFromJson(AppMaintenanceWindowInfoDto info, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return;

        var timeZone = GetString(root, "timeZone");
        var startTime = GetString(root, "startTime");
        var startUtc = GetString(root, "startUtc");
        var durationMinutes = GetInt(root, "durationMinutes") ?? 60;
        var rebootGraceSeconds = GetInt(root, "rebootGraceSeconds") ?? 60;

        var days = new List<int>();
        if (GetProperty(root, "daysOfWeek") is { ValueKind: JsonValueKind.Array } daysElement)
        {
            foreach (var day in daysElement.EnumerateArray())
            {
                if (day.ValueKind == JsonValueKind.Number && day.TryGetInt32(out var value) && value is >= 1 and <= 7)
                    days.Add(value);
            }
        }

        info.Enabled = GetProperty(root, "enabled") is { ValueKind: JsonValueKind.True };
        info.DurationMinutes = durationMinutes;
        info.RebootGraceSeconds = rebootGraceSeconds;
        info.DaysOfWeek = days;

        // Modo local: startTime (HH:mm) + timeZone IANA válida e não-UTC.
        // Modo legado: startUtc ("HH:mm") interpretado como UTC.
        var tz = ResolveTimeZone(timeZone);
        var validLocalTz = !string.IsNullOrWhiteSpace(timeZone)
            && tz is not null
            && !string.Equals(tz!.Id, TimeZoneInfo.Utc.Id, StringComparison.OrdinalIgnoreCase);

        if (validLocalTz && TryParseTime(startTime, out var localStart))
        {
            var tzInfo = tz!;
            info.TimeZone = tzInfo.Id;
            info.StartTime = startTime!;
            info.UsesUtc = false;

            var occurrence = ComputeOccurrences(
                localStart, tzInfo, days, TimeSpan.FromMinutes(durationMinutes), DateTime.UtcNow);
            FillOccurrence(info, occurrence);
            return;
        }

        // Legado (ou timeZone ausente/inválida/UTC): hora em UTC. Quando o JSON
        // só tem startTime + timeZone (inválida), startTime assume o papel de UTC
        // para que IsOpenNow continue coerente com a interpretação UTC.
        var legacyTime = !string.IsNullOrWhiteSpace(startUtc) ? startUtc : startTime;
        info.TimeZone = "UTC";
        info.StartTime = string.IsNullOrWhiteSpace(startTime) ? legacyTime ?? string.Empty : startTime!;
        info.UsesUtc = true;

        if (TryParseTime(legacyTime, out var utcStart))
        {
            var occurrence = ComputeOccurrences(
                utcStart, TimeZoneInfo.Utc, days, TimeSpan.FromMinutes(durationMinutes), DateTime.UtcNow);
            FillOccurrence(info, occurrence);
        }
    }

    private static void FillOccurrence(
        AppMaintenanceWindowInfoDto info,
        (DateTime? StartUtc, DateTime? EndUtc, bool IsOpenNow, DateTime? NextStartUtc) occurrence)
    {
        info.StartUtc = occurrence.StartUtc is null
            ? string.Empty
            : occurrence.StartUtc.Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        info.EndUtc = occurrence.EndUtc is null
            ? string.Empty
            : occurrence.EndUtc.Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        info.IsOpenNow = occurrence.IsOpenNow;
        info.NextStartUtc = occurrence.NextStartUtc;
    }

    /// <summary>
    /// Ocorrência vigente (contendo utcNow) e a próxima abertura. A janela é do
    /// dia em que ABRIU (pode cruzar a meia-noite); fim = abertura + duração
    /// (RebootGraceSeconds é tolerância pós-janela, não faz parte da abertura).
    /// </summary>
    private static (DateTime? StartUtc, DateTime? EndUtc, bool IsOpenNow, DateTime? NextStartUtc) ComputeOccurrences(
        TimeOnly start,
        TimeZoneInfo timeZone,
        IReadOnlyList<int> days,
        TimeSpan duration,
        DateTime utcNow)
    {
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZone);

        // Ocorrência vigente: pode ter aberto hoje ou ontem (janela que cruza
        // a meia-noite).
        foreach (var dayOffset in new[] { 0, -1 })
        {
            var localDate = localNow.Date.AddDays(dayOffset);
            if (!DayMatches(localDate, days))
                continue;

            var startUtc = LocalToUtc(localDate + start.ToTimeSpan(), timeZone);
            var endUtc = startUtc + duration;
            if (startUtc <= utcNow && utcNow < endUtc)
            {
                var next = FindNextStart(start, timeZone, days, utcNow);
                return (startUtc, endUtc, true, next);
            }
        }

        // Sem janela aberta: StartUtc/EndUtc passam a representar a PRÓXIMA
        // janela (IsOpenNow=false diz qual é o caso). A UI mostra "próxima
        // janela abre em …" e nunca recebe um StartUtc vazio quando existe
        // próxima ocorrência válida.
        var nextStart = FindNextStart(start, timeZone, days, utcNow);
        return nextStart is null
            ? (null, null, false, null)
            : (nextStart, nextStart.Value + duration, false, nextStart);
    }

    private static DateTime? FindNextStart(
        TimeOnly start,
        TimeZoneInfo timeZone,
        IReadOnlyList<int> days,
        DateTime utcNow)
    {
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZone);
        for (var offset = 0; offset <= 8; offset++)
        {
            var localDate = localNow.Date.AddDays(offset);
            if (!DayMatches(localDate, days))
                continue;

            var startUtc = LocalToUtc(localDate + start.ToTimeSpan(), timeZone);
            if (startUtc > utcNow)
                return startUtc;
        }

        return null;
    }

    private static bool DayMatches(DateTime localDate, IReadOnlyList<int> days)
    {
        if (days.Count == 0)
            return true;

        var isoDay = localDate.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)localDate.DayOfWeek;
        return days.Contains(isoDay);
    }

    private static DateTime LocalToUtc(DateTime local, TimeZoneInfo timeZone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        // Gap de DST: o horário local não existe. Avança até existir — a janela
        // abre no primeiro instante válido, nunca "nunca".
        for (var i = 0; i < 4 && timeZone.IsInvalidTime(unspecified); i++)
            unspecified = unspecified.AddMinutes(15);

        if (timeZone.IsInvalidTime(unspecified))
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), timeZone);

        // Ambiguidade (fim de DST): a PRIMEIRA ocorrência é a escolhida.
        if (timeZone.IsAmbiguousTime(unspecified))
        {
            var offsets = timeZone.GetAmbiguousTimeOffsets(unspecified);
            var first = offsets.Max();
            return DateTime.SpecifyKind(unspecified - first, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(unspecified, timeZone);
    }

    /// <summary>
    /// Resolve IANA/Windows id para TimeZoneInfo; null quando inválido/ausente
    /// (o chamador decide o fallback UTC).
    /// </summary>
    private static TimeZoneInfo? ResolveTimeZone(string? timeZone)
    {
        if (string.IsNullOrWhiteSpace(timeZone))
            return null;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }

    private static bool TryParseTime(string? value, out TimeOnly time)
    {
        time = default;
        return !string.IsNullOrWhiteSpace(value)
            && TimeOnly.TryParseExact(value.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }

    private static JsonElement? GetProperty(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }

        return null;
    }

    private static string? GetString(JsonElement root, string name)
        => GetProperty(root, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static int? GetInt(JsonElement root, string name)
        => GetProperty(root, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number)
            ? number
            : null;
}