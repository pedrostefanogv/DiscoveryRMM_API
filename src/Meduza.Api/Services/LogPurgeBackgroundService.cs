using Meduza.Core.Interfaces;
using Microsoft.Extensions.Hosting;

namespace Meduza.Api.Services;

public class LogPurgeBackgroundService : BackgroundService
{
    private static readonly int[] DefaultAllowedDays = new[] { 30, 90, 180, 365 };
    private const int DefaultRetentionDays = 90;

    private readonly ILogRepository _logRepo;
    private readonly IConfiguration _config;
    private readonly ILogger<LogPurgeBackgroundService> _logger;

    public LogPurgeBackgroundService(ILogRepository logRepo, IConfiguration config, ILogger<LogPurgeBackgroundService> logger)
    {
        _logRepo = logRepo;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await PurgeOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await PurgeOnceAsync(stoppingToken);
        }
    }

    private async Task PurgeOnceAsync(CancellationToken stoppingToken)
    {
        var retentionDays = GetRetentionDays();
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

        try
        {
            var deleted = await _logRepo.PurgeAsync(cutoff);
            _logger.LogInformation("Log purge completed. Deleted {Count} entries older than {Days} days.", deleted, retentionDays);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Log purge failed.");
        }
    }

    private int GetRetentionDays()
    {
        var allowed = _config.GetSection("LogRetention:AllowedDays").Get<int[]>() ?? DefaultAllowedDays;
        var configured = _config.GetValue<int?>("LogRetention:Days") ?? DefaultRetentionDays;

        if (!allowed.Contains(configured))
            return DefaultRetentionDays;

        return configured;
    }
}
