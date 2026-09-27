namespace Discovery.Core.Interfaces;

/// <summary>
/// Limpeza dos objetos de gravação no storage (S3/local) e dos registros
/// expirados. É best-effort: falhas de storage são logadas e não propagadas.
/// </summary>
public interface IRecordingStorageCleanupService
{
    /// <summary>Remove os arquivos das gravações vinculadas ao agente.</summary>
    Task DeleteForAgentAsync(Guid agentId, CancellationToken ct = default);

    /// <summary>Remove arquivos e registros de gravações vencidas. Retorna quantas foram removidas.</summary>
    Task<int> CleanupExpiredAsync(DateTime cutoffUtc, CancellationToken ct = default);
}
