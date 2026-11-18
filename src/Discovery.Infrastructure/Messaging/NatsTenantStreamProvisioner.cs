using System.Collections.Concurrent;
using System.Globalization;
using Discovery.Core.Configuration;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Discovery.Infrastructure.Messaging;

/// <summary>
/// Provisiona streams JetStream isolados por tenant (single-flight + memoização em memória).
/// </summary>
public sealed class NatsTenantStreamProvisioner : INatsTenantStreamProvisioner
{
    private readonly NatsConnection _connection;
    private readonly IOptionsMonitor<NatsFanoutStreamOptions> _fanoutOptions;
    private readonly IOptionsMonitor<NatsSupportChatStreamOptions> _supportChatOptions;
    private readonly ILogger<NatsTenantStreamProvisioner> _logger;

    private readonly ConcurrentDictionary<string, byte> _ensuredStreams = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _streamLocks = new(StringComparer.Ordinal);

    public NatsTenantStreamProvisioner(
        NatsConnection connection,
        IOptionsMonitor<NatsFanoutStreamOptions> fanoutOptions,
        IOptionsMonitor<NatsSupportChatStreamOptions> supportChatOptions,
        ILogger<NatsTenantStreamProvisioner> logger)
    {
        _connection = connection;
        _fanoutOptions = fanoutOptions;
        _supportChatOptions = supportChatOptions;
        _logger = logger;
    }

    public Task EnsureGlobalFanoutStreamAsync(CancellationToken ct = default)
    {
        var opts = _fanoutOptions.CurrentValue;
        if (!opts.Enabled)
            return Task.CompletedTask;

        var prefix = string.IsNullOrWhiteSpace(opts.Name)
            ? NatsSubjectBuilder.LegacyFanoutStreamName
            : opts.Name.Trim();
        var streamName = prefix.Equals(NatsSubjectBuilder.LegacyFanoutStreamName, StringComparison.Ordinal)
            ? NatsSubjectBuilder.GlobalFanoutStreamName
            : $"{prefix}_GLOBAL";

        var maxAge = ParseFlexibleDuration(opts.MaxAge, TimeSpan.FromHours(24), "Nats:FanoutStream:MaxAge");
        var duplicateWindow = ParseFlexibleDuration(opts.DuplicateWindow, TimeSpan.FromMinutes(2), "Nats:FanoutStream:DuplicateWindow");
        var maxBytes = opts.MaxBytes > 0 ? opts.MaxBytes : 134_217_728;

        return EnsureStreamAsync(
            streamName,
            [NatsSubjectBuilder.GlobalAgentsCommandStreamSubject],
            maxAge,
            maxBytes,
            duplicateWindow,
            ct);
    }

    public Task EnsureTenantFanoutStreamAsync(Guid clientId, CancellationToken ct = default)
    {
        var opts = _fanoutOptions.CurrentValue;
        if (!opts.Enabled || clientId == Guid.Empty)
            return Task.CompletedTask;

        var prefix = string.IsNullOrWhiteSpace(opts.Name)
            ? NatsSubjectBuilder.LegacyFanoutStreamName
            : opts.Name.Trim();
        var streamName = prefix.Equals(NatsSubjectBuilder.LegacyFanoutStreamName, StringComparison.Ordinal)
            ? NatsSubjectBuilder.TenantFanoutStreamName(clientId)
            : $"{prefix}_{clientId:N}";

        var maxAge = ParseFlexibleDuration(opts.MaxAge, TimeSpan.FromHours(24), "Nats:FanoutStream:MaxAge");
        var duplicateWindow = ParseFlexibleDuration(opts.DuplicateWindow, TimeSpan.FromMinutes(2), "Nats:FanoutStream:DuplicateWindow");
        var maxBytes = opts.MaxBytes > 0 ? opts.MaxBytes : 134_217_728;

        string[] subjects =
        [
            NatsSubjectBuilder.TenantSiteAgentsCommandStreamSubject(clientId),
            NatsSubjectBuilder.TenantClientAgentsCommandStreamSubject(clientId),
        ];

        return EnsureStreamAsync(streamName, subjects, maxAge, maxBytes, duplicateWindow, ct);
    }

