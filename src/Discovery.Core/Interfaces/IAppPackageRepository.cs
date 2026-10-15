using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

public interface IAppPackageRepository
{
    Task<AppPackage?> GetByInstallationTypeAndPackageIdAsync(AppInstallationType installationType, string packageId, CancellationToken cancellationToken = default);

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

    /// <summary>Remove um pacote custom pelo packageId. Retorna false se não existir.</summary>
    Task<bool> DeleteCustomAsync(string packageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Zera o instalador do pacote custom (FileObjectKey, FileBucket, FilePublicUrl,
    /// FileContentType, FileSizeBytes, FileChecksum e FileName). Retorna um snapshot
    /// do estado ANTERIOR (com a object key para o handler apagar o objeto no
    /// storage) ou <c>null</c> se o pacote não existir ou não for Custom.
    /// </summary>
    Task<AppPackage?> ClearCustomFileAsync(string packageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// A object key está referenciada por algum pacote, como INSTALADOR ou como
    /// ÍCONE? Usado para NÃO descartar um asset já vinculado (e para descartar o
    /// que foi publicado mas nunca foi salvo no cadastro).
    /// </summary>
    Task<bool> IsAssetObjectKeyReferencedAsync(string objectKey, CancellationToken cancellationToken = default);
}
