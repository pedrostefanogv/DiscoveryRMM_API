using System.Text.Json;
using Meduza.Core.DTOs;
using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace Meduza.Infrastructure.Services;

public class AppStoreService : IAppStoreService
{
    private const string WingetLatestPackagesUrl = "https://github.com/pedrostefanogv/winget-package-explo/releases/latest/download/packages.json";
    private const string WingetCacheKey = "app_store_winget_packages";
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private readonly IAppApprovalRuleRepository _approvalRepo;
    private readonly IAppApprovalAuditService _auditService;
    private readonly ISiteRepository _siteRepository;
    private readonly IAgentRepository _agentRepository;
    private readonly IMemoryCache _cache;

    public AppStoreService(
        IAppApprovalRuleRepository approvalRepo,
        IAppApprovalAuditService auditService,
        ISiteRepository siteRepository,
        IAgentRepository agentRepository,
        IMemoryCache cache)
    {
        _approvalRepo = approvalRepo;
        _auditService = auditService;
        _siteRepository = siteRepository;
        _agentRepository = agentRepository;
        _cache = cache;
    }

    public async Task<AppCatalogSearchResultDto> SearchCatalogAsync(
        AppInstallationType installationType,
        string? search,
        string? architecture,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var normalizedLimit = Math.Clamp(limit, 1, 200);
        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var normalizedArch = string.IsNullOrWhiteSpace(architecture) ? null : architecture.Trim();
        var normalizedCursor = string.IsNullOrWhiteSpace(cursor) ? null : cursor.Trim();

        if (installationType != AppInstallationType.Winget)
        {
            return new AppCatalogSearchResultDto
            {
                GeneratedAt = null,
                TotalPackagesInSource = 0,
                ReturnedItems = 0,
                Cursor = normalizedCursor,
                NextCursor = null,
                Limit = normalizedLimit,
                HasMore = false,
                Search = normalizedSearch,
                Architecture = normalizedArch,
                Items = []
            };
        }

        var feed = await GetWingetFeedAsync(cancellationToken);
        IEnumerable<AppCatalogPackageDto> query = feed.Packages;

        if (!string.IsNullOrWhiteSpace(normalizedSearch))
        {
            query = query.Where(x =>
                ContainsIgnoreCase(x.Id, normalizedSearch) ||
                ContainsIgnoreCase(x.Name, normalizedSearch) ||
                ContainsIgnoreCase(x.Publisher, normalizedSearch) ||
                ContainsIgnoreCase(x.Description, normalizedSearch) ||
                x.Tags.Any(tag => ContainsIgnoreCase(tag, normalizedSearch)));
        }

        if (!string.IsNullOrWhiteSpace(normalizedArch))
        {
            query = query.Where(x => x.InstallerUrlsByArch.Keys.Any(k => k.Equals(normalizedArch, StringComparison.OrdinalIgnoreCase)));
        }

        var filtered = query
            .OrderBy(x => x.Name)
            .ThenBy(x => x.Id)
            .ToList();

        if (!string.IsNullOrWhiteSpace(normalizedCursor) && TryDecodeCatalogCursor(normalizedCursor, out var cursorName, out var cursorId))
        {
            filtered = filtered
                .Where(x => CompareCatalogSort(x, cursorName, cursorId) > 0)
                .ToList();
        }

        var pageSlice = filtered
            .Take(normalizedLimit + 1)
            .ToList();

        var hasMore = pageSlice.Count > normalizedLimit;
        var items = hasMore ? pageSlice.Take(normalizedLimit).ToList() : pageSlice;
        var nextCursor = hasMore ? EncodeCatalogCursor(items[^1]) : null;

        return new AppCatalogSearchResultDto
        {
            GeneratedAt = feed.GeneratedAt,
            TotalPackagesInSource = feed.TotalPackages,
            ReturnedItems = items.Count,
            Cursor = normalizedCursor,
            NextCursor = nextCursor,
            Limit = normalizedLimit,
            HasMore = hasMore,
            Search = normalizedSearch,
            Architecture = normalizedArch,
            Items = items
        };
    }

