using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Repositório para o manifesto canônico de chunks P2P.
/// Upsert-only: se o manifesto já existe para o artifactId, sobrescreve
/// apenas se sha256 diferente OU generatedAt mais novo.
/// </summary>
public interface IP2pArtifactManifestRepository
{
    /// <summary>
    /// Retorna o manifesto do artifact, ou null se não existir.
    /// </summary>
    Task<P2pArtifactManifest?> GetByArtifactIdAsync(Guid artifactId, CancellationToken ct = default);

    /// <summary>
    /// Insere ou atualiza o manifesto.
    /// </summary>
    Task UpsertAsync(P2pArtifactManifest manifest, CancellationToken ct = default);
}
