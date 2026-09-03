using Discovery.Core.Enums;

namespace Discovery.Core.DTOs;

public class AppCatalogPackageDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Homepage { get; set; } = string.Empty;
    public string License { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string InstallCommand { get; set; } = string.Empty;

    /// <summary>Switches para instalação silenciosa (fallback: SilentWithProgressCommand → InstallCommand).</summary>
    public string SilentCommand { get; set; } = string.Empty;

    /// <summary>Switches para instalação silenciosa com progresso.</summary>
    public string SilentWithProgressCommand { get; set; } = string.Empty;
    public DateTime? LastUpdated { get; set; }
    public IReadOnlyList<string> Tags { get; set; } = [];
    public IReadOnlyDictionary<string, string> InstallerUrlsByArch { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// InstallerType por arquitetura (ex.: "wix", "burn", "nullsoft", "inno", "zip", "portable"),
    /// lido do manifesto winget. Permite ao agent executar o instalador baixado
    /// com a estratégia correta (msiexec vs exe vs portable) sem adivinhar pela extensão.
    /// </summary>
    public IReadOnlyDictionary<string, string> InstallerTypesByArch { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public class AppCatalogSearchResultDto
{
    public DateTime? GeneratedAt { get; set; }
    public int TotalPackagesInSource { get; set; }
    public int ReturnedItems { get; set; }
    public string? Cursor { get; set; }
    public string? NextCursor { get; set; }
    public int Limit { get; set; }
    public bool HasMore { get; set; }
    public string? Search { get; set; }
    public string? Architecture { get; set; }
    public IReadOnlyList<AppCatalogPackageDto> Items { get; set; } = [];
}

public class UpsertCustomAppCatalogPackageRequest
{
    public string PackageId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Publisher { get; set; }
    public string? Version { get; set; }
    public string? Description { get; set; }
    public string? IconUrl { get; set; }
    public string? SiteUrl { get; set; }
    public string? InstallCommand { get; set; }
    public string? MetadataJson { get; set; }
    public string? FileObjectKey { get; set; }
    public string? FileBucket { get; set; }
    public string? FilePublicUrl { get; set; }
    public string? FileContentType { get; set; }
    public long? FileSizeBytes { get; set; }
    public string? FileChecksum { get; set; }
}

public class AppApprovalRuleResolvedDto
{
    public Guid RuleId { get; set; }
    public AppApprovalScopeType ScopeType { get; set; }
    public Guid? ScopeId { get; set; }
    public AppInstallationType InstallationType { get; set; }
    public string PackageId { get; set; } = string.Empty;
    public AppApprovalActionType Action { get; set; }
    public bool? AutoUpdateEnabled { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class EffectiveApprovedAppDto
{
    public AppInstallationType InstallationType { get; set; }
    public string PackageId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string IconUrl { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string InstallCommand { get; set; } = string.Empty;

    /// <summary>Switches para instalação silenciosa (fallback: SilentWithProgressCommand → InstallCommand).</summary>
    public string SilentCommand { get; set; } = string.Empty;

    /// <summary>Switches para instalação silenciosa com progresso.</summary>
    public string SilentWithProgressCommand { get; set; } = string.Empty;
    public IReadOnlyDictionary<string, string> InstallerUrlsByArch { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> InstallerTypesByArch { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public bool AutoUpdateEnabled { get; set; }
    public AppApprovalScopeType SourceScope { get; set; }
}

public class EffectiveApprovedAppPageDto
{
    public AppApprovalScopeType ScopeType { get; set; }
    public Guid? ScopeId { get; set; }
    public AppInstallationType InstallationType { get; set; }
    public string? Search { get; set; }
    public string? Cursor { get; set; }
    public string? NextCursor { get; set; }
    public int Limit { get; set; }
    public int ReturnedItems { get; set; }
    public bool HasMore { get; set; }
    public IReadOnlyList<EffectiveApprovedAppDto> Items { get; set; } = [];
}

public class AppCatalogSyncResultDto
{
    public AppInstallationType InstallationType { get; set; }
    public bool Success { get; set; }
    public int PackagesUpserted { get; set; }
    public int PagesProcessed { get; set; }
    public DateTime SyncedAt { get; set; }
    public DateTime? SourceGeneratedAt { get; set; }
    public TimeSpan Duration { get; set; }
    public string? Error { get; set; }
}

/// <summary>Status de um job de sincronização de catálogo em background.</summary>
public class AppCatalogSyncStatusDto
{
    /// <summary>Indica se existe um job em andamento para o tipo informado.</summary>
    public bool Running { get; set; }
    /// <summary>Resultado da última sincronização concluída (sucesso ou falha). Null se nunca sincronizado nesta instância.</summary>
    public AppCatalogSyncResultDto? LastResult { get; set; }
}

public class ChocolateySyncResultDto
{
    public bool Success { get; set; }
    public int PackagesUpserted { get; set; }
    public int PagesProcessed { get; set; }
    public DateTime SyncedAt { get; set; }
    public TimeSpan Duration { get; set; }
    public string? Error { get; set; }
}

public class WingetSyncResultDto
{
    public bool Success { get; set; }
    public int PackagesUpserted { get; set; }
    public DateTime SyncedAt { get; set; }
    public DateTime? SourceGeneratedAt { get; set; }
    public TimeSpan Duration { get; set; }
    public string? Error { get; set; }
}
