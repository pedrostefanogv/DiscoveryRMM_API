using System.Globalization;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Séries temporais dos gráficos: usa amostras brutas para janelas curtas e
/// agregados horários/diários para janelas longas (retenção).
/// </summary>
public sealed class SensorSeriesService(
    DiscoveryDbContext db,
    ISensorDefinitionService definitionService) : ISensorSeriesService
{
    private const int MaxPointsPerChannel = 720;

    /// <summary>
    /// Só entrega série/eventos de sensor que REALMENTE se aplica ao agente —
    /// evita ler histórico de sensor de outro escopo passando ids na URL.
    /// </summary>
    private async Task<bool> IsApplicableAsync(Guid agentId, Guid sensorId, CancellationToken cancellationToken)
    {
        var applicable = await definitionService.ResolveApplicableAsync(agentId, cancellationToken);
        return applicable.Any(s => s.Id == sensorId);
    }

    public async Task<SensorSeriesDto?> GetSeriesAsync(
        Guid agentId,
        Guid sensorId,
        string? targetKey,
        DateTime from,
        DateTime to,
        IReadOnlyList<string>? channels,
        CancellationToken cancellationToken = default)
    {
        if (!await IsApplicableAsync(agentId, sensorId, cancellationToken)) return null;

        var definition = await db.SensorDefinitions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sensorId && s.DeletedAt == null, cancellationToken);
        if (definition is null) return null;

        var toUtc = Normalize(to, DateTime.UtcNow);
        var fromUtc = Normalize(from, toUtc.AddHours(-24));
        if (fromUtc >= toUtc) fromUtc = toUtc.AddHours(-1);
        if ((toUtc - fromUtc).TotalDays > 400) fromUtc = toUtc.AddDays(-400);

        var normalizedTarget = targetKey?.Trim() ?? string.Empty;
        var span = toUtc - fromUtc;
        var declaredKeys = SensorJson.ParseChannels(definition.ChannelsJson).Select(c => c.Key).ToList();

        IReadOnlyList<string> channelKeys;
        List<SeriesBucket> buckets;
        string resolution;

        if (span.TotalHours <= 24)
        {
            resolution = "raw";
            var samples = await db.AgentSensorSamples.AsNoTracking()
                .Where(s => s.AgentId == agentId && s.SensorId == sensorId
                            && s.TargetKey == normalizedTarget
                            && s.CapturedAt >= fromUtc && s.CapturedAt <= toUtc)
                .OrderBy(s => s.CapturedAt)
                .Take(20000)
                .ToListAsync(cancellationToken);

            channelKeys = ResolveChannelKeys(channels, declaredKeys, samples);
            buckets = BuildRawBuckets(samples, fromUtc, toUtc, span);
        }
        else if (span.TotalDays <= 90)
        {
            resolution = "hourly";
            var rollups = await db.AgentSensorRollups.AsNoTracking()
                .Where(r => r.AgentId == agentId && r.SensorId == sensorId
                            && r.TargetKey == normalizedTarget
                            && r.BucketStart >= fromUtc && r.BucketStart <= toUtc)
                .OrderBy(r => r.BucketStart)
                .Take(50000)
                .ToListAsync(cancellationToken);

            channelKeys = ResolveChannelKeys(channels, declaredKeys, rollups);
            buckets = BuildRollupBuckets(rollups, fromUtc, toUtc, span, daily: false);
        }
        else
        {
            resolution = "daily";
            var rollups = await db.AgentSensorRollups.AsNoTracking()
                .Where(r => r.AgentId == agentId && r.SensorId == sensorId
                            && r.TargetKey == normalizedTarget
                            && r.BucketStart >= fromUtc && r.BucketStart <= toUtc)
                .OrderBy(r => r.BucketStart)
                .Take(200000)
                .ToListAsync(cancellationToken);

            channelKeys = ResolveChannelKeys(channels, declaredKeys, rollups);
            buckets = BuildRollupBuckets(rollups, fromUtc, toUtc, span, daily: true);
        }

        if (channels is { Count: > 0 })
            channelKeys = channels.Where(c => channelKeys.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();

        var channelsPayload = channelKeys.Select(key => new SensorSeriesChannelDto
        {
            Key = key,
            Label = declaredKeys.Contains(key, StringComparer.OrdinalIgnoreCase)
                ? SensorJson.ParseChannels(definition.ChannelsJson).First(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Label
                : key,
            Unit = ResolveUnit(definition, key),
            Points = buckets.Select(b => new SensorSeriesPointDto
            {
                T = b.Start,
                V = b.Values.TryGetValue(key, out var value) ? value : null,
                Min = b.Mins.TryGetValue(key, out var min) ? min : null,
                Max = b.Maxs.TryGetValue(key, out var max) ? max : null
            }).ToList(),
            Stats = BuildStats(buckets, key)
        }).ToList();

        return new SensorSeriesDto
        {
            AgentId = agentId,
            SensorId = sensorId,
            TargetKey = normalizedTarget,
            From = fromUtc,
            To = toUtc,
            Resolution = resolution,
            StepSeconds = (int)Math.Max(60, buckets.Count > 0 ? (toUtc - fromUtc).TotalSeconds / Math.Max(1, buckets.Count) : 60),
            WarningThreshold = definition.WarningThreshold,
            CriticalThreshold = definition.CriticalThreshold,
            Comparison = definition.Comparison,
            ThresholdUnit = definition.ThresholdUnit,
            Channels = channelsPayload,
            StatusStrips = BuildStatusStrips(buckets, toUtc)
        };
    }

    public async Task<SensorEventPageDto> GetEventsAsync(
        Guid agentId,
        Guid sensorId,
        string? targetKey,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (!await IsApplicableAsync(agentId, sensorId, cancellationToken)) return new SensorEventPageDto();

        var take = Math.Clamp(limit, 1, 200);
        var query = db.AgentSensorEvents.AsNoTracking()
            .Where(e => e.AgentId == agentId && e.SensorId == sensorId);

        if (!string.IsNullOrWhiteSpace(targetKey))
            query = query.Where(e => e.TargetKey == targetKey);

        if (TryParseCursor(cursor, out var cursorTicks, out var cursorId))
        {
            query = query.Where(e => e.OccurredAt < cursorTicks
                                     || (e.OccurredAt == cursorTicks && e.Id.CompareTo(cursorId) < 0));
        }

        var rows = await query
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Take(take + 1)
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (rows.Count > take)
        {
            var last = rows[take - 1];
            nextCursor = string.Create(CultureInfo.InvariantCulture, $"{last.OccurredAt.ToUniversalTime().Ticks}:{last.Id}");
            rows = rows.Take(take).ToList();
        }

        return new SensorEventPageDto
        {
            Items = rows.Select(e => new SensorEventDto
            {
                Id = e.Id,
                TargetKey = e.TargetKey,
                Kind = e.Kind,
                Status = e.Status,
                Severity = e.Severity,
                PreviousStatus = e.PreviousStatus,
                PreviousSeverity = e.PreviousSeverity,
                Message = e.Message,
                ExitCode = e.ExitCode,
                OccurredAt = e.OccurredAt
            }).ToList(),
            NextCursor = nextCursor
        };
    }

    // ── Buckets ────────────────────────────────────────────────────────────

    private sealed class SeriesBucket
    {
        public DateTime Start { get; init; }
        public Dictionary<string, double> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> Mins { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> Maxs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Counts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SensorStatus WorstStatus { get; set; } = SensorStatus.Pending;
        public int? EdgeStatus { get; set; }
    }

    private static List<SeriesBucket> BuildRawBuckets(
        IReadOnlyList<AgentSensorSample> samples,
        DateTime fromUtc,
        DateTime toUtc,
        TimeSpan span)
    {
        var step = StepFor(span, samples.Count);
        var bucketCount = Math.Max(1, (int)Math.Ceiling(span.TotalSeconds / step.TotalSeconds));
        var buckets = new List<SeriesBucket>(bucketCount);
        for (var i = 0; i < bucketCount; i++)
            buckets.Add(new SeriesBucket { Start = fromUtc.AddSeconds(i * step.TotalSeconds) });

        foreach (var sample in samples)
        {
            var offset = (int)((sample.CapturedAt - fromUtc).TotalSeconds / step.TotalSeconds);
            if (offset < 0) offset = 0;
            if (offset >= buckets.Count) offset = buckets.Count - 1;
            var bucket = buckets[offset];
            Merge(bucket, SensorJson.ParseChannelsValues(sample.ChannelsJson));
            bucket.WorstStatus = Worse(bucket.WorstStatus, sample.Status);
        }

        return buckets;
    }

    private static List<SeriesBucket> BuildRollupBuckets(
        IReadOnlyList<AgentSensorRollup> rollups,
        DateTime fromUtc,
        DateTime toUtc,
        TimeSpan span,
        bool daily)
    {
        var step = daily ? TimeSpan.FromDays(1) : TimeSpan.FromHours(1);
        var bucketCount = Math.Max(1, (int)Math.Ceiling(span.TotalSeconds / step.TotalSeconds));
        var starts = new List<DateTime>(bucketCount);
        var start = Truncate(fromUtc, step);
        for (var i = 0; i < bucketCount; i++)
        {
            starts.Add(start.AddTicks(step.Ticks * i));
        }

        var buckets = starts.Select(s => new SeriesBucket { Start = s }).ToList();

        foreach (var rollup in rollups)
        {
            var offset = (int)((Truncate(rollup.BucketStart, step) - buckets[0].Start).Ticks / step.Ticks);
            if (offset < 0 || offset >= buckets.Count) continue;
            var bucket = buckets[offset];
            if (rollup.Avg is double avg) Merge(bucket, new Dictionary<string, double> { [rollup.Channel] = avg });
            if (rollup.Min is double min) bucket.Mins[rollup.Channel] = bucket.Mins.TryGetValue(rollup.Channel, out var currentMin) ? Math.Min(currentMin, min) : min;
            if (rollup.Max is double max) bucket.Maxs[rollup.Channel] = bucket.Maxs.TryGetValue(rollup.Channel, out var currentMax) ? Math.Max(currentMax, max) : max;
            if (rollup.WorstStatus >= 0) bucket.WorstStatus = Worse(bucket.WorstStatus, (SensorStatus)rollup.WorstStatus);
        }

        return buckets;
    }

    private static void Merge(SeriesBucket bucket, IReadOnlyDictionary<string, double> values)
    {
        foreach (var (key, value) in values)
        {
            if (!double.IsFinite(value)) continue;
            if (bucket.Values.TryGetValue(key, out var current))
            {
                var count = bucket.Counts.GetValueOrDefault(key) + 1;
                bucket.Counts[key] = count;
                bucket.Values[key] = (current * (count - 1) + value) / count;
                bucket.Mins[key] = Math.Min(bucket.Mins.GetValueOrDefault(key, value), value);
                bucket.Maxs[key] = Math.Max(bucket.Maxs.GetValueOrDefault(key, value), value);
            }
            else
            {
                bucket.Values[key] = value;
                bucket.Mins[key] = value;
                bucket.Maxs[key] = value;
                bucket.Counts[key] = 1;
            }
        }
    }

    private static IReadOnlyList<SensorStatusStripDto> BuildStatusStrips(IReadOnlyList<SeriesBucket> buckets, DateTime toUtc)
    {
        var strips = new List<SensorStatusStripDto>();
        if (buckets.Count == 0) return strips;

        SensorStatusStripDto? current = null;
        foreach (var bucket in buckets)
        {
            if (bucket.WorstStatus == SensorStatus.Pending) continue;
            if (current is null || current.Status != bucket.WorstStatus)
            {
                if (current is not null) strips.Add(current with { To = bucket.Start });
                current = new SensorStatusStripDto { From = bucket.Start, To = bucket.Start, Status = bucket.WorstStatus };
            }
        }

        if (current is not null) strips.Add(current with { To = toUtc });
        return strips;
    }

    private static SensorSeriesStatsDto BuildStats(IReadOnlyList<SeriesBucket> buckets, string key)
    {
        var values = buckets
            .Select(b => b.Values.TryGetValue(key, out var value) ? (double?)value : null)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();

        if (values.Count == 0) return new SensorSeriesStatsDto();

        var sorted = values.OrderBy(v => v).ToList();
        var p95Index = Math.Min(sorted.Count - 1, (int)Math.Ceiling(sorted.Count * 0.95) - 1);
        return new SensorSeriesStatsDto
        {
            Min = sorted[0],
            Max = sorted[^1],
            Avg = sorted.Average(),
            P95 = sorted[p95Index],
            Last = values[^1],
            Count = values.Count
        };
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static TimeSpan StepFor(TimeSpan span, int sampleCount)
    {
        if (span.TotalHours <= 1) return TimeSpan.FromMinutes(1);
        if (span.TotalHours <= 6) return TimeSpan.FromMinutes(5);
        if (span.TotalHours <= 24) return TimeSpan.FromMinutes(15);
        var bySpan = TimeSpan.FromSeconds(span.TotalSeconds / MaxPointsPerChannel);
        if (bySpan < TimeSpan.FromMinutes(5)) return TimeSpan.FromMinutes(5);
        if (bySpan > TimeSpan.FromHours(1)) return TimeSpan.FromHours(1);
        return bySpan;
    }

    private static List<string> ResolveChannelKeys(
        IReadOnlyList<string>? requested,
        IReadOnlyList<string> declared,
        IReadOnlyList<AgentSensorSample> samples)
    {
        if (requested is { Count: > 0 }) return requested.ToList();
        if (declared.Count > 0) return declared.ToList();

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in samples.Take(200))
        {
            foreach (var key in SensorJson.ParseChannelsValues(sample.ChannelsJson).Keys) keys.Add(key);
        }

        return keys.ToList();
    }

    private static List<string> ResolveChannelKeys(
        IReadOnlyList<string>? requested,
        IReadOnlyList<string> declared,
        IReadOnlyList<AgentSensorRollup> rollups)
    {
        if (requested is { Count: > 0 }) return requested.ToList();
        if (declared.Count > 0) return declared.ToList();
        return rollups.Select(r => r.Channel).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static SensorChannelUnit ResolveUnit(SensorDefinition definition, string key)
    {
        var declared = SensorJson.ParseChannels(definition.ChannelsJson)
            .FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (declared is not null) return declared.Unit;

        return key switch
        {
            "free_percent" or "used_percent" or "loss_percent" or "busy_percent" => SensorChannelUnit.Percent,
            "free_bytes" or "total_bytes" or "available_bytes" or "bytes" or "working_set_bytes" => SensorChannelUnit.Bytes,
            "rx_bps" or "tx_bps" or "read_bps" or "write_bps" => SensorChannelUnit.BytesPerSecond,
            "download_mbps" or "upload_mbps" => SensorChannelUnit.MegabitsPerSecond,
            "uptime_seconds" => SensorChannelUnit.Seconds,
            "running" or "open" or "start_pending" => SensorChannelUnit.Boolean,
            "status_code" or "count" or "exit_code" => SensorChannelUnit.Count,
            _ => SensorChannelUnit.Milliseconds
        };
    }

    private static SensorStatus Worse(SensorStatus current, SensorStatus candidate)
        => Rank(candidate) > Rank(current) ? candidate : current;

    private static int Rank(SensorStatus status) => status switch
    {
        SensorStatus.Failing => 4,
        SensorStatus.Warning => 3,
        SensorStatus.Unknown => 2,
        SensorStatus.Passing => 1,
        _ => 0
    };

    private static DateTime Truncate(DateTime value, TimeSpan step)
    {
        var utc = value.ToUniversalTime();
        if (step >= TimeSpan.FromDays(1)) return new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc);
        if (step >= TimeSpan.FromHours(1)) return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);
        return utc;
    }

    private static DateTime Normalize(DateTime value, DateTime fallback)
    {
        if (value == default) return fallback.ToUniversalTime();
        return value.Kind == DateTimeKind.Utc
            ? value
            : value.ToUniversalTime();
    }

    private static bool TryParseCursor(string? cursor, out DateTime ticks, out Guid id)
    {
        ticks = default;
        id = default;
        if (string.IsNullOrWhiteSpace(cursor)) return false;

        var parts = cursor.Split(':', 2);
        if (parts.Length != 2) return false;
        if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawTicks)) return false;
        if (!Guid.TryParse(parts[1], out var parsedId)) return false;

        ticks = new DateTime(rawTicks, DateTimeKind.Utc);
        id = parsedId;
        return true;
    }
}
