namespace Discovery.Api;

/// <summary>
/// Resolves agent package profile settings from configuration.
/// Supports per-profile overrides via AgentPackage:Profiles:{profile}:* keys.
/// </summary>
internal static class AgentPackageStartup
{
    public static string ResolveActiveProfile(IConfiguration configuration)
    {
        var configured = configuration["AgentPackage:ActiveProfile"];
        if (string.IsNullOrWhiteSpace(configured) || string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase))
            return OperatingSystem.IsWindows() ? "windows" : "linux";

        return configured.Trim().ToLowerInvariant();
    }

    public static string? ResolveSetting(IConfiguration configuration, string profile, string key)
    {
        var profileValue = configuration[$"AgentPackage:Profiles:{profile}:{key}"];
        if (!string.IsNullOrWhiteSpace(profileValue))
            return profileValue;

        return configuration[$"AgentPackage:{key}"];
    }

    public static void ValidateRequired(IConfiguration configuration, string profile, string key)
    {
        var value = ResolveSetting(configuration, profile, key);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Missing AgentPackage setting: {key} (profile: {profile}).");
    }
}
