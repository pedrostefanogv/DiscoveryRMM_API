using Discovery.Core.DTOs;

namespace Discovery.Core.Interfaces;

public interface IAgentPackageService
{
    /// <summary>
    /// Ensures the base Discovery binary exists by running a no-package Wails build.
    /// This is intended to run at API startup to reduce installer build latency.
    /// </summary>
    Task PrebuildBaseBinaryAsync(bool forceRebuild = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds a ZIP package in memory containing the agent binary and a
    /// pre-configured debug_config.json with the given deploy token and server URLs.
    /// </summary>
    Task<byte[]> BuildPortablePackageAsync(string rawDeployToken, string? publicApiBaseUrl = null);

    /// <summary>
    /// Builds a NSIS installer executable with defaults embedded at build time.
    /// The deploy token is embedded as ARG_DEFAULT_KEY.
    /// </summary>
    Task<(byte[] Content, string FileName)> BuildInstallerAsync(string rawDeployToken, string? publicApiBaseUrl = null);

    /// <summary>
    /// Synchronizes the agent source repository with the configured branch.
    /// Executes git fetch + git reset --hard to the target branch on origin.
    /// Only allows branches from the configured safe list (dev, release, beta, lts).
    /// Returns sync result with before/after commit hashes.
    /// </summary>
    Task<AgentRepositorySyncResult> SyncRepositoryAsync(string branch, CancellationToken cancellationToken = default);
}
