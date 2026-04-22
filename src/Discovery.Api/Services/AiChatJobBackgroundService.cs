using System.Threading.Channels;
using Discovery.Core.Interfaces;

namespace Discovery.Api.Services;

public sealed class AiChatJobBackgroundService : BackgroundService, IAiChatJobQueue
{
    private const int RecoveryBatchSize = 100;

    private readonly Channel<AiChatJobWorkItem> _queue = Channel.CreateUnbounded<AiChatJobWorkItem>();
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AiChatJobBackgroundService> _logger;

    public AiChatJobBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<AiChatJobBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public ValueTask EnqueueAsync(Guid jobId, Guid agentId, CancellationToken cancellationToken = default)
        => _queue.Writer.WriteAsync(new AiChatJobWorkItem(jobId, agentId), cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverPendingJobsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var workItem = await _queue.Reader.ReadAsync(stoppingToken);
                await ProcessWorkItemAsync(workItem, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in AI chat job background service.");
            }
        }
    }

    private async Task RecoverPendingJobsAsync(CancellationToken cancellationToken)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var jobRepository = scope.ServiceProvider.GetRequiredService<IAiChatJobRepository>();
        var recoverableJobs = await jobRepository.GetRecoverableAsync(RecoveryBatchSize, cancellationToken);

        foreach (var job in recoverableJobs)
        {
            await EnqueueAsync(job.Id, job.AgentId, cancellationToken);
        }

        if (recoverableJobs.Count > 0)
        {
            _logger.LogInformation("Recovered {Count} pending AI chat jobs for background processing.", recoverableJobs.Count);
        }
    }

    private async Task ProcessWorkItemAsync(AiChatJobWorkItem workItem, CancellationToken cancellationToken)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var jobRepository = scope.ServiceProvider.GetRequiredService<IAiChatJobRepository>();
        var aiChatService = scope.ServiceProvider.GetRequiredService<IAiChatService>();

        var job = await jobRepository.GetByIdAsync(workItem.JobId, workItem.AgentId, cancellationToken);
        if (job is null)
            return;

        if (job.Status is "Completed" or "Failed")
            return;

        try
        {
            job.Status = "Processing";
            job.StartedAt = DateTime.UtcNow;
            job.ErrorMessage = null;
            await jobRepository.UpdateAsync(job, cancellationToken);

            var result = await aiChatService.ProcessSyncAsync(
                workItem.AgentId,
                job.UserMessage,
                job.SessionId,
                cancellationToken);

            job.Status = "Completed";
            job.AssistantMessage = result.AssistantMessage;
            job.TokensUsed = result.TokensUsed;
            job.CompletedAt = DateTime.UtcNow;
            job.ErrorMessage = null;
            await jobRepository.UpdateAsync(job, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("AI chat job background service stopping while processing JobId={JobId}.", workItem.JobId);
        }
        catch (Exception ex)
        {
            job.Status = "Failed";
            job.ErrorMessage = ex.Message;
            job.CompletedAt = DateTime.UtcNow;
            await jobRepository.UpdateAsync(job, cancellationToken);

            _logger.LogError(ex, "Failed to process AI chat job JobId={JobId} for AgentId={AgentId}.", workItem.JobId, workItem.AgentId);
        }
    }

    private readonly record struct AiChatJobWorkItem(Guid JobId, Guid AgentId);
}