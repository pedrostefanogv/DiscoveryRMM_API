using Discovery.Core.Enums;
using Discovery.Core.Helpers;

namespace Discovery.Core.ValueObjects;

/// <summary>
/// Política efetiva para self-update do binário do agent.
/// É separada de AutoUpdateSettings, que continua representando atualização de apps/software.
/// </summary>
public class AgentUpdatePolicy
{
    public bool Enabled { get; set; } = false;
    public string Channel { get; set; } = "stable";
    public bool CheckOnStartup { get; set; } = true;
    public bool CheckPeriodically { get; set; } = true;
    public bool CheckOnSyncManifest { get; set; } = true;
    public int CheckEveryHours { get; set; } = 6;
    public string? TargetVersion { get; set; }
    public string? MinimumRequiredVersion { get; set; }
    public bool AllowDeferral { get; set; } = true;
    public int MaxDeferralHours { get; set; } = 24;
    public AgentReleaseArtifactType PreferredArtifactType { get; set; } = AgentReleaseArtifactType.Portable;
    public int RolloutPercentage { get; set; } = 100;
    public bool RequireSignatureValidation { get; set; } = false;

    public void Normalize()
    {
        Channel = string.IsNullOrWhiteSpace(Channel)
            ? "stable"
            : Channel.Trim().ToLowerInvariant();

        TargetVersion = NormalizeVersion(TargetVersion);
        MinimumRequiredVersion = NormalizeVersion(MinimumRequiredVersion);
        CheckEveryHours = Math.Clamp(CheckEveryHours, 1, 168);
        MaxDeferralHours = Math.Clamp(MaxDeferralHours, 0, 720);
        RolloutPercentage = Math.Clamp(RolloutPercentage, 0, 100);
    }

    public IReadOnlyList<string> Validate()
    {
        Normalize();

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Channel))
            errors.Add("AgentUpdatePolicy.Channel is required.");

        if (CheckEveryHours is < 1 or > 168)
            errors.Add("AgentUpdatePolicy.CheckEveryHours must be between 1 and 168.");

        if (MaxDeferralHours is < 0 or > 720)
            errors.Add("AgentUpdatePolicy.MaxDeferralHours must be between 0 and 720.");

        if (RolloutPercentage is < 0 or > 100)
            errors.Add("AgentUpdatePolicy.RolloutPercentage must be between 0 and 100.");

        if (!string.IsNullOrWhiteSpace(TargetVersion) && !SemanticVersion.TryParse(TargetVersion, out _))
            errors.Add("AgentUpdatePolicy.TargetVersion must be a valid semantic version.");

        if (!string.IsNullOrWhiteSpace(MinimumRequiredVersion) && !SemanticVersion.TryParse(MinimumRequiredVersion, out _))
            errors.Add("AgentUpdatePolicy.MinimumRequiredVersion must be a valid semantic version.");

        return errors;
    }

    private static string? NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        var trimmed = version.Trim();
        return trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase)
            ? trimmed[1..]
            : trimmed;
    }
}
