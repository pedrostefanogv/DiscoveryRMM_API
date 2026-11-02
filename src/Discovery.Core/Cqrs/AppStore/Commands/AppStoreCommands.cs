using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;

namespace Discovery.Core.Cqrs.AppStore.Commands;

// ── Custom package management ────────────────────────────────────────
public sealed record UpsertCustomAppPackageCommand(
    string PackageId,
    string Name,
    string? Publisher = null,
    string? Version = null,
    string? Description = null,
    string? IconUrl = null,
    string? SiteUrl = null,
    string? InstallCommand = null,
    string? MetadataJson = null,
    string? FileObjectKey = null,
    string? FileBucket = null,
    string? FilePublicUrl = null,
    string? FileContentType = null,
    long? FileSizeBytes = null,
    string? FileChecksum = null,
    string? UninstallCommand = null,
    bool ExtractZip = false,
    string? FileName = null,
    string? IconObjectKey = null,
    // ── Tipo de comando e detecção (M223) ────────────────────────────
    // Estes campos chegavam do editor (site) mas NÃO existiam no command, então
    // o MVC os descartava no binding: o handler montava o request sem eles e o
    // pacote era salvo com DetectionMode=None e InventorySoftwareId nulo —
    // "vincular ao inventário" parecia não salvar. Mantidos no fim da lista
    // posicional para não quebrar chamadas existentes.
    int InstallCommandKind = 0,
    int UninstallCommandKind = 0,
    int DetectionMode = 0,
    string? InventorySoftwareId = null,
    string? DetectionName = null,
    string? DetectionPublisher = null,
    string? DetectionMinVersion = null,
    string? DetectionCommand = null
) : ICommand<Result<AppCatalogPackageDto>>;

/// <summary>
/// SOFT DELETE do pacote custom: exige a confirmação digitando o packageId,
/// remove OBRIGATORIAMENTE as regras de aprovação (definitivo, com auditoria) e
/// move o pacote para a lixeira. NÃO apaga arquivo/ícone do object storage
/// (precisam sobreviver a um restore). O purge é outro comando.
/// </summary>
public sealed record DeleteCustomAppPackageCommand(
    string PackageId,
    string? Confirm = null,
    string? Reason = null,
    string? ChangedBy = null) : ICommand<Result<VoidResult>>;

/// <summary>Retira o pacote custom da lixeira. As aprovações foram excluídas no soft delete e precisam ser recadastradas.</summary>
public sealed record RestoreCustomAppPackageCommand(string PackageId) : ICommand<Result<RestoreCustomAppPackageResult>>;

/// <summary>
/// PURGE (exclusão definitiva) de um pacote custom que está na lixeira: apaga as
/// regras restantes, remove o instalador/ícone do object storage e apaga a linha.
/// Em falha do storage, verifica a existência do objeto: inexistente → prossegue;
/// existente/verificação falhou → retém a linha (purge_error) e devolve 409,
/// exceto com <c>force</c>, que apaga a linha SEM tocar no storage.
/// </summary>
public sealed record PurgeCustomAppPackageCommand(
    string PackageId,
    string? Confirm = null,
    bool Force = false) : ICommand<Result<PurgeCustomAppPackageResult>>;

/// <summary>
/// Localiza (e, sem dry-run, remove com auditoria) regras de aprovação Custom
/// cujo pacote não existe mais no catálogo.
/// </summary>
public sealed record CleanupOrphanApprovalRulesCommand(
    bool DryRun = true,
    string? ChangedBy = null) : ICommand<Result<CleanupOrphanApprovalRulesResult>>;

/// <summary>
/// Remove apenas o instalador publicado do pacote custom (mantém o pacote e o
/// ícone). Usado pelo editor para excluir o arquivo e enviar outro em seguida.
/// </summary>
public sealed record DeleteCustomAppPackageFileCommand(string PackageId) : ICommand<Result<VoidResult>>;

/// <summary>Remove o ícone/capa publicado de um pacote custom (mantém o instalador).</summary>
public sealed record DeleteCustomAppPackageIconCommand(string PackageId) : ICommand<Result<VoidResult>>;

// ── Approval rules ───────────────────────────────────────────────────
public sealed record UpsertAppApprovalRuleCommand(
    AppApprovalScopeType ScopeType,
    Guid? ScopeId,
    int InstallationType,
    string PackageId,
    int Action,
    bool? AutoUpdateEnabled,
    string? Reason,
    string? ChangedBy,
    string? IpAddress
) : ICommand<Result<AppApprovalRuleResolvedDto>>;

public sealed record DeleteAppApprovalRuleCommand(
    Guid RuleId,
    string? Reason = null,
    string? ChangedBy = null,
    string? IpAddress = null
) : ICommand<Result<VoidResult>>;
