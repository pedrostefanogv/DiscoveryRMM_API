using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Discovery.Infrastructure.Services.Remote.Recording;

/// <summary>
/// Remove objetos de gravação do storage configurado (local ou S3) e registros
/// expirados. As linhas de remote_session_recordings caem por cascata ao excluir
/// o agente, mas os arquivos ficariam órfãos — daí este serviço.
/// </summary>
public class RecordingStorageCleanupService : IRecordingStorageCleanupService
{
    private readonly DiscoveryDbContext _db;
    private readonly RemoteAccessOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RecordingStorageCleanupService> _logger;

    public RecordingStorageCleanupService(
        DiscoveryDbContext db,
        IOptions<RemoteAccessOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<RecordingStorageCleanupService> logger)
    {
        _db = db;
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task DeleteForAgentAsync(Guid agentId, CancellationToken ct = default)
    {
        var recordings = await (
                from recording in _db.RemoteSessionRecordings.AsNoTracking()
                join session in _db.RemoteSessions.AsNoTracking() on recording.RemoteSessionId equals session.Id
                where session.AgentId == agentId && recording.StorageUrl != null
                select recording)
            .ToListAsync(ct);

        await DeleteStorageObjectsAsync(recordings, ct);

        // WP3: os arquivos NDJSON das gravações de terminal também ficam órfãos
        // no purge do agente (as linhas caem por cascata).
        var terminalRecordings = await _db.RemoteTerminalRecordings
            .AsNoTracking()
            .Where(r => r.AgentId == agentId && r.StorageUrl != null)
            .ToListAsync(ct);

        await DeleteTerminalObjectsAsync(terminalRecordings, ct);
    }

    public async Task<int> CleanupExpiredAsync(DateTime cutoffUtc, CancellationToken ct = default)
    {
        var expired = await _db.RemoteSessionRecordings
            .Where(r => r.RetentionExpiresAt != null && r.RetentionExpiresAt < cutoffUtc)
            .ToListAsync(ct);

        // Terminal: a expiração é por linha (started/completed + Retention:DefaultDays),
        // não pelo cutoff global de MaxDays usado no vídeo.
        var nowUtc = DateTime.UtcNow;
        var expiredTerminal = await _db.RemoteTerminalRecordings
            .Where(r => r.RetentionExpiresAt != null && r.RetentionExpiresAt < nowUtc)
            .ToListAsync(ct);

        // Rede de segurança: linha que nunca foi finalizada (agent morreu antes do
        // frame de saída e a varredura não a pegou, ex.: API parada) fica com
        // RetentionExpiresAt nulo e nunca expiraria. Após DefaultDays ela é tratada
        // como expirada para não acumular arquivo/linha órfãos.
        var stuckCutoff = nowUtc.AddDays(-Math.Max(1, _options.Recording.Retention.DefaultDays));
        var stuckTerminal = await _db.RemoteTerminalRecordings
            .Where(r => r.RetentionExpiresAt == null && r.StartedAt < stuckCutoff)
            .ToListAsync(ct);

        if (stuckTerminal.Count > 0)
        {
            _logger.LogWarning(
                "Removendo {Count} gravação(ões) de terminal nunca finalizadas (mais de {Days} dias)",
                stuckTerminal.Count, _options.Recording.Retention.DefaultDays);
            expiredTerminal = expiredTerminal.Concat(stuckTerminal).ToList();
        }

        if (expired.Count == 0 && expiredTerminal.Count == 0)
            return 0;

        if (expired.Count > 0)
        {
            await DeleteStorageObjectsAsync(expired, ct);

            // Evita referência pendurada em remote_sessions.recording_id (não há FK).
            var sessionIds = expired.Select(r => r.RemoteSessionId).Distinct().ToList();
            var sessions = await _db.RemoteSessions
                .Where(s => sessionIds.Contains(s.Id) && s.RecordingId != null)
                .ToListAsync(ct);
            foreach (var session in sessions)
                session.RecordingId = null;

            _db.RemoteSessionRecordings.RemoveRange(expired);
        }

        await DeleteTerminalObjectsAsync(expiredTerminal, ct);
        if (expiredTerminal.Count > 0)
            _db.RemoteTerminalRecordings.RemoveRange(expiredTerminal);

        await _db.SaveChangesAsync(ct);
        return expired.Count + expiredTerminal.Count;
    }

    /// <summary>
    /// Remove o NDJSON da gravação de terminal do(s) storage(s) onde ele está.
    /// Com StorageProvider=S3 o objeto do bucket também sai (a cópia local pode
    /// continuar existindo com KeepLocalCopyAfterUpload=true). Objeto
    /// inexistente no S3 é tolerado: retenção e purge precisam ser idempotentes.
    /// </summary>
    private async Task DeleteTerminalObjectsAsync(
        IReadOnlyList<RemoteTerminalRecording> recordings,
        CancellationToken ct)
    {
        foreach (var recording in recordings)
        {
            try
            {
                var fullPath = TerminalRecordingStoragePaths.ResolveFullPath(
                    _options,
                    recording.RemoteSessionId,
                    recording.StorageUrl);

                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                    _logger.LogInformation("NDJSON de gravação de terminal removido: {Path}", fullPath);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Falha ao remover o NDJSON da gravação de terminal {RecordingId}", recording.Id);
            }

            if (recording.StorageProvider != RecordingStorageProvider.S3)
                continue;

            try
            {
                var storage = new TerminalRecordingS3Storage(_options, _logger, _httpClientFactory);
                await storage.DeleteAsync(
                    TerminalRecordingS3Storage.BuildKey(recording.RemoteSessionId),
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Falha ao remover o NDJSON da gravação de terminal {RecordingId} do S3", recording.Id);
            }
        }
    }

    private async Task DeleteStorageObjectsAsync(IReadOnlyList<RemoteSessionRecording> recordings, CancellationToken ct)
    {
        foreach (var recording in recordings)
        {
            try
            {
                await DeleteStorageObjectAsync(recording, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Falha ao remover a gravação {RecordingId} do storage", recording.Id);
            }
        }
    }

    private async Task DeleteStorageObjectAsync(RemoteSessionRecording recording, CancellationToken ct)
    {
        var target = recording.StorageUrl?.Trim();
        if (string.IsNullOrWhiteSpace(target))
            return;

        if (recording.StorageProvider == RecordingStorageProvider.S3)
        {
            var url = target.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? target
                : BuildS3Url(target);

            using var response = await _httpClientFactory.CreateClient("S3Recording").DeleteAsync(url, ct);
            if (response.IsSuccessStatusCode)
                _logger.LogInformation("Gravação S3 removida: {Url}", url);
            else
                _logger.LogWarning("Storage S3 respondeu {Status} ao remover a gravação {RecordingId}", (int)response.StatusCode, recording.Id);
            return;
        }

        var relative = target.StartsWith("http", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(target, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath.TrimStart('/')
            : target.TrimStart('/');

        var fullPath = Path.Combine(_options.Recording.Local.BasePath, relative);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            _logger.LogInformation("Gravação local removida: {Path}", fullPath);
        }
    }

    private string BuildS3Url(string key)
    {
        var s3 = _options.Recording.S3;
        return s3.UsePathStyle
            ? $"{s3.Endpoint}/{s3.Bucket}/{key}"
            : $"https://{s3.Bucket}.{s3.Endpoint}/{key}";
    }
}
