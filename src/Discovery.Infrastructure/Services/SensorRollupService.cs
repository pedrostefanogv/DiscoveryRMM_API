using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Agregação horária das amostras (avg/min/max/last por canal) para gráficos de
/// longo prazo. Idempotente: apaga e regrava o bucket da hora informada.
/// </summary>
public sealed class SensorRollupService(
    DiscoveryDbContext db,
    ILogger<SensorRollupService> logger) : ISensorRollupService
{
    public async Task<int> RollupAsync(DateTime utcHourStart, CancellationToken cancellationToken = default)
    {
        var hour = new DateTime(utcHourStart.ToUniversalTime().Year, utcHourStart.ToUniversalTime().Month,
            utcHourStart.ToUniversalTime().Day, utcHourStart.ToUniversalTime().Hour, 0, 0, DateTimeKind.Utc);
        var hourEnd = hour.AddHours(1);

        var samples = await db.AgentSensorSamples.AsNoTracking()
            .Where(s => s.CapturedAt >= hour && s.CapturedAt < hourEnd)
            .OrderBy(s => s.CapturedAt)
            .ToListAsync(cancellationToken);

        // Idempotência: recalcular a mesma hora substitui o bucket.
        await db.AgentSensorRollups
            .Where(r => r.BucketStart == hour)
            .ExecuteDeleteAsync(cancellationToken);

        if (samples.Count == 0) return 0;

        var rows = new List<Core.Entities.AgentSensorRollup>();
        var groups = samples.GroupBy(s => new { s.AgentId, s.SensorId, s.TargetKey });

        foreach (var group in groups)
        {
            var failures = group.Count(s => s.Status is SensorStatus.Warning or SensorStatus.Failing);
            var worst = group.Select(s => s.Status).Aggregate(SensorStatus.Pending, Worse);

            var perChannel = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
            foreach (var sample in group)
            {
                foreach (var (key, value) in SensorJson.ParseChannelsValues(sample.ChannelsJson))
                {
                    if (!double.IsFinite(value)) continue;
                    if (!perChannel.TryGetValue(key, out var values))
                    {
                        values = [];
                        perChannel[key] = values;
                    }

                    values.Add(value);
                }
            }

            foreach (var (channel, values) in perChannel)
            {
                rows.Add(new Core.Entities.AgentSensorRollup
                {
                    AgentId = group.Key.AgentId,
                    SensorId = group.Key.SensorId,
                    TargetKey = group.Key.TargetKey,
                    Channel = channel,
                    BucketStart = hour,
                    Avg = values.Average(),
                    Min = values.Min(),
                    Max = values.Max(),
                    Last = values[^1],
                    Samples = values.Count,
                    Failures = failures,
                    WorstStatus = (int)worst
                });
            }
        }

        if (rows.Count == 0) return 0;

        db.AgentSensorRollups.AddRange(rows);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogDebug("Sensor rollup for {Hour:O}: {Rows} bucket row(s) from {Samples} sample(s).", hour, rows.Count, samples.Count);
        return rows.Count;
    }

    private static SensorStatus Worse(SensorStatus current, SensorStatus candidate) => candidate switch
    {
        SensorStatus.Failing => current == SensorStatus.Failing ? current : SensorStatus.Failing,
        SensorStatus.Warning => current is SensorStatus.Failing ? current : SensorStatus.Warning,
        SensorStatus.Unknown => current is SensorStatus.Failing or SensorStatus.Warning ? current : SensorStatus.Unknown,
        SensorStatus.Passing => current is SensorStatus.Pending ? SensorStatus.Passing : current,
        _ => current
    };
}
