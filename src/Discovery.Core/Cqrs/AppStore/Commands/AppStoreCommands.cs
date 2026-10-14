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

/// <summary>Remove um pacote custom da loja (e seus arquivos no object storage).</summary>
public sealed record DeleteCustomAppPackageCommand(string PackageId) : ICommand<Result<VoidResult>>;

/// <summary>
/// Remove apenas o instalador publicado do pacote custom (mantém o pacote e o
/// ícone). Usado pelo editor para excluir o arquivo e enviar outro em seguida.
/// </summary>
public sealed record DeleteCustomAppPackageFileCommand(string PackageId) : ICommand<Result<VoidResult>>;

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
