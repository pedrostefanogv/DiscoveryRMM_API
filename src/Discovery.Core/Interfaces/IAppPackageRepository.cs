using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

public interface IAppPackageRepository
{
    /// <summary>
    /// Busca um pacote. Leituras de catálogo EXCLUEM pacotes na lixeira por padrão;
    /// passe <paramref name="includeDeleted"/> = true quando a lixeira precisa ser
    /// visível (upsert/restauração/purge).
    /// </summary>
    Task<AppPackage?> GetByInstallationTypeAndPackageIdAsync(AppInstallationType installationType, string packageId, bool includeDeleted = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Busca vários pacotes do mesmo tipo em UMA consulta (evita N+1 na listagem
    /// de regras de aprovação). Pacotes Custom casam sem diferenciar maiúsculas;
    /// Winget/Chocolatey usam igualdade exata (já gravados em lowercase).
    /// </summary>
    Task<IReadOnlyList<AppPackage>> GetByInstallationTypeAndPackageIdsAsync(
        AppInstallationType installationType,
        IReadOnlyCollection<string> packageIds,
        CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<AppPackage> Items, int TotalCount)> SearchPageAsync(
        AppInstallationType installationType,
        string? search,
        string? architecture,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AppPackage>> GetAllByInstallationTypeAsync(
        AppInstallationType installationType,
        CancellationToken cancellationToken = default);

    Task<int> BulkUpsertAsync(
        IReadOnlyList<AppPackage> packages,
        AppInstallationType installationType,
        CancellationToken cancellationToken = default,
        bool preventDowngrade = false);

    Task<AppPackage> UpsertCustomAsync(AppPackage package, CancellationToken cancellationToken = default);

    /// <summary>
    /// EXCLUSÃO FÍSICA do pacote custom (purge da lixeira). Retorna false se não existir.
    /// O soft delete usa <see cref="SoftDeleteCustomAsync"/>.
    /// </summary>
    Task<bool> DeleteCustomAsync(string packageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marca o pacote custom como excluído (lixeira): is_deleted/deleted_at/deleted_by/delete_reason.
    /// Retorna o snapshot do estado anterior ou null se o pacote não existir/for marcado.
    /// </summary>
    Task<AppPackage?> SoftDeleteCustomAsync(string packageId, string? deletedBy, string? reason, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Soft delete não implementado neste double de teste.");

    /// <summary>Retira o pacote da lixeira (limpa is_deleted/deleted_at/by/reason e as marcas de purge). false = não encontrado.</summary>
    Task<bool> RestoreCustomAsync(string packageId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Restore não implementado neste double de teste.");

    /// <summary>Lista só os pacotes na lixeira (is_deleted), paginação por cursor (Name + Id).</summary>
    Task<(IReadOnlyList<AppPackage> Items, bool HasMore)> ListCustomTrashAsync(
        string? search, string? cursor, int limit, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Lixeira não implementada neste double de teste.");

    /// <summary>Registra falha de purge (purge_failed_at/purge_error) — a linha fica retida na lixeira.</summary>
    Task SetPurgeFailureAsync(string packageId, string error, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Purge failure não implementado neste double de teste.");

    /// <summary>
    /// Zera o instalador do pacote custom (FileObjectKey, FileBucket, FilePublicUrl,
    /// FileContentType, FileSizeBytes, FileChecksum e FileName). Retorna um snapshot
    /// do estado ANTERIOR (com a object key para o handler apagar o objeto no
    /// storage) ou <c>null</c> se o pacote não existir ou não for Custom.
    /// </summary>
    Task<AppPackage?> ClearCustomFileAsync(string packageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Zera o ícone do pacote custom (IconObjectKey e IconUrl). Retorna um snapshot
    /// do estado ANTERIOR (com a object key para o handler apagar o objeto no
    /// storage) ou <c>null</c> se o pacote não existir ou não for Custom.
    /// </summary>
    Task<AppPackage?> ClearCustomIconAsync(string packageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// A object key está referenciada por algum pacote, como INSTALADOR ou como
    /// ÍCONE? Usado para NÃO descartar um asset já vinculado (e para descartar o
    /// que foi publicado mas nunca foi salvo no cadastro).
    /// </summary>
    Task<bool> IsAssetObjectKeyReferencedAsync(string objectKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Todas as object keys de assets referenciadas por pacotes (instalador OU
    /// ícone). Usado pela sanitização do object storage: uma consulta evita um
    /// SELECT por objeto varrido no bucket.
    /// </summary>
    Task<IReadOnlySet<string>> GetReferencedAssetKeysAsync(CancellationToken cancellationToken = default);
}
