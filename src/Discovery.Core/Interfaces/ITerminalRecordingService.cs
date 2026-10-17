using Discovery.Core.DTOs;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Consulta, download e exclusão das gravações incrementais de terminal.
/// A ingestão em si é feita pelo hosted service de NATS; este serviço cobre o
/// ciclo de vida disparado por request (StopRecording e endpoints REST).
/// </summary>
public interface ITerminalRecordingService
{
    /// <summary>Obtém os metadados da gravação de terminal de uma sessão do agent.</summary>
    Task<TerminalRecordingDto?> GetAsync(Guid agentId, Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// Obtém o tenant (cliente) e o site da sessão dona da gravação, sem carregar
    /// os metadados completos. Usado para a autorização scoped dos endpoints REST:
    /// retorna <c>null</c> quando a gravação não existe (o chamador responde 404).
    /// </summary>
    Task<TerminalRecordingTenant?> GetTenantAsync(Guid agentId, Guid sessionId, CancellationToken ct = default);

    /// <summary>Abre o NDJSON para download. Retorna null quando não há gravação.</summary>
    Task<TerminalRecordingDownloadDto?> OpenDownloadAsync(Guid agentId, Guid sessionId, CancellationToken ct = default);

    /// <summary>Exclui a gravação (arquivo + metadados). Retorna false quando não existe.</summary>
    Task<bool> DeleteAsync(Guid agentId, Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// Finaliza a gravação ativa (se houver). Idempotente: chamadas repetidas
    /// não alteram uma gravação já finalizada.
    /// </summary>
    Task FinalizeAsync(Guid sessionId, string status, string? lastError = null, CancellationToken ct = default);
}

/// <summary>
/// Dono da gravação de terminal: cliente (tenant) e site da sessão remota.
/// Base da autorização scoped dos endpoints de gravação.
/// </summary>
public sealed record TerminalRecordingTenant(Guid ClientId, Guid SiteId);
