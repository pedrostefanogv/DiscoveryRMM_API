namespace Meduza.Core.Interfaces;

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
    Task<byte[]> BuildPortablePackageAsync(string rawDeployToken);

    /// <summary>
    /// Builds a NSIS installer executable with defaults embedded at build time.
    /// The deploy token is embedded as ARG_DEFAULT_KEY.
    /// </summary>
    Task<(byte[] Content, string FileName)> BuildInstallerAsync(string rawDeployToken);
}
