using Discovery.Core.Interfaces;

namespace Discovery.Api.Services;

/// <summary>
/// Warmup service that prebuilds Discovery base binary and update installer on API startup.
/// This reduces latency for the first self-update build/refresh request.
/// </summary>
public sealed class AgentPackagePrebuildHostedService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);

    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly IHostApplicationLifetime _hostApplicationLifetime;
    private readonly ILogger<AgentPackagePrebuildHostedService> _logger;

    public AgentPackagePrebuildHostedService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        IHostApplicationLifetime hostApplicationLifetime,
        ILogger<AgentPackagePrebuildHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _hostApplicationLifetime = hostApplicationLifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _configuration.GetValue<bool?>("AgentPackage:PrebuildOnStartup") ?? true;
        if (!enabled)
        {
            _logger.LogInformation("Agent prebuild on startup is disabled by config.");
            return;
        }

        await WaitForApplicationStartedAsync(stoppingToken);

        _logger.LogInformation(
            "Agent prebuild startup scheduled after application started. Delay={DelaySeconds}s",
            StartupDelay.TotalSeconds);

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Agent prebuild on startup canceled before delay completed.");
            return;
        }

        var activeProfile = ResolveActiveProfile();
        var targetPlatform = ResolveConfigForProfile(activeProfile, "InstallerTargetPlatform") ?? "windows/amd64";
        _logger.LogInformation(
            "Agent prebuild startup with profile={Profile}, host={Host}, target={Target}",
            activeProfile,
            OperatingSystem.IsWindows() ? "windows" : "linux",
            targetPlatform);

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IAgentPackageService>();
            _logger.LogInformation("Agent prebuild: starting clean base binary build...");
            await service.PrebuildBaseBinaryAsync(forceRebuild: true, stoppingToken);

            _logger.LogInformation("Agent prebuild: generating update installer artifact...");
            var (content, fileName) = await service.BuildUpdateInstallerAsync();

            _logger.LogInformation(
                "Agent prebuild on startup finished successfully. Update installer generated: {FileName} ({SizeBytes} bytes)",
                fileName,
                content.Length);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Agent prebuild on startup canceled during host shutdown.");
        }
        catch (Exception ex)
        {
            // Do not fail API startup because of prebuild; installer endpoint can still retry later.
            _logger.LogWarning(ex, "Agent prebuild on startup failed.");
        }
    }

    private async Task WaitForApplicationStartedAsync(CancellationToken cancellationToken)
    {
        if (_hostApplicationLifetime.ApplicationStarted.IsCancellationRequested)
            return;

        var startedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var startedRegistration = _hostApplicationLifetime.ApplicationStarted.Register(
            () => startedTcs.TrySetResult());
        using var cancellationRegistration = cancellationToken.Register(
            () => startedTcs.TrySetCanceled(cancellationToken));

        await startedTcs.Task;
    }

    private string ResolveActiveProfile()
    {
        var configured = _configuration["AgentPackage:ActiveProfile"];
        if (string.IsNullOrWhiteSpace(configured) || string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase))
            return OperatingSystem.IsWindows() ? "windows" : "linux";

        return configured.Trim().ToLowerInvariant();
    }

    private string? ResolveConfigForProfile(string profile, string key)
    {
        var profileValue = _configuration[$"AgentPackage:Profiles:{profile}:{key}"];
        if (!string.IsNullOrWhiteSpace(profileValue))
            return profileValue;

        return _configuration[$"AgentPackage:{key}"];
    }
}
