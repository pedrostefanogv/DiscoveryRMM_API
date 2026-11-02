using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AppStore.Commands;
using Discovery.Core.Cqrs.AppStore.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

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

/// <summary>Constantes de auditoria para os eventos do ciclo de vida do pacote custom.</summary>
internal static class CustomPackageAuditReasons
{
    public const string Deleted = "custom_package_deleted";
    public const string Purged = "custom_package_purged";
}

public sealed class DeleteCustomAppPackageCommandHandler(
    IAppPackageRepository repository,
    IAppApprovalRuleRepository approvalRules,
    IAppApprovalAuditRepository auditRepo,
    IAppStoreService appStore)
    : IRequestHandler<DeleteCustomAppPackageCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteCustomAppPackageCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.PackageId))
            return Result<VoidResult>.Failure(Error.Validation("packageId", "PackageId e obrigatorio."));

        var packageId = cmd.PackageId.Trim();

        // D2: a confirmação é DIGITAR o packageId — evita excluir o pacote errado.
        if (!string.Equals(cmd.Confirm?.Trim(), packageId, StringComparison.OrdinalIgnoreCase))
            return Result<VoidResult>.Failure(Error.Validation(
                "confirm",
                $"Confirmacao obrigatoria: digite o packageId '{packageId}' para confirmar. " +
                "As regras de aprovacao deste pacote serao EXCLUIDAS definitivamente."));

        var package = await repository.GetByInstallationTypeAndPackageIdAsync(
            AppInstallationType.Custom, packageId, includeDeleted: true, cancellationToken: ct);

        if (package is null)
            return Result<VoidResult>.Failure(Error.NotFound($"Pacote custom '{packageId}' nao encontrado."));

        if (package.IsDeleted)
            return Result<VoidResult>.Failure(Error.Validation(
                "packageId", $"Pacote custom '{packageId}' ja esta na lixeira."));

        // D1: as regras de aprovacao sao removidas ANTES do soft delete, de forma
        // OBRIGATORIA — se uma regra nao puder ser removida, o pacote NAO sai do
        // catalogo (regra orfa continuaría valendo). O arquivo/icone NAO sao
        // apagados do storage aqui: precisam sobreviver a um restore.
        IReadOnlyList<Discovery.Core.Entities.AppApprovalRule> rules;
        try
        {
            rules = await approvalRules.GetByPackageAsync(AppInstallationType.Custom, package.PackageId);
        }
        catch (Exception ex)
        {
            return Result<VoidResult>.Failure(Error.Internal(
                $"Nao foi possivel listar as regras de aprovacao do pacote '{package.PackageId}': {ex.Message}. " +
                "O pacote NAO foi excluido."));
        }

        foreach (var rule in rules)
        {
            bool removed;
            try
            {
                removed = await appStore.DeleteRuleAsync(
                    rule.Id,
                    "Pacote custom movido para a lixeira; aprovacao excluida definitivamente.",
                    cmd.ChangedBy,
                    ipAddress: null,
                    ct);
            }
            catch (Exception ex)
            {
                return Result<VoidResult>.Failure(Error.Internal(
                    $"Falha ao remover a regra de aprovacao '{rule.Id}' do pacote '{package.PackageId}': {ex.Message}. " +
                    "O pacote NAO foi excluido."));
            }

            if (!removed)
                return Result<VoidResult>.Failure(Error.Internal(
                    $"Regra de aprovacao '{rule.Id}' do pacote '{package.PackageId}' nao pode ser removida. " +
                    "O pacote NAO foi excluido."));
        }

        var deleted = await repository.SoftDeleteCustomAsync(
            package.PackageId, cmd.ChangedBy, cmd.Reason, ct);
        if (deleted is null)
            return Result<VoidResult>.Failure(Error.NotFound($"Pacote custom '{packageId}' nao encontrado."));

        // Evento do pacote na trilha de aprovacao (sem regra associada).
        await auditRepo.CreateAsync(new Discovery.Core.Entities.AppApprovalAudit
        {
            RuleId = null,
            ChangeType = AppApprovalAuditChangeType.Deleted,
            ScopeType = AppApprovalScopeType.Global,
            InstallationType = AppInstallationType.Custom,
            PackageId = package.PackageId,
            Reason = CustomPackageAuditReasons.Deleted,
            ChangedBy = cmd.ChangedBy,
            ChangedAt = DateTime.UtcNow
        });

        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class RestoreCustomAppPackageCommandHandler(IAppPackageRepository repository)
    : IRequestHandler<RestoreCustomAppPackageCommand, Result<RestoreCustomAppPackageResult>>
{
    public async Task<Result<RestoreCustomAppPackageResult>> Handle(RestoreCustomAppPackageCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.PackageId))
            return Result<RestoreCustomAppPackageResult>.Failure(Error.Validation("packageId", "PackageId e obrigatorio."));

        var restored = await repository.RestoreCustomAsync(cmd.PackageId.Trim(), ct);
        if (!restored)
            return Result<RestoreCustomAppPackageResult>.Failure(
                Error.NotFound($"Pacote custom '{cmd.PackageId}' nao encontrado na lixeira."));

        return Result<RestoreCustomAppPackageResult>.Success(new RestoreCustomAppPackageResult
        {
            PackageId = cmd.PackageId.Trim(),
            Message = "Aprovações anteriores foram excluídas — recadastre as aprovações para este pacote."
        });
    }
}

