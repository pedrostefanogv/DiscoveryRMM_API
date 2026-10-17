using System.Threading.Channels;
using Discovery.Core.Configuration;
using Discovery.Infrastructure.Services.Remote.Recording;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.Core;

namespace Discovery.Infrastructure.Messaging;

/// <summary>
/// Assina os frames de gravação de terminal publicados pelo agent
/// (<c>tenant.*.site.*.agent.*.remote.session.*.recording.term</c>) e os envia
/// para um worker via Channel. O loop do NATS nunca faz I/O de disco/DB: só
/// valida o subject, faz o parse e enfileira. Erros de parse/fila/DB são
/// contabilizados em dropped_frames e nunca derrubam o serviço.
/// </summary>
public sealed class TerminalRecordingIngestService : BackgroundService
{
    public const string Subject = "tenant.*.site.*.agent.*.remote.session.*.recording.term";

    private const int QueueCapacity = 8_192;

    private readonly NatsConnection _connection;
    private readonly TerminalRecordingCoordinator _coordinator;
    private readonly IOptionsMonitor<RemoteAccessOptions> _options;
    private readonly ILogger<TerminalRecordingIngestService> _logger;
    private readonly Channel<TerminalRecordingIngestItem> _queue =
        Channel.CreateBounded<TerminalRecordingIngestItem>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

    private long _queueDropped;
    private DateTime _lastDropLogUtc = DateTime.MinValue;
    private DateTime _lastInvalidLogUtc = DateTime.MinValue;

    public TerminalRecordingIngestService(
        NatsConnection connection,
        TerminalRecordingCoordinator coordinator,
        IOptionsMonitor<RemoteAccessOptions> options,
        ILogger<TerminalRecordingIngestService> logger)
    {
        _connection = connection;
        _coordinator = coordinator;
        _options = options;
        _logger = logger;
    }

