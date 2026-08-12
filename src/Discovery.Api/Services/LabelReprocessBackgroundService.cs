using System.Threading.Channels;
using Discovery.Core.Interfaces;

namespace Discovery.Api.Services;

/// <summary>
/// Processes label reprocessing requests in the background so the HTTP request
/// returns immediately ("Reprocessamento iniciado") while the batch runs async.
/// </summary>
public sealed class LabelReprocessBackgroundService : BackgroundService, ILabelReprocessQueue
{
    private readonly Channel<byte> _queue = Channel.CreateUnbounded<byte>();
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<LabelReprocessBackgroundService> _logger;

    public LabelReprocessBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<LabelReprocessBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public ValueTask EnqueueAsync(CancellationToken cancellationToken = default)
        => _queue.Writer.WriteAsync(0, cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _queue.Reader.ReadAsync(stoppingToken);
                await ProcessAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao reprocessar labels de agentes em background.");
            }
        }
    }

    private async Task ProcessAsync(CancellationToken ct)
    {
        _logger.LogInformation("Iniciando reprocessamento de labels em background.");
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IAgentAutoLabelingService>();
            await service.ReprocessAllAgentsAsync("manual-reprocess", cancellationToken: ct);
            _logger.LogInformation("Reprocessamento de labels concluído.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Reprocessamento de labels cancelado.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao reprocessar labels de agentes.");
        }
    }
}
