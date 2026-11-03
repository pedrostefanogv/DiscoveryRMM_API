using Discovery.Core.Interfaces;
using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Quartz job que fecha os buckets horários dos sensores (agregados usados nos
/// gráficos de longo prazo). Roda a cada 5 minutos e recalcula as últimas 3
/// horas fechadas — a hora corrente é sempre reescrita de forma idempotente.
/// </summary>
[DisallowConcurrentExecution]
public sealed class SensorRollupJob : IJob
{
    public static readonly JobKey Key = new("sensor-rollup", "sensors");

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var scopeFactory = context.GetScopedService<IServiceScopeFactory>();
        var logger = context.GetLogger<SensorRollupJob>();
        var ct = context.CancellationToken;

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var rollup = scope.ServiceProvider.GetRequiredService<ISensorRollupService>();

            var now = DateTime.UtcNow;
            var currentHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);

            for (var offset = 3; offset >= 0; offset--)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    await rollup.RollupAsync(currentHour.AddHours(-offset), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "SensorRollup: falha ao agregar a hora {Hour:O}.", currentHour.AddHours(-offset));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "SensorRollup: erro no ciclo de agregação.");
        }
    }
}
