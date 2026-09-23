using System.Collections.Concurrent;
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
/// </summary>
public sealed class LabelReprocessBackgroundService : BackgroundService, ILabelReprocessQueue
{
    private const int MaxTrackedJobs = 50;

    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<string, AgentLabelReprocessStatusResponse> _jobs = new();
    private readonly ConcurrentQueue<string> _jobOrder = new();
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<LabelReprocessBackgroundService> _logger;

    public LabelReprocessBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<LabelReprocessBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async ValueTask<string> EnqueueAsync(CancellationToken cancellationToken = default)
    {
        var jobId = Guid.NewGuid().ToString("N");
        var status = new AgentLabelReprocessStatusResponse
        {
            JobId = jobId,
            State = "Queued",
            StartedAt = DateTime.UtcNow,
            Message = "Reprocessamento enfileirado."
        };

        _jobs[jobId] = status;
        _jobOrder.Enqueue(jobId);
        TrimOldJobs();

        // Sem ContinueWith: uma falha na escrita precisa propagar (e o job rastreado
        // removido), em vez de devolver um jobId de um job que nunca foi enfileirado.
        try
        {
            await _queue.Writer.WriteAsync(jobId, cancellationToken);
        }
        catch
        {
            _jobs.TryRemove(jobId, out _);
            throw;
        }

        return jobId;
    }

    public Task<AgentLabelReprocessStatusResponse?> GetStatusAsync(string jobId, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (!_jobs.TryGetValue(jobId, out var status))
            return Task.FromResult<AgentLabelReprocessStatusResponse?>(null);

        // Devolve um snapshot: o objeto interno e mutado pelo worker, e serializar
        // enquanto ele e atualizado poderia emitir um estado inconsistente.
        lock (status)
        {
            return Task.FromResult<AgentLabelReprocessStatusResponse?>(new AgentLabelReprocessStatusResponse
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
            });
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

            await service.ReprocessAllAgentsAsync("manual-reprocess", 200, progress, ct);

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
        if (_jobs.TryGetValue(jobId, out var status))
        {
            lock (status)
            {
                update(status);
            }
        }
    }

    /// <summary>Mantem apenas os jobs mais recentes em memoria.</summary>
    private void TrimOldJobs()
    {
        while (_jobOrder.Count > MaxTrackedJobs && _jobOrder.TryDequeue(out var oldest))
            _jobs.TryRemove(oldest, out _);
    }
}
