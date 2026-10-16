using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;

namespace Discovery.Infrastructure.Services;

public class AppStoreService : IAppStoreService
{
    /// <summary>Limite de app_approval_rules.reason (varchar(500), migração M226).</summary>
    private const int MaxReasonLength = 500;

    /// <summary>
    /// Teto do texto de instalação/desinstalação do pacote custom. A coluna é
    /// <c>text</c> (migração M227) — scripts PowerShell/CMD reais passam de 9 mil
    /// caracteres — e este limite de aplicação evita aceitar um corpo sem fim.
    /// O editor da Loja usa o mesmo valor (COMMAND_MAX).
    /// </summary>
    public const int MaxCustomCommandLength = 200_000;

    private readonly IAppApprovalRuleRepository _approvalRepo;
    private readonly IAppApprovalAuditService _auditService;
    private readonly IConfigurationResolver _configurationResolver;
    private readonly ISiteRepository _siteRepository;
    private readonly IAgentRepository _agentRepository;
    private readonly IAppPackageRepository _appPackageRepo;
    private readonly ICustomPackageAssetService _customAssets;

    public AppStoreService(
        IAppApprovalRuleRepository approvalRepo,
        IAppApprovalAuditService auditService,
        IConfigurationResolver configurationResolver,
        ISiteRepository siteRepository,
        IAgentRepository agentRepository,
        IAppPackageRepository appPackageRepo,
        ICustomPackageAssetService customAssets)
    {
        _approvalRepo = approvalRepo;
        _auditService = auditService;
        _configurationResolver = configurationResolver;
        _siteRepository = siteRepository;
        _agentRepository = agentRepository;
        _appPackageRepo = appPackageRepo;
        _customAssets = customAssets;
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

        var (items, total) = await _appPackageRepo.SearchPageAsync(
            installationType,
            normalizedSearch,
            normalizedArch,
            normalizedCursor,
            normalizedLimit + 1,
            cancellationToken);

        var hasMore = items.Count > normalizedLimit;
        var page = hasMore ? items.Take(normalizedLimit).ToList() : items.ToList();
        var lastItem = hasMore ? page[^1] : null;
        var nextCursor = hasMore && lastItem is not null
            ? CursorPaginationHelper.EncodeNameCursor(lastItem.Name, lastItem.Id)
            : null;

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

        // O agent executa este comando para instalar; sem ele o pacote é inútil.
        if (string.IsNullOrWhiteSpace(request.InstallCommand))
            throw new InvalidOperationException("InstallCommand e obrigatorio: informe o comando de instalacao do pacote.");

        // O objeto enviado pode ter sido descartado pela limpeza de uploads não
        // vinculados (24h): sem esta checagem o pacote ficaria apontando para um
        // arquivo inexistente e a instalação falharia só no agent.
        if (!string.IsNullOrWhiteSpace(request.FileObjectKey))
        {
            bool? stillExists = null;
            try
            {
                stillExists = await _customAssets.DescribeCompletedAssetAsync(
                    request.FileObjectKey!, request.FileName ?? "arquivo", null, cancellationToken) is not null;
            }
            catch (NotSupportedException)
            {
                // Provider sem suporte à checagem (ex.: storage local): segue sem validar.
                stillExists = null;
            }
            catch (InvalidOperationException)
            {
                stillExists = null;
            }

            if (stillExists == false)
            {
                throw new InvalidOperationException(
                    "O arquivo enviado não está mais disponível no object storage. " +
                    "Uploads que não são vinculados a um pacote são removidos após 24h — anexe o instalador novamente.");
            }
        }

        // Tipo do comando: 0 Command (cmd /C), 1 CmdScript (.cmd), 2 PowerShellScript (.ps1).
        var installCommandKind = NormalizeCommandKind(request.InstallCommandKind, "InstallCommandKind");
        var uninstallCommandKind = NormalizeCommandKind(request.UninstallCommandKind, "UninstallCommandKind");

        // Deteccao: cada modo exige os campos que o agent precisa para decidir se ja
        // esta instalado. Sem eles a deteccao nunca daria evidencia positiva.
        var detectionMode = NormalizeDetectionMode(request.DetectionMode);
        var inventorySoftwareId = TrimToNull(request.InventorySoftwareId);
        var detectionName = TrimToNull(request.DetectionName);
        var detectionPublisher = TrimToNull(request.DetectionPublisher);
        var detectionMinVersion = TrimToNull(request.DetectionMinVersion);
        var detectionCommand = TrimToNull(request.DetectionCommand);

        if (detectionMode == AppDetectionMode.InventoryLink && detectionName is null)
            throw new InvalidOperationException("DetectionName e obrigatorio quando DetectionMode=InventoryLink.");

        if (detectionMode == AppDetectionMode.Command && detectionCommand is null)
            throw new InvalidOperationException("DetectionCommand e obrigatorio quando DetectionMode=Command.");

        if (request.DetectionCommand is { Length: > 2000 })
            throw new InvalidOperationException("DetectionCommand excede o limite de 2000 caracteres.");

        // O texto do comando é o script inteiro (PowerShell/CMD): o limite é só
        // uma trava de payload, não uma regra de negócio do tamanho do script.
        if (request.InstallCommand is { Length: > MaxCustomCommandLength })
            throw new InvalidOperationException(
                $"InstallCommand excede o limite de {MaxCustomCommandLength} caracteres.");

        if (request.UninstallCommand is { Length: > MaxCustomCommandLength })
            throw new InvalidOperationException(
                $"UninstallCommand excede o limite de {MaxCustomCommandLength} caracteres.");

        var normalizedPackageId = request.PackageId.Trim();

        // Estado ANTERIOR: trocar o instalador/ícone deixa o objeto antigo no
        // storage — sem apagá-lo o vazamento é permanente (o índice de limpeza
        // dele já expirou muito antes de o vínculo ser trocado).
        var previous = await _appPackageRepo.GetByInstallationTypeAndPackageIdAsync(
            AppInstallationType.Custom, normalizedPackageId, cancellationToken);

        // Quando ha icone enviado ao storage, a loja serve a imagem pela propria
        // API (endpoint anonimo /custom/{id}/icon); caso contrario vale a URL externa.
        var iconUrl = !string.IsNullOrWhiteSpace(request.IconObjectKey)
            ? $"/api/v1/app-store/custom/{Uri.EscapeDataString(normalizedPackageId)}/icon"
            : request.IconUrl;

        var saved = await _appPackageRepo.UpsertCustomAsync(new Discovery.Core.Entities.AppPackage
        {
            InstallationType = AppInstallationType.Custom,
            PackageId = normalizedPackageId,
            Name = request.Name.Trim(),
            Publisher = request.Publisher,
            Version = request.Version,
            Description = request.Description,
            IconUrl = iconUrl,
            SiteUrl = request.SiteUrl,
            InstallCommand = request.InstallCommand,
            UninstallCommand = request.UninstallCommand,
            InstallCommandKind = installCommandKind,
            UninstallCommandKind = uninstallCommandKind,
            DetectionMode = detectionMode,
            InventorySoftwareId = inventorySoftwareId,
            DetectionName = detectionName,
            DetectionPublisher = detectionPublisher,
            DetectionMinVersion = detectionMinVersion,
            DetectionCommand = detectionCommand,
            ExtractZip = request.ExtractZip,
            FileName = request.FileName,
            IconObjectKey = request.IconObjectKey,
            MetadataJson = request.MetadataJson,
            FileObjectKey = request.FileObjectKey,
            FileBucket = request.FileBucket,
            FilePublicUrl = request.FilePublicUrl,
            FileContentType = request.FileContentType,
            FileSizeBytes = request.FileSizeBytes,
            FileChecksum = request.FileChecksum,
            LastUpdated = DateTime.UtcNow
        }, cancellationToken);

        await DeleteReplacedAssetAsync(previous?.FileObjectKey, request.FileObjectKey, cancellationToken);
        await DeleteReplacedAssetAsync(previous?.IconObjectKey, request.IconObjectKey, cancellationToken);

        return MapUnifiedToDto(saved);
    }

    /// <summary>
    /// Remove do storage o asset que foi SUBSTITUÍDO no cadastro (a linha do
    /// catálogo já aponta para o novo). Best-effort: uma falha aqui não invalida o
    /// salvamento — o objeto antigo fica sem referência e o serviço de limpeza o
    /// remove depois.
    ///
    /// Sem <paramref name="newObjectKey"/> NÃO houve substituição: o editor só
    /// envia os campos de arquivo quando o operador sobe um arquivo NOVO, e numa
    /// edição parcial o repositório PRESERVA a chave vinculada (ver
    /// <c>AppPackageRepository.UpsertCustomAsync</c>). Tratar esse caso como
    /// "arquivo substituído" apagava do storage o instalador que a linha continuava
    /// apontando — o pacote ficava publicado sem arquivo e o download no agent
    /// falhava (HTTP 406/404) sem forma de recuperar o objeto.
    /// </summary>
    private async Task DeleteReplacedAssetAsync(
        string? previousObjectKey,
        string? newObjectKey,
        CancellationToken cancellationToken)
    {
        // Edição parcial (request sem chave de asset): nada foi substituído.
        if (string.IsNullOrWhiteSpace(newObjectKey))
            return;

        if (string.IsNullOrWhiteSpace(previousObjectKey))
            return;

        if (string.Equals(previousObjectKey, newObjectKey, StringComparison.Ordinal))
            return;

        try
        {
            await _customAssets.DeleteAsync(previousObjectKey, cancellationToken);
        }
        catch
        {
            // best-effort
        }
    }

    public async Task<bool> DeleteCustomPackageFileAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageId))
            return false;

        var previous = await _appPackageRepo.ClearCustomFileAsync(packageId.Trim(), cancellationToken);
        if (previous is null)
            return false;

        // Best-effort: a linha do catalogo e a fonte de verdade; uma falha ao
        // remover o objeto no storage nao deve impedir a exclusao do instalador.
        if (!string.IsNullOrWhiteSpace(previous.FileObjectKey))
        {
            try
            {
                await _customAssets.DeleteAsync(previous.FileObjectKey, cancellationToken);
            }
            catch
            {
                // Objeto orfao no storage nao quebra a operacao.
            }
        }

        return true;
    }

    public async Task<bool> DeleteCustomPackageIconAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageId))
            return false;

        var previous = await _appPackageRepo.ClearCustomIconAsync(packageId.Trim(), cancellationToken);
        if (previous is null)
            return false;

        // Best-effort: a linha do catalogo e a fonte de verdade; uma falha ao
        // remover o objeto no storage nao deve impedir a exclusao do icone.
        if (!string.IsNullOrWhiteSpace(previous.IconObjectKey))
        {
            try
            {
                await _customAssets.DeleteAsync(previous.IconObjectKey, cancellationToken);
            }
            catch
            {
                // Objeto orfao no storage nao quebra a operacao.
            }
        }

        return true;
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
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

        // A coluna app_approval_rules.reason é varchar(500) (M226). Sem esta
        // validação o INSERT estoura 22001 e o operador recebe um 500 genérico.
        if (normalizedReason is not null && normalizedReason.Length > MaxReasonLength)
            throw new InvalidOperationException($"Motivo muito longo: o limite e de {MaxReasonLength} caracteres.");

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
            var created = await _approvalRepo.CreateAsync(new Discovery.Core.Entities.AppApprovalRule
            {
                ScopeType = scopeType,
                ClientId = clientId,
                SiteId = siteId,
                AgentId = agentId,
                InstallationType = installationType,
                PackageId = normalizedPackageId,
                Action = action,
                AutoUpdateEnabled = autoUpdateEnabled,
                Reason = normalizedReason
            });

            await _auditService.LogAsync(
                AppApprovalAuditChangeType.Created,
                created,
                null,
                null,
                normalizedReason,
                changedBy,
                ipAddress);

            var createdDto = ToRuleDto(created);
            await EnrichRuleDtosAsync([createdDto], cancellationToken);
            return createdDto;
        }

        var oldAction = existing.Action;
        var oldAutoUpdateEnabled = existing.AutoUpdateEnabled;
        existing.Action = action;
        existing.AutoUpdateEnabled = autoUpdateEnabled;
        // Motivo é opcional: um save sem motivo não apaga o que já estava gravado.
        if (normalizedReason is not null)
            existing.Reason = normalizedReason;
        await _approvalRepo.UpdateAsync(existing);

        var updated = await _approvalRepo.GetByIdAsync(existing.Id) ?? existing;
        await _auditService.LogAsync(
            AppApprovalAuditChangeType.Updated,
            updated,
            oldAction,
            oldAutoUpdateEnabled,
            normalizedReason,
            changedBy,
            ipAddress);
        var updatedDto = ToRuleDto(updated);
        await EnrichRuleDtosAsync([updatedDto], cancellationToken);
        return updatedDto;
    }

    public async Task<IReadOnlyList<AppApprovalRuleResolvedDto>> GetRulesByScopeAsync(
        AppApprovalScopeType scopeType,
        Guid? scopeId,
        AppInstallationType installationType,
        CancellationToken cancellationToken = default)
    {
        var rules = await _approvalRepo.GetByScopeAsync(scopeType, scopeId, installationType);
        var dtos = rules.Select(ToRuleDto).ToList();

        // Nome do pacote para a listagem (a regra guarda só o packageId): UMA
        // consulta por tipo de instalação em vez de uma por regra.
        await EnrichRuleDtosAsync(dtos, cancellationToken);

        return dtos;
    }

    /// <summary>Preenche PackageName nos DTOs a partir do catálogo.</summary>
    private async Task EnrichRuleDtosAsync(
        IReadOnlyList<AppApprovalRuleResolvedDto> dtos,
        CancellationToken cancellationToken)
    {
        if (dtos.Count == 0)
            return;

        foreach (var group in dtos.GroupBy(dto => dto.InstallationType))
        {
            var packages = await _appPackageRepo.GetByInstallationTypeAndPackageIdsAsync(
                group.Key,
                group.Select(dto => dto.PackageId).ToList(),
                cancellationToken);

            // Índice sem case: pacote custom pode estar gravado com outro caso.
            var namesById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var package in packages)
            {
                if (!string.IsNullOrWhiteSpace(package.Name))
                    namesById[package.PackageId] = package.Name;
            }

            foreach (var dto in group)
            {
                if (namesById.TryGetValue(dto.PackageId, out var name))
                    dto.PackageName = name;
            }
        }
    }

    public async Task<bool> DeleteRuleAsync(Guid ruleId, CancellationToken cancellationToken = default)
    {
        return await DeleteRuleAsync(ruleId, null, null, null, cancellationToken);
    }

    public async Task<bool> DeleteRuleAsync(Guid ruleId, string? reason, string? changedBy, string? ipAddress, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var existing = await _approvalRepo.GetByIdAsync(ruleId);
        if (existing is null)
            return false;

        await _auditService.LogAsync(
            AppApprovalAuditChangeType.Deleted,
            existing,
            existing.Action,
            existing.AutoUpdateEnabled,
            reason,
            changedBy,
            ipAddress);

        await _approvalRepo.DeleteAsync(ruleId);
        return true;
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

    public async Task<bool> IsPackageApprovedAsync(
        Guid? clientId,
        Guid? siteId,
        Guid? agentId,
        AppInstallationType installationType,
        string packageId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageId))
            return false;

        var policy = await ResolvePolicyForDirectScopeAsync(clientId, siteId, agentId, cancellationToken);
        if (policy == AppStorePolicyType.Disabled)
            return false;

        // Todos os pacotes do catálogo estão liberados: não enumera o catálogo.
        if (policy == AppStorePolicyType.All)
            return true;

        var approved = await BuildEffectiveApprovedAppsAsync(clientId, siteId, agentId, installationType, cancellationToken);
        return approved.Any(a => string.Equals(a.PackageId, packageId.Trim(), StringComparison.OrdinalIgnoreCase));
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

    /// <summary>Valida e converte o tipo de comando vindo do request (int no wire).</summary>
    private static AppCommandKind NormalizeCommandKind(int value, string field)
    {
        if (!Enum.IsDefined(typeof(AppCommandKind), value))
            throw new InvalidOperationException(
                $"{field} invalido: use 0 (comando unico), 1 (script CMD/BAT) ou 2 (script PowerShell).");

        return (AppCommandKind)value;
    }

    /// <summary>Valida e converte o modo de deteccao vindo do request (int no wire).</summary>
    private static AppDetectionMode NormalizeDetectionMode(int value)
    {
        if (!Enum.IsDefined(typeof(AppDetectionMode), value))
            throw new InvalidOperationException(
                "DetectionMode invalido: use 0 (nenhuma), 1 (vinculo com inventario) ou 2 (comando).");

        return (AppDetectionMode)value;
    }

    private static string? TrimToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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

    private async Task<List<Discovery.Core.Entities.AppApprovalRule>> LoadRulesForContextAsync(
        Guid? clientId,
        Guid? siteId,
        Guid? agentId,
        AppInstallationType installationType)
    {
        var rules = new List<Discovery.Core.Entities.AppApprovalRule>();
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
        var (items, _) = await _appPackageRepo.SearchPageAsync(
            installationType,
            search,
            null,
            cursor,
            limit + 1,
            cancellationToken);

        var hasMore = items.Count > limit;
        var page = hasMore ? items.Take(limit).ToList() : items.ToList();
        var lastItem = hasMore ? page[^1] : null;
        var nextCursor = hasMore && lastItem is not null
            ? CursorPaginationHelper.EncodeNameCursor(lastItem.Name, lastItem.Id)
            : null;

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
        var (items, _) = await _appPackageRepo.SearchPageAsync(
            installationType,
            search,
            null,
            cursor,
            limit + 1,
            cancellationToken);

        var hasMore = items.Count > limit;
        var page = hasMore ? items.Take(limit).ToList() : items.ToList();
        var lastItem = hasMore ? page[^1] : null;
        var nextCursor = hasMore && lastItem is not null
            ? CursorPaginationHelper.EncodeNameCursor(lastItem.Name, lastItem.Id)
            : null;

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

        // O catalogo e a fonte de metadata para TODOS os tipos, inclusive Custom:
        // antes Custom devolvia apenas o packageId, entao o agent recebia o pacote
        // aprovado sem nome, descricao, comando de instalacao ou arquivo.
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
                    UninstallCommand = package.UninstallCommand,
                    InstallCommandKind = package.InstallCommandKind,
                    UninstallCommandKind = package.UninstallCommandKind,
                    DetectionMode = package.DetectionMode,
                    InventorySoftwareId = package.InventorySoftwareId,
                    DetectionName = package.DetectionName,
                    DetectionPublisher = package.DetectionPublisher,
                    DetectionMinVersion = package.DetectionMinVersion,
                    DetectionCommand = package.DetectionCommand,
                    ExtractZip = package.ExtractZip,
                    FileName = package.FileName,
                    FileSizeBytes = package.FileSizeBytes,
                    FileUrl = package.HasFile
                        ? $"/api/v1/agent-auth/me/app-store/custom/{Uri.EscapeDataString(package.Id)}/file"
                        : string.Empty,
                    FileChecksum = package.FileChecksum,
                    SilentCommand = package.SilentCommand,
                    SilentWithProgressCommand = package.SilentWithProgressCommand,
                    InstallerUrlsByArch = package.InstallerUrlsByArch,
                    InstallerTypesByArch = package.InstallerTypesByArch,
                    AutoUpdateEnabled = x.AutoUpdateEnabled,
                    SourceScope = x.SourceScope
                };
            })
            .ToList();
    }

    private static EffectiveResolvedRule BuildEffectiveRuleState(
        IReadOnlyList<Discovery.Core.Entities.AppApprovalRule> rules,
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

    private static List<Discovery.Core.Entities.AppApprovalRule> SelectLatestRulesForPackage(
        IEnumerable<Discovery.Core.Entities.AppApprovalRule> rules,
        string packageId,
        AppApprovalScopeType maxScopeType)
    {
        var applicableScopes = GetApplicableScopes(maxScopeType);
        var selectedRules = new List<Discovery.Core.Entities.AppApprovalRule>();

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
        IReadOnlyList<Discovery.Core.Entities.AppApprovalRule> selectedRules,
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

    private static AppCatalogPackageDto MapUnifiedToDto(Discovery.Core.Entities.AppPackage pkg)
    {
        var installerUrls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var installerTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tags = Array.Empty<string>();
        var category = string.Empty;
        var license = string.Empty;
        var silentCommand = string.Empty;
        var silentWithProgressCommand = string.Empty;

        if (!string.IsNullOrWhiteSpace(pkg.MetadataJson))
        {
            try
            {
                using var json = JsonDocument.Parse(pkg.MetadataJson);
                var root = json.RootElement;
                tags = ParseStringArray(root, "tags").ToArray();
                installerUrls = ParseInstallerUrls(root).ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
                installerTypes = ParseInstallerTypes(root).ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
                category = TryGetString(root, "category");
                license = TryGetString(root, "license");
                if (string.IsNullOrWhiteSpace(license))
                    license = TryGetString(root, "licenseUrl");
                silentCommand = TryGetStringIgnoreCase(root, "silent");
                silentWithProgressCommand = TryGetStringIgnoreCase(root, "silentWithProgress");
            }
            catch (JsonException)
            {
                // Metadata malformado nao deve derrubar listagem.
            }
        }

        return new AppCatalogPackageDto
        {
            Id = pkg.PackageId,
            InstallationType = pkg.InstallationType,
            Name = pkg.Name,
            Publisher = pkg.Publisher ?? string.Empty,
            Version = pkg.Version ?? string.Empty,
            Architecture = BuildArchitectureLabel(installerUrls.Keys),
            Description = pkg.Description ?? string.Empty,
            Homepage = pkg.SiteUrl ?? string.Empty,
            License = license,
            Category = category,
            Icon = ResolveIconUrl(pkg.IconUrl, pkg.SiteUrl),
            InstallCommand = pkg.InstallCommand ?? string.Empty,
            UninstallCommand = pkg.UninstallCommand ?? string.Empty,
            InstallCommandKind = (int)pkg.InstallCommandKind,
            UninstallCommandKind = (int)pkg.UninstallCommandKind,
            DetectionMode = (int)pkg.DetectionMode,
            InventorySoftwareId = pkg.InventorySoftwareId,
            DetectionName = pkg.DetectionName,
            DetectionPublisher = pkg.DetectionPublisher,
            DetectionMinVersion = pkg.DetectionMinVersion,
            DetectionCommand = pkg.DetectionCommand,
            ExtractZip = pkg.ExtractZip,
            FileName = pkg.FileName ?? string.Empty,
            FileSizeBytes = pkg.FileSizeBytes,
            FileContentType = pkg.FileContentType ?? string.Empty,
            FileChecksum = pkg.FileChecksum ?? string.Empty,
            HasFile = !string.IsNullOrWhiteSpace(pkg.FileObjectKey),
            SilentCommand = silentCommand,
            SilentWithProgressCommand = silentWithProgressCommand,
            LastUpdated = pkg.LastUpdated,
            Tags = tags,
            InstallerUrlsByArch = installerUrls,
            InstallerTypesByArch = installerTypes
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
            UninstallCommand = package.UninstallCommand,
            InstallCommandKind = package.InstallCommandKind,
            UninstallCommandKind = package.UninstallCommandKind,
            DetectionMode = package.DetectionMode,
            InventorySoftwareId = package.InventorySoftwareId,
            DetectionName = package.DetectionName,
            DetectionPublisher = package.DetectionPublisher,
            DetectionMinVersion = package.DetectionMinVersion,
            DetectionCommand = package.DetectionCommand,
            ExtractZip = package.ExtractZip,
            FileName = package.FileName,
            FileSizeBytes = package.FileSizeBytes,
            FileUrl = package.HasFile
                ? $"/api/v1/agent-auth/me/app-store/custom/{Uri.EscapeDataString(package.Id)}/file"
                : string.Empty,
            FileChecksum = package.FileChecksum,
            SilentCommand = package.SilentCommand,
            SilentWithProgressCommand = package.SilentWithProgressCommand,
            InstallerUrlsByArch = package.InstallerUrlsByArch,
            InstallerTypesByArch = package.InstallerTypesByArch,
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

    private static AppApprovalRuleResolvedDto ToRuleDto(Discovery.Core.Entities.AppApprovalRule rule)
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
            // PackageName é preenchido por EnrichRuleDtoAsync (vem do catálogo).
            Reason = rule.Reason,
            Action = rule.Action,
            AutoUpdateEnabled = rule.AutoUpdateEnabled,
            CreatedAt = rule.CreatedAt,
            UpdatedAt = rule.UpdatedAt
        };
    }

    private static bool ContainsIgnoreCase(string? source, string value)
    {
        return source?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string ResolveIconUrl(string? iconUrl, string? siteUrl)
    {
        if (!string.IsNullOrWhiteSpace(iconUrl))
            return iconUrl;

        if (string.IsNullOrWhiteSpace(siteUrl))
            return string.Empty;

        var trimmedSiteUrl = siteUrl.Trim();
        if (Uri.TryCreate(trimmedSiteUrl, UriKind.Absolute, out var siteUri)
            && !string.IsNullOrWhiteSpace(siteUri.Host))
        {
            var host = Uri.EscapeDataString(siteUri.Host);
            return $"https://www.google.com/s2/favicons?domain={host}&sz=64";
        }

        return trimmedSiteUrl;
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

    /// <summary>Busca uma propriedade string ignorando diferenças de caixa no nome da propriedade.</summary>
    private static string TryGetStringIgnoreCase(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            var value = property.Value;
            if (value.ValueKind == JsonValueKind.String)
                return value.GetString()?.Trim() ?? string.Empty;

            if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                return value.GetRawText();

            return string.Empty;
        }

        return string.Empty;
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

    /// <summary>
    /// Rótulo de arquitetura(s) do pacote a partir das chaves de
    /// installerUrlsByArch (ex.: "x64" ou "arm64, x64"). Vazio quando o pacote
    /// não publica instalador por arquitetura (custom, por exemplo). "neutral"
    /// (instalador que serve qualquer arquitetura) é exibido como "todas".
    /// </summary>
    private static string BuildArchitectureLabel(IEnumerable<string> architectures)
    {
        var labels = architectures
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(ArchitectureToken)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(ArchitectureSortKey)
            .ThenBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return labels.Count == 0 ? string.Empty : string.Join(", ", labels);
    }

    private static string ArchitectureToken(string architecture)
    {
        var token = architecture.Trim().ToLowerInvariant();
        return token == "neutral" ? "todas" : token;
    }

    private static int ArchitectureSortKey(string label) => label switch
    {
        "x64" => 0,
        "arm64" => 1,
        "x86" => 2,
        "todas" => 3,
        _ => 4,
    };

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

    /// <summary>
    /// Extrai installerTypesByArch do metadata JSON (quando presente — pacotes
    /// sincronizados após a correção do parser o incluem; antigos não têm e
    /// retornam dicionário vazio, mantendo compatibilidade).
    /// </summary>
    private static IReadOnlyDictionary<string, string> ParseInstallerTypes(JsonElement element)
    {
        if (!element.TryGetProperty("installerTypesByArch", out var obj) || obj.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in obj.EnumerateObject())
        {
            var value = property.Value;
            if (value.ValueKind != JsonValueKind.String)
                continue;

            var installerType = value.GetString();
            if (!string.IsNullOrWhiteSpace(installerType))
                result[property.Name] = installerType.Trim();
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
