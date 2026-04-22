using System.Text.RegularExpressions;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Interfaces;

namespace Discovery.Infrastructure.Services;

public class MeshCentralIdentityMapper : IMeshCentralIdentityMapper
{
    private static readonly Regex InvalidChars = new("[^a-zA-Z0-9._-]", RegexOptions.Compiled);

    public string ResolveProvisioningUsername(User user)
    {
        if (!string.IsNullOrWhiteSpace(user.MeshCentralUsername))
            return Normalize(user.MeshCentralUsername);

        return BuildStableUsername(user.Id);
    }

    public string SuggestUsername(string localUsername, Guid? userId = null)
    {
        if (userId.HasValue)
            return BuildStableUsername(userId.Value);

        var normalized = Normalize(localUsername);
        if (normalized.Length > 24)
            normalized = normalized[..24];

        return $"mdz-preview-{normalized}";
    }

    private static string BuildStableUsername(Guid userId)
        => $"mdz-{userId:N}";

    private static string Normalize(string raw)
    {
        var normalized = raw.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return "user";

        normalized = InvalidChars.Replace(normalized, "-");
        normalized = Regex.Replace(normalized, "-+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "user" : normalized;
    }
}