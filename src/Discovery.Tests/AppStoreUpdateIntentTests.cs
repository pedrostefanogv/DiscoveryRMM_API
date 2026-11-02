using System.Globalization;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Contrato da simulação de intenção de atualização
/// (GET /api/v1/app-store/approvals/update-intent):
///  - task de automação que atualiza o pacote é DONA da atualização (precedência);
///  - auto-update da Loja só aparece quando não há task;
///  - conflito é sinalizado quando os dois motores agiriam no mesmo pacote;
///  - a janela de manutenção do servidor acompanha a resposta;
///  - pacote negado nunca entra na lista.
/// </summary>
[TestFixture]
public class AppStoreUpdateIntentTests
{
    private static readonly AppInstallationType Tipo = AppInstallationType.Winget;

    // ── Fakes ───────────────────────────────────────────────────────────

    private sealed class FakeAppStoreService : IAppStoreService
    {
        public AppApprovalResolvedPageDto Page { get; set; } = new();
        public List<EffectiveApprovedAppDto> EffectiveApps { get; set; } = [];
        public List<(Guid? ClientId, Guid? SiteId, Guid? AgentId)> EffectiveCalls { get; } = [];
        public List<AppApprovalScopeType> Calls { get; } = [];

        public Task<AppApprovalResolvedPageDto> GetResolvedRulesAsync(
            AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType,
            string? search = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(scopeType);
            return Task.FromResult(Page);
        }

        public Task<IReadOnlyList<EffectiveApprovedAppDto>> GetEffectiveAppsAsync(
            Guid? clientId, Guid? siteId, Guid? agentId, AppInstallationType installationType,
            CancellationToken cancellationToken = default)
        {
            EffectiveCalls.Add((clientId, siteId, agentId));
            return Task.FromResult<IReadOnlyList<EffectiveApprovedAppDto>>(EffectiveApps);
        }

