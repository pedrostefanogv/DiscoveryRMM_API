using System.Collections.Concurrent;
using System.Text;
using Discovery.Core.Configuration;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Discovery.Infrastructure.Services.Remote.Recording;

/// <summary>
/// Dono do estado das gravações de terminal em andamento. O worker do NATS
/// chama <see cref="ProcessFrameAsync"/> de forma serializada; cada gravação
/// tem seu próprio gate, writer e rate limit. O DB é acessado por escopo.
/// </summary>
public sealed class TerminalRecordingCoordinator : IAsyncDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<RemoteAccessOptions> _options;
    private readonly ILogger<TerminalRecordingCoordinator> _logger;
    private readonly ConcurrentDictionary<Guid, TerminalRecordingSessionState> _sessions = new();
    private readonly ConcurrentDictionary<string, DateTime> _logThrottle = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _createGate = new(1, 1);

    public TerminalRecordingCoordinator(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<RemoteAccessOptions> options,
        ILogger<TerminalRecordingCoordinator> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    public bool IsEnabled
    {
        get
        {
            var options = _options.CurrentValue;
            return options.Recording.Enabled && options.Recording.Terminal.Enabled;
        }
    }

    public int ActiveRecordings => _sessions.Count;

    public async Task ProcessFrameAsync(
        Guid sessionId,
        Guid agentId,
        Guid clientId,
        TerminalRecordingFrameDto frame,
        string rawJson,
        CancellationToken ct = default)
    {
        if (!IsEnabled)
            return;

        var state = await GetOrCreateStateAsync(sessionId, agentId, clientId, ct);
        if (state is null)
            return;

        string? finalStatus = null;
        string? finalError = null;

        await state.Gate.WaitAsync(ct);
        try
        {
            if (state.FinalizeGuard.IsFinalized)
                return;

            DrainPendingDrops(state);

            // A linha já inclui o sessionId; o tamanho usado no limite e nos
            // metadados é o tamanho efetivamente gravado (inclui o \n).
            var line = TerminalRecordingFrameParser.BuildNdjsonLine(rawJson, sessionId);
            var payloadBytes = Encoding.UTF8.GetByteCount(line) + 1;

            // Frames de controle encerram a gravação: se o exit for descartado
            // pelo rate limit, a linha fica em "recording" para sempre e o estado
            // da sessão nunca sai do dicionário (vazamento em memória).
            var isControlFrame = string.Equals(frame.Kind, "exit", StringComparison.Ordinal)
                                 || string.Equals(frame.Kind, "error", StringComparison.Ordinal);

            var decision = state.RateLimiter.TryAcquire(payloadBytes, ignoreWindowLimits: isControlFrame);
            if (!decision.Accepted)
            {
                state.Metrics.RecordDroppedFrame();
                state.Dirty = true;
                state.LastRateLimitReason = decision.Reason;

                if (ShouldLog($"rate:{sessionId}:{decision.Reason}", TimeSpan.FromSeconds(10)))
                {
                    _logger.LogWarning(
                        "Gravação de terminal {SessionId}: frame descartado pelo rate limit ({Reason}). Total descartado={Dropped}",
                        sessionId, decision.Reason, state.Metrics.DroppedFrames);
                }

                if (string.Equals(decision.Reason, TerminalRecordingRateLimitReasons.RecordingBytes, StringComparison.Ordinal))
                {
                    // Teto absoluto atingido: encerra como failed em vez de deixar
                    // o estado aberto recebendo (e descartando) frames sem fim.
                    finalStatus = RemoteTerminalRecordingStatus.Failed;
                    finalError = "limite máximo de bytes da gravação de terminal atingido";
                    state.LastError = finalError;
                }
                else
                {
                    return;
                }
            }
            else
            {
                await state.Writer.AppendAsync(line, ct);
                state.Metrics.RecordFrame(payloadBytes, DateTime.UtcNow);
                state.Dirty = true;

                if (string.Equals(frame.Kind, "exit", StringComparison.Ordinal))
                {
                    finalStatus = RemoteTerminalRecordingStatus.Completed;
                }
                else if (string.Equals(frame.Kind, "error", StringComparison.Ordinal))
                {
                    finalStatus = RemoteTerminalRecordingStatus.Failed;
                    finalError = string.IsNullOrWhiteSpace(frame.Data) ? "frame de erro recebido do agent" : frame.Data;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            state.Metrics.RecordDroppedFrame();
            state.Dirty = true;
            state.LastError = Truncate(ex.Message, 2000);

            if (ShouldLog($"write:{sessionId}", TimeSpan.FromSeconds(10)))
                _logger.LogWarning(ex, "Falha ao persistir frame da gravação de terminal {SessionId}", sessionId);
        }
        finally
        {
            state.Gate.Release();
        }

        if (finalStatus is not null)
            await FinalizeAsync(sessionId, finalStatus, finalError, ct);
    }

    /// <summary>
    /// Contabiliza um frame que nem chegou ao writer (fila cheia, parse inválido
    /// ou falha de DB). Não bloqueia: só incrementa um contador atômico que o
    /// próximo acesso sob o gate (frame/flush/finalize) drena para os metadados.
    /// Quando não há gravação criada ainda, apenas ignora.
    /// </summary>
    public void RecordDropped(Guid sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var state) && !state.FinalizeGuard.IsFinalized)
            state.IncrementDroppedFrames();
    }

    private static void DrainPendingDrops(TerminalRecordingSessionState state)
    {
        var pending = state.DrainDroppedFrames();
        if (pending <= 0)
            return;

        state.Metrics.RecordDroppedFrames(pending);
        state.Dirty = true;
    }

    /// <summary>Persiste os contadores das gravações ativas que mudaram.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        foreach (var state in _sessions.Values)
        {
            if (!state.Gate.Wait(0))
                continue;

            try
            {
                if (!state.FinalizeGuard.IsFinalized)
                {
                    DrainPendingDrops(state);

                    // O writer tem buffer próprio (64 KB): sem este flush os
                    // frames ficariam só em memória até a próxima escrita passar
                    // do intervalo, então o download veria um arquivo atrasado e
                    // um crash perderia o buffer inteiro.
                    await state.Writer.FlushAsync(ct);

                    if (state.Dirty)
                    {
                        await PersistAsync(state, finalStatus: null, lastError: null, ct);
                        state.Dirty = false;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (ShouldLog($"flush:{state.SessionId}", TimeSpan.FromSeconds(30)))
                    _logger.LogWarning(ex, "Falha ao persistir contadores da gravação de terminal {SessionId}", state.SessionId);
            }
            finally
            {
                state.Gate.Release();
            }
        }
    }

    /// <summary>
    /// Finaliza as gravações cuja SESSÃO REMOTA já não está ativa (encerrada ou
    /// expirada) e que não receberam frame de saída nem StopRecording — cenário
    /// de agent que caiu no meio da sessão. Sem isto o estado em memória e o
    /// writer ficariam abertos até o restart da API e a linha ficaria
    /// "recording" para sempre (a retenção só apaga linhas já finalizadas).
    ///
    /// Só finaliza com evidência de que a sessão morreu: terminal ocioso (sem
    /// output) é normal e continua ativo, então a idade do último frame serve
    /// apenas para filtrar candidatos, nunca como critério de encerramento.
    /// </summary>
    public async Task<int> SweepInactiveAsync(TimeSpan grace, CancellationToken ct = default)
    {
        if (_sessions.IsEmpty)
            return 0;

        var now = DateTime.UtcNow;
        var candidates = new List<Guid>();
        foreach (var state in _sessions.Values)
        {
            if (state.FinalizeGuard.IsFinalized)
                continue;

            var lastActivity = state.Metrics.LastFrameAtUtc ?? state.StartedAtUtc;
            if (now - lastActivity < grace)
                continue;

            candidates.Add(state.SessionId);
        }

        if (candidates.Count == 0)
            return 0;

        List<Guid> stillActive;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
            stillActive = await db.RemoteSessions
                .AsNoTracking()
                .Where(s => candidates.Contains(s.Id)
                            && s.Status == "active"
                            && s.ExpiresAt > now)
                .Select(s => s.Id)
                .ToListAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Sem o banco não há como decidir: tenta de novo no próximo ciclo.
            if (ShouldLog("sweep:query", TimeSpan.FromMinutes(1)))
                _logger.LogWarning(ex, "Falha ao consultar sessões para a varredura da gravação de terminal");
            return 0;
        }

        var finalized = 0;
        foreach (var sessionId in candidates)
        {
            if (stillActive.Contains(sessionId))
                continue;

            await FinalizeAsync(
                sessionId,
                RemoteTerminalRecordingStatus.Completed,
                "sessão remota encerrada sem frame de saída (finalização pela varredura)",
                ct);
            finalized++;
        }

        if (finalized > 0)
        {
            _logger.LogInformation(
                "Varredura de gravação de terminal finalizou {Count} gravação(ões) de sessões encerradas (ativas: {Active})",
                finalized, stillActive.Count);
        }

        return finalized;
    }

    /// <summary>Finaliza uma gravação ativa. Idempotente.</summary>
    public async Task FinalizeAsync(
        Guid sessionId,
        string status,
        string? lastError = null,
        CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(sessionId, out var state))
        {
            await FinalizeStateAsync(state, status, lastError, ct);

            if (_sessions.TryGetValue(sessionId, out var current) && ReferenceEquals(current, state))
                _sessions.TryRemove(sessionId, out _);

            return;
        }

        await FinalizePersistedAsync(sessionId, status, lastError, ct);
    }

    private async Task FinalizeStateAsync(
        TerminalRecordingSessionState state,
        string status,
        string? lastError,
        CancellationToken ct)
    {
        await state.Gate.WaitAsync(ct);
        try
        {
            if (!state.FinalizeGuard.TryBeginFinalize())
                return;

            DrainPendingDrops(state);

            if (!string.IsNullOrWhiteSpace(lastError))
                state.LastError = Truncate(lastError, 2000);

            try
            {
                await state.Writer.FinalizeAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                state.LastError = Truncate(ex.Message, 2000);
                _logger.LogWarning(ex, "Falha ao finalizar o NDJSON da gravação de terminal {SessionId}", state.SessionId);
            }

            // WP4: com StorageProvider=S3 o NDJSON completo é enviado ao S3.
            // O arquivo local já está fechado/flushado aqui. Upload é
            // best-effort: em falha o local é preservado e o erro vai para
            // LastError; o modo Local não passa por este caminho.
            var (uploaded, uploadError) = await TryUploadToS3Async(state.SessionId, state.FullPath, ct);
            var keepLocalCopy = _options.CurrentValue.Recording.Terminal.KeepLocalCopyAfterUpload;
            if (uploaded)
            {
                state.StorageProvider = Discovery.Core.Enums.RecordingStorageProvider.S3;
            }
            else if (uploadError is not null)
            {
                state.LastError = Truncate(uploadError, 2000);
            }

            // Persiste o provider/erro antes de apagar a cópia local: se o banco
            // falhar, o arquivo continua disponível para download/tentativa.
            await PersistAsync(state, status, state.LastError, ct);

            if (uploaded && !keepLocalCopy)
                DeleteLocalCopy(state.FullPath);

            _logger.LogInformation(
                "Gravação de terminal {SessionId} finalizada: status={Status}, frames={Frames}, bytes={Bytes}, dropped={Dropped}, maxFps={MaxFps}",
                state.SessionId, status, state.Metrics.Frames, state.Metrics.Bytes, state.Metrics.DroppedFrames, state.Metrics.MaxFps);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Erro ao finalizar a gravação de terminal {SessionId}", state.SessionId);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    /// <summary>
    /// Fecha uma linha que ficou como "recording" sem estado em memória
    /// (restart da API, StopRecording após o processo ter subido de novo).
    /// Não sobrescreve uma gravação já finalizada.
    /// </summary>
    private async Task FinalizePersistedAsync(
        Guid sessionId,
        string status,
        string? lastError,
        CancellationToken ct)
    {
        // Serializa com a criação do estado: sem o gate, um frame que chega
        // enquanto esta finalização lê/grava a linha cria um estado em memória
        // para uma gravação recém-finalizada — o writer fica aberto até o
        // shutdown e o arquivo continua crescendo (frame tardio após restart ou
        // StopRecording concorrente com o primeiro frame).
        await _createGate.WaitAsync(ct);
        try
        {
            if (_sessions.TryGetValue(sessionId, out var raced))
            {
                await FinalizeStateAsync(raced, status, lastError, ct);

                if (_sessions.TryGetValue(sessionId, out var current) && ReferenceEquals(current, raced))
                    _sessions.TryRemove(sessionId, out _);

                return;
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
            var row = await db.RemoteTerminalRecordings.FirstOrDefaultAsync(r => r.RemoteSessionId == sessionId, ct);
            if (row is null || !string.Equals(row.Status, RemoteTerminalRecordingStatus.Recording, StringComparison.Ordinal))
                return;

            var now = DateTime.UtcNow;
            row.Status = status;
            row.CompletedAt = now;
            row.RetentionExpiresAt = now.AddDays(ResolveRetentionDays());
            if (!string.IsNullOrWhiteSpace(lastError))
                row.LastError = Truncate(lastError, 2000);

            // Mesmo sem estado em memória (restart da API / StopRecording
            // tardio) o NDJSON local pode ser enviado ao S3 agora.
            var options = _options.CurrentValue;
            var fullPath = TerminalRecordingStoragePaths.ResolveFullPath(options, sessionId, row.StorageUrl);
            var (uploaded, uploadError) = await TryUploadToS3Async(sessionId, fullPath, ct);
            if (uploaded)
            {
                row.StorageProvider = Discovery.Core.Enums.RecordingStorageProvider.S3;
            }
            else if (uploadError is not null)
            {
                row.LastError = Truncate(uploadError, 2000);
            }

            await db.SaveChangesAsync(ct);

            if (uploaded && !options.Recording.Terminal.KeepLocalCopyAfterUpload)
                DeleteLocalCopy(fullPath);
            _logger.LogInformation("Gravação de terminal {SessionId} finalizada a partir do registro persistido (status={Status})", sessionId, status);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao finalizar a gravação de terminal {SessionId} no banco", sessionId);
        }
        finally
        {
            _createGate.Release();
        }
    }

    private async Task<TerminalRecordingSessionState?> GetOrCreateStateAsync(
        Guid sessionId,
        Guid agentId,
        Guid clientId,
        CancellationToken ct)
    {
        if (_sessions.TryGetValue(sessionId, out var existing))
            return existing;

        await _createGate.WaitAsync(ct);
        try
        {
            if (_sessions.TryGetValue(sessionId, out existing))
                return existing;

            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();

            var session = await db.RemoteSessions
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == sessionId, ct);

            if (session is null)
            {
                LogThrottled($"missing-session:{sessionId}", TimeSpan.FromMinutes(1),
                    "Frame de gravação de terminal recebido para sessão inexistente {SessionId} (agent {AgentId})",
                    sessionId, agentId);
                return null;
            }

            if (session.AgentId != agentId)
            {
                LogThrottled($"agent-mismatch:{sessionId}", TimeSpan.FromMinutes(1),
                    "Frame de gravação de terminal rejeitado: agent {AgentId} não é dono da sessão {SessionId}",
                    agentId, sessionId);
                return null;
            }

            var effectiveClientId = clientId != Guid.Empty ? clientId : session.TenantId;
            if (session.TenantId != Guid.Empty && effectiveClientId != session.TenantId)
            {
                LogThrottled($"client-mismatch:{sessionId}", TimeSpan.FromMinutes(1),
                    "Frame de gravação de terminal rejeitado: client {ClientId} não corresponde ao tenant da sessão {SessionId}",
                    effectiveClientId, sessionId);
                return null;
            }

            var row = await db.RemoteTerminalRecordings
                .FirstOrDefaultAsync(r => r.RemoteSessionId == sessionId, ct);

            if (row is not null
                && !string.Equals(row.Status, RemoteTerminalRecordingStatus.Recording, StringComparison.Ordinal))
            {
                LogThrottled($"already-finalized:{sessionId}", TimeSpan.FromMinutes(1),
                    "Frame de gravação de terminal ignorado: sessão {SessionId} já possui gravação finalizada ({Status})",
                    sessionId, row.Status);
                return null;
            }

            var options = _options.CurrentValue;
            var terminalOptions = options.Recording.Terminal;
            var now = DateTime.UtcNow;

            if (row is null)
            {
                row = new RemoteTerminalRecording
                {
                    Id = Guid.NewGuid(),
                    RemoteSessionId = sessionId,
                    AgentId = agentId,
                    ClientId = effectiveClientId,
                    Status = RemoteTerminalRecordingStatus.Recording,
                    StartedAt = now,
                    LastFrameAt = now,
                    StorageProvider = Discovery.Core.Enums.RecordingStorageProvider.Local,
                    StorageUrl = TerminalRecordingStoragePaths.ResolveRelativePath(sessionId),
                    RetentionExpiresAt = now.AddDays(ResolveRetentionDays()),
                };

                db.RemoteTerminalRecordings.Add(row);
                await db.SaveChangesAsync(ct);
            }

            var fullPath = TerminalRecordingStoragePaths.ResolveFullPath(options, sessionId, row.StorageUrl);

            var metrics = new TerminalRecordingMetrics();
            metrics.Restore(row.Frames, row.Bytes, row.DroppedFrames, row.MaxFps);

            var limiter = new TerminalRecordingRateLimiter(terminalOptions);
            limiter.RestoreTotalBytes(row.Bytes);

            var writer = new TerminalRecordingNdjsonWriter(
                fullPath,
                TimeSpan.FromMilliseconds(Math.Max(100, terminalOptions.FlushIntervalMs)));

            var state = new TerminalRecordingSessionState(
                row.Id,
                sessionId,
                agentId,
                row.ClientId,
                row.StartedAt,
                fullPath,
                writer,
                limiter,
                metrics);

            _sessions[sessionId] = state;

            _logger.LogInformation(
                "Gravação de terminal iniciada/retomada: session={SessionId}, agent={AgentId}, path={Path}",
                sessionId, agentId, fullPath);

            return state;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (ShouldLog($"create:{sessionId}", TimeSpan.FromSeconds(30)))
                _logger.LogWarning(ex, "Falha ao abrir a gravação de terminal {SessionId}", sessionId);
            return null;
        }
        finally
        {
            _createGate.Release();
        }
    }

    /// <summary>
    /// Envia o NDJSON ao S3 quando RemoteAccess:Recording:StorageProvider == "S3"
    /// e Recording:Terminal:UploadToS3 está ligado. Sucesso: (true, null); falha:
    /// (false, mensagem) com o arquivo local intacto e o erro indo para LastError.
    /// Modo Local (default) não passa por aqui: (false, null).
    /// </summary>
    private async Task<(bool Uploaded, string? Error)> TryUploadToS3Async(
        Guid sessionId,
        string fullPath,
        CancellationToken ct)
    {
        var options = _options.CurrentValue;
        var terminal = options.Recording.Terminal;

        if (!terminal.UploadToS3
            || !string.Equals(options.Recording.StorageProvider, "S3", StringComparison.OrdinalIgnoreCase))
            return (false, null);

        if (!File.Exists(fullPath))
        {
            if (ShouldLog($"s3-missing:{sessionId}", TimeSpan.FromMinutes(1)))
            {
                _logger.LogWarning(
                    "Gravação de terminal {SessionId} não enviada ao S3: NDJSON não encontrado em {Path}",
                    sessionId, fullPath);
            }

            // Gravação sem nenhum frame aceito não chega a criar o arquivo:
            // não é falha de upload, só não há o que enviar.
            return (false, null);
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var httpClientFactory = scope.ServiceProvider.GetService<IHttpClientFactory>();
            if (httpClientFactory is null)
            {
                // Host sem AddHttpClient: mantém o modo local em vez de falhar o finalize.
                if (ShouldLog($"s3-nofactory:{sessionId}", TimeSpan.FromMinutes(1)))
                {
                    _logger.LogWarning(
                        "Upload da gravação de terminal {SessionId} ao S3 indisponível: IHttpClientFactory não registrado",
                        sessionId);
                }

                return (false, null);
            }

            var storage = new TerminalRecordingS3Storage(
                options,
                scope.ServiceProvider.GetService<ILogger<TerminalRecordingS3Storage>>() ?? (ILogger)NullLogger.Instance,
                httpClientFactory);

            await storage.UploadFileAsync(TerminalRecordingS3Storage.BuildKey(sessionId), fullPath, ct);

            return (true, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Falha ao enviar a gravação de terminal {SessionId} ao S3; mantendo a cópia local em {Path}",
                sessionId, fullPath);

            return (false, $"falha no upload da gravação de terminal ao S3: {ex.Message}");
        }
    }

    /// <summary>Remove a cópia local depois de um upload bem-sucedido (KeepLocalCopyAfterUpload=false).</summary>
    private void DeleteLocalCopy(string fullPath)
    {
        try
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                _logger.LogInformation("Cópia local do NDJSON removida após upload ao S3: {Path}", fullPath);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Falha ao remover a cópia local do NDJSON após upload ao S3: {Path}", fullPath);
        }
    }

    private async Task PersistAsync(
        TerminalRecordingSessionState state,
        string? finalStatus,
        string? lastError,
        CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();

        var row = await db.RemoteTerminalRecordings
            .FirstOrDefaultAsync(r => r.Id == state.RecordingId, ct);

        if (row is null)
            return;

        row.Frames = state.Metrics.Frames;
        row.Bytes = state.Metrics.Bytes;
        row.DroppedFrames = state.Metrics.DroppedFrames;
        row.MaxFps = state.Metrics.MaxFps;
        // Só muda em finalize com upload bem-sucedido; durante a gravação
        // continua Local (o writer sempre escreve em disco).
        row.StorageProvider = state.StorageProvider;

        if (state.Metrics.LastFrameAtUtc.HasValue)
            row.LastFrameAt = state.Metrics.LastFrameAtUtc;

        if (!string.IsNullOrWhiteSpace(lastError))
            row.LastError = Truncate(lastError, 2000);

        if (finalStatus is not null)
        {
            var now = DateTime.UtcNow;
            row.Status = finalStatus;
            row.CompletedAt = now;
            row.RetentionExpiresAt = now.AddDays(ResolveRetentionDays());
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        if (_sessions.IsEmpty)
            return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        foreach (var sessionId in _sessions.Keys.ToArray())
        {
            try
            {
                await FinalizeAsync(sessionId, RemoteTerminalRecordingStatus.Completed, "shutdown da API", timeout.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Tempo esgotado ao finalizar a gravação de terminal {SessionId} no shutdown", sessionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao finalizar a gravação de terminal {SessionId} no shutdown", sessionId);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync(CancellationToken.None);
        _createGate.Dispose();
    }

    private int ResolveRetentionDays()
    {
        var days = _options.CurrentValue.Recording.Retention.DefaultDays;
        return days > 0 ? days : 30;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private bool ShouldLog(string key, TimeSpan interval)
        => ShouldLogCore(_logThrottle, key, interval);

    private void LogThrottled(string key, TimeSpan interval, string message, params object?[] args)
    {
        if (ShouldLog(key, interval))
            _logger.LogWarning(message, args);
    }

    internal static bool ShouldLogCore(
        ConcurrentDictionary<string, DateTime> throttle,
        string key,
        TimeSpan interval)
    {
        var now = DateTime.UtcNow;
        if (throttle.TryGetValue(key, out var last) && now - last < interval)
            return false;

        throttle[key] = now;

        if (throttle.Count > 5_000)
        {
            foreach (var entry in throttle.Where(pair => now - pair.Value > TimeSpan.FromMinutes(10)).ToList())
                throttle.TryRemove(entry.Key, out _);
        }

        return true;
    }
}
