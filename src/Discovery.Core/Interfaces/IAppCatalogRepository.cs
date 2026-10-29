using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Catálogo curado de aplicativos de terceiros (WU-21, D24). App de terceiros
/// só é auto-aprovado se estiver aqui com <c>Status=active</c>.
/// </summary>
public interface IAppCatalogRepository
{
    Task<AppCatalogEntry?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Cria/atualiza a versão curada. Valida origem (winget|msi), SHA-256 (64
    /// hex), URL https e campos obrigatórios — rejeitar cedo é melhor que
    /// instalar binário sem integridade.
    /// </summary>
    Task<AppCatalogEntry> UpsertAsync(AppCatalogEntry entry, CancellationToken ct = default);

    /// <summary>Revoga uma versão (CVE/regressão): sai da auto-aprovação na hora.</summary>
    Task<bool> RevokeAsync(Guid id, string? revokedBy, CancellationToken ct = default);

    /// <summary>
    /// Promove uma entrada a <c>active</c> (curadoria humana). Uma versão
    /// importada como <c>pending</c> só passa a auto-aprovar depois disto.
    /// </summary>
    Task<AppCatalogEntry?> ActivateAsync(Guid id, string? curatedBy, CancellationToken ct = default);

    Task<IReadOnlyList<AppCatalogEntry>> ListAsync(
        string? source,
        string? status,
        string? search,
        int limit,
        CancellationToken ct = default);

    /// <summary>Chaves "source:packageId" das versões ativas (match do inventário).</summary>
    Task<IReadOnlySet<string>> GetActiveKeysAsync(CancellationToken ct = default);

    /// <summary>
    /// Versões ativas dos apps informados (WU-21). Usado ao montar o comando de
    /// instalação: o payload leva URL + SHA-256 + switches curados.
    /// </summary>
    Task<IReadOnlyList<AppCatalogEntry>> GetActiveEntriesForAsync(
        string source,
        IEnumerable<string> packageIds,
        CancellationToken ct = default);
}
