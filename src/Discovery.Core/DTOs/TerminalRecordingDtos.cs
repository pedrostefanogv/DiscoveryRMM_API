namespace Discovery.Core.DTOs;

/// <summary>
/// Frame de gravação de terminal publicado pelo agent em
/// <c>tenant.{c}.site.{s}.agent.{a}.remote.session.{sessionId}.recording.term</c>.
/// </summary>
public sealed record TerminalRecordingFrameDto(
    string Data,
    long Seq,
    long TimestampMs,
    string Kind,
    int? ExitCode,
    int? Cols,
    int? Rows,
    string? Backend,
    string? ShellKind,
    bool IsKnownKind);

/// <summary>Metadados da gravação de terminal expostos pela API.</summary>
public sealed record TerminalRecordingDto(
    Guid RecordingId,
    Guid SessionId,
    Guid AgentId,
    Guid ClientId,
    string Status,
    DateTime StartedAtUtc,
    DateTime? LastFrameAtUtc,
    DateTime? CompletedAtUtc,
    long Frames,
    long Bytes,
    long DroppedFrames,
    int MaxFps,
    int StorageProvider,
    DateTime? RetentionExpiresAtUtc,
    string? LastError,
    string DownloadUrl);

/// <summary>
/// Arquivo NDJSON pronto para download. Quando a gravação foi enviada ao S3,
/// S3Url traz a URL pré-assinada (TTL curto) e o controller responde 302 em vez
/// de transmitir o arquivo; Content fica nulo nesse caso. No modo Local o
/// comportamento é o de sempre (stream).
/// </summary>
public sealed record TerminalRecordingDownloadDto(
    Stream? Content,
    string FileName,
    long Length,
    string? S3Url = null);