public sealed class PurgeCustomAppPackageCommandHandler(
    IAppPackageRepository repository,
    IAppApprovalRuleRepository approvalRules,
    IAppApprovalAuditRepository auditRepo,
    IAppStoreService appStore,
    ICustomPackageAssetService assets,
    ILogger<PurgeCustomAppPackageCommandHandler> logger)
    : IRequestHandler<PurgeCustomAppPackageCommand, Result<PurgeCustomAppPackageResult>>
{
    public async Task<Result<PurgeCustomAppPackageResult>> Handle(PurgeCustomAppPackageCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.PackageId))
            return Result<PurgeCustomAppPackageResult>.Failure(Error.Validation("packageId", "PackageId e obrigatorio."));

        var packageId = cmd.PackageId.Trim();

        if (!string.Equals(cmd.Confirm?.Trim(), packageId, StringComparison.OrdinalIgnoreCase))
            return Result<PurgeCustomAppPackageResult>.Failure(Error.Validation(
                "confirm",
                $"Confirmacao obrigatoria: digite o packageId '{packageId}' para confirmar o purge definitivo."));

        var package = await repository.GetByInstallationTypeAndPackageIdAsync(
            AppInstallationType.Custom, packageId, includeDeleted: true, cancellationToken: ct);

        if (package is null)
            return Result<PurgeCustomAppPackageResult>.Failure(Error.NotFound($"Pacote custom '{packageId}' nao encontrado."));

        if (!package.IsDeleted)
            return Result<PurgeCustomAppPackageResult>.Failure(Error.Validation(
                "packageId",
                $"Pacote custom '{packageId}' nao esta na lixeira: use a exclusao normal (soft delete) antes do purge."));

        // (a) Regras restantes saem em HARD, com auditoria.
        var rules = await approvalRules.GetByPackageAsync(AppInstallationType.Custom, package.PackageId);
        foreach (var rule in rules)
        {
            try
            {
                await appStore.DeleteRuleAsync(
                    rule.Id,
                    "Pacote custom purgado da lixeira.",
                    changedBy: null,
                    ipAddress: null,
                    ct);
            }
            catch (Exception ex)
            {
                // Uma regra que não sai não retém o purge: ela é órfã de qualquer
                // forma e a sanitização/limeza de órfãos a remove depois.
                logger.LogWarning(ex, "Falha ao remover a regra {RuleId} durante o purge do pacote {PackageId}.", rule.Id, package.PackageId);
            }
        }

        // (b) + (c) Apaga os assets no storage; em falha, verifica existência.
        foreach (var objectKey in new[] { package.FileObjectKey, package.IconObjectKey })
        {
            if (string.IsNullOrWhiteSpace(objectKey))
                continue;

            string? failure = null;
            try
            {
                await assets.DeleteAsync(objectKey, ct);
            }
            catch (NotSupportedException)
            {
                // Provider sem storage S3 (local): nada a remover no bucket.
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }

            if (failure is null)
                continue;

            // D4: o delete no storage falhou — verifica SE o objeto existe.
            bool? exists;
            try
            {
                exists = await assets.ExistsAsync(objectKey, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Verificacao de existencia falhou para {ObjectKey} durante o purge.", objectKey);
                exists = null; // verificação falhou
            }

            if (exists == true || exists is null)
            {
                if (cmd.Force)
                {
                    // force=true: apaga a linha SEM tocar no storage de novo — o
                    // varredor de assets órfãos remove o objeto depois.
                    logger.LogWarning(
                        "Purge FORCADO do pacote {PackageId}: asset {ObjectKey} mantido no storage (falha: {Error}).",
                        package.PackageId, objectKey, failure);
                    continue;
                }

                var error = exists == true
                    ? $"O objeto '{objectKey}' continua presente no object storage apos a tentativa de remocao: {failure}"
                    : $"Nao foi possivel verificar o objeto '{objectKey}' no object storage: {failure}";

                // (c) NÃO apaga a linha: grava purge_failed_at/purge_error e 409.
                await repository.SetPurgeFailureAsync(package.PackageId, error, ct);
                return Result<PurgeCustomAppPackageResult>.Success(new PurgeCustomAppPackageResult
                {
                    PackageId = package.PackageId,
                    PurgeFailed = true,
                    ObjectKey = objectKey,
                    Error = error
                });
            }
        }

        // (d) DELETE físico + auditoria do evento.
        var purged = await repository.DeleteCustomAsync(package.PackageId, ct);
        if (!purged)
            return Result<PurgeCustomAppPackageResult>.Failure(Error.NotFound($"Pacote custom '{packageId}' nao encontrado."));

        await auditRepo.CreateAsync(new Discovery.Core.Entities.AppApprovalAudit
        {
            RuleId = null,
            ChangeType = AppApprovalAuditChangeType.Deleted,
            ScopeType = AppApprovalScopeType.Global,
            InstallationType = AppInstallationType.Custom,
            PackageId = package.PackageId,
            Reason = CustomPackageAuditReasons.Purged,
            ChangedBy = null,
            ChangedAt = DateTime.UtcNow
        });

        return Result<PurgeCustomAppPackageResult>.Success(new PurgeCustomAppPackageResult
        {
            PackageId = package.PackageId
        });
    }
}

