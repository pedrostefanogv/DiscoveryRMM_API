using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Discovery.Core.DTOs;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;

namespace Discovery.Api.Services;

/// <summary>
/// Processes label reprocessing requests in the background so the HTTP request
/// returns immediately while the batch runs async. Tambem mantem o progresso
/// consultavel por jobId — antes o endpoint apenas dizia "iniciado" e nao havia
/// como saber se o trabalho terminou ou quanto falta.
///
/// <para>
/// MULTI-INSTANCIA: o estado e mantido em memoria (rapido) e publicado no Redis a cada
/// atualizacao, com TTL. Assim um <c>GET /agent-labels/reprocess/{jobId}</c> que caia em
/// outra replica encontra o progresso. A coalescencia de jobs continua por instancia
/// (a fila e serial e idempotente), mas o acompanhamento deixa de responder 404.
/// Redis e best-effort: falha de cache nunca derruba o reprocessamento.
/// </para>
/// </summary>
public sealed class LabelReprocessBackgroundService : BackgroundService, ILabelReprocessQueue
{
    /// <summary>Quantos jobs concluidos manter em memoria (por instancia).</summary>
    private const int MaxTrackedJobs = 50;

    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<string, AgentLabelReprocessStatusResponse> _jobs = new();
    private readonly ConcurrentDictionary<string, string?> _jobActors = new();
    private readonly ConcurrentQueue<string> _jobOrder = new();
    private readonly IServiceProvider _serviceProvider;
    private readonly IRedisService _redis;
    private readonly ILogger<LabelReprocessBackgroundService> _logger;

    private static readonly JsonSerializerOptions ProgressJson = new(JsonSerializerDefaults.Web);

    public LabelReprocessBackgroundService(
        IServiceProvider serviceProvider,
        IRedisService redis,
        ILogger<LabelReprocessBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _redis = redis;
        _logger = logger;
    }

    public async ValueTask<string> EnqueueAsync(string? actor = null, CancellationToken cancellationToken = default, bool coalesce = true)
    {
        // Idempotencia: cliques repetidos em "Reprocessar" enfileiravam varias
        // passagens completas pela frota (a fila e serial). Se ja ha um job ativo,
        // devolve o mesmo id em vez de duplicar o trabalho.
        if (coalesce)
        {
            foreach (var active in _jobs)
            {
                if (active.Value.State is "Queued" or "Running")
                    return active.Key;
            }
        }

        var jobId = Guid.NewGuid().ToString("N");
        var status = new AgentLabelReprocessStatusResponse
        {
            JobId = jobId,
            State = "Queued",
            StartedAt = DateTime.UtcNow,
            Message = "Reprocessamento enfileirado."
        };

        _jobs[jobId] = status;
        _jobActors[jobId] = actor;
        _jobOrder.Enqueue(jobId);
        TrimOldJobs();

        // Publica o estado inicial: outra replica ja consegue acompanhar o job.
        await PersistStatusAsync(jobId, Snapshot(status));

        // Sem ContinueWith: uma falha na escrita precisa propagar (e o job rastreado
        // removido), em vez de devolver um jobId de um job que nunca foi enfileirado.
        try
        {
            await _queue.Writer.WriteAsync(jobId, cancellationToken);
        }
        catch
        {
            _jobs.TryRemove(jobId, out _);
            _jobActors.TryRemove(jobId, out _);
            throw;
        }

        return jobId;
    }

    public async Task<AgentLabelReprocessStatusResponse?> GetStatusAsync(string jobId, CancellationToken cancellationToken = default)
    {
        // Snapshot: o objeto interno e mutado pelo worker, e serializar enquanto ele e
        // atualizado poderia emitir um estado inconsistente.
        if (_jobs.TryGetValue(jobId, out var status))
        {
            lock (status)
            {
                return Snapshot(status);
            }
        }

        // Nao esta nesta replica: pode ter sido enfileirado em outra (multi-instancia).
        return await ReadPersistedStatusAsync(jobId, cancellationToken);
    }

    private static AgentLabelReprocessStatusResponse Snapshot(AgentLabelReprocessStatusResponse status) => new()
    {
        JobId = status.JobId,
        State = status.State,
        Processed = status.Processed,
        Total = status.Total,
        Percent = status.Percent,
        IsCompleted = status.IsCompleted,
        Message = status.Message,
        StartedAt = status.StartedAt,
        FinishedAt = status.FinishedAt
    };

