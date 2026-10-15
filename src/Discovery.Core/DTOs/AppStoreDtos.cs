using Discovery.Core.Enums;

namespace Discovery.Core.DTOs;

public class AppCatalogPackageDto
{
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Origem do pacote (Winget/Chocolatey/Custom). O frontend usava o
    /// installCommand como heurística quando este campo faltava, classificando
    /// um pacote Custom com "choco"/"winget" no comando como outro tipo.
    /// </summary>
    public AppInstallationType InstallationType { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Arquitetura(s) com instalador publicado (chaves de installerUrlsByArch,
    /// ex.: "x64" ou "x64, arm64"). Vazio quando o pacote não tem instalador por
    /// arquitetura (pacotes custom, por exemplo).
    /// </summary>
    public string Architecture { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;
    public string Homepage { get; set; } = string.Empty;
    public string License { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string InstallCommand { get; set; } = string.Empty;

    /// <summary>Comando de desinstalação (pacotes custom).</summary>
    public string UninstallCommand { get; set; } = string.Empty;

    /// <summary>
    /// Tipo do comando de instalação (AppCommandKind): 0 comando único, 1 script CMD/BAT,
    /// 2 script PowerShell. Int no wire para o JSON sair numérico (o
    /// JsonStringEnumConverter global serializaria o enum como string).
    /// </summary>
    public int InstallCommandKind { get; set; }

    /// <summary>Tipo do comando de desinstalação (AppCommandKind).</summary>
    public int UninstallCommandKind { get; set; }

    /// <summary>Modo de detecção (AppDetectionMode): 0 nenhum, 1 vínculo com inventário, 2 comando.</summary>
    public int DetectionMode { get; set; }

    /// <summary>softwareId (Guid) do app vinculado no catálogo de inventário.</summary>
    public string? InventorySoftwareId { get; set; }

    /// <summary>Nome do software para o agent casar com o inventário local.</summary>
    public string? DetectionName { get; set; }

    /// <summary>Fabricante do software para o agent casar com o inventário local.</summary>
    public string? DetectionPublisher { get; set; }

    /// <summary>Versão mínima aceita na detecção por inventário.</summary>
    public string? DetectionMinVersion { get; set; }

    /// <summary>Comando PowerShell de detecção (exit 0 = já instalado).</summary>
    public string? DetectionCommand { get; set; }

    /// <summary>Arquivo enviado é ZIP e deve ser extraído no host.</summary>
    public bool ExtractZip { get; set; }

    /// <summary>Nome original do instalador enviado ao storage.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Tamanho do instalador enviado ao storage.</summary>
    public long? FileSizeBytes { get; set; }

    /// <summary>Tipo MIME do instalador enviado ao storage.</summary>
    public string FileContentType { get; set; } = string.Empty;

    /// <summary>Checksum (ex.: SHA-256) do instalador, quando informado.</summary>
    public string FileChecksum { get; set; } = string.Empty;

    /// <summary>Indica se o pacote possui arquivo/instalador no object storage.</summary>
    public bool HasFile { get; set; }

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
    public string? UninstallCommand { get; set; }

    /// <summary>Tipo do comando de instalação (AppCommandKind): 0 comando único, 1 CMD/BAT, 2 PowerShell.</summary>
    public int InstallCommandKind { get; set; }

    /// <summary>Tipo do comando de desinstalação (AppCommandKind).</summary>
    public int UninstallCommandKind { get; set; }

    /// <summary>Modo de detecção (AppDetectionMode): 0 nenhum, 1 vínculo com inventário, 2 comando.</summary>
    public int DetectionMode { get; set; }

    /// <summary>softwareId (Guid) do app vinculado no catálogo de inventário (sem FK no servidor).</summary>
    public string? InventorySoftwareId { get; set; }

    /// <summary>Snapshot do nome do software no inventário (usado pelo agent).</summary>
    public string? DetectionName { get; set; }

    /// <summary>Snapshot do fabricante do software no inventário (usado pelo agent).</summary>
    public string? DetectionPublisher { get; set; }

    /// <summary>Versão mínima aceita na detecção por inventário.</summary>
    public string? DetectionMinVersion { get; set; }

    /// <summary>Comando PowerShell de detecção (exit 0 = já instalado).</summary>
    public string? DetectionCommand { get; set; }

    public bool ExtractZip { get; set; }
    public string? MetadataJson { get; set; }
    public string? FileObjectKey { get; set; }
    public string? FileBucket { get; set; }
    public string? FilePublicUrl { get; set; }
    public string? FileContentType { get; set; }
    public string? FileName { get; set; }
    public long? FileSizeBytes { get; set; }
    public string? FileChecksum { get; set; }

    /// <summary>Chave do icone/capa no object storage (opcional).</summary>
    public string? IconObjectKey { get; set; }
}

/// <summary>Asset (instalador ou icone) enviado ao object storage para um pacote custom.</summary>
public class CustomPackageAssetDto
{
    public string ObjectKey { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }

    /// <summary>SHA-256 (hex) do arquivo, calculado no upload e verificado pelo agent.</summary>
    public string? Checksum { get; set; }
}

public class AppApprovalRuleResolvedDto
{
    public Guid RuleId { get; set; }
    public AppApprovalScopeType ScopeType { get; set; }
    public Guid? ScopeId { get; set; }
    public AppInstallationType InstallationType { get; set; }
    public string PackageId { get; set; } = string.Empty;

    /// <summary>Nome do pacote no catálogo (vazio quando o pacote não existe mais).</summary>
    public string? PackageName { get; set; }

    /// <summary>Motivo informado ao criar/atualizar a regra (pode ser nulo).</summary>
    public string? Reason { get; set; }

    public AppApprovalActionType Action { get; set; }
    public bool? AutoUpdateEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
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

    /// <summary>Comando de desinstalação (pacotes custom).</summary>
    public string UninstallCommand { get; set; } = string.Empty;

    /// <summary>Tipo do comando de instalação (AppCommandKind).</summary>
    public int InstallCommandKind { get; set; }

    /// <summary>Tipo do comando de desinstalação (AppCommandKind).</summary>
    public int UninstallCommandKind { get; set; }

    /// <summary>Modo de detecção (AppDetectionMode): 0 nenhum, 1 vínculo com inventário, 2 comando.</summary>
    public int DetectionMode { get; set; }

    /// <summary>softwareId (Guid) do app vinculado no catálogo de inventário.</summary>
    public string? InventorySoftwareId { get; set; }

    /// <summary>Nome do software no inventário local, usado pelo agent para detecção.</summary>
    public string? DetectionName { get; set; }

    /// <summary>Fabricante do software no inventário local (opcional).</summary>
    public string? DetectionPublisher { get; set; }

    /// <summary>Versão mínima aceita na detecção por inventário (opcional).</summary>
    public string? DetectionMinVersion { get; set; }

    /// <summary>Comando PowerShell de detecção (exit 0 = já instalado).</summary>
    public string? DetectionCommand { get; set; }

    /// <summary>Arquivo do pacote é ZIP e deve ser extraído no host pelo agent.</summary>
    public bool ExtractZip { get; set; }

    /// <summary>Nome do instalador para download pelo agent.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Tamanho do instalador em bytes.</summary>
    public long? FileSizeBytes { get; set; }

    /// <summary>URL (relativa) para o agent baixar o instalador autenticado.</summary>
    public string FileUrl { get; set; } = string.Empty;

    /// <summary>SHA-256 (hex) do instalador, para o agent verificar a integridade.</summary>
    public string FileChecksum { get; set; } = string.Empty;

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
