using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;

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
    private readonly AgentRebuildCoordinator _rebuildCoordinator;
    private readonly ILogger<AgentPackagePrebuildHostedService> _logger;

    public AgentPackagePrebuildHostedService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        IHostApplicationLifetime hostApplicationLifetime,
        AgentRebuildCoordinator rebuildCoordinator,
        ILogger<AgentPackagePrebuildHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _hostApplicationLifetime = hostApplicationLifetime;
        _rebuildCoordinator = rebuildCoordinator;
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
            if (_rebuildCoordinator.IsRunning)
            {
                _logger.LogInformation("Agent prebuild startup skipped: an agent rebuild is already running in the coordinator.");
                return;
            }

            await using var scope = _serviceProvider.CreateAsyncScope();
            var packageService = scope.ServiceProvider.GetRequiredService<IAgentPackageService>();
            var agentUpdateService = scope.ServiceProvider.GetRequiredService<IAgentUpdateService>();

            var version = await ResolveStartupBuildVersionAsync(packageService, agentUpdateService, activeProfile, stoppingToken);
            var commitHash = await packageService.GetAgentCommitHashAsync(stoppingToken);
            var currentBuild = await agentUpdateService.GetCurrentBuildAsync(
                platform: "windows",
                architecture: "amd64",
                artifactType: AgentReleaseArtifactType.Installer,
                cancellationToken: stoppingToken);
            var currentLocalPath = await agentUpdateService.GetCurrentBuildLocalPathAsync(
                platform: "windows",
                architecture: "amd64",
                artifactType: AgentReleaseArtifactType.Installer,
                cancellationToken: stoppingToken);

            var hasKnownCommit = !string.IsNullOrWhiteSpace(commitHash)
                && !string.Equals(commitHash, "unknown", StringComparison.OrdinalIgnoreCase);

            var stage2AlreadyUpToDate = currentBuild is not null
                && string.Equals(currentBuild.Version, version, StringComparison.OrdinalIgnoreCase)
                && hasKnownCommit
                && string.Equals(currentBuild.CommitHash, commitHash, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(currentLocalPath)
                && File.Exists(currentLocalPath);

            if (stage2AlreadyUpToDate)
            {
                await packageService.PrebuildBaseBinaryAsync(forceRebuild: false, stoppingToken);
                _logger.LogInformation(
                    "Agent prebuild startup skipped stage2 rebuild/publication: current build is already up to date (BuildId={BuildId}, version={Version}, commitHash={CommitHash}).",
                    currentBuild!.Id,
                    currentBuild.Version,
                    currentBuild.CommitHash);

                try
                {
                    _logger.LogInformation("Agent prebuild: warming generic zero-touch installer cache...");
                    var genericArtifact = await packageService.GetGenericInstallerArtifactAsync(forceRebuild: false, cancellationToken: stoppingToken);

                    _logger.LogInformation(
                        "Agent prebuild startup warmed generic zero-touch installer cache successfully. File={FileName}, size={SizeBytes} bytes, s3Key={S3Key}",
                        genericArtifact.FileName,
                        genericArtifact.SizeBytes,
                        genericArtifact.S3ObjectKey ?? "(none)");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Do not fail startup/stage2 publication due to zero-touch warmup issues.
                    _logger.LogWarning(ex, "Agent prebuild startup failed to warm generic zero-touch installer cache.");
                }

                return;
            }

            var (started, status) = _rebuildCoordinator.TryStart(StartupBuildActor);
            if (!started)
            {
                _logger.LogInformation(
                    "Agent prebuild startup joined running rebuild in coordinator. RunId={RunId}",
                    status.RunId);
                return;
            }

            _logger.LogInformation(
                "Agent prebuild startup queued rebuild in coordinator. RunId={RunId}, targetVersion={Version}, commitHash={CommitHash}",
                status.RunId,
                version,
                commitHash);

            if (status.RunId is { } runId)
            {
                var final = await _rebuildCoordinator.WaitForCompletionAsync(runId, stoppingToken);
                if (final.State == AgentRebuildState.Succeeded && final.Build is { } publishedBuild)
                {
                    _logger.LogInformation(
                        "Agent prebuild startup finished via coordinator. BuildId={BuildId}, version={Version}, file={FileName}",
                        publishedBuild.Id,
                        publishedBuild.Version,
                        publishedBuild.FileName);
                }
                else if (final.State == AgentRebuildState.Failed)
                {
                    _logger.LogWarning("Agent prebuild startup failed via coordinator: {Error}", final.Error ?? "unknown");
                }
            }
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
        IAgentPackageService packageService,
        IAgentUpdateService agentUpdateService,
        string activeProfile,
        CancellationToken cancellationToken)
    {
        // Level 1: explicit config override
        var configuredVersion = ResolveConfigForProfile(activeProfile, "StartupStage2Version");
        var normalizedConfigured = NormalizeSemanticVersion(configuredVersion);
        if (!string.IsNullOrWhiteSpace(normalizedConfigured))
        {
            _logger.LogInformation("Agent stage2 version from config override: {Version}", normalizedConfigured);
            return normalizedConfigured;
        }

        // Level 2: read from agent source code (build/config.yml → agent-version.json)
        var sourceVersion = await packageService.GetAgentSourceVersionAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(sourceVersion)
            && !string.Equals(sourceVersion, "unknown", StringComparison.OrdinalIgnoreCase)
            && SemanticVersion.TryParse(sourceVersion, out _))
        {
            _logger.LogInformation("Agent stage2 version from source: {Version}", sourceVersion);
            return sourceVersion;
        }

        // Level 3: keep current DB version if no source version is available
        var currentBuild = await agentUpdateService.GetCurrentBuildAsync(
            platform: "windows",
            architecture: "amd64",
            artifactType: AgentReleaseArtifactType.Installer,
            cancellationToken: cancellationToken);

        if (!string.IsNullOrWhiteSpace(currentBuild?.Version))
        {
            _logger.LogInformation("Agent stage2 version from current DB build: {Version}", currentBuild.Version);
            return currentBuild.Version;
        }

        // Level 4: assembly version (last resort before hardcoded default)
        var assemblyVersion = typeof(AgentPackagePrebuildHostedService).Assembly.GetName().Version;
        if (assemblyVersion is not null)
        {
            var fallback = $"{Math.Max(assemblyVersion.Major, 1)}.{Math.Max(assemblyVersion.Minor, 0)}.{Math.Max(assemblyVersion.Build, 0)}";
            if (SemanticVersion.TryParse(fallback, out _))
            {
                _logger.LogWarning("Agent stage2 version from API assembly (fallback): {Version}", fallback);
                return fallback;
            }
        }

        _logger.LogWarning("Agent stage2 version: all sources exhausted, using hardcoded '1.0.0'");
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