        public Task<AppCatalogSearchResultDto> SearchCatalogAsync(AppInstallationType installationType, string? search, string? architecture, int limit, string? cursor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppCatalogPackageDto?> GetCatalogPackageByIdAsync(AppInstallationType installationType, string packageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppCatalogPackageDto> UpsertCustomCatalogPackageAsync(UpsertCustomAppCatalogPackageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteCustomPackageFileAsync(string packageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteCustomPackageIconAsync(string packageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppApprovalRuleResolvedDto> UpsertRuleAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, string packageId, AppApprovalActionType action, bool? autoUpdateEnabled, string? reason, string? changedBy, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AppApprovalRuleResolvedDto>> GetRulesByScopeAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteRuleAsync(Guid ruleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteRuleAsync(Guid ruleId, string? reason, string? changedBy, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppApprovalAuditPageDto> GetAuditHistoryAsync(AppInstallationType installationType, string? packageId, AppApprovalScopeType? scopeType, Guid? scopeId, string? changedBy, DateTime? changedFrom, DateTime? changedTo, AppApprovalAuditChangeType? changeType, int limit, Guid? cursor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsPackageApprovedAsync(Guid? clientId, Guid? siteId, Guid? agentId, AppInstallationType installationType, string packageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EffectiveApprovedAppPageDto> GetEffectiveAppsPageAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, string? search, int limit, string? cursor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppApprovalPackageDiffDto> GetPackageDiffAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, string packageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppEffectivePackageDiffPageDto> GetEffectiveAppDiffsAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, string? search, int limit, string? cursor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeAutomationTaskRepository : IAutomationTaskRepository
    {
        public List<AutomationTaskDefinition> Tasks { get; } = [];

        public Task<IReadOnlyList<AutomationTaskDefinition>> GetListPageAsync(
            AppApprovalScopeType? scopeType, Guid? scopeId, bool activeOnly, bool deletedOnly,
            bool includeDeleted, string? search, Guid? clientId, Guid? siteId, Guid? agentId,
            IReadOnlyList<AppApprovalScopeType>? scopeTypes,
            IReadOnlyList<AutomationTaskActionType>? actionTypes,
            IReadOnlyList<string>? triggers, bool? isActive, string? cursor, int limit)
        {
            // Respeita apenas os filtros que o serviço usa de verdade.
            var query = Tasks.AsEnumerable();
            if (scopeType.HasValue)
            {
                query = query.Where(t => t.ScopeType == scopeType.Value);
                if (scopeId.HasValue)
                {
                    query = scopeType.Value switch
                    {
                        AppApprovalScopeType.Client => query.Where(t => t.ClientId == scopeId),
                        AppApprovalScopeType.Site => query.Where(t => t.SiteId == scopeId),
                        AppApprovalScopeType.Agent => query.Where(t => t.AgentId == scopeId),
                        _ => query
                    };
                }
            }
            if (actionTypes is { Count: > 0 })
                query = query.Where(t => actionTypes.Contains(t.ActionType));
            if (isActive.HasValue)
                query = query.Where(t => t.IsActive == isActive.Value);

            return Task.FromResult<IReadOnlyList<AutomationTaskDefinition>>(query.ToList());
        }

        public Task<AutomationTaskDefinition> CreateAsync(AutomationTaskDefinition task) => throw new NotSupportedException();
        public Task<AutomationTaskDefinition?> GetByIdAsync(Guid id, bool includeInactive = false) => throw new NotSupportedException();
        public Task<AutomationTaskDefinition?> GetByIdIncludingDeletedAsync(Guid id, bool includeInactive = false) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<Guid, string>> GetNamesByIdsAsync(IReadOnlyCollection<Guid> ids) => throw new NotSupportedException();
        public Task<int> CountAsync(AppApprovalScopeType? scopeType, Guid? scopeId, bool activeOnly, bool deletedOnly, bool includeDeleted, string? search, Guid? clientId, Guid? siteId, Guid? agentId, IReadOnlyList<AppApprovalScopeType>? scopeTypes, IReadOnlyList<AutomationTaskActionType>? actionTypes, IReadOnlyList<string>? triggers, bool? isActive) => throw new NotSupportedException();
        public Task<bool> ExistsKeyAsync(string key, Guid? clientId, Guid? siteId, Guid? agentId, Guid? excludeId = null) => throw new NotSupportedException();
        public Task UpdateAsync(AutomationTaskDefinition task) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id) => throw new NotSupportedException();
        public Task<AutomationTaskDefinition?> RestoreAsync(Guid id) => throw new NotSupportedException();
        public Task<IReadOnlyList<AutomationTaskDefinition>> GetActiveTasksForAgentAsync(Guid agentId, Guid? agentSiteId, Guid? siteClientId, int limit = 200) => throw new NotSupportedException();
    }

    private sealed class FakeConfigurationResolver : IConfigurationResolver
    {
        public ServerConfiguration Server { get; set; } = new();
        public Task<ServerConfiguration> GetServerAsync() => Task.FromResult(Server);
        public Task<ClientConfiguration?> GetClientAsync(Guid clientId) => throw new NotSupportedException();
        public Task<SiteConfiguration?> GetSiteAsync(Guid siteId) => throw new NotSupportedException();
        public Task<T?> GetEffectiveValueAsync<T>(string level, string key, Guid? targetId = null) => throw new NotSupportedException();
        public Task<T?> GetConfigurationObjectAsync<T>(string objectType) where T : class => throw new NotSupportedException();
        public Task<Discovery.Core.ValueObjects.BrandingSettings> GetBrandingSettingsAsync() => throw new NotSupportedException();
        public Task<Discovery.Core.ValueObjects.AIIntegrationSettings> GetAISettingsAsync() => throw new NotSupportedException();
        public Task<Discovery.Core.ValueObjects.ResolvedConfiguration> ResolveForSiteAsync(Guid siteId) => throw new NotSupportedException();
        public Task<Discovery.Core.ValueObjects.ResolvedConfiguration> ResolveForClientAsync(Guid clientId) => throw new NotSupportedException();
        public Task<Discovery.Core.Configuration.BackgroundProcessingSettings> ResolveBackgroundProcessingAsync(Guid? clientId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ValidateInheritanceAsync() => throw new NotSupportedException();
        public void ClearCache() => throw new NotSupportedException();
    }

    /// <summary>
    /// Configurações CRUD por nível (Server/Client/Site) — devolve o blob
    /// maintenanceWindowJson CRU de cada nível (sem herança aplicada),
    /// replicando o que o agent recebe pela cascata site ?? client ?? server.
    /// </summary>
    private sealed class FakeConfigurationService : IConfigurationService
    {
        public ServerConfiguration? Server { get; set; }
        public ClientConfiguration? Client { get; set; }
        public SiteConfiguration? Site { get; set; }

        public Task<ServerConfiguration> GetServerConfigAsync()
            => Task.FromResult(Server ?? new ServerConfiguration());
        public Task<ClientConfiguration?> GetClientConfigAsync(Guid clientId)
            => Task.FromResult(Client?.ClientId == clientId ? Client : Client);
        public Task<SiteConfiguration?> GetSiteConfigAsync(Guid siteId)
            => Task.FromResult(Site?.SiteId == siteId ? Site : Site);

        public Task<ServerConfiguration> PatchServerAsync(Dictionary<string, object> updates, string? updatedBy = null) => throw new NotSupportedException();
        public Task<ServerConfiguration> ResetServerAsync(string? resetBy = null) => throw new NotSupportedException();
        public Task<ClientConfiguration> CreateClientConfigAsync(Guid clientId, ClientConfiguration config, string? createdBy = null) => throw new NotSupportedException();
        public Task<ClientConfiguration> UpdateClientAsync(Guid clientId, ClientConfiguration config, string? updatedBy = null) => throw new NotSupportedException();
        public Task<ClientConfiguration> PatchClientAsync(Guid clientId, Dictionary<string, object> updates, string? updatedBy = null) => throw new NotSupportedException();
        public Task DeleteClientConfigAsync(Guid clientId, string? deletedBy = null) => throw new NotSupportedException();
        public Task ResetClientPropertyAsync(Guid clientId, string propertyName, string? resetBy = null) => throw new NotSupportedException();
        public Task<SiteConfiguration> CreateSiteConfigAsync(Guid siteId, SiteConfiguration config, string? createdBy = null) => throw new NotSupportedException();
        public Task<SiteConfiguration> UpdateSiteAsync(Guid siteId, SiteConfiguration config, string? updatedBy = null) => throw new NotSupportedException();
        public Task<SiteConfiguration> PatchSiteAsync(Guid siteId, Dictionary<string, object> updates, string? updatedBy = null) => throw new NotSupportedException();
        public Task DeleteSiteConfigAsync(Guid siteId, string? deletedBy = null) => throw new NotSupportedException();
        public Task ResetSitePropertyAsync(Guid siteId, string propertyName, string? resetBy = null) => throw new NotSupportedException();
        public Task<(bool IsValid, string[] Errors)> ValidateAsync(object config) => throw new NotSupportedException();
        public Task<(bool IsValid, string[] Errors)> ValidateJsonAsync(string objectType, string json) => throw new NotSupportedException();
    }

    private sealed class FakeSiteRepository : ISiteRepository
    {
        public Site? Site { get; set; }

        public Task<Site?> GetByIdAsync(Guid id) => Task.FromResult(Site is not null && Site.Id == id ? Site : null);
        public Task<IEnumerable<Site>> GetByClientIdAsync(Guid clientId, bool includeInactive = false) => throw new NotSupportedException();
        public Task<IEnumerable<Site>> GetByClientIdsAsync(IEnumerable<Guid> clientIds, bool includeInactive = false) => throw new NotSupportedException();
        public Task<IEnumerable<Site>> GetAllAsync(bool includeInactive = false) => throw new NotSupportedException();
        public Task<IEnumerable<Site>> GetByIdsAsync(IEnumerable<Guid> siteIds, bool includeInactive = false) => throw new NotSupportedException();
        public Task<Site> CreateAsync(Site site) => throw new NotSupportedException();
        public Task UpdateAsync(Site site) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id) => throw new NotSupportedException();
    }

    private sealed class FakeAgentRepository : IAgentRepository
    {
        public Agent? Agent { get; set; }

        public Task<Agent?> GetByIdAsync(Guid id) => Task.FromResult(Agent is not null && Agent.Id == id ? Agent : null);
        public Task<IReadOnlyList<Agent>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IEnumerable<Agent>> GetAllAsync() => throw new NotSupportedException();
        public Task<IEnumerable<Agent>> GetBySiteIdAsync(Guid siteId) => throw new NotSupportedException();
        public Task<IEnumerable<Agent>> GetByClientIdAsync(Guid clientId) => throw new NotSupportedException();
        public Task<Agent> CreateAsync(Agent agent) => throw new NotSupportedException();
        public Task UpdateAsync(Agent agent) => throw new NotSupportedException();
        public Task UpdateStatusAsync(Guid id, Discovery.Core.Enums.AgentStatus status, string? ipAddress) => throw new NotSupportedException();
        public Task SetPolicySyncAsync(Guid id, string fingerprint, DateTime syncedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Agent>> GetOnlineAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task ApproveZeroTouchAsync(Guid agentId) => throw new NotSupportedException();
        public Task SetMaintenanceAsync(Guid id, bool enabled, string? reason, Guid changedByUserId) => throw new NotSupportedException();
        public Task TransferSiteAsync(Guid agentId, Guid newSiteId) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id) => throw new NotSupportedException();
        public Task<IReadOnlyList<Agent>> FindByFingerprintAsync(string fingerprintHash, Guid clientId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeClientRepository : IClientRepository
    {
        public Client? Client { get; set; }

        public Task<Client?> GetByIdAsync(Guid id) => Task.FromResult(Client is not null && Client.Id == id ? Client : null);
        public Task<IEnumerable<Client>> GetAllAsync(bool includeInactive = false) => throw new NotSupportedException();
        public Task<Client> CreateAsync(Client client) => throw new NotSupportedException();
        public Task UpdateAsync(Client client) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id) => throw new NotSupportedException();
    }

    private static AppStoreUpdateIntentService Build(
        FakeAppStoreService appStore,
        FakeAutomationTaskRepository tasks,
        FakeConfigurationResolver? config = null,
        FakeConfigurationService? configService = null,
        FakeSiteRepository? siteRepo = null,
        FakeAgentRepository? agentRepo = null,
        FakeClientRepository? clientRepo = null)
        => new(appStore, tasks, config ?? new FakeConfigurationResolver(), siteRepo, agentRepo, clientRepo, configService);

    private static AppApprovalResolvedRuleDto Rule(
        string packageId,
        AppApprovalActionType action = AppApprovalActionType.Allow,
        bool autoUpdate = false,
        string? name = null)
        => new()
        {
            PackageId = packageId,
            PackageName = name ?? packageId,
            InstallationType = Tipo,
            Action = action,
            AutoUpdateEnabled = autoUpdate
        };

    private static AutomationTaskDefinition AutomationUpdateTask(
        string packageId,
        AutomationTaskActionType action = AutomationTaskActionType.UpdatePackage,
        AppApprovalScopeType scope = AppApprovalScopeType.Global,
        Guid? scopeId = null,
        AppInstallationType? installationType = null,
        string? cron = null,
        string name = "Navegadores",
        string? includeTagsJson = null,
        string? excludeTagsJson = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            ActionType = action,
            // null = mesmo default do executor (Winget)
            InstallationType = installationType,
            PackageId = packageId,
            ScopeType = scope,
            IncludeTagsJson = includeTagsJson,
            ExcludeTagsJson = excludeTagsJson,
            ClientId = scope == AppApprovalScopeType.Client ? scopeId : null,
            SiteId = scope == AppApprovalScopeType.Site ? scopeId : null,
            AgentId = scope == AppApprovalScopeType.Agent ? scopeId : null,
            IsActive = true,
            TriggerRecurring = cron is not null,
            ScheduleCron = cron,
            UpdatedAt = DateTime.UtcNow
        };

    // ── Casos ───────────────────────────────────────────────────────────

    [Test]
    public async Task Sem_automacao_o_auto_update_da_loja_e_o_dono()
    {
        var appStore = new FakeAppStoreService { Page = new AppApprovalResolvedPageDto { Items = [Rule("Microsoft.VCRedist", autoUpdate: true)] } };
        var service = Build(appStore, new FakeAutomationTaskRepository());

        var page = await service.GetUpdateIntentAsync(AppApprovalScopeType.Global, null, Tipo);

        Assert.That(page.Count, Is.EqualTo(1));
        var item = page.Items[0];
        Assert.That(item.Owner, Is.EqualTo(AppUpdateOwner.AutoUpdate));
        Assert.That(item.Conflict, Is.False);
        Assert.That(item.AutoUpdateEnabled, Is.True);
    }

    [Test]
    public async Task Sem_auto_update_e_sem_automacao_o_dono_e_manual()
    {
        var appStore = new FakeAppStoreService { Page = new AppApprovalResolvedPageDto { Items = [Rule("Microsoft.VCRedist")] } };
        var service = Build(appStore, new FakeAutomationTaskRepository());

        var page = await service.GetUpdateIntentAsync(AppApprovalScopeType.Global, null, Tipo);

        Assert.That(page.Items[0].Owner, Is.EqualTo(AppUpdateOwner.Manual));
        Assert.That(page.Items[0].Conflict, Is.False);
    }

    [Test]
    public async Task Automacao_de_update_tem_precedencia_e_marca_conflito()
    {
        var appStore = new FakeAppStoreService { Page = new AppApprovalResolvedPageDto { Items = [Rule("Google.Chrome", autoUpdate: true)] } };
        var tasks = new FakeAutomationTaskRepository();
        tasks.Tasks.Add(AutomationUpdateTask("Google.Chrome", cron: "0 3 * * *"));
        var service = Build(appStore, tasks);

        var page = await service.GetUpdateIntentAsync(AppApprovalScopeType.Global, null, Tipo);

        var item = page.Items[0];
        Assert.That(item.Owner, Is.EqualTo(AppUpdateOwner.Automation));
        Assert.That(item.AutomationTaskName, Is.EqualTo("Navegadores"));
        Assert.That(item.AutomationTriggerSummary, Does.Contain("0 3 * * *"));
        Assert.That(item.Conflict, Is.True);
        Assert.That(item.ConflictReason, Does.Contain("Navegadores"));
    }

    [Test]
    public async Task Automacao_de_outro_pacote_nao_afeta_o_item()
    {
        var appStore = new FakeAppStoreService { Page = new AppApprovalResolvedPageDto { Items = [Rule("Google.Chrome", autoUpdate: true)] } };
        var tasks = new FakeAutomationTaskRepository();
        tasks.Tasks.Add(AutomationUpdateTask("Mozilla.Firefox"));
        var service = Build(appStore, tasks);

        var page = await service.GetUpdateIntentAsync(AppApprovalScopeType.Global, null, Tipo);

        Assert.That(page.Items[0].Owner, Is.EqualTo(AppUpdateOwner.AutoUpdate));
        Assert.That(page.Items[0].Conflict, Is.False);
    }

    [Test]
    public async Task Task_de_install_ou_remove_nao_disputa_atualizacao()
    {
        var appStore = new FakeAppStoreService { Page = new AppApprovalResolvedPageDto { Items = [Rule("Google.Chrome", autoUpdate: true)] } };
        var tasks = new FakeAutomationTaskRepository();
        tasks.Tasks.Add(AutomationUpdateTask("Google.Chrome", action: AutomationTaskActionType.InstallPackage));
        tasks.Tasks.Add(AutomationUpdateTask("Google.Chrome", action: AutomationTaskActionType.RemovePackage));
        var service = Build(appStore, tasks);

        var page = await service.GetUpdateIntentAsync(AppApprovalScopeType.Global, null, Tipo);

        Assert.That(page.Items[0].Owner, Is.EqualTo(AppUpdateOwner.AutoUpdate));
    }

    [Test]
    public async Task UpdateOrInstall_conta_como_automacao()
    {
        var appStore = new FakeAppStoreService { Page = new AppApprovalResolvedPageDto { Items = [Rule("Google.Chrome")] } };
        var tasks = new FakeAutomationTaskRepository();
        tasks.Tasks.Add(AutomationUpdateTask("Google.Chrome", action: AutomationTaskActionType.UpdateOrInstallPackage));
        var service = Build(appStore, tasks);

        var page = await service.GetUpdateIntentAsync(AppApprovalScopeType.Global, null, Tipo);

        Assert.That(page.Items[0].Owner, Is.EqualTo(AppUpdateOwner.Automation));
        Assert.That(page.Items[0].Conflict, Is.False, "sem auto-update da loja não há conflito");
    }

    [Test]
    public async Task Task_sem_installation_type_assume_winget()
    {
        var appStore = new FakeAppStoreService { Page = new AppApprovalResolvedPageDto { Items = [Rule("Google.Chrome")] } };
        var tasks = new FakeAutomationTaskRepository();
        tasks.Tasks.Add(AutomationUpdateTask("Google.Chrome", installationType: null));
        var service = Build(appStore, tasks);

        var page = await service.GetUpdateIntentAsync(AppApprovalScopeType.Global, null, Tipo);

        Assert.That(page.Items[0].Owner, Is.EqualTo(AppUpdateOwner.Automation));
    }

    [Test]
    public async Task Pacote_negado_nao_aparece_na_intencao()
    {
        var appStore = new FakeAppStoreService
        {
            Page = new AppApprovalResolvedPageDto
            {
                Items = [Rule("Pacote.Negado", AppApprovalActionType.Deny), Rule("Pacote.Permitido", autoUpdate: true)]
            }
        };
        var service = Build(appStore, new FakeAutomationTaskRepository());

        var page = await service.GetUpdateIntentAsync(AppApprovalScopeType.Global, null, Tipo);

        Assert.That(page.Count, Is.EqualTo(1));
        Assert.That(page.Items[0].PackageId, Is.EqualTo("Pacote.Permitido"));
    }

    [Test]
    public async Task Janela_do_servidor_acompanha_a_intencao()
    {
        var appStore = new FakeAppStoreService { Page = new AppApprovalResolvedPageDto { Items = [Rule("Google.Chrome")] } };
        var config = new FakeConfigurationResolver
        {
            Server = new ServerConfiguration
            {
                MaintenanceWindowJson = """{"enabled":true,"daysOfWeek":[1,2,3,4,5],"startTime":"22:00","timeZone":"America/Sao_Paulo","startUtc":"01:00","durationMinutes":120,"rebootGraceSeconds":30}"""
            }
        };
        var service = Build(appStore, new FakeAutomationTaskRepository(), config);

        var page = await service.GetUpdateIntentAsync(AppApprovalScopeType.Global, null, Tipo);

        Assert.That(page.MaintenanceWindow.Enabled, Is.True);
        Assert.That(page.MaintenanceWindow.StartTime, Is.EqualTo("22:00"));
        Assert.That(page.MaintenanceWindow.TimeZone, Is.EqualTo("America/Sao_Paulo"));
        Assert.That(page.MaintenanceWindow.DurationMinutes, Is.EqualTo(120));
        Assert.That(page.MaintenanceWindow.RebootGraceSeconds, Is.EqualTo(30));
        Assert.That(page.MaintenanceWindow.DaysOfWeek, Is.EqualTo(new[] { 1, 2, 3, 4, 5 }));
        Assert.That(page.MaintenanceWindow.UsesUtc, Is.False);

        // StartUtc é agora um INSTANTE UTC calculado de verdade (22:00 em
        // America/Sao_Paulo = -03:00 → 01:00Z do dia seguinte), não a hora local.
        Assert.That(page.MaintenanceWindow.StartUtc, Does.Match(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$"));
        var startUtc = DateTime.Parse(page.MaintenanceWindow.StartUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.That(startUtc.Kind, Is.EqualTo(DateTimeKind.Utc));
        var localTime = TimeZoneInfo.ConvertTimeFromUtc(startUtc, TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"));
        Assert.That(localTime.TimeOfDay, Is.EqualTo(new TimeSpan(22, 0, 0)));
        Assert.That(page.MaintenanceWindow.EndUtc, Is.Not.Empty);
        Assert.That(page.MaintenanceWindow.NextStartUtc is null || page.MaintenanceWindow.NextStartUtc.Value.Kind == DateTimeKind.Utc, Is.True);
    }

    [Test]
    public async Task Janela_ausente_ou_invalida_nao_quebra_e_marca_utc()
    {
        var appStore = new FakeAppStoreService { Page = new AppApprovalResolvedPageDto { Items = [] } };
        var config = new FakeConfigurationResolver { Server = new ServerConfiguration { MaintenanceWindowJson = "{oops" } };
        var service = Build(appStore, new FakeAutomationTaskRepository(), config);

        var page = await service.GetUpdateIntentAsync(AppApprovalScopeType.Global, null, Tipo);

        Assert.That(page.MaintenanceWindow.Enabled, Is.False);
        Assert.That(page.MaintenanceWindow.UsesUtc, Is.True);
    }

    [Test]
    public async Task Escopo_cliente_carrega_tasks_do_cliente()
    {
        var clientId = Guid.NewGuid();
        var appStore = new FakeAppStoreService { Page = new AppApprovalResolvedPageDto { Items = [Rule("Google.Chrome")] } };
        var tasks = new FakeAutomationTaskRepository();
        tasks.Tasks.Add(AutomationUpdateTask("Google.Chrome", scope: AppApprovalScopeType.Client, scopeId: clientId, name: "Task do cliente"));
        var service = Build(appStore, tasks);

        var page = await service.GetUpdateIntentAsync(AppApprovalScopeType.Client, clientId, Tipo);

        Assert.That(page.Items[0].Owner, Is.EqualTo(AppUpdateOwner.Automation));
        Assert.That(page.Items[0].AutomationTaskName, Is.EqualTo("Task do cliente"));
        Assert.That(appStore.Calls, Is.EqualTo(new[] { AppApprovalScopeType.Client }));
    }

    // ── Cascata da janela de manutenção (site ?? client ?? server) ──────

    [Test]
    public async Task Janela_do_site_vence_o_servidor_na_cascata()
    {
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var appStore = new FakeAppStoreService { Page = new AppApprovalResolvedPageDto { Items = [Rule("Google.Chrome")] } };
        var site = new Site { Id = siteId, ClientId = clientId };
        var config = new FakeConfigurationResolver
        {
            Server = new ServerConfiguration
            {
                MaintenanceWindowJson = """{"enabled":true,"startUtc":"03:00","durationMinutes":90}"""
            }
        };
        var configService = new FakeConfigurationService
        {
            Site = new SiteConfiguration
            {
                SiteId = siteId,
                MaintenanceWindowJson = """{"enabled":true,"startTime":"22:00","timeZone":"America/Sao_Paulo","durationMinutes":60}"""
            }
        };
        var service = Build(appStore, new FakeAutomationTaskRepository(), config, configService,
            siteRepo: new FakeSiteRepository { Site = site });

        var page = await service.GetUpdateIntentAsync(AppApprovalScopeType.Site, siteId, Tipo);

        Assert.That(page.MaintenanceWindow.StartTime, Is.EqualTo("22:00"));
        Assert.That(page.MaintenanceWindow.TimeZone, Is.EqualTo("America/Sao_Paulo"));
        Assert.That(page.MaintenanceWindow.UsesUtc, Is.False, "janela do site (local) vence o servidor (UTC)");
    }

    [Test]
    public async Task Janela_do_site_vazia_bloqueia_niveis_inferiores()
    {
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var appStore = new FakeAppStoreService { Page = new AppApprovalResolvedPageDto { Items = [Rule("Google.Chrome")] } };
        var site = new Site { Id = siteId, ClientId = clientId };
        var config = new FakeConfigurationResolver
        {
            Server = new ServerConfiguration
            {
                // JSON válido no servidor — mas o site gravou string vazia,
                // e string.Empty (não null) BLOQUEIA o nível de baixo, igual
                // à cascata crua que o agent recebe.
                MaintenanceWindowJson = """{"enabled":true,"startUtc":"03:00"}"""
            }
        };
        var configService = new FakeConfigurationService
        {
            Site = new SiteConfiguration { SiteId = siteId, MaintenanceWindowJson = "" }
        };
        var service = Build(appStore, new FakeAutomationTaskRepository(), config, configService,
            siteRepo: new FakeSiteRepository { Site = site });

        var page = await service.GetUpdateIntentAsync(AppApprovalScopeType.Site, siteId, Tipo);

        Assert.That(page.MaintenanceWindow.Enabled, Is.False);
        Assert.That(page.MaintenanceWindow.UsesUtc, Is.True);
    }

    [Test]
    public async Task Escopo_agente_resolve_cascata_via_site_e_cliente()
    {
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var appStore = new FakeAppStoreService { Page = new AppApprovalResolvedPageDto { Items = [Rule("Google.Chrome")] } };
        var config = new FakeConfigurationResolver
        {
            Server = new ServerConfiguration
            {
                MaintenanceWindowJson = """{"enabled":true,"startUtc":"03:00"}"""
            }
        };
        var configService = new FakeConfigurationService
        {
            Client = new ClientConfiguration { ClientId = clientId, MaintenanceWindowJson = """{"enabled":true,"startUtc":"05:00"}""" }
        };
        var service = Build(appStore, new FakeAutomationTaskRepository(), config, configService,
            siteRepo: new FakeSiteRepository { Site = new Site { Id = siteId, ClientId = clientId } },
            agentRepo: new FakeAgentRepository { Agent = new Agent { Id = agentId, SiteId = siteId } },
            clientRepo: new FakeClientRepository { Client = new Client { Id = clientId } });

        var page = await service.GetUpdateIntentAsync(AppApprovalScopeType.Agent, agentId, Tipo);

        Assert.That(page.MaintenanceWindow.StartTime, Is.EqualTo("05:00"),
            "site sem janela: cliente vence o servidor");
        Assert.That(page.MaintenanceWindow.UsesUtc, Is.True);
    }

    [Test]
    public async Task TimeZone_invalida_cai_para_utc_com_isOpenNow_coerente()
    {
        var appStore = new FakeAppStoreService { Page = new AppApprovalResolvedPageDto { Items = [Rule("Google.Chrome")] } };
        var config = new FakeConfigurationResolver
        {
            // 00:00 "UTC" com duração de 24h: janela SEMPRE aberta em UTC.
            Server = new ServerConfiguration
            {
                MaintenanceWindowJson = """{"enabled":true,"startTime":"00:00","timeZone":"America/Sao_Pauloooo","durationMinutes":1440}"""
            }
        };
        var service = Build(appStore, new FakeAutomationTaskRepository(), config);

        var page = await service.GetUpdateIntentAsync(AppApprovalScopeType.Global, null, Tipo);

        Assert.That(page.MaintenanceWindow.TimeZone, Is.EqualTo("UTC"));
        Assert.That(page.MaintenanceWindow.UsesUtc, Is.True);
        Assert.That(page.MaintenanceWindow.IsOpenNow, Is.True,
            "janela 00:00-24:00 em UTC está aberta agora");
    }

    // ── Política (Disabled / All / PreApproved) ────────────────────────

    [Test]
    public async Task Politica_disabled_lista_vazia()
    {
        var appStore = new FakeAppStoreService
        {
            Page = new AppApprovalResolvedPageDto
            {
                Policy = AppStorePolicyType.Disabled,
                Items = [Rule("Google.Chrome", autoUpdate: true)]
            }
        };
        var service = Build(appStore, new FakeAutomationTaskRepository());

        var page = await service.GetUpdateIntentAsync(AppApprovalScopeType.Global, null, Tipo);

        Assert.That(page.Policy, Is.EqualTo(AppStorePolicyType.Disabled));
        Assert.That(page.Count, Is.EqualTo(0));
        Assert.That(page.Items, Is.Empty);
    }

    [Test]
    public async Task Politica_all_lista_o_catalogo_efetivo()
    {
        var appStore = new FakeAppStoreService
        {
            Page = new AppApprovalResolvedPageDto
            {
                Policy = AppStorePolicyType.All,
                // Regra sem autoUpdate: pacote do catálogo sem regra explícita
                // de auto-atualização continua Manual (o teste ao lado cobre o
                // caso com regra explícita ligada).
                Items = [Rule("Google.Chrome")]
            },
            EffectiveApps =
            [
                new EffectiveApprovedAppDto { InstallationType = Tipo, PackageId = "Google.Chrome", Name = "Google Chrome" },
                new EffectiveApprovedAppDto { InstallationType = Tipo, PackageId = "Mozilla.Firefox", Name = "Firefox" }
            ]
        };
        var tasks = new FakeAutomationTaskRepository();
        tasks.Tasks.Add(AutomationUpdateTask("Mozilla.Firefox", name: "Firefox task"));
        var service = Build(appStore, tasks);

        var page = await service.GetUpdateIntentAsync(AppApprovalScopeType.Global, null, Tipo);

        Assert.That(page.Policy, Is.EqualTo(AppStorePolicyType.All));
        Assert.That(page.Count, Is.EqualTo(2));
        Assert.That(page.Items.Select(i => i.PackageId),
            Is.EqualTo(new[] { "Google.Chrome", "Mozilla.Firefox" }).AsCollection);
        // Catálogo efetivo NÃO herda autoUpdate da regra: Mozilla tem task,
        // Google não tem task e não tem regra explícita para auto-update.
        Assert.That(page.Items.First(i => i.PackageId == "Mozilla.Firefox").Owner, Is.EqualTo(AppUpdateOwner.Automation));
        Assert.That(page.Items.First(i => i.PackageId == "Google.Chrome").Owner, Is.EqualTo(AppUpdateOwner.Manual));
        Assert.That(page.Items.First(i => i.PackageId == "Google.Chrome").AutoUpdateEnabled, Is.False);
        var effectiveCall = appStore.EffectiveCalls[0];
        Assert.Multiple(() =>
        {
            Assert.That(effectiveCall.ClientId, Is.Null);
            Assert.That(effectiveCall.SiteId, Is.Null);
            Assert.That(effectiveCall.AgentId, Is.Null);
        });
    }

    [Test]
    public async Task Politica_all_com_regra_explicita_usa_autoUpdate_da_regra()
    {
        var appStore = new FakeAppStoreService
        {
            Page = new AppApprovalResolvedPageDto
            {
                Policy = AppStorePolicyType.All,
                Items = [Rule("Google.Chrome", autoUpdate: true)],
            },
            EffectiveApps =
            [
                new EffectiveApprovedAppDto { InstallationType = Tipo, PackageId = "Google.Chrome", Name = "Google Chrome" }
            ]
        };
        var service = Build(appStore, new FakeAutomationTaskRepository());

        var page = await service.GetUpdateIntentAsync(AppApprovalScopeType.Global, null, Tipo);

        var item = page.Items[0];
        Assert.That(item.AutoUpdateEnabled, Is.True, "regra explícita informa autoUpdate mesmo em política All");
        Assert.That(item.Owner, Is.EqualTo(AppUpdateOwner.AutoUpdate));
    }

    // ── Conflito por filtros de tags ───────────────────────────────────

    [Test]
    public async Task Task_com_filtros_de_tags_marca_conflito_de_cobertura()
    {
        var appStore = new FakeAppStoreService { Page = new AppApprovalResolvedPageDto { Items = [Rule("Google.Chrome")] } };
        var tasks = new FakeAutomationTaskRepository();
        tasks.Tasks.Add(AutomationUpdateTask("Google.Chrome", includeTagsJson: """["ti"]""", name: "Chrome ti"));
        var service = Build(appStore, tasks);

        var page = await service.GetUpdateIntentAsync(AppApprovalScopeType.Global, null, Tipo);

        var item = page.Items[0];
        Assert.That(item.Owner, Is.EqualTo(AppUpdateOwner.Automation));
        Assert.That(item.Conflict, Is.True);
        Assert.That(item.ConflictReason, Does.Contain("filtros de tags"));
        Assert.That(item.ConflictReason, Does.Contain("Chrome ti"));
    }

    [Test]
    public async Task Task_com_tags_vazias_nao_marca_conflito()
    {
        var appStore = new FakeAppStoreService { Page = new AppApprovalResolvedPageDto { Items = [Rule("Google.Chrome")] } };
        var tasks = new FakeAutomationTaskRepository();
        tasks.Tasks.Add(AutomationUpdateTask("Google.Chrome", includeTagsJson: "[]"));
        var service = Build(appStore, tasks);

        var page = await service.GetUpdateIntentAsync(AppApprovalScopeType.Global, null, Tipo);

        Assert.That(page.Items[0].Owner, Is.EqualTo(AppUpdateOwner.Automation));
        Assert.That(page.Items[0].Conflict, Is.False);
    }

    // ── Truncamento de tasks por escopo ────────────────────────────────

    [Test]
    public async Task Tasks_no_limite_por_escopo_marca_truncated()
    {
        var appStore = new FakeAppStoreService { Page = new AppApprovalResolvedPageDto { Items = [Rule("Google.Chrome")] } };
        var tasks = new FakeAutomationTaskRepository();
        for (var i = 0; i < 500; i++)
            tasks.Tasks.Add(AutomationUpdateTask("Pacote." + i));
        var service = Build(appStore, tasks);

        var page = await service.GetUpdateIntentAsync(AppApprovalScopeType.Global, null, Tipo);

        Assert.That(page.Truncated, Is.True);
    }

    [Test]
    public async Task Tasks_abaixo_do_limite_nao_marca_truncated()
    {
        var appStore = new FakeAppStoreService { Page = new AppApprovalResolvedPageDto { Items = [Rule("Google.Chrome")] } };
        var tasks = new FakeAutomationTaskRepository();
        tasks.Tasks.Add(AutomationUpdateTask("Google.Chrome"));
        var service = Build(appStore, tasks);

        var page = await service.GetUpdateIntentAsync(AppApprovalScopeType.Global, null, Tipo);

        Assert.That(page.Truncated, Is.False);
    }

    [Test]
    public void Handler_recusa_escopo_sem_id()
    {
        var handler = new Discovery.Infrastructure.Cqrs.AppStore.GetAppStoreUpdateIntentQueryHandler(
            new AppStoreUpdateIntentService(new FakeAppStoreService(), new FakeAutomationTaskRepository(), new FakeConfigurationResolver()));

        var result = handler.Handle(
            new Discovery.Core.Cqrs.AppStore.Queries.GetAppStoreUpdateIntentQuery(AppApprovalScopeType.Site, null, (int)Tipo),
            CancellationToken.None).GetAwaiter().GetResult();

        Assert.That(result.IsFailure, Is.True);
    }
}