    public long QueueDropped => Interlocked.Read(ref _queueDropped);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_coordinator.IsEnabled)
        {
            _logger.LogInformation("Ingestão de gravação de terminal desabilitada (RemoteAccess:Recording:Terminal:Enabled=false)");
            return;
        }

        // Deixa o host concluir o startup antes de abrir a assinatura.
        await Task.Yield();

        var worker = Task.Run(() => ConsumeAsync(stoppingToken), CancellationToken.None);
        var flusher = Task.Run(() => FlushLoopAsync(stoppingToken), CancellationToken.None);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ListenAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Assinatura de gravação de terminal encerrou; reconectando em 2s");
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
            }
        }
        finally
        {
            _queue.Writer.TryComplete();
            await ObserveAsync(worker, TimeSpan.FromSeconds(10), "worker");
            // O stoppingToken já está cancelado: o worker parou de ler e o que
            // sobrou na fila seria perdido antes de finalizar as gravações.
            await DrainPendingFramesAsync();
            await ObserveAsync(flusher, TimeSpan.FromSeconds(5), "flusher");
            await _coordinator.ShutdownAsync(CancellationToken.None);
            _logger.LogInformation("Ingestão de gravação de terminal encerrada");
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        _logger.LogInformation("Assinando frames de gravação de terminal em {Subject}", Subject);

        await foreach (var msg in _connection.SubscribeAsync<string>(Subject, cancellationToken: ct))
        {
            try
            {
                if (!TryParseSubject(msg.Subject, out var clientId, out var agentId, out var sessionId))
                {
                    LogInvalid("subject fora do padrão: {Subject}", msg.Subject);
                    continue;
                }

                var parsed = TerminalRecordingFrameParser.Parse(msg.Data);
                if (!parsed.IsSuccess || parsed.Frame is null)
                {
                    _coordinator.RecordDropped(sessionId);
                    LogInvalid("frame inválido na sessão {SessionId}: {Error}", sessionId, parsed.Error);
                    continue;
                }

                var item = new TerminalRecordingIngestItem(
                    sessionId,
                    agentId,
                    clientId,
                    parsed.Frame,
                    msg.Data ?? string.Empty);

                if (!_queue.Writer.TryWrite(item))
                {
                    Interlocked.Increment(ref _queueDropped);
                    _coordinator.RecordDropped(sessionId);
                    LogQueueDrop(sessionId);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Erro ao processar mensagem de gravação de terminal em {Subject}", msg.Subject);
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(ct))
                await ProcessItemAsync(item, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown: o finally do ExecuteAsync drena a fila e finaliza as
            // gravações abertas.
        }
    }

    private async Task ProcessItemAsync(TerminalRecordingIngestItem item, CancellationToken ct)
    {
        try
        {
            await _coordinator.ProcessFrameAsync(
                item.SessionId,
                item.AgentId,
                item.ClientId,
                item.Frame,
                item.RawJson,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _coordinator.RecordDropped(item.SessionId);
            _logger.LogWarning(ex, "Falha ao ingerir frame da gravação de terminal {SessionId}", item.SessionId);
        }
    }

    private async Task DrainPendingFramesAsync()
    {
        var (processed, failed) = await DrainReaderAsync(
            _queue.Reader,
            ProcessItemAsync,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        if (processed > 0 || failed > 0)
        {
            _logger.LogInformation(
                "Fila de gravação de terminal drenada no shutdown: {Processed} frame(s) processado(s), {Failed} com erro.",
                processed, failed);
        }
    }

    /// <summary>
    /// Processa o que restou no channel sem usar o token de shutdown (que já
    /// está cancelado) e com orçamento de tempo próprio. Um item com erro não
    /// interrompe o dreno dos demais.
    /// </summary>
    internal static async Task<(int Processed, int Failed)> DrainReaderAsync<T>(
        ChannelReader<T> reader,
        Func<T, CancellationToken, Task> handler,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : timeout);

        var processed = 0;
        var failed = 0;
        while (reader.TryRead(out var item))
        {
            if (budget.IsCancellationRequested)
                break;

            try
            {
                await handler(item, budget.Token);
                processed++;
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                failed++;
            }
        }

        return (processed, failed);
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        // Primeira varredura já no primeiro ciclo: depois de um restart existem
        // gravações órfãs em memória de sessões que morreram enquanto a API subia.
        var nextSweepUtc = DateTime.UtcNow;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var interval = Math.Max(100, _options.CurrentValue.Recording.Terminal.FlushIntervalMs);
                await Task.Delay(TimeSpan.FromMilliseconds(interval), ct);

                try
                {
                    await _coordinator.FlushAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Falha no flush periódico das gravações de terminal");
                }

                // Finaliza gravações cuja sessão remota já encerrou/expirou (agent
                // caiu sem frame de saída): libera writer, memória e a linha no banco.
                try
                {
                    var terminal = _options.CurrentValue.Recording.Terminal;
                    if (terminal.SweepIntervalSeconds > 0 && DateTime.UtcNow >= nextSweepUtc)
                    {
                        nextSweepUtc = DateTime.UtcNow.AddSeconds(terminal.SweepIntervalSeconds);
                        var grace = TimeSpan.FromSeconds(Math.Max(0, terminal.InactivityGraceSeconds));
                        await _coordinator.SweepInactiveAsync(grace, ct);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Falha na varredura de gravações de terminal inativas");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Formato: tenant.{clientId}.site.{siteId}.agent.{agentId}.remote.session.{sessionId}.recording.term
    /// </summary>
    public static bool TryParseSubject(
        string? subject,
        out Guid clientId,
        out Guid agentId,
        out Guid sessionId)
    {
        clientId = Guid.Empty;
        agentId = Guid.Empty;
        sessionId = Guid.Empty;

        if (string.IsNullOrWhiteSpace(subject))
            return false;

        var parts = subject.Split('.');
        if (parts.Length != 11)
            return false;

        if (!string.Equals(parts[0], "tenant", StringComparison.Ordinal)
            || !string.Equals(parts[2], "site", StringComparison.Ordinal)
            || !string.Equals(parts[4], "agent", StringComparison.Ordinal)
            || !string.Equals(parts[6], "remote", StringComparison.Ordinal)
            || !string.Equals(parts[7], "session", StringComparison.Ordinal)
            || !string.Equals(parts[9], "recording", StringComparison.Ordinal)
            || !string.Equals(parts[10], "term", StringComparison.Ordinal))
        {
            return false;
        }

        return Guid.TryParse(parts[1], out clientId)
               && Guid.TryParse(parts[5], out agentId)
               && Guid.TryParse(parts[8], out sessionId);
    }

    private async Task ObserveAsync(Task task, TimeSpan timeout, string name)
    {
        try
        {
            await task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Tempo esgotado aguardando o {Name} da ingestão de gravação de terminal", name);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "O {Name} da ingestão de gravação de terminal terminou com erro", name);
        }
    }

    private void LogInvalid(string message, params object?[] args)
    {
        var now = DateTime.UtcNow;
        if (now - _lastInvalidLogUtc < TimeSpan.FromSeconds(10))
            return;

        _lastInvalidLogUtc = now;
        _logger.LogWarning(message, args);
    }

    private void LogQueueDrop(Guid sessionId)
    {
        var now = DateTime.UtcNow;
        if (now - _lastDropLogUtc < TimeSpan.FromSeconds(10))
            return;

        _lastDropLogUtc = now;
        _logger.LogWarning(
            "Fila de ingestão de gravação de terminal cheia; frames descartados (último session={SessionId}, total={Total})",
            sessionId, QueueDropped);
    }

    private sealed record TerminalRecordingIngestItem(
        Guid SessionId,
        Guid AgentId,
        Guid ClientId,
        Discovery.Core.DTOs.TerminalRecordingFrameDto Frame,
        string RawJson);
}
