using Discovery.Core.Configuration;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Discovery.Infrastructure.Services.Remote.Recording;

/// <summary>
/// Serviço de background que monta arquivos de gravação a partir de frames recebidos.
/// Converte frames individuais em container WebM ou MP4.
/// </summary>
public class RecordingAssemblerService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly RemoteAccessOptions _options;
    private readonly ILogger<RecordingAssemblerService> _logger;

    public RecordingAssemblerService(
        IServiceProvider services,
        IOptions<RemoteAccessOptions> options,
        ILogger<RecordingAssemblerService> logger)
    {
        _services = services;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Recording.Enabled)
        {
            _logger.LogInformation("Recording disabled — assembler service nao iniciado");
            return;
        }

        _logger.LogInformation("RecordingAssemblerService iniciado — intervalo 5min");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                await ProcessCompletedRecordingsAsync(stoppingToken);
                await CleanupExpiredRecordingsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro no RecordingAssemblerService");
            }
        }
    }

    private async Task ProcessCompletedRecordingsAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRemoteSessionRepository>();

        // Busca gravações com status "recording" cujas sessões já foram encerradas
        // e monta o arquivo final no storage configurado
        _logger.LogDebug("Processando gravações pendentes de assembly...");

        // Placeholder: a implementação completa leria os frames do buffer
        // e usaria FFmpeg/libavformat para montar o container.
        //
        // Streams a combinar no container final:
        //   - Screen frames: remote.session.{id}.recording.frame
        //   - Terminal output: remote.session.{id}.recording.term (multi-tab)
        //   - Áudio (futuro): remote.session.{id}.recording.audio
        await Task.CompletedTask;
    }

    private async Task CleanupExpiredRecordingsAsync(CancellationToken ct)
    {
        if (!_options.Recording.Retention.AutoDeleteExpired)
            return;

        var retention = _options.Recording.Retention;
        var cutoff = DateTime.UtcNow.AddDays(-retention.MaxDays);

        _logger.LogDebug("Limpando gravações expiradas (cutoff: {Cutoff})", cutoff);

        // Deletaria arquivos do storage S3/local e registros do banco
        await Task.CompletedTask;
    }
}
