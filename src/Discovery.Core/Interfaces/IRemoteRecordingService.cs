using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Serviço de gravação de sessões remotas.
/// </summary>
public interface IRemoteRecordingService
{
    /// <summary>Inicia a gravação de uma sessão.</summary>
    Task<RemoteSessionRecording> StartRecordingAsync(Guid sessionId, Guid userId, CancellationToken ct = default);

    /// <summary>Encerra a gravação e inicia o assembly do arquivo final.</summary>
    Task<RemoteSessionRecording> StopRecordingAsync(Guid sessionId, Guid userId, CancellationToken ct = default);

    /// <summary>Obtém URL de download da gravação.</summary>
    Task<string> GetDownloadUrlAsync(Guid recordingId, CancellationToken ct = default);

    /// <summary>Exclui uma gravação (LGPD Art. 18).</summary>
    Task DeleteRecordingAsync(Guid recordingId, CancellationToken ct = default);

    /// <summary>Recebe um frame de gravação do agent.</summary>
    Task IngestFrameAsync(Guid sessionId, byte[] frameData, CancellationToken ct = default);
}
