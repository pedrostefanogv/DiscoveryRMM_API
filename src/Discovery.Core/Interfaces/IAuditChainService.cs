namespace Discovery.Core.Interfaces;

/// <summary>
/// Hash chain por tabela de auditoria (escopo). Calcula
/// entry_hash = SHA256(prev_head ?? scope || payload) em hex minúsculas.
/// </summary>
public interface IAuditChainService
{
    /// <summary>
    /// Calcula o par (prev_hash, entry_hash) para a próxima entrada do escopo.
    /// Em caso de falha retorna (null, null) — fail-open para disponibilidade.
    /// </summary>
    Task<(string? PrevHash, string? EntryHash)> AppendAsync(string scope, string payloadHashBase, CancellationToken ct = default);

    /// <summary>Último entry_hash não nulo do escopo (null se a chain está vazia).</summary>
    Task<string?> GetScopeHeadAsync(string scope, CancellationToken ct = default);
}
