using System.Text.Json;
using Meduza.Core.DTOs;
using Meduza.Core.Enums;
using Meduza.Core.Interfaces;

namespace Meduza.Infrastructure.Services;

public class AppStoreService : IAppStoreService
{
    private readonly IAppApprovalRuleRepository _approvalRepo;
    private readonly IAppApprovalAuditService _auditService;
    private readonly IConfigurationResolver _configurationResolver;
    private readonly ISiteRepository _siteRepository;
    private readonly IAgentRepository _agentRepository;
    private readonly IWingetPackageRepository _wingetRepo;
    private readonly IChocolateyPackageRepository _chocolateyRepo;
    private readonly IAppPackageRepository _appPackageRepo;

    public AppStoreService(
        IAppApprovalRuleRepository approvalRepo,
        IAppApprovalAuditService auditService,
        IConfigurationResolver configurationResolver,
        ISiteRepository siteRepository,
        IAgentRepository agentRepository,
        IWingetPackageRepository wingetRepo,
        IChocolateyPackageRepository chocolateyRepo,
        IAppPackageRepository appPackageRepo)
    {
        _approvalRepo = approvalRepo;
        _auditService = auditService;
        _configurationResolver = configurationResolver;
        _siteRepository = siteRepository;
        _agentRepository = agentRepository;
        _wingetRepo = wingetRepo;
        _chocolateyRepo = chocolateyRepo;
        _appPackageRepo = appPackageRepo;
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
        var offset = DecodeUnifiedCursor(normalizedCursor);

        var (items, total) = await _appPackageRepo.SearchAsync(
            installationType,
            normalizedSearch,
            normalizedArch,
            normalizedLimit + 1,
            offset,
            cancellationToken);

        var hasMore = items.Count > normalizedLimit;
        var page = hasMore ? items.Take(normalizedLimit).ToList() : items.ToList();
        var nextCursor = hasMore ? EncodeUnifiedCursor(offset + normalizedLimit) : null;

        return new AppCatalogSearchResultDto
        {
            GeneratedAt = page.FirstOrDefault()?.SourceGeneratedAt,
            TotalPackagesInSource = total,
            ReturnedItems = page.Count,
            Cursor = cursor,
            NextCursor = nextCursor,
            Limit = normalizedLimit,
            HasMore = hasMore,
            Search = normalizedSearch,
            Architecture = normalizedArch,
            Items = page.Select(MapUnifiedToDto).ToList()
        };
    }

