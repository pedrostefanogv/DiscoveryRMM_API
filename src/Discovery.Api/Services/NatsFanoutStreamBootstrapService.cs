using System.Globalization;
using Discovery.Core.Configuration;
using Discovery.Core.Helpers;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Discovery.Api.Services;

/// <summary>
/// Garante no startup a criacao/atualizacao idempotente do stream de fan-out.
/// </summary>
public class NatsFanoutStreamBootstrapService : BackgroundService
{
    private readonly NatsConnection _connection;
    private readonly IOptionsMonitor<NatsFanoutStreamOptions> _optionsMonitor;
    private readonly ILogger<NatsFanoutStreamBootstrapService> _logger;

    public NatsFanoutStreamBootstrapService(
        NatsConnection connection,
        IOptionsMonitor<NatsFanoutStreamOptions> optionsMonitor,
        ILogger<NatsFanoutStreamBootstrapService> logger)
    {
        _connection = connection;
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _optionsMonitor.CurrentValue;
        if (!options.Enabled)
        {
            _logger.LogInformation("NATS fan-out stream bootstrap disabled (Nats:FanoutStream:Enabled=false).");
            return;
        }

        var streamName = string.IsNullOrWhiteSpace(options.Name)
            ? "DISCOVERY_FANOUT_COMMANDS"
            : options.Name.Trim();

        var subjects = (options.Subjects ?? Array.Empty<string>())
            .Where(subject => !string.IsNullOrWhiteSpace(subject))
            .Select(subject => subject.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (subjects.Length == 0)
        {
            subjects =
            [
                NatsSubjectBuilder.SiteAgentsCommandStreamSubject,
                NatsSubjectBuilder.ClientAgentsCommandStreamSubject,
                NatsSubjectBuilder.GlobalAgentsCommandStreamSubject,
            ];
        }

        var maxAge = ParseFlexibleDuration(options.MaxAge, TimeSpan.FromHours(24), "Nats:FanoutStream:MaxAge");
        var duplicateWindow = ParseFlexibleDuration(options.DuplicateWindow, TimeSpan.FromMinutes(2), "Nats:FanoutStream:DuplicateWindow");
        var maxBytes = options.MaxBytes > 0 ? options.MaxBytes : 134_217_728;

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
        var attempts = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            attempts++;

            try
            {
                await js.CreateOrUpdateStreamAsync(streamConfig, stoppingToken);
                _logger.LogInformation(
                    "JetStream fan-out stream ensured on startup: {StreamName} (subjects={Subjects}, maxAge={MaxAge}, maxBytes={MaxBytes}, dupeWindow={DuplicateWindow})",
                    streamName,
                    string.Join(',', subjects),
                    maxAge,
                    maxBytes,
                    duplicateWindow);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (options.MaxRetryAttempts > 0 && attempts >= options.MaxRetryAttempts)
                {
                    _logger.LogError(
                        ex,
                        "Unable to ensure JetStream fan-out stream {StreamName} after {Attempts} attempts. Service will continue without stopping API startup.",
                        streamName,
                        attempts);
                    return;
                }

                var retryDelaySeconds = Math.Max(1, options.RetryDelaySeconds);
                _logger.LogWarning(
                    ex,
                    "Failed to ensure JetStream fan-out stream {StreamName} (attempt {Attempt}). Retrying in {DelaySeconds}s...",
                    streamName,
                    attempts,
                    retryDelaySeconds);

                await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds), stoppingToken);
            }
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
