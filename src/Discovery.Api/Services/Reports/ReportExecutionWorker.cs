using System.Threading.Channels;
using Discovery.Core.Configuration;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace Discovery.Api.Services.Reports;

public sealed record ReportExecutionWorkItem(Guid ExecutionId, Guid? ClientId);

/// <summary>
/// Fila bounded para processamento assincrono imediato de relatorios.
/// </summary>
public sealed class ReportExecutionQueue : IReportExecutionQueue
{
    private readonly Channel<ReportExecutionWorkItem> _channel = Channel.CreateBounded<ReportExecutionWorkItem>(
        new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = false,
            SingleWriter = false
        });

    public bool TryEnqueue(Guid executionId, Guid? clientId)
        => _channel.Writer.TryWrite(new ReportExecutionWorkItem(executionId, clientId));

    internal ChannelReader<ReportExecutionWorkItem> Reader => _channel.Reader;
}

/// <summary>
/// Worker em background que drena a fila de relatorios com escopo DI e
/// DbContext isolados por execucao.
/// </summary>
public sealed class ReportExecutionWorker(
    IReportExecutionQueue queue,
    IServiceScopeFactory scopeFactory,
    IOptions<ReportingOptions> options,
    ILogger<ReportExecutionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (queue is not ReportExecutionQueue concreteQueue)
            return;

        var concurrency = Math.Clamp(options.Value.MaxConcurrentExecutions, 1, 16);
        using var semaphore = new SemaphoreSlim(concurrency, concurrency);

        await foreach (var item in concreteQueue.Reader.ReadAllAsync(stoppingToken))
        {
            await semaphore.WaitAsync(stoppingToken);
            _ = Task.Run(async () =>
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var reportService = scope.ServiceProvider.GetRequiredService<IReportService>();
                    await reportService.ProcessExecutionAsync(item.ExecutionId, item.ClientId, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Shutdown normal
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Erro ao processar execucao de relatorio {ExecutionId} no worker.", item.ExecutionId);
                }
                finally
                {
                    semaphore.Release();
                }
            }, stoppingToken);
        }
    }
}