    public Task EnsureTenantSupportChatStreamAsync(Guid clientId, CancellationToken ct = default)
    {
        var opts = _supportChatOptions.CurrentValue;
        if (!opts.Enabled || clientId == Guid.Empty)
            return Task.CompletedTask;

        var prefix = string.IsNullOrWhiteSpace(opts.Name)
            ? NatsSubjectBuilder.LegacySupportChatStreamName
            : opts.Name.Trim();
        var streamName = prefix.Equals(NatsSubjectBuilder.LegacySupportChatStreamName, StringComparison.Ordinal)
            ? NatsSubjectBuilder.TenantSupportChatStreamName(clientId)
            : $"{prefix}_{clientId:N}";

        var maxAge = ParseFlexibleDuration(opts.MaxAge, TimeSpan.FromHours(24), "Nats:SupportChatStream:MaxAge");
        var duplicateWindow = ParseFlexibleDuration(opts.DuplicateWindow, TimeSpan.FromMinutes(2), "Nats:SupportChatStream:DuplicateWindow");
        var maxBytes = opts.MaxBytes > 0 ? opts.MaxBytes : 268_435_456;

        return EnsureStreamAsync(
            streamName,
            [NatsSubjectBuilder.TenantSupportChatStreamSubject(clientId)],
            maxAge,
            maxBytes,
            duplicateWindow,
            ct);
    }

    public async Task MigrateLegacyWildcardStreamsAsync(CancellationToken ct = default)
    {
        var js = new NatsJSContext(_connection);

        await DeleteLegacyStreamIfWildcardAsync(
            js,
            NatsSubjectBuilder.LegacyFanoutStreamName,
            [
                NatsSubjectBuilder.SiteAgentsCommandStreamSubject,
                NatsSubjectBuilder.ClientAgentsCommandStreamSubject,
            ],
            ct);

        await DeleteLegacyStreamIfWildcardAsync(
            js,
            NatsSubjectBuilder.LegacySupportChatStreamName,
            [NatsSubjectBuilder.SupportChatStreamSubject],
            ct);
    }

    private async Task EnsureStreamAsync(
        string streamName,
        string[] subjects,
        TimeSpan maxAge,
        long maxBytes,
        TimeSpan duplicateWindow,
        CancellationToken ct)
    {
        if (_ensuredStreams.ContainsKey(streamName))
            return;

        var gate = _streamLocks.GetOrAdd(streamName, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_ensuredStreams.ContainsKey(streamName))
                return;

            var streamConfig = new StreamConfig(name: streamName, subjects: subjects)
            {
                Retention = StreamConfigRetention.Limits,
                Discard = StreamConfigDiscard.Old,
                Storage = StreamConfigStorage.File,
                MaxAge = maxAge,
                MaxBytes = maxBytes,
                DuplicateWindow = duplicateWindow,
            };

            var js = new NatsJSContext(_connection);
            await js.CreateOrUpdateStreamAsync(streamConfig, ct);
            _ensuredStreams[streamName] = 1;

            _logger.LogInformation(
                "JetStream tenant stream ensured: {StreamName} (subjects={Subjects}, maxAge={MaxAge}, maxBytes={MaxBytes})",
                streamName,
                string.Join(',', subjects),
                maxAge,
                maxBytes);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task DeleteLegacyStreamIfWildcardAsync(
        NatsJSContext js,
        string streamName,
        string[] expectedWildcards,
        CancellationToken ct)
    {
        try
        {
            var stream = await js.GetStreamAsync(streamName, cancellationToken: ct);
            var configuredSubjects = stream.Info.Config.Subjects ?? [];
            if (configuredSubjects.Any(s => expectedWildcards.Contains(s, StringComparer.OrdinalIgnoreCase)))
            {
                await js.DeleteStreamAsync(streamName, ct);
                _ensuredStreams.TryRemove(streamName, out _);
                _logger.LogInformation(
                    "Deleted legacy cross-tenant wildcard JetStream stream {StreamName} (subjects={Subjects}) before provisioning per-tenant streams.",
                    streamName,
                    string.Join(',', configuredSubjects));
            }
        }
        catch (NatsJSApiException ex) when (ex.Error.Code == 404)
        {
            // Legacy stream already absent — nothing to migrate.
        }
    }

    private TimeSpan ParseFlexibleDuration(string? raw, TimeSpan fallback, string optionName)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        var value = raw.Trim();

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsedTimeSpan) && parsedTimeSpan >= TimeSpan.Zero)
            return parsedTimeSpan;

        if (value.EndsWith("ms", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)
            && ms >= 0)
            return TimeSpan.FromMilliseconds(ms);

        if (value.Length > 1
            && double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && number >= 0)
        {
            return char.ToLowerInvariant(value[^1]) switch
            {
                's' => TimeSpan.FromSeconds(number),
                'm' => TimeSpan.FromMinutes(number),
                'h' => TimeSpan.FromHours(number),
                'd' => TimeSpan.FromDays(number),
                _ => LogDurationFallback(optionName, value, fallback),
            };
        }

        return LogDurationFallback(optionName, value, fallback);
    }

    private TimeSpan LogDurationFallback(string optionName, string rawValue, TimeSpan fallback)
    {
        _logger.LogWarning(
            "Invalid duration value '{Value}' for {Option}. Using fallback {Fallback}.",
            rawValue,
            optionName,
            fallback);

        return fallback;
    }
}
