using Discovery.Core.Configuration;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Discovery.Infrastructure.Services.Remote.Recording;

/// <summary>
/// Consulta/download/exclusão da gravação de terminal. A ingestão roda no
/// hosted service; este serviço cobre o lado REST e o StopRecording.
/// </summary>
public sealed class TerminalRecordingService : ITerminalRecordingService
{
    private readonly DiscoveryDbContext _db;
    private readonly RemoteAccessOptions _options;
    private readonly TerminalRecordingCoordinator _coordinator;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TerminalRecordingService> _logger;

    public TerminalRecordingService(
        DiscoveryDbContext db,
        IOptions<RemoteAccessOptions> options,
        TerminalRecordingCoordinator coordinator,
        IHttpClientFactory httpClientFactory,
        ILogger<TerminalRecordingService> logger)
    {
        _db = db;
        _options = options.Value;
        _coordinator = coordinator;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<TerminalRecordingDto?> GetAsync(Guid agentId, Guid sessionId, CancellationToken ct = default)
    {
        var row = await (
                from session in _db.RemoteSessions.AsNoTracking()
                join recording in _db.RemoteTerminalRecordings.AsNoTracking()
                    on session.Id equals recording.RemoteSessionId
                where session.Id == sessionId && session.AgentId == agentId
                select new
                {
                    recording.Id,
                    recording.RemoteSessionId,
                    recording.AgentId,
                    recording.ClientId,
                    recording.Status,
                    recording.StartedAt,
                    recording.LastFrameAt,
                    recording.CompletedAt,
                    recording.Frames,
                    recording.Bytes,
                    recording.DroppedFrames,
                    recording.MaxFps,
                    recording.StorageProvider,
                    recording.RetentionExpiresAt,
                    recording.LastError
                })
            .FirstOrDefaultAsync(ct);

        if (row is null)
            return null;

        return new TerminalRecordingDto(
            row.Id,
            row.RemoteSessionId,
            row.AgentId,
            row.ClientId,
            row.Status,
            row.StartedAt,
            row.LastFrameAt,
            row.CompletedAt,
            row.Frames,
            row.Bytes,
            row.DroppedFrames,
            row.MaxFps,
            (int)row.StorageProvider,
            row.RetentionExpiresAt,
            row.LastError,
            $"/api/v1/remote-sessions/{agentId}/recording/terminal/download?sessionId={sessionId}");
    }

    public async Task<TerminalRecordingTenant?> GetTenantAsync(Guid agentId, Guid sessionId, CancellationToken ct = default)
    {
        var row = await (
                from session in _db.RemoteSessions.AsNoTracking()
                join recording in _db.RemoteTerminalRecordings.AsNoTracking()
                    on session.Id equals recording.RemoteSessionId
                where session.Id == sessionId && session.AgentId == agentId
                select new { recording.ClientId, session.SiteId })
            .FirstOrDefaultAsync(ct);

        // Sem linha não existe gravação: o chamador responde 404 (não 403), para
        // não revelar a existência de sessões de outro tenant.
        return row is null ? null : new TerminalRecordingTenant(row.ClientId, row.SiteId);
    }

    public async Task<TerminalRecordingDownloadDto?> OpenDownloadAsync(Guid agentId, Guid sessionId, CancellationToken ct = default)
    {
        var row = await (
                from session in _db.RemoteSessions.AsNoTracking()
                join recording in _db.RemoteTerminalRecordings.AsNoTracking()
                    on session.Id equals recording.RemoteSessionId
                where session.Id == sessionId && session.AgentId == agentId
                select new { recording.StorageUrl, recording.StorageProvider })
            .FirstOrDefaultAsync(ct);

        if (row is null)
            return null;

        // Com StorageProvider=S3 o objeto existe no bucket (o upload só é
        // confirmado no finalize) e o download vira um 302 para a URL
        // pré-assinada, sem passar o conteúdo pela API. A rota e a autorização
        // não mudam; o modo Local segue exatamente como antes (stream).
        if (IsS3DownloadEnabled(row.StorageProvider))
        {
            return new TerminalRecordingDownloadDto(
                null,
                $"terminal-{sessionId:N}.ndjson",
                0,
                TerminalRecordingS3Storage.PresignUrl(
                    _options.Recording.S3,
                    TerminalRecordingS3Storage.BuildKey(sessionId)));
        }

        var fullPath = TerminalRecordingStoragePaths.ResolveFullPath(_options, sessionId, row.StorageUrl);
        if (!File.Exists(fullPath))
        {
            _logger.LogWarning(
                "Gravação de terminal {SessionId} existe no banco mas o NDJSON não foi encontrado em {Path}",
                sessionId, fullPath);
            return null;
        }

        var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return new TerminalRecordingDownloadDto(
            stream,
            $"terminal-{sessionId:N}.ndjson",
            stream.Length);
    }

    public async Task<bool> DeleteAsync(Guid agentId, Guid sessionId, CancellationToken ct = default)
    {
        // Finaliza primeiro para o writer parar de escrever antes de apagar o arquivo.
        await FinalizeAsync(sessionId, RemoteTerminalRecordingStatus.Completed, null, ct);

        // Limpa o tracking: o finalize acima pode ter atualizado a linha em outro escopo.
        _db.ChangeTracker.Clear();

        var row = await (
                from session in _db.RemoteSessions.AsNoTracking()
                join recording in _db.RemoteTerminalRecordings
                    on session.Id equals recording.RemoteSessionId
                where session.Id == sessionId && session.AgentId == agentId
                select recording)
            .FirstOrDefaultAsync(ct);

        if (row is null)
            return false;

        var fullPath = TerminalRecordingStoragePaths.ResolveFullPath(_options, sessionId, row.StorageUrl);
        try
        {
            if (File.Exists(fullPath))
                File.Delete(fullPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Falha ao remover o NDJSON da gravação de terminal {SessionId} em {Path}", sessionId, fullPath);
        }

        // O upload ao S3 é adicional: a exclusão (LGPD) precisa remover a cópia
        // remota também. A decisão usa o provider GRAVADO na linha (onde o
        // objeto realmente foi enviado), não o provider configurado agora —
        // trocar de Local para S3 não pode impedir a exclusão do objeto.
        // Objeto inexistente é tolerado (404 -> false).
        if (row.StorageProvider == RecordingStorageProvider.S3)
        {
            try
            {
                var storage = new TerminalRecordingS3Storage(_options, _logger, _httpClientFactory);
                await storage.DeleteAsync(TerminalRecordingS3Storage.BuildKey(sessionId), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Falha ao remover o NDJSON da gravação de terminal {SessionId} do S3", sessionId);
            }
        }

        _db.RemoteTerminalRecordings.Remove(row);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Gravação de terminal {SessionId} excluída (agent {AgentId}, arquivo {Path})",
            sessionId, agentId, fullPath);

        return true;
    }

    public Task FinalizeAsync(Guid sessionId, string status, string? lastError = null, CancellationToken ct = default)
        => _coordinator.FinalizeAsync(sessionId, status, lastError, ct);

    /// <summary>
    /// A gravação está no S3 quando a linha foi marcada como S3 no finalize
    /// (upload confirmado) e o provider configurado continua sendo S3.
    /// </summary>
    private bool IsS3DownloadEnabled(RecordingStorageProvider recordedProvider)
        => recordedProvider == RecordingStorageProvider.S3
           && string.Equals(_options.Recording.StorageProvider, "S3", StringComparison.OrdinalIgnoreCase);
}