    private async Task PersistStatusAsync(string jobId, AgentLabelReprocessStatusResponse snapshot)
    {
        try
        {
            await _redis.SetAsync(
                AgentLabelingCacheKeys.ReprocessProgressPrefix + jobId,
                JsonSerializer.Serialize(snapshot, ProgressJson),
                AgentLabelingCacheKeys.ReprocessProgressTtlSeconds);
        }
        catch (Exception ex)
        {
            // Best-effort: o acompanhamento local continua funcionando.
            _logger.LogWarning(ex, "Falha ao publicar o progresso do job {JobId} no Redis.", jobId);
        }
    }

    private async Task<AgentLabelReprocessStatusResponse?> ReadPersistedStatusAsync(string jobId, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        try
        {
            var raw = await _redis.GetAsync(AgentLabelingCacheKeys.ReprocessProgressPrefix + jobId);
            return string.IsNullOrWhiteSpace(raw)
                ? null
                : JsonSerializer.Deserialize<AgentLabelReprocessStatusResponse>(raw, ProgressJson);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao ler o progresso do job {JobId} no Redis.", jobId);
            return null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            string jobId;
            try
            {
                jobId = await _queue.Reader.ReadAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await ProcessAsync(jobId, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao reprocessar labels de agentes em background.");
                UpdateJob(jobId, status =>
                {
                    status.State = "Failed";
                    status.IsCompleted = true;
                    status.FinishedAt = DateTime.UtcNow;
                    status.Message = "Falha no reprocessamento.";
                });
            }
        }
    }

    private async Task ProcessAsync(string jobId, CancellationToken ct)
    {
        _logger.LogInformation("Iniciando reprocessamento de labels em background (job {JobId}).", jobId);
        UpdateJob(jobId, status =>
        {
            status.State = "Running";
            status.Message = "Reprocessando agentes...";
        });

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IAgentAutoLabelingService>();

            var progress = new Progress<AgentLabelReprocessProgress>(report =>
            {
                UpdateJob(jobId, status =>
                {
                    status.Processed = report.Processed;
                    status.Total = report.Total;
                    status.Percent = report.Percent;
                    status.IsCompleted = report.IsCompleted;
                    status.Message = report.Message ?? status.Message;
                });
            });

            var actor = _jobActors.TryGetValue(jobId, out var recordedActor) ? recordedActor : null;
            await service.ReprocessAllAgentsAsync("manual-reprocess", 200, progress, actor, ct);

            UpdateJob(jobId, status =>
            {
                status.State = "Completed";
                status.IsCompleted = true;
                status.Percent = 100;
                status.FinishedAt = DateTime.UtcNow;
                status.Message = status.Total > 0
                    ? $"Reprocessamento concluido: {status.Processed}/{status.Total} agentes."
                    : "Reprocessamento concluido.";
            });

            _logger.LogInformation("Reprocessamento de labels concluido (job {JobId}).", jobId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Reprocessamento de labels cancelado (job {JobId}).", jobId);
            UpdateJob(jobId, status =>
            {
                status.State = "Canceled";
                status.IsCompleted = true;
                status.FinishedAt = DateTime.UtcNow;
                status.Message = "Reprocessamento cancelado.";
            });
        }
    }

    private void UpdateJob(string jobId, Action<AgentLabelReprocessStatusResponse> update)
    {
        AgentLabelReprocessStatusResponse? snapshot = null;

        if (_jobs.TryGetValue(jobId, out var status))
        {
            lock (status)
            {
                update(status);
                snapshot = Snapshot(status);
            }
        }

        // Persistencia best-effort (o helper trata e loga qualquer falha de Redis).
        if (snapshot is not null)
            _ = PersistStatusAsync(jobId, snapshot);
    }

    /// <summary>Mantem apenas os jobs mais recentes em memoria.</summary>
    private void TrimOldJobs()
    {
        while (_jobOrder.Count > MaxTrackedJobs && _jobOrder.TryDequeue(out var oldest))
        {
            _jobs.TryRemove(oldest, out _);
            _jobActors.TryRemove(oldest, out _);
        }
    }
}
