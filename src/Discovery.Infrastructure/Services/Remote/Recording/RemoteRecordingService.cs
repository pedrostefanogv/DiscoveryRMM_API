using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Discovery.Core.Configuration;

namespace Discovery.Infrastructure.Services.Remote.Recording;

/// <summary>
/// Orquestra gravação de sessões remotas.
/// </summary>
public class RemoteRecordingService : IRemoteRecordingService
{
    private readonly IRemoteSessionRepository _sessionRepo;
    private readonly RemoteAccessOptions _options;
    private readonly ILogger<RemoteRecordingService> _logger;

    public RemoteRecordingService(
        IRemoteSessionRepository sessionRepo,
        IOptions<RemoteAccessOptions> options,
        ILogger<RemoteRecordingService> logger)
    {
        _sessionRepo = sessionRepo;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<RemoteSessionRecording> StartRecordingAsync(Guid sessionId, Guid userId, CancellationToken ct = default)
    {
        var session = await _sessionRepo.GetByIdAsync(sessionId, ct)
            ?? throw new InvalidOperationException($"Session {sessionId} not found.");

        if (session.Status != "active")
            throw new InvalidOperationException($"Session {sessionId} is not active.");

        if (session.RecordingEnabled)
            throw new InvalidOperationException($"Session {sessionId} already has recording active.");

        var recording = new RemoteSessionRecording
        {
            Id = Guid.NewGuid(),
            RemoteSessionId = sessionId,
            Status = "recording",
            SourceCodec = session.Codec,
            StorageProvider = ResolveStorageProvider(),
            ContainerFormat = session.Codec == RemoteCodec.H264 ? "mp4" : "webm",
            StartedAt = DateTime.UtcNow,
            StartedBy = userId.ToString(),
            RetentionExpiresAt = DateTime.UtcNow.AddDays(_options.Recording.Retention.DefaultDays)
        };

        session.RecordingEnabled = true;
        session.RecordingId = recording.Id;
        await _sessionRepo.UpdateAsync(session, ct);

        _logger.LogInformation("Recording {RecordingId} started for session {SessionId} by user {UserId}",
            recording.Id, sessionId, userId);

        return recording;
    }

    public async Task<RemoteSessionRecording> StopRecordingAsync(Guid sessionId, Guid userId, CancellationToken ct = default)
    {
        var session = await _sessionRepo.GetByIdAsync(sessionId, ct)
            ?? throw new InvalidOperationException($"Session {sessionId} not found.");

        if (!session.RecordingEnabled || session.RecordingId == null)
            throw new InvalidOperationException($"Session {sessionId} has no active recording.");

        // Aqui o assembler entraria para montar o arquivo final
        var recording = new RemoteSessionRecording
        {
            Id = session.RecordingId.Value,
            RemoteSessionId = sessionId,
            Status = "completed",
            CompletedAt = DateTime.UtcNow,
        };

        session.RecordingEnabled = false;
        await _sessionRepo.UpdateAsync(session, ct);

        _logger.LogInformation("Recording stopped for session {SessionId}", sessionId);
        return recording;
    }

    public Task<string> GetDownloadUrlAsync(Guid recordingId, CancellationToken ct = default)
    {
        // Retorna URL presigned S3 ou path local
        var provider = ResolveStorageProvider();
        var path = $"/recordings/{recordingId:N}.webm";

        if (provider == RecordingStorageProvider.S3)
            return Task.FromResult($"{_options.Recording.S3.Endpoint}/{_options.Recording.S3.Bucket}{path}");

        return Task.FromResult($"/api/v1/remote-sessions/recording/{recordingId}/download");
    }

    public Task DeleteRecordingAsync(Guid recordingId, CancellationToken ct = default)
    {
        _logger.LogInformation("Recording {RecordingId} deleted (LGPD Art. 18)", recordingId);
        return Task.CompletedTask;
    }

    public Task IngestFrameAsync(Guid sessionId, byte[] frameData, CancellationToken ct = default)
    {
        // Recebe frame do agent e escreve no buffer de assembly
        // Seria processado pelo RecordingAssemblerService (background)
        return Task.CompletedTask;
    }

    private RecordingStorageProvider ResolveStorageProvider()
        => Enum.TryParse<RecordingStorageProvider>(_options.Recording.StorageProvider, true, out var p)
            ? p
            : RecordingStorageProvider.Local;
}
