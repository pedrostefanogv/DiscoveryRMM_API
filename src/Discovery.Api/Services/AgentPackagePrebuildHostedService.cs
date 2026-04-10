using Discovery.Core.Interfaces;

namespace Discovery.Api.Services;

/// <summary>
/// Warmup service that prebuilds Discovery base binary on API startup.
/// This reduces latency for the first installer generation request.
/// </summary>
public sealed class AgentPackagePrebuildHostedService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AgentPackagePrebuildHostedService> _logger;

    public AgentPackagePrebuildHostedService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<AgentPackagePrebuildHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var enabled = _configuration.GetValue<bool?>("AgentPackage:PrebuildOnStartup") ?? true;
        if (!enabled)
        {
            _logger.LogInformation("Agent prebuild on startup is disabled by config.");
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
            _logger.LogInformation("Agent prebuild: starting clean build...");
            await service.PrebuildBaseBinaryAsync(forceRebuild: true, cancellationToken);
            _logger.LogInformation("Agent prebuild on startup finished successfully.");
        }
        catch (Exception ex)
        {
            // Do not fail API startup because of prebuild; installer endpoint can still retry later.
            _logger.LogWarning(ex, "Agent prebuild on startup failed.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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
