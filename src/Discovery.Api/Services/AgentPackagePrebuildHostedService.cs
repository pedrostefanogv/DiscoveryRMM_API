using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;

namespace Discovery.Api.Services;

/// <summary>
/// Warmup service that prebuilds Discovery base binary and update installers on API startup.
/// This reduces latency for the first self-update build/refresh and zero-touch download requests.
/// </summary>
public sealed class AgentPackagePrebuildHostedService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);
    private const string StartupBuildActor = "startup-prebuild";

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
        var prebuildConfigured = _configuration.GetValue<bool?>("AgentPackage:PrebuildOnStartup") ?? true;
        if (!prebuildConfigured)
            _logger.LogWarning("AgentPackage:PrebuildOnStartup=false ignored because stage2 rebuild on startup is mandatory.");

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
            var packageService = scope.ServiceProvider.GetRequiredService<IAgentPackageService>();
            var agentUpdateService = scope.ServiceProvider.GetRequiredService<IAgentUpdateService>();
            var syncInvalidationPublisher = scope.ServiceProvider.GetRequiredService<ISyncInvalidationPublisher>();

            _logger.LogInformation("Agent prebuild: starting clean base binary build...");
            await packageService.PrebuildBaseBinaryAsync(forceRebuild: true, stoppingToken);

            _logger.LogInformation("Agent prebuild: generating update installer artifact...");
            var (content, fileName) = await packageService.BuildUpdateInstallerAsync(stoppingToken);
            var version = await ResolveStartupBuildVersionAsync(agentUpdateService, activeProfile, stoppingToken);
            var contentType = ResolveConfigForProfile(activeProfile, "InstallerContentType")
                ?? "application/x-msdownload";

            await using var stream = new MemoryStream(content, writable: false);
            var publishedBuild = await agentUpdateService.RefreshCurrentBuildAsync(
                version: version,
                platform: "windows",
                architecture: "amd64",
                artifactType: AgentReleaseArtifactType.Installer,
                fileName: fileName,
                contentType: contentType,
                content: stream,
                signatureThumbprint: null,
                actor: StartupBuildActor,
                cancellationToken: stoppingToken);

            await syncInvalidationPublisher.PublishGlobalAsync(
                SyncResourceType.AgentUpdate,
                "agent-build-refreshed-startup",
                cancellationToken: stoppingToken);

            _logger.LogInformation(
                "Agent prebuild startup published stage2 build successfully. BuildId={BuildId}, version={Version}, file={FileName}",
                publishedBuild.Id,
                publishedBuild.Version,
                publishedBuild.FileName);

            try
            {
                _logger.LogInformation("Agent prebuild: warming generic zero-touch installer cache...");
                var (genericContent, genericFileName) = await packageService.BuildGenericInstallerAsync(cancellationToken: stoppingToken);

                _logger.LogInformation(
                    "Agent prebuild startup warmed generic zero-touch installer cache successfully. File={FileName}, size={SizeBytes} bytes",
                    genericFileName,
                    genericContent.LongLength);
            }
            catch (Exception ex)
            {
                // Do not fail startup/stage2 publication due to zero-touch warmup issues.
                _logger.LogWarning(ex, "Agent prebuild startup failed to warm generic zero-touch installer cache.");
            }

            _logger.LogInformation(
                "Agent prebuild on startup finished successfully. Stage2 installer generated: {FileName} ({SizeBytes} bytes)",
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

    private async Task<string> ResolveStartupBuildVersionAsync(
        IAgentUpdateService agentUpdateService,
        string activeProfile,
        CancellationToken cancellationToken)
    {
        var configuredVersion = ResolveConfigForProfile(activeProfile, "StartupStage2Version");
        var normalizedConfigured = NormalizeSemanticVersion(configuredVersion);
        if (!string.IsNullOrWhiteSpace(normalizedConfigured))
            return normalizedConfigured;

        var currentBuild = await agentUpdateService.GetCurrentBuildAsync(
            platform: "windows",
            architecture: "amd64",
            artifactType: AgentReleaseArtifactType.Installer,
            cancellationToken: cancellationToken);

        if (!string.IsNullOrWhiteSpace(currentBuild?.Version))
            return currentBuild.Version;

        var assemblyVersion = typeof(AgentPackagePrebuildHostedService).Assembly.GetName().Version;
        if (assemblyVersion is not null)
        {
            var fallback = $"{Math.Max(assemblyVersion.Major, 1)}.{Math.Max(assemblyVersion.Minor, 0)}.{Math.Max(assemblyVersion.Build, 0)}";
            if (SemanticVersion.TryParse(fallback, out _))
                return fallback;
        }

        return "1.0.0";
    }

    private string? NormalizeSemanticVersion(string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
            return null;

        var normalized = rawVersion.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[1..];

        if (!SemanticVersion.TryParse(normalized, out _))
        {
            _logger.LogWarning(
                "Ignoring invalid semantic version configured for startup stage2 publication: {Version}",
                rawVersion);
            return null;
        }

        return normalized;
    }
}