    public async Task<AppCatalogPackageDto?> GetCatalogPackageByIdAsync(
        AppInstallationType installationType,
        string packageId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageId))
            return null;

        var package = await _appPackageRepo.GetByInstallationTypeAndPackageIdAsync(
            installationType,
            packageId.Trim(),
            cancellationToken);

        return package is null ? null : MapUnifiedToDto(package);
    }

    public async Task<AppCatalogPackageDto> UpsertCustomCatalogPackageAsync(
        UpsertCustomAppCatalogPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.PackageId))
            throw new InvalidOperationException("PackageId e obrigatorio.");

        if (string.IsNullOrWhiteSpace(request.Name))
            throw new InvalidOperationException("Name e obrigatorio.");

        var saved = await _appPackageRepo.UpsertCustomAsync(new Meduza.Core.Entities.AppPackage
        {
            InstallationType = AppInstallationType.Custom,
            PackageId = request.PackageId,
            Name = request.Name.Trim(),
            Publisher = request.Publisher,
            Version = request.Version,
            Description = request.Description,
            IconUrl = request.IconUrl,
            SiteUrl = request.SiteUrl,
            InstallCommand = request.InstallCommand,
            MetadataJson = request.MetadataJson,
            FileObjectKey = request.FileObjectKey,
            FileBucket = request.FileBucket,
            FilePublicUrl = request.FilePublicUrl,
            FileContentType = request.FileContentType,
            FileSizeBytes = request.FileSizeBytes,
            FileChecksum = request.FileChecksum,
            LastUpdated = DateTime.UtcNow
        }, cancellationToken);

        return MapUnifiedToDto(saved);
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
        var normalizedPackageId = NormalizePackageId(packageId, installationType);
        var (clientId, siteId, agentId) = ResolveScopeIds(scopeType, scopeId);

        if (installationType == AppInstallationType.Winget)
        {
            var package = await GetCatalogPackageByIdAsync(installationType, normalizedPackageId, cancellationToken);
            if (package is null)
                throw new InvalidOperationException($"Package '{normalizedPackageId}' nao encontrado no catalogo Winget. Execute uma sincronizacao primeiro.");
        }

        if (installationType == AppInstallationType.Chocolatey)
        {
            var package = await GetCatalogPackageByIdAsync(installationType, normalizedPackageId, cancellationToken);
            if (package is null)
                throw new InvalidOperationException($"Package '{normalizedPackageId}' nao encontrado no catalogo Chocolatey. Execute uma sincronizacao primeiro.");
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
        var policy = await ResolvePolicyForDirectScopeAsync(clientId, siteId, agentId, cancellationToken);
        if (policy == AppStorePolicyType.Disabled)
            return [];

        if (policy == AppStorePolicyType.All)
            return await BuildAllCatalogAsEffectiveAppsAsync(
                installationType,
                GetDirectSourceScope(clientId, siteId, agentId),
                cancellationToken);

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
        var policy = await ResolvePolicyForScopeAsync(scopeContext, cancellationToken);

        if (policy == AppStorePolicyType.Disabled)
        {
            return new EffectiveApprovedAppPageDto
            {
                ScopeType = scopeType,
                ScopeId = scopeId,
                InstallationType = installationType,
                Search = normalizedSearch,
                Cursor = normalizedCursor,
                NextCursor = null,
                Limit = normalizedLimit,
                ReturnedItems = 0,
                HasMore = false,
                Items = []
            };
        }

        if (policy == AppStorePolicyType.All)
        {
            return await BuildEffectivePageFromCatalogAsync(
                scopeType,
                scopeId,
                installationType,
                normalizedSearch,
                normalizedLimit,
                normalizedCursor,
                cancellationToken);
        }

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
        var normalizedPackageId = NormalizePackageId(packageId, installationType);
        var scopeContext = await ResolveScopeContextAsync(scopeType, scopeId);
        var policy = await ResolvePolicyForScopeAsync(scopeContext, cancellationToken);
        var package = await GetCatalogPackageByIdAsync(installationType, normalizedPackageId, cancellationToken);

        if (policy == AppStorePolicyType.Disabled)
        {
            return BuildPolicyDrivenDiffDto(
                installationType,
                normalizedPackageId,
                package,
                false,
                "Bloqueado pela politica efetiva AppStorePolicy=Disabled.");
        }

        if (policy == AppStorePolicyType.All)
        {
            return BuildPolicyDrivenDiffDto(
                installationType,
                normalizedPackageId,
                package,
                true,
                "Permitido pela politica efetiva AppStorePolicy=All.");
        }

        var rules = await LoadRulesForContextAsync(scopeContext.ClientId, scopeContext.SiteId, scopeContext.AgentId, installationType);
        var selectedRules = SelectLatestRulesForPackage(rules, normalizedPackageId, scopeContext.ScopeType);

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
        var policy = await ResolvePolicyForScopeAsync(scopeContext, cancellationToken);

        if (policy == AppStorePolicyType.Disabled)
        {
            return new AppEffectivePackageDiffPageDto
            {
                ScopeType = scopeType,
                ScopeId = scopeId,
                InstallationType = installationType,
                Search = normalizedSearch,
                ReturnedItems = 0,
                Cursor = normalizedCursor,
                NextCursor = null,
                Limit = normalizedLimit,
                HasMore = false,
                Items = []
            };
        }

        if (policy == AppStorePolicyType.All)
        {
            return await BuildDiffPageFromCatalogAsync(
                scopeType,
                scopeId,
                installationType,
                normalizedSearch,
                normalizedLimit,
                normalizedCursor,
                cancellationToken);
        }

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

    private static string NormalizePackageId(string packageId, AppInstallationType installationType = AppInstallationType.Winget)
    {
        if (string.IsNullOrWhiteSpace(packageId))
            throw new InvalidOperationException("PackageId e obrigatorio.");

        // Chocolatey package IDs are case-insensitive; normalize to lowercase.
        return installationType == AppInstallationType.Chocolatey
            ? packageId.Trim().ToLowerInvariant()
            : packageId.Trim();
    }

    private async Task<AppStorePolicyType> ResolvePolicyForScopeAsync(
        ScopeContext scopeContext,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        if (scopeContext.SiteId.HasValue)
        {
            var resolved = await _configurationResolver.ResolveForSiteAsync(scopeContext.SiteId.Value);
            return resolved.AppStorePolicy;
        }

        var server = await _configurationResolver.GetServerAsync();
        if (scopeContext.ClientId.HasValue)
        {
            var client = await _configurationResolver.GetClientAsync(scopeContext.ClientId.Value);
            return client?.AppStorePolicy ?? server.AppStorePolicy;
        }

        return server.AppStorePolicy;
    }

    private async Task<AppStorePolicyType> ResolvePolicyForDirectScopeAsync(
        Guid? clientId,
        Guid? siteId,
        Guid? agentId,
        CancellationToken cancellationToken)
    {
        if (siteId.HasValue)
        {
            var resolved = await _configurationResolver.ResolveForSiteAsync(siteId.Value);
            return resolved.AppStorePolicy;
        }

        if (agentId.HasValue)
        {
            var agent = await _agentRepository.GetByIdAsync(agentId.Value);
            if (agent is null)
                throw new InvalidOperationException("Agent not found.");

            var resolved = await _configurationResolver.ResolveForSiteAsync(agent.SiteId);
            return resolved.AppStorePolicy;
        }

        var scopeContext = new ScopeContext(
            clientId.HasValue ? AppApprovalScopeType.Client : AppApprovalScopeType.Global,
            clientId,
            null,
            null);

        return await ResolvePolicyForScopeAsync(scopeContext, cancellationToken);
    }

    private static AppApprovalScopeType GetDirectSourceScope(Guid? clientId, Guid? siteId, Guid? agentId)
    {
        if (agentId.HasValue)
            return AppApprovalScopeType.Agent;

        if (siteId.HasValue)
            return AppApprovalScopeType.Site;

        if (clientId.HasValue)
            return AppApprovalScopeType.Client;

        return AppApprovalScopeType.Global;
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

    private async Task<IReadOnlyList<EffectiveApprovedAppDto>> BuildAllCatalogAsEffectiveAppsAsync(
        AppInstallationType installationType,
        AppApprovalScopeType sourceScope,
        CancellationToken cancellationToken)
    {
        var items = await _appPackageRepo.GetAllByInstallationTypeAsync(installationType, cancellationToken);
        return items
            .OrderBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase)
            .Select(x => MapCatalogToEffectiveDto(MapUnifiedToDto(x), installationType, sourceScope))
            .ToList();
    }

    private async Task<EffectiveApprovedAppPageDto> BuildEffectivePageFromCatalogAsync(
        AppApprovalScopeType scopeType,
        Guid? scopeId,
        AppInstallationType installationType,
        string? search,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var offset = DecodeUnifiedCursor(cursor);
        var (items, _) = await _appPackageRepo.SearchAsync(
            installationType,
            search,
            null,
            limit + 1,
            offset,
            cancellationToken);

        var hasMore = items.Count > limit;
        var page = hasMore ? items.Take(limit).ToList() : items.ToList();
        var nextCursor = hasMore ? EncodeUnifiedCursor(offset + limit) : null;

        return new EffectiveApprovedAppPageDto
        {
            ScopeType = scopeType,
            ScopeId = scopeId,
            InstallationType = installationType,
            Search = search,
            Cursor = cursor,
            NextCursor = nextCursor,
            Limit = limit,
            ReturnedItems = page.Count,
            HasMore = hasMore,
            Items = page
                .Select(x => MapCatalogToEffectiveDto(MapUnifiedToDto(x), installationType, scopeType))
                .ToList()
        };
    }

    private async Task<AppEffectivePackageDiffPageDto> BuildDiffPageFromCatalogAsync(
        AppApprovalScopeType scopeType,
        Guid? scopeId,
        AppInstallationType installationType,
        string? search,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var offset = DecodeUnifiedCursor(cursor);
        var (items, _) = await _appPackageRepo.SearchAsync(
            installationType,
            search,
            null,
            limit + 1,
            offset,
            cancellationToken);

        var hasMore = items.Count > limit;
        var page = hasMore ? items.Take(limit).ToList() : items.ToList();
        var nextCursor = hasMore ? EncodeUnifiedCursor(offset + limit) : null;

        var diffItems = page
            .Select(x =>
            {
                var dto = MapUnifiedToDto(x);
                return BuildPolicyDrivenDiffDto(
                    installationType,
                    dto.Id,
                    dto,
                    true,
                    "Permitido pela politica efetiva AppStorePolicy=All.");
            })
            .ToList();

        return new AppEffectivePackageDiffPageDto
        {
            ScopeType = scopeType,
            ScopeId = scopeId,
            InstallationType = installationType,
            Search = search,
            ReturnedItems = diffItems.Count,
            Cursor = cursor,
            NextCursor = nextCursor,
            Limit = limit,
            HasMore = hasMore,
            Items = diffItems
        };
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

        var packageIndex = await GetCatalogPackageIndexAsync(installationType, cancellationToken);

        if (installationType != AppInstallationType.Winget && installationType != AppInstallationType.Chocolatey)
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
        var items = await _appPackageRepo.GetAllByInstallationTypeAsync(installationType, cancellationToken);
        return items.ToDictionary(
            x => x.PackageId,
            MapUnifiedToDto,
            StringComparer.OrdinalIgnoreCase);
    }

    private static AppCatalogPackageDto MapUnifiedToDto(Meduza.Core.Entities.AppPackage pkg)
    {
        var installerUrls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tags = Array.Empty<string>();
        var category = string.Empty;
        var license = string.Empty;

        if (!string.IsNullOrWhiteSpace(pkg.MetadataJson))
        {
            try
            {
                using var json = JsonDocument.Parse(pkg.MetadataJson);
                var root = json.RootElement;
                tags = ParseStringArray(root, "tags").ToArray();
                installerUrls = ParseInstallerUrls(root).ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
                category = TryGetString(root, "category");
                license = TryGetString(root, "license");
                if (string.IsNullOrWhiteSpace(license))
                    license = TryGetString(root, "licenseUrl");
            }
            catch (JsonException)
            {
                // Metadata malformado nao deve derrubar listagem.
            }
        }

        return new AppCatalogPackageDto
        {
            Id = pkg.PackageId,
            Name = pkg.Name,
            Publisher = pkg.Publisher ?? string.Empty,
            Version = pkg.Version ?? string.Empty,
            Description = pkg.Description ?? string.Empty,
            Homepage = pkg.SiteUrl ?? string.Empty,
            License = license,
            Category = category,
            Icon = pkg.IconUrl ?? string.Empty,
            InstallCommand = pkg.InstallCommand ?? string.Empty,
            LastUpdated = pkg.LastUpdated,
            Tags = tags,
            InstallerUrlsByArch = installerUrls
        };
    }

    private static EffectiveApprovedAppDto MapCatalogToEffectiveDto(
        AppCatalogPackageDto package,
        AppInstallationType installationType,
        AppApprovalScopeType sourceScope)
    {
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
            AutoUpdateEnabled = false,
            SourceScope = sourceScope
        };
    }

    private static AppApprovalPackageDiffDto BuildPolicyDrivenDiffDto(
        AppInstallationType installationType,
        string packageId,
        AppCatalogPackageDto? package,
        bool isAllowed,
        string reason)
    {
        return new AppApprovalPackageDiffDto
        {
            InstallationType = installationType,
            PackageId = packageId,
            PackageName = package?.Name ?? string.Empty,
            Publisher = package?.Publisher ?? string.Empty,
            Description = package?.Description ?? string.Empty,
            Version = package?.Version ?? string.Empty,
            IconUrl = package?.Icon ?? string.Empty,
            IsAllowed = isAllowed,
            AutoUpdateEnabled = false,
            EffectiveSourceScope = null,
            EffectiveReason = reason,
            Levels = []
        };
    }

    private static string? EncodeUnifiedCursor(int offset)
    {
        if (offset <= 0)
            return null;

        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"app:{offset}"));
    }

    private static int DecodeUnifiedCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return 0;

        try
        {
            var raw = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            if (raw.StartsWith("app:") && int.TryParse(raw[4..], out var offset))
                return offset;
        }
        catch (FormatException)
        {
        }

        return 0;
    }

    // ── Winget helpers ───────────────────────────────────────────────────────

    private async Task<AppCatalogSearchResultDto> SearchWingetCatalogAsync(
        string? search,
        string? architecture,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var offset = DecodeWingetCursor(cursor);
        var (items, total) = await _wingetRepo.SearchAsync(search, architecture, limit + 1, offset, cancellationToken);

        var hasMore = items.Count > limit;
        var page = hasMore ? items.Take(limit).ToList() : (IReadOnlyList<Meduza.Core.Entities.WingetPackage>)items;
        var nextCursor = hasMore ? EncodeWingetCursor(offset + limit) : null;

        return new AppCatalogSearchResultDto
        {
            GeneratedAt = page.FirstOrDefault()?.SourceGeneratedAt,
            TotalPackagesInSource = total,
            ReturnedItems = page.Count,
            Cursor = cursor,
            NextCursor = nextCursor,
            Limit = limit,
            HasMore = hasMore,
            Search = search,
            Architecture = architecture,
            Items = page.Select(MapWingetToDto).ToList()
        };
    }

    private async Task<AppCatalogPackageDto?> GetWingetPackageByIdAsync(
        string packageId,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var pkg = await _wingetRepo.GetByPackageIdAsync(packageId);
        return pkg is null ? null : MapWingetToDto(pkg);
    }

    private async Task<IReadOnlyDictionary<string, AppCatalogPackageDto>> BuildWingetPackageIndexAsync(
        CancellationToken cancellationToken)
    {
        var (items, _) = await _wingetRepo.SearchAsync(null, null, int.MaxValue, 0, cancellationToken);
        return items.ToDictionary(
            x => x.PackageId,
            MapWingetToDto,
            StringComparer.OrdinalIgnoreCase);
    }

    private static AppCatalogPackageDto MapWingetToDto(Meduza.Core.Entities.WingetPackage pkg)
    {
        Dictionary<string, string>? installerUrls;
        try
        {
            installerUrls = JsonSerializer.Deserialize<Dictionary<string, string>>(pkg.InstallerUrlsJson);
        }
        catch (JsonException)
        {
            installerUrls = null;
        }

        return new AppCatalogPackageDto
        {
            Id = pkg.PackageId,
            Name = pkg.Name,
            Publisher = pkg.Publisher,
            Version = pkg.Version,
            Description = pkg.Description,
            Homepage = pkg.Homepage,
            License = pkg.License,
            Category = pkg.Category,
            Icon = pkg.Icon,
            InstallCommand = pkg.InstallCommand,
            LastUpdated = pkg.LastUpdated,
            Tags = pkg.Tags.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            InstallerUrlsByArch = installerUrls ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static string? EncodeWingetCursor(int offset)
    {
        if (offset <= 0) return null;
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"winget:{offset}"));
    }

    private static int DecodeWingetCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        try
        {
            var raw = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            if (raw.StartsWith("winget:") && int.TryParse(raw[7..], out var offset))
                return offset;
        }
        catch (FormatException) { }
        return 0;
    }

    // ── Chocolatey helpers ───────────────────────────────────────────────────

    private async Task<AppCatalogSearchResultDto> SearchChocolateyCatalogAsync(
        string? search,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var offset = DecodeChocolateyCursor(cursor);
        var (items, total) = await _chocolateyRepo.SearchAsync(search, limit + 1, offset, cancellationToken);

        var hasMore = items.Count > limit;
        var page = hasMore ? items.Take(limit).ToList() : (IReadOnlyList<Meduza.Core.Entities.ChocolateyPackage>)items;
        var nextCursor = hasMore ? EncodeChocolateyCursor(offset + limit) : null;

        return new AppCatalogSearchResultDto
        {
            GeneratedAt = null,
            TotalPackagesInSource = total,
            ReturnedItems = page.Count,
            Cursor = cursor,
            NextCursor = nextCursor,
            Limit = limit,
            HasMore = hasMore,
            Search = search,
            Architecture = null,
            Items = page.Select(MapChocolateyToDto).ToList()
        };
    }

    private async Task<AppCatalogPackageDto?> GetChocolateyPackageByIdAsync(
        string packageId,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var pkg = await _chocolateyRepo.GetByPackageIdAsync(packageId);
        return pkg is null ? null : MapChocolateyToDto(pkg);
    }

    private async Task<IReadOnlyDictionary<string, AppCatalogPackageDto>> BuildChocolateyPackageIndexAsync(
        CancellationToken cancellationToken)
    {
        // Build a full dictionary for packages already approved (IDs known from rules).
        // We search without filter to get all available packages, using large limit.
        var (items, _) = await _chocolateyRepo.SearchAsync(null, int.MaxValue, 0, cancellationToken);
        return items.ToDictionary(
            x => x.PackageId,
            MapChocolateyToDto,
            StringComparer.OrdinalIgnoreCase);
    }

    private static AppCatalogPackageDto MapChocolateyToDto(Meduza.Core.Entities.ChocolateyPackage pkg)
    {
        return new AppCatalogPackageDto
        {
            Id = pkg.PackageId,
            Name = pkg.Name,
            Publisher = pkg.Publisher,
            Version = pkg.Version,
            Description = pkg.Description,
            Homepage = pkg.Homepage,
            License = pkg.LicenseUrl,
            Category = string.Empty,
            Icon = string.Empty,
            InstallCommand = $"choco install {pkg.PackageId}",
            LastUpdated = pkg.LastUpdated,
            Tags = pkg.Tags.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            InstallerUrlsByArch = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static string? EncodeChocolateyCursor(int offset)
    {
        if (offset <= 0) return null;
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"choco:{offset}"));
    }

    private static int DecodeChocolateyCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        try
        {
            var raw = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            if (raw.StartsWith("choco:") && int.TryParse(raw[6..], out var offset))
                return offset;
        }
        catch (FormatException) { }
        return 0;
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

}