    public async Task<AppCatalogPackageDto?> GetCatalogPackageByIdAsync(
        AppInstallationType installationType,
        string packageId,
        CancellationToken cancellationToken = default)
    {
        if (installationType != AppInstallationType.Winget)
            return null;

        if (string.IsNullOrWhiteSpace(packageId))
            return null;

        var feed = await GetWingetFeedAsync(cancellationToken);
        return feed.Packages.FirstOrDefault(x => x.Id.Equals(packageId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public async Task<AppApprovalRuleResolvedDto> UpsertRuleAsync(
        AppApprovalScopeType scopeType,
        Guid? scopeId,
        AppInstallationType installationType,
        string packageId,
        AppApprovalActionType action,
        bool? autoUpdateEnabled,
        string? reason,
        string? changedBy,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        var normalizedPackageId = NormalizePackageId(packageId);
        var (clientId, siteId, agentId) = ResolveScopeIds(scopeType, scopeId);

        if (installationType == AppInstallationType.Winget)
        {
            var package = await GetCatalogPackageByIdAsync(installationType, normalizedPackageId, cancellationToken);
            if (package is null)
                throw new InvalidOperationException($"Package '{normalizedPackageId}' nao encontrado no catalogo Winget mais recente.");
        }

        var existing = await _approvalRepo.GetByUniqueKeyAsync(
            scopeType,
            clientId,
            siteId,
            agentId,
            installationType,
            normalizedPackageId);

        if (existing is null)
        {
            var created = await _approvalRepo.CreateAsync(new Meduza.Core.Entities.AppApprovalRule
            {
                ScopeType = scopeType,
                ClientId = clientId,
                SiteId = siteId,
                AgentId = agentId,
                InstallationType = installationType,
                PackageId = normalizedPackageId,
                Action = action,
                AutoUpdateEnabled = autoUpdateEnabled
            });

            await _auditService.LogAsync(
                AppApprovalAuditChangeType.Created,
                created,
                null,
                null,
                reason,
                changedBy,
                ipAddress);

            return ToRuleDto(created);
        }

        var oldAction = existing.Action;
        var oldAutoUpdateEnabled = existing.AutoUpdateEnabled;
        existing.Action = action;
        existing.AutoUpdateEnabled = autoUpdateEnabled;
        await _approvalRepo.UpdateAsync(existing);

        var updated = await _approvalRepo.GetByIdAsync(existing.Id) ?? existing;
        await _auditService.LogAsync(
            AppApprovalAuditChangeType.Updated,
            updated,
            oldAction,
            oldAutoUpdateEnabled,
            reason,
            changedBy,
            ipAddress);
        return ToRuleDto(updated);
    }

    public async Task<IReadOnlyList<AppApprovalRuleResolvedDto>> GetRulesByScopeAsync(
        AppApprovalScopeType scopeType,
        Guid? scopeId,
        AppInstallationType installationType,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var rules = await _approvalRepo.GetByScopeAsync(scopeType, scopeId, installationType);
        return rules.Select(ToRuleDto).ToList();
    }

    public async Task DeleteRuleAsync(Guid ruleId, CancellationToken cancellationToken = default)
    {
        await DeleteRuleAsync(ruleId, null, null, null, cancellationToken);
    }

    public async Task DeleteRuleAsync(Guid ruleId, string? reason, string? changedBy, string? ipAddress, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var existing = await _approvalRepo.GetByIdAsync(ruleId);
        if (existing is null)
            return;

        await _auditService.LogAsync(
            AppApprovalAuditChangeType.Deleted,
            existing,
            existing.Action,
            existing.AutoUpdateEnabled,
            reason,
            changedBy,
            ipAddress);

        await _approvalRepo.DeleteAsync(ruleId);
    }

    public async Task<AppApprovalAuditPageDto> GetAuditHistoryAsync(
        AppInstallationType installationType,
        string? packageId,
        AppApprovalScopeType? scopeType,
        Guid? scopeId,
        string? changedBy,
        DateTime? changedFrom,
        DateTime? changedTo,
        AppApprovalAuditChangeType? changeType,
        int limit,
        Guid? cursor,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return await _auditService.GetHistoryAsync(
            installationType,
            packageId,
            scopeType,
            scopeId,
            changedBy,
            changedFrom,
            changedTo,
            changeType,
            limit,
                cursor);
    }

    public async Task<IReadOnlyList<EffectiveApprovedAppDto>> GetEffectiveAppsAsync(
        Guid? clientId,
        Guid? siteId,
        Guid? agentId,
        AppInstallationType installationType,
        CancellationToken cancellationToken = default)
    {
        return await BuildEffectiveApprovedAppsAsync(clientId, siteId, agentId, installationType, cancellationToken);
    }

    public async Task<EffectiveApprovedAppPageDto> GetEffectiveAppsPageAsync(
        AppApprovalScopeType scopeType,
        Guid? scopeId,
        AppInstallationType installationType,
        string? search,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var normalizedLimit = Math.Clamp(limit, 1, 200);
        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var normalizedCursor = string.IsNullOrWhiteSpace(cursor) ? null : cursor.Trim();
        var scopeContext = await ResolveScopeContextAsync(scopeType, scopeId);
        var effective = await BuildEffectiveApprovedAppsAsync(
            scopeContext.ClientId,
            scopeContext.SiteId,
            scopeContext.AgentId,
            installationType,
            cancellationToken);

        var filtered = effective
            .Where(x => MatchesEffectiveSearch(x, normalizedSearch))
            .OrderBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(normalizedCursor))
        {
            filtered = filtered
                .Where(x => string.Compare(x.PackageId, normalizedCursor, StringComparison.OrdinalIgnoreCase) > 0)
                .ToList();
        }

        var pageSlice = filtered.Take(normalizedLimit + 1).ToList();
        var hasMore = pageSlice.Count > normalizedLimit;
        var pageItems = hasMore ? pageSlice.Take(normalizedLimit).ToList() : pageSlice;

        return new EffectiveApprovedAppPageDto
        {
            ScopeType = scopeType,
            ScopeId = scopeId,
            InstallationType = installationType,
            Search = normalizedSearch,
            Cursor = normalizedCursor,
            NextCursor = hasMore ? pageItems[^1].PackageId : null,
            Limit = normalizedLimit,
            ReturnedItems = pageItems.Count,
            HasMore = hasMore,
            Items = pageItems
        };
    }

    public async Task<AppApprovalPackageDiffDto> GetPackageDiffAsync(
        AppApprovalScopeType scopeType,
        Guid? scopeId,
        AppInstallationType installationType,
        string packageId,
        CancellationToken cancellationToken = default)
    {
        var normalizedPackageId = NormalizePackageId(packageId);
        var scopeContext = await ResolveScopeContextAsync(scopeType, scopeId);
        var rules = await LoadRulesForContextAsync(scopeContext.ClientId, scopeContext.SiteId, scopeContext.AgentId, installationType);
        var selectedRules = SelectLatestRulesForPackage(rules, normalizedPackageId, scopeContext.ScopeType);
        var package = await GetCatalogPackageByIdAsync(installationType, normalizedPackageId, cancellationToken);

        return BuildPackageDiffDto(scopeContext, installationType, normalizedPackageId, selectedRules, package);
    }

    public async Task<AppEffectivePackageDiffPageDto> GetEffectiveAppDiffsAsync(
        AppApprovalScopeType scopeType,
        Guid? scopeId,
        AppInstallationType installationType,
        string? search,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var normalizedLimit = Math.Clamp(limit, 1, 200);
        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var normalizedCursor = string.IsNullOrWhiteSpace(cursor) ? null : cursor.Trim();
        var scopeContext = await ResolveScopeContextAsync(scopeType, scopeId);
        var rules = await LoadRulesForContextAsync(scopeContext.ClientId, scopeContext.SiteId, scopeContext.AgentId, installationType);
        var packageIndex = await GetCatalogPackageIndexAsync(installationType, cancellationToken);

        var effectivePackages = rules
            .GroupBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var selectedRules = SelectLatestRulesForPackage(group, group.Key, scopeContext.ScopeType);
                var effectiveState = BuildEffectiveRuleState(selectedRules, group.Key, scopeContext.ScopeType);
                packageIndex.TryGetValue(group.Key, out var package);
                return new
                {
                    PackageId = group.Key,
                    Package = package,
                    SelectedRules = selectedRules,
                    EffectiveState = effectiveState
                };
            })
            .Where(x => x.EffectiveState.IsAllowed)
            .Where(x => MatchesDiffSearch(x.PackageId, x.Package, normalizedSearch))
            .OrderBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(normalizedCursor))
        {
            effectivePackages = effectivePackages
                .Where(x => string.Compare(x.PackageId, normalizedCursor, StringComparison.OrdinalIgnoreCase) > 0)
                .ToList();
        }

        var pageSlice = effectivePackages
            .Take(normalizedLimit + 1)
            .ToList();

        var hasMore = pageSlice.Count > normalizedLimit;
        var pageItems = hasMore
            ? pageSlice.Take(normalizedLimit).ToList()
            : pageSlice;

        var pagedItems = pageItems
            .Select(x => BuildPackageDiffDto(scopeContext, installationType, x.PackageId, x.SelectedRules, x.Package))
            .ToList();

        return new AppEffectivePackageDiffPageDto
        {
            ScopeType = scopeType,
            ScopeId = scopeId,
            InstallationType = installationType,
            Search = normalizedSearch,
            ReturnedItems = pagedItems.Count,
            Cursor = normalizedCursor,
            NextCursor = hasMore ? pageItems[^1].PackageId : null,
            Limit = normalizedLimit,
            HasMore = hasMore,
            Items = pagedItems
        };
    }

    private async Task<WingetFeedSnapshot> GetWingetFeedAsync(CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue<WingetFeedSnapshot>(WingetCacheKey, out var cached) && cached is not null)
            return cached;

        using var response = await SharedHttpClient.GetAsync(WingetLatestPackagesUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var root = json.RootElement;
        var generated = TryGetDateTime(root, "generated");
        var count = TryGetInt(root, "count");

        var packages = new List<AppCatalogPackageDto>();
        if (root.TryGetProperty("packages", out var packagesElement) && packagesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in packagesElement.EnumerateArray())
            {
                var dto = new AppCatalogPackageDto
                {
                    Id = TryGetString(item, "id"),
                    Name = TryGetString(item, "name"),
                    Publisher = TryGetString(item, "publisher"),
                    Version = TryGetString(item, "version"),
                    Description = TryGetString(item, "description"),
                    Homepage = TryGetString(item, "homepage"),
                    License = TryGetString(item, "license"),
                    Category = TryGetString(item, "category"),
                    Icon = TryGetString(item, "icon"),
                    InstallCommand = TryGetString(item, "installCommand"),
                    LastUpdated = TryGetDateTime(item, "lastUpdated"),
                    Tags = ParseStringArray(item, "tags"),
                    InstallerUrlsByArch = ParseInstallerUrls(item)
                };

                if (!string.IsNullOrWhiteSpace(dto.Id))
                    packages.Add(dto);
            }
        }

        var snapshot = new WingetFeedSnapshot
        {
            GeneratedAt = generated,
            TotalPackages = count > 0 ? count : packages.Count,
            Packages = packages
        };

        _cache.Set(WingetCacheKey, snapshot, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10)
        });

        return snapshot;
    }

    private static string NormalizePackageId(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
            throw new InvalidOperationException("PackageId e obrigatorio.");

        return packageId.Trim();
    }

    private static (Guid? ClientId, Guid? SiteId, Guid? AgentId) ResolveScopeIds(AppApprovalScopeType scopeType, Guid? scopeId)
    {
        return scopeType switch
        {
            AppApprovalScopeType.Global when scopeId is null => (null, null, null),
            AppApprovalScopeType.Client when scopeId.HasValue => (scopeId.Value, null, null),
            AppApprovalScopeType.Site when scopeId.HasValue => (null, scopeId.Value, null),
            AppApprovalScopeType.Agent when scopeId.HasValue => (null, null, scopeId.Value),
            _ => throw new InvalidOperationException("ScopeId invalido para o ScopeType informado.")
        };
    }

    private async Task<ScopeContext> ResolveScopeContextAsync(AppApprovalScopeType scopeType, Guid? scopeId)
    {
        return scopeType switch
        {
            AppApprovalScopeType.Global when scopeId is null => new ScopeContext(scopeType, null, null, null),
            AppApprovalScopeType.Client when scopeId.HasValue => new ScopeContext(scopeType, scopeId.Value, null, null),
            AppApprovalScopeType.Site when scopeId.HasValue => await ResolveSiteScopeContextAsync(scopeId.Value),
            AppApprovalScopeType.Agent when scopeId.HasValue => await ResolveAgentScopeContextAsync(scopeId.Value),
            _ => throw new InvalidOperationException("ScopeId invalido para o ScopeType informado.")
        };
    }

    private async Task<ScopeContext> ResolveSiteScopeContextAsync(Guid siteId)
    {
        var site = await _siteRepository.GetByIdAsync(siteId);
        if (site is null)
            throw new InvalidOperationException("Site not found.");

        return new ScopeContext(AppApprovalScopeType.Site, site.ClientId, site.Id, null);
    }

    private async Task<ScopeContext> ResolveAgentScopeContextAsync(Guid agentId)
    {
        var agent = await _agentRepository.GetByIdAsync(agentId);
        if (agent is null)
            throw new InvalidOperationException("Agent not found.");

        var site = await _siteRepository.GetByIdAsync(agent.SiteId);
        if (site is null)
            throw new InvalidOperationException("Site not found for this agent.");

        return new ScopeContext(AppApprovalScopeType.Agent, site.ClientId, site.Id, agent.Id);
    }

    private async Task<List<Meduza.Core.Entities.AppApprovalRule>> LoadRulesForContextAsync(
        Guid? clientId,
        Guid? siteId,
        Guid? agentId,
        AppInstallationType installationType)
    {
        var rules = new List<Meduza.Core.Entities.AppApprovalRule>();
        rules.AddRange(await _approvalRepo.GetByScopeAsync(AppApprovalScopeType.Global, null, installationType));

        if (clientId.HasValue)
            rules.AddRange(await _approvalRepo.GetByScopeAsync(AppApprovalScopeType.Client, clientId.Value, installationType));
        if (siteId.HasValue)
            rules.AddRange(await _approvalRepo.GetByScopeAsync(AppApprovalScopeType.Site, siteId.Value, installationType));
        if (agentId.HasValue)
            rules.AddRange(await _approvalRepo.GetByScopeAsync(AppApprovalScopeType.Agent, agentId.Value, installationType));

        return rules;
    }

    private async Task<IReadOnlyList<EffectiveApprovedAppDto>> BuildEffectiveApprovedAppsAsync(
        Guid? clientId,
        Guid? siteId,
        Guid? agentId,
        AppInstallationType installationType,
        CancellationToken cancellationToken)
    {
        var rules = await LoadRulesForContextAsync(clientId, siteId, agentId, installationType);
        var scopeOrder = new[]
        {
            AppApprovalScopeType.Global,
            AppApprovalScopeType.Client,
            AppApprovalScopeType.Site,
            AppApprovalScopeType.Agent
        };

        var groupedByPackage = rules
            .GroupBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var effective = new List<EffectiveResolvedRule>();

        foreach (var kvp in groupedByPackage)
        {
            var packageRules = kvp.Value;
            var state = new EffectiveResolvedRule
            {
                PackageId = kvp.Key,
                IsAllowed = false,
                AutoUpdateEnabled = false,
                SourceScope = AppApprovalScopeType.Global
            };

            foreach (var scope in scopeOrder)
            {
                var selectedRule = packageRules
                    .Where(x => x.ScopeType == scope)
                    .OrderByDescending(x => x.UpdatedAt)
                    .FirstOrDefault();

                if (selectedRule is null)
                    continue;

                state.IsAllowed = selectedRule.Action == AppApprovalActionType.Allow;
                state.SourceScope = selectedRule.ScopeType;

                if (selectedRule.AutoUpdateEnabled.HasValue)
                    state.AutoUpdateEnabled = selectedRule.AutoUpdateEnabled.Value;
            }

            if (state.IsAllowed)
                effective.Add(state);
        }

        if (installationType != AppInstallationType.Winget)
        {
            return effective
                .OrderBy(x => x.PackageId)
                .Select(x => new EffectiveApprovedAppDto
                {
                    InstallationType = installationType,
                    PackageId = x.PackageId,
                    AutoUpdateEnabled = x.AutoUpdateEnabled,
                    SourceScope = x.SourceScope
                })
                .ToList();
        }

        var packageIndex = await GetCatalogPackageIndexAsync(installationType, cancellationToken);
        return effective
            .OrderBy(x => x.PackageId)
            .Select(x =>
            {
                if (!packageIndex.TryGetValue(x.PackageId, out var package))
                {
                    return new EffectiveApprovedAppDto
                    {
                        InstallationType = installationType,
                        PackageId = x.PackageId,
                        AutoUpdateEnabled = x.AutoUpdateEnabled,
                        SourceScope = x.SourceScope
                    };
                }

                return new EffectiveApprovedAppDto
                {
                    InstallationType = installationType,
                    PackageId = package.Id,
                    Name = package.Name,
                    Description = package.Description,
                    IconUrl = package.Icon,
                    Publisher = package.Publisher,
                    Version = package.Version,
                    InstallCommand = package.InstallCommand,
                    InstallerUrlsByArch = package.InstallerUrlsByArch,
                    AutoUpdateEnabled = x.AutoUpdateEnabled,
                    SourceScope = x.SourceScope
                };
            })
            .ToList();
    }

    private static EffectiveResolvedRule BuildEffectiveRuleState(
        IReadOnlyList<Meduza.Core.Entities.AppApprovalRule> rules,
        string packageId,
        AppApprovalScopeType maxScopeType)
    {
        var state = new EffectiveResolvedRule
        {
            PackageId = packageId,
            IsAllowed = false,
            AutoUpdateEnabled = false,
            SourceScope = AppApprovalScopeType.Global,
            HasRule = false,
            HasBlockingRule = false
        };

        foreach (var scope in GetApplicableScopes(maxScopeType))
        {
            var selectedRule = rules
                .Where(x => x.ScopeType == scope)
                .OrderByDescending(x => x.UpdatedAt)
                .FirstOrDefault();

            if (selectedRule is null)
                continue;

            state.HasRule = true;
            state.SourceScope = selectedRule.ScopeType;
            state.IsAllowed = selectedRule.Action == AppApprovalActionType.Allow;
            state.HasBlockingRule = selectedRule.Action == AppApprovalActionType.Deny;

            if (selectedRule.AutoUpdateEnabled.HasValue)
                state.AutoUpdateEnabled = selectedRule.AutoUpdateEnabled.Value;
        }

        return state;
    }

    private static List<Meduza.Core.Entities.AppApprovalRule> SelectLatestRulesForPackage(
        IEnumerable<Meduza.Core.Entities.AppApprovalRule> rules,
        string packageId,
        AppApprovalScopeType maxScopeType)
    {
        var applicableScopes = GetApplicableScopes(maxScopeType);
        var selectedRules = new List<Meduza.Core.Entities.AppApprovalRule>();

        foreach (var applicableScope in applicableScopes)
        {
            var rule = rules
                .Where(x => x.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase))
                .Where(x => x.ScopeType == applicableScope)
                .OrderByDescending(x => x.UpdatedAt)
                .FirstOrDefault();

            if (rule is not null)
                selectedRules.Add(rule);
        }

        return selectedRules;
    }

    private AppApprovalPackageDiffDto BuildPackageDiffDto(
        ScopeContext scopeContext,
        AppInstallationType installationType,
        string packageId,
        IReadOnlyList<Meduza.Core.Entities.AppApprovalRule> selectedRules,
        AppCatalogPackageDto? package)
    {
        var effectiveState = BuildEffectiveRuleState(selectedRules, packageId, scopeContext.ScopeType);
        var levels = GetApplicableScopes(scopeContext.ScopeType)
            .Select(applicableScope =>
            {
                var rule = selectedRules.FirstOrDefault(x => x.ScopeType == applicableScope);
                return new AppApprovalDiffLevelDto
                {
                    ScopeType = applicableScope,
                    ScopeId = GetScopeId(applicableScope, scopeContext.ClientId, scopeContext.SiteId, scopeContext.AgentId),
                    Action = rule?.Action,
                    AutoUpdateEnabled = rule?.AutoUpdateEnabled,
                    UpdatedAt = rule?.UpdatedAt,
                    AppliedToEffectiveResult = effectiveState.SourceScope == applicableScope && rule is not null,
                    Outcome = rule is null
                        ? "no-rule"
                        : rule.Action == AppApprovalActionType.Deny
                            ? "blocked"
                            : "allowed",
                    Reason = rule is null
                        ? "Nenhuma regra definida neste nivel."
                        : rule.Action == AppApprovalActionType.Deny
                            ? "Pacote bloqueado neste nivel, sobrescrevendo herdanca anterior."
                            : "Pacote permitido neste nivel."
                };
            })
            .ToList();

        return new AppApprovalPackageDiffDto
        {
            InstallationType = installationType,
            PackageId = packageId,
            PackageName = package?.Name ?? string.Empty,
            Publisher = package?.Publisher ?? string.Empty,
            Description = package?.Description ?? string.Empty,
            Version = package?.Version ?? string.Empty,
            IconUrl = package?.Icon ?? string.Empty,
            IsAllowed = effectiveState.IsAllowed,
            AutoUpdateEnabled = effectiveState.AutoUpdateEnabled,
            EffectiveSourceScope = effectiveState.HasRule ? effectiveState.SourceScope : null,
            EffectiveReason = effectiveState.IsAllowed
                ? effectiveState.HasRule
                    ? $"Permitido por regra no nivel {effectiveState.SourceScope}."
                    : "Nenhuma regra de allow encontrada."
                : effectiveState.HasBlockingRule
                    ? $"Bloqueado por regra deny no nivel {effectiveState.SourceScope}."
                    : "Nenhuma regra de allow encontrada.",
            Levels = levels
        };
    }

    private async Task<IReadOnlyDictionary<string, AppCatalogPackageDto>> GetCatalogPackageIndexAsync(
        AppInstallationType installationType,
        CancellationToken cancellationToken)
    {
        if (installationType != AppInstallationType.Winget)
            return new Dictionary<string, AppCatalogPackageDto>(StringComparer.OrdinalIgnoreCase);

        var feed = await GetWingetFeedAsync(cancellationToken);
        return feed.Packages.ToDictionary(x => x.Id, x => x, StringComparer.OrdinalIgnoreCase);
    }

    private static bool MatchesDiffSearch(string packageId, AppCatalogPackageDto? package, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;

        return ContainsIgnoreCase(packageId, search)
            || ContainsIgnoreCase(package?.Name, search)
            || ContainsIgnoreCase(package?.Publisher, search)
            || ContainsIgnoreCase(package?.Description, search);
    }

    private static bool MatchesEffectiveSearch(EffectiveApprovedAppDto app, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;

        return ContainsIgnoreCase(app.PackageId, search)
            || ContainsIgnoreCase(app.Name, search)
            || ContainsIgnoreCase(app.Publisher, search)
            || ContainsIgnoreCase(app.Description, search);
    }

    private static string EncodeCatalogCursor(AppCatalogPackageDto item)
    {
        var raw = $"{item.Name}\n{item.Id}";
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw));
    }

    private static bool TryDecodeCatalogCursor(string cursor, out string name, out string id)
    {
        name = string.Empty;
        id = string.Empty;

        try
        {
            var raw = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = raw.Split('\n', 2);
            if (parts.Length != 2)
                return false;

            name = parts[0];
            id = parts[1];
            return !string.IsNullOrWhiteSpace(id);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static int CompareCatalogSort(AppCatalogPackageDto item, string cursorName, string cursorId)
    {
        var nameComparison = string.Compare(item.Name, cursorName, StringComparison.OrdinalIgnoreCase);
        if (nameComparison != 0)
            return nameComparison;

        return string.Compare(item.Id, cursorId, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<AppApprovalScopeType> GetApplicableScopes(AppApprovalScopeType scopeType)
    {
        return scopeType switch
        {
            AppApprovalScopeType.Global => [AppApprovalScopeType.Global],
            AppApprovalScopeType.Client => [AppApprovalScopeType.Global, AppApprovalScopeType.Client],
            AppApprovalScopeType.Site => [AppApprovalScopeType.Global, AppApprovalScopeType.Client, AppApprovalScopeType.Site],
            _ => [AppApprovalScopeType.Global, AppApprovalScopeType.Client, AppApprovalScopeType.Site, AppApprovalScopeType.Agent]
        };
    }

    private static Guid? GetScopeId(AppApprovalScopeType scopeType, Guid? clientId, Guid? siteId, Guid? agentId)
    {
        return scopeType switch
        {
            AppApprovalScopeType.Client => clientId,
            AppApprovalScopeType.Site => siteId,
            AppApprovalScopeType.Agent => agentId,
            _ => null
        };
    }

    private static AppApprovalRuleResolvedDto ToRuleDto(Meduza.Core.Entities.AppApprovalRule rule)
    {
        return new AppApprovalRuleResolvedDto
        {
            RuleId = rule.Id,
            ScopeType = rule.ScopeType,
            ScopeId = rule.ScopeType switch
            {
                AppApprovalScopeType.Client => rule.ClientId,
                AppApprovalScopeType.Site => rule.SiteId,
                AppApprovalScopeType.Agent => rule.AgentId,
                _ => null
            },
            InstallationType = rule.InstallationType,
            PackageId = rule.PackageId,
            Action = rule.Action,
            AutoUpdateEnabled = rule.AutoUpdateEnabled,
            UpdatedAt = rule.UpdatedAt
        };
    }

    private static bool ContainsIgnoreCase(string? source, string value)
    {
        return source?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return string.Empty;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.GetRawText()
        };
    }

    private static DateTime? TryGetDateTime(JsonElement element, string propertyName)
    {
        var raw = TryGetString(element, propertyName);
        if (DateTime.TryParse(raw, out var parsed))
            return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);

        return null;
    }

    private static int TryGetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return 0;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
            return intValue;

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsedFromString))
            return parsedFromString;

        return 0;
    }

    private static IReadOnlyList<string> ParseStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    result.Add(value.Trim());
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> ParseInstallerUrls(JsonElement element)
    {
        if (!element.TryGetProperty("installerUrlsByArch", out var obj) || obj.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in obj.EnumerateObject())
        {
            var value = property.Value;
            if (value.ValueKind != JsonValueKind.String)
                continue;

            var url = value.GetString();
            if (!string.IsNullOrWhiteSpace(url))
                result[property.Name] = url.Trim();
        }

        return result;
    }

    private sealed class WingetFeedSnapshot
    {
        public DateTime? GeneratedAt { get; init; }
        public int TotalPackages { get; init; }
        public IReadOnlyList<AppCatalogPackageDto> Packages { get; init; } = [];
    }

    private sealed class EffectiveResolvedRule
    {
        public string PackageId { get; init; } = string.Empty;
        public bool IsAllowed { get; set; }
        public bool AutoUpdateEnabled { get; set; }
        public AppApprovalScopeType SourceScope { get; set; }
        public bool HasRule { get; set; }
        public bool HasBlockingRule { get; set; }
    }

    private sealed record ScopeContext(AppApprovalScopeType ScopeType, Guid? ClientId, Guid? SiteId, Guid? AgentId);

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Meduza-AppStore/1.0");
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }
}
