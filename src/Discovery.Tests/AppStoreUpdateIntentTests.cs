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
        public List<AppApprovalScopeType> Calls { get; } = [];

        public Task<AppApprovalResolvedPageDto> GetResolvedRulesAsync(
            AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType,
            string? search = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(scopeType);
            return Task.FromResult(Page);
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
        public Task<IReadOnlyList<EffectiveApprovedAppDto>> GetEffectiveAppsAsync(Guid? clientId, Guid? siteId, Guid? agentId, AppInstallationType installationType, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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

    private static AppStoreUpdateIntentService Build(
        FakeAppStoreService appStore,
        FakeAutomationTaskRepository tasks,
        FakeConfigurationResolver? config = null)
        => new(appStore, tasks, config ?? new FakeConfigurationResolver());

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
        string name = "Navegadores")
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            ActionType = action,
            // null = mesmo default do executor (Winget)
            InstallationType = installationType,
            PackageId = packageId,
            ScopeType = scope,
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
        Assert.That(page.MaintenanceWindow.StartUtc, Is.EqualTo("01:00"));
        Assert.That(page.MaintenanceWindow.DurationMinutes, Is.EqualTo(120));
        Assert.That(page.MaintenanceWindow.RebootGraceSeconds, Is.EqualTo(30));
        Assert.That(page.MaintenanceWindow.DaysOfWeek, Is.EqualTo(new[] { 1, 2, 3, 4, 5 }));
        Assert.That(page.MaintenanceWindow.UsesUtc, Is.False);
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