public sealed class CleanupOrphanApprovalRulesCommandHandler(IAppStoreService appStore)
    : IRequestHandler<CleanupOrphanApprovalRulesCommand, Result<CleanupOrphanApprovalRulesResult>>
{
    public async Task<Result<CleanupOrphanApprovalRulesResult>> Handle(CleanupOrphanApprovalRulesCommand cmd, CancellationToken ct)
    {
        var result = await appStore.CleanupOrphanApprovalRulesAsync(cmd.DryRun, cmd.ChangedBy, ct);
        return Result<CleanupOrphanApprovalRulesResult>.Success(result);
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

public sealed class GetAppStoreResolvedApprovalsQueryHandler(IAppStoreService svc)
    : IRequestHandler<GetAppStoreResolvedApprovalsQuery, Result<AppApprovalResolvedPageDto>>
{
    public async Task<Result<AppApprovalResolvedPageDto>> Handle(GetAppStoreResolvedApprovalsQuery q, CancellationToken ct)
    {
        // Escopo sem id não tem cadeia para resolver: antes isso virava
        // InvalidOperationException (500) dentro do service. A aba também não
        // consulta nesse estado, mas o endpoint precisa recusar com 400.
        if (!Enum.IsDefined(q.ScopeType))
        {
            return Result<AppApprovalResolvedPageDto>.Failure(
                Error.Validation("scopeType", "Escopo de aprovação inválido."));
        }

        if (q.ScopeType != AppApprovalScopeType.Global && q.ScopeId is null)
        {
            return Result<AppApprovalResolvedPageDto>.Failure(
                Error.Validation("scopeId", "Informe o cliente, site ou agente do escopo consultado."));
        }

        var page = await svc.GetResolvedRulesAsync(q.ScopeType, q.ScopeId, (AppInstallationType)q.InstallationType, q.Search, ct);
        return Result<AppApprovalResolvedPageDto>.Success(page);
    }
}

public sealed class GetAppStoreUpdateIntentQueryHandler(IAppStoreUpdateIntentService svc)
    : IRequestHandler<GetAppStoreUpdateIntentQuery, Result<AppStoreUpdateIntentPageDto>>
{
    public async Task<Result<AppStoreUpdateIntentPageDto>> Handle(GetAppStoreUpdateIntentQuery q, CancellationToken ct)
    {
        if (!Enum.IsDefined(q.ScopeType))
        {
            return Result<AppStoreUpdateIntentPageDto>.Failure(
                Error.Validation("scopeType", "Escopo de aprovação inválido."));
        }

        if (!Enum.IsDefined((AppInstallationType)q.InstallationType))
        {
            return Result<AppStoreUpdateIntentPageDto>.Failure(
                Error.Validation("installationType", "Tipo de instalação inválido."));
        }

        if (q.ScopeType != AppApprovalScopeType.Global && q.ScopeId is null)
        {
            return Result<AppStoreUpdateIntentPageDto>.Failure(
                Error.Validation("scopeId", "Informe o cliente, site ou agente do escopo consultado."));
        }

        var page = await svc.GetUpdateIntentAsync(
            q.ScopeType, q.ScopeId, (AppInstallationType)q.InstallationType, q.Search, ct);
        return Result<AppStoreUpdateIntentPageDto>.Success(page);
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