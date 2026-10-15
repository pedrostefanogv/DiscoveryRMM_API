using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AppStore.Commands;
using Discovery.Core.Cqrs.AppStore.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AppStore;

// ── Catalog ──────────────────────────────────────────────────────────

public sealed class SearchAppStoreQueryHandler(IAppStoreService svc)
    : IRequestHandler<SearchAppStoreQuery, Result<AppCatalogSearchResultDto>>
{
    public async Task<Result<AppCatalogSearchResultDto>> Handle(SearchAppStoreQuery q, CancellationToken ct)
    {
        var result = await svc.SearchCatalogAsync(q.InstallationType, q.Search, q.Architecture, q.Limit, q.Cursor, ct);
        return Result<AppCatalogSearchResultDto>.Success(result);
    }
}

public sealed class GetAppStoreCatalogQueryHandler(IAppStoreService svc)
    : IRequestHandler<GetAppStoreCatalogQuery, Result<AppCatalogSearchResultDto>>
{
    public async Task<Result<AppCatalogSearchResultDto>> Handle(GetAppStoreCatalogQuery q, CancellationToken ct)
    {
        var instType = (AppInstallationType)q.InstallationType;
        var result = await svc.SearchCatalogAsync(instType, q.Search, q.Architecture, q.Limit, q.Cursor, ct);
        return Result<AppCatalogSearchResultDto>.Success(result);
    }
}

public sealed class GetCatalogPackageByIdQueryHandler(IAppStoreService svc)
    : IRequestHandler<GetCatalogPackageByIdQuery, Result<AppCatalogPackageDto>>
{
    public async Task<Result<AppCatalogPackageDto>> Handle(GetCatalogPackageByIdQuery q, CancellationToken ct)
    {
        var pkg = await svc.GetCatalogPackageByIdAsync((AppInstallationType)q.InstallationType, q.PackageId, ct);
        return pkg is null
            ? Result<AppCatalogPackageDto>.Failure(Error.NotFound($"Package '{q.PackageId}' not found."))
            : Result<AppCatalogPackageDto>.Success(pkg);
    }
}

// ── Custom package ───────────────────────────────────────────────────

public sealed class UpsertCustomAppPackageCommandHandler(IAppStoreService svc)
    : IRequestHandler<UpsertCustomAppPackageCommand, Result<AppCatalogPackageDto>>
{
    public async Task<Result<AppCatalogPackageDto>> Handle(UpsertCustomAppPackageCommand cmd, CancellationToken ct)
    {
        var request = new UpsertCustomAppCatalogPackageRequest
        {
            PackageId = cmd.PackageId,
            Name = cmd.Name,
            Publisher = cmd.Publisher,
            Version = cmd.Version,
            Description = cmd.Description,
            IconUrl = cmd.IconUrl,
            SiteUrl = cmd.SiteUrl,
            InstallCommand = cmd.InstallCommand,
            MetadataJson = cmd.MetadataJson,
            FileObjectKey = cmd.FileObjectKey,
            FileBucket = cmd.FileBucket,
            FilePublicUrl = cmd.FilePublicUrl,
            FileContentType = cmd.FileContentType,
            FileSizeBytes = cmd.FileSizeBytes,
            FileChecksum = cmd.FileChecksum,
            UninstallCommand = cmd.UninstallCommand,
            ExtractZip = cmd.ExtractZip,
            FileName = cmd.FileName,
            IconObjectKey = cmd.IconObjectKey,
            // Sem estes campos a detecção e o tipo de comando escolhidos no editor
            // eram descartados (o pacote era salvo com DetectionMode=None).
            InstallCommandKind = cmd.InstallCommandKind,
            UninstallCommandKind = cmd.UninstallCommandKind,
            DetectionMode = cmd.DetectionMode,
            InventorySoftwareId = cmd.InventorySoftwareId,
            DetectionName = cmd.DetectionName,
            DetectionPublisher = cmd.DetectionPublisher,
            DetectionMinVersion = cmd.DetectionMinVersion,
            DetectionCommand = cmd.DetectionCommand
        };
        try
        {
            var result = await svc.UpsertCustomCatalogPackageAsync(request, ct);
            return Result<AppCatalogPackageDto>.Success(result);
        }
        catch (InvalidOperationException ex)
        {
            // Validação do serviço (PackageId/Name/InstallCommand) deve virar 400
            // com a mensagem, não um 500 genérico.
            return Result<AppCatalogPackageDto>.Failure(Error.Validation("customPackage", ex.Message));
        }
    }
}

public sealed class DeleteCustomAppPackageCommandHandler(
    IAppPackageRepository repository,
    IAppApprovalRuleRepository approvalRules,
    IAppStoreService appStore,
    ICustomPackageAssetService assets)
    : IRequestHandler<DeleteCustomAppPackageCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteCustomAppPackageCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.PackageId))
            return Result<VoidResult>.Failure(Error.Validation("packageId", "PackageId e obrigatorio."));

        var package = await repository.GetByInstallationTypeAndPackageIdAsync(
            AppInstallationType.Custom, cmd.PackageId, ct);

        if (package is null)
            return Result<VoidResult>.Failure(Error.NotFound($"Pacote custom '{cmd.PackageId}' nao encontrado."));

        // Remove as regras de aprovação do pacote ANTES de apagar a linha: sem
        // isso ficariam órfãs e voltariam a valer se o mesmo packageId fosse
        // recriado. O histórico de auditoria é preservado (RuleId vira null).
        await DeleteApprovalRulesAsync(approvalRules, appStore, package.PackageId, ct);

        // Best-effort: a linha do catalogo e a fonte de verdade; uma falha ao
        // remover o objeto no storage nao deve impedir o delete do pacote.
        await TryDeleteAssetAsync(assets, package.FileObjectKey, ct);
        await TryDeleteAssetAsync(assets, package.IconObjectKey, ct);

        await repository.DeleteCustomAsync(package.PackageId, ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }

    private static async Task DeleteApprovalRulesAsync(
        IAppApprovalRuleRepository approvalRules,
        IAppStoreService appStore,
        string packageId,
        CancellationToken ct)
    {
        IReadOnlyList<Discovery.Core.Entities.AppApprovalRule> rules;
        try
        {
            rules = await approvalRules.GetByPackageAsync(AppInstallationType.Custom, packageId);
        }
        catch
        {
            // Falha ao listar regras não pode impedir a exclusão do pacote.
            return;
        }

        foreach (var rule in rules)
        {
            try
            {
                await appStore.DeleteRuleAsync(
                    rule.Id,
                    "Pacote custom excluído da loja.",
                    changedBy: null,
                    ipAddress: null,
                    ct);
            }
            catch
            {
                // best-effort: uma regra que não puder ser removida não bloqueia o delete.
            }
        }
    }

    private static async Task TryDeleteAssetAsync(
        ICustomPackageAssetService assets,
        string? objectKey,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
            return;

        try
        {
            await assets.DeleteAsync(objectKey, ct);
        }
        catch
        {
            // Ignorado de proposito: objeto orfao no storage nao quebra o delete.
        }
    }
}

public sealed class DeleteCustomAppPackageFileCommandHandler(IAppStoreService svc)
    : IRequestHandler<DeleteCustomAppPackageFileCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteCustomAppPackageFileCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.PackageId))
            return Result<VoidResult>.Failure(Error.Validation("packageId", "PackageId e obrigatorio."));

        // O service faz o clear dos campos de arquivo no catalogo e apaga o objeto
        // no storage em best-effort; um false significa pacote inexistente/nao Custom.
        var deleted = await svc.DeleteCustomPackageFileAsync(cmd.PackageId, ct);
        return deleted
            ? Result<VoidResult>.Success(VoidResult.Value)
            : Result<VoidResult>.Failure(Error.NotFound($"Pacote custom '{cmd.PackageId}' nao encontrado."));
    }
}

public sealed class DeleteCustomAppPackageIconCommandHandler(IAppStoreService svc)
    : IRequestHandler<DeleteCustomAppPackageIconCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteCustomAppPackageIconCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.PackageId))
            return Result<VoidResult>.Failure(Error.Validation("packageId", "PackageId e obrigatorio."));

        // O service limpa IconObjectKey/IconUrl no catalogo e apaga o objeto no
        // storage em best-effort; false = pacote inexistente ou nao Custom.
        var deleted = await svc.DeleteCustomPackageIconAsync(cmd.PackageId, ct);
        return deleted
            ? Result<VoidResult>.Success(VoidResult.Value)
            : Result<VoidResult>.Failure(Error.NotFound($"Pacote custom '{cmd.PackageId}' nao encontrado."));
    }
}

// ── Effective apps ───────────────────────────────────────────────────

public sealed class GetAppStoreEffectiveAppsQueryHandler(IAppStoreService svc)
    : IRequestHandler<GetAppStoreEffectiveAppsQuery, Result<IReadOnlyList<EffectiveApprovedAppDto>>>
{
    public async Task<Result<IReadOnlyList<EffectiveApprovedAppDto>>> Handle(GetAppStoreEffectiveAppsQuery q, CancellationToken ct)
    {
        var apps = await svc.GetEffectiveAppsAsync(q.ClientId, q.SiteId, q.AgentId, q.InstallationType, ct);
        return Result<IReadOnlyList<EffectiveApprovedAppDto>>.Success(apps);
    }
}

// ── Approvals ────────────────────────────────────────────────────────

public sealed class GetAppStoreApprovalsQueryHandler(IAppStoreService svc)
    : IRequestHandler<GetAppStoreApprovalsQuery, Result<IReadOnlyList<AppApprovalRuleResolvedDto>>>
{
    public async Task<Result<IReadOnlyList<AppApprovalRuleResolvedDto>>> Handle(GetAppStoreApprovalsQuery q, CancellationToken ct)
    {
        var instType = (AppInstallationType)(q.InstallationType ?? 0);
        var rules = await svc.GetRulesByScopeAsync(q.ScopeType ?? AppApprovalScopeType.Global, q.ScopeId, instType, ct);
        return Result<IReadOnlyList<AppApprovalRuleResolvedDto>>.Success(rules);
    }
}

public sealed class UpsertAppApprovalRuleCommandHandler(IAppStoreService svc)
    : IRequestHandler<UpsertAppApprovalRuleCommand, Result<AppApprovalRuleResolvedDto>>
{
    public async Task<Result<AppApprovalRuleResolvedDto>> Handle(UpsertAppApprovalRuleCommand cmd, CancellationToken ct)
    {
        var rule = await svc.UpsertRuleAsync(
            cmd.ScopeType, cmd.ScopeId,
            (AppInstallationType)cmd.InstallationType, cmd.PackageId,
            (AppApprovalActionType)cmd.Action, cmd.AutoUpdateEnabled,
            cmd.Reason, cmd.ChangedBy, cmd.IpAddress, ct);
        return Result<AppApprovalRuleResolvedDto>.Success(rule);
    }
}

public sealed class DeleteAppApprovalRuleCommandHandler(IAppStoreService svc)
    : IRequestHandler<DeleteAppApprovalRuleCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteAppApprovalRuleCommand cmd, CancellationToken ct)
    {
        var deleted = await svc.DeleteRuleAsync(cmd.RuleId, cmd.Reason, cmd.ChangedBy, cmd.IpAddress, ct);
        // Regra inexistente não é sucesso silencioso: a UI avisava "Regra removida"
        // para algo que já não existia (duplo clique / lista velha).
        return deleted
            ? Result<VoidResult>.Success(VoidResult.Value)
            : Result<VoidResult>.Failure(Error.NotFound($"Regra '{cmd.RuleId}' nao encontrada."));
    }
}

// ── Audit ────────────────────────────────────────────────────────────

public sealed class GetAppStoreApprovalAuditQueryHandler(IAppStoreService svc)
    : IRequestHandler<GetAppStoreApprovalAuditQuery, Result<AppApprovalAuditPageDto>>
{
    public async Task<Result<AppApprovalAuditPageDto>> Handle(GetAppStoreApprovalAuditQuery q, CancellationToken ct)
    {
        var instType = (AppInstallationType)(q.InstallationType ?? 0);
        var page = await svc.GetAuditHistoryAsync(instType, q.PackageId, q.ScopeType, q.ScopeId, q.ChangedBy, q.ChangedFrom, q.ChangedTo, q.ChangeType, q.Limit, q.Cursor, ct);
        return Result<AppApprovalAuditPageDto>.Success(page);
    }
}

// ── Package diff ─────────────────────────────────────────────────────

public sealed class GetAppStorePackageDiffQueryHandler(IAppStoreService svc)
    : IRequestHandler<GetAppStorePackageDiffQuery, Result<AppApprovalPackageDiffDto>>
{
    public async Task<Result<AppApprovalPackageDiffDto>> Handle(GetAppStorePackageDiffQuery q, CancellationToken ct)
    {
        var diff = await svc.GetPackageDiffAsync(q.ScopeType, q.ScopeId, q.InstallationType, q.PackageId, ct);
        return Result<AppApprovalPackageDiffDto>.Success(diff);
    }
}

// ── Effective diffs ──────────────────────────────────────────────────

public sealed class GetAppStoreEffectiveDiffsQueryHandler(IAppStoreService svc)
    : IRequestHandler<GetAppStoreEffectiveDiffsQuery, Result<AppEffectivePackageDiffPageDto>>
{
    public async Task<Result<AppEffectivePackageDiffPageDto>> Handle(GetAppStoreEffectiveDiffsQuery q, CancellationToken ct)
    {
        var instType = (AppInstallationType)q.InstallationType;
        var page = await svc.GetEffectiveAppDiffsAsync(q.ScopeType, q.ScopeId, instType, q.Search, q.Limit, q.Cursor, ct);
        return Result<AppEffectivePackageDiffPageDto>.Success(page);
    }
}

// ── Sync ─────────────────────────────────────────────────────────────
// A sincronização de catálogo não usa mais MediatR: é disparada em background
// via AppCatalogBackgroundSyncService (POST app-store/sync → 202) e o resultado
// é consultável em GET app-store/sync/status.