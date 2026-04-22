using System.Text.RegularExpressions;

namespace Discovery.Core.Helpers;

/// <summary>
/// Implementação mínima de comparação SemVer 2.0.0 para seleção segura de releases do agent.
/// </summary>
public readonly partial record struct SemanticVersion(
    int Major,
    int Minor,
    int Patch,
    string? PreRelease = null,
    string? BuildMetadata = null) : IComparable<SemanticVersion>
{
    [GeneratedRegex(
        "^v?(?<major>0|[1-9]\\d*)\\.(?<minor>0|[1-9]\\d*)\\.(?<patch>0|[1-9]\\d*)(?:-(?<pre>[0-9A-Za-z.-]+))?(?:\\+(?<meta>[0-9A-Za-z.-]+))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionRegex();

    public static bool TryParse(string? raw, out SemanticVersion version)
    {
        version = default;

        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var match = SemanticVersionRegex().Match(raw.Trim());
        if (!match.Success)
            return false;

        version = new SemanticVersion(
            int.Parse(match.Groups["major"].Value),
            int.Parse(match.Groups["minor"].Value),
            int.Parse(match.Groups["patch"].Value),
            match.Groups["pre"].Success ? match.Groups["pre"].Value : null,
            match.Groups["meta"].Success ? match.Groups["meta"].Value : null);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
            return major;

        var minor = Minor.CompareTo(other.Minor);
        if (minor != 0)
            return minor;

        var patch = Patch.CompareTo(other.Patch);
        if (patch != 0)
            return patch;

        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    public override string ToString()
    {
        var value = $"{Major}.{Minor}.{Patch}";
        if (!string.IsNullOrWhiteSpace(PreRelease))
            value += "-" + PreRelease;
        if (!string.IsNullOrWhiteSpace(BuildMetadata))
            value += "+" + BuildMetadata;
        return value;
    }

    private static int ComparePreRelease(string? left, string? right)
    {
        var leftEmpty = string.IsNullOrWhiteSpace(left);
        var rightEmpty = string.IsNullOrWhiteSpace(right);

        if (leftEmpty && rightEmpty)
            return 0;

        if (leftEmpty)
            return 1;

        if (rightEmpty)
            return -1;

        var leftParts = left!.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var rightParts = right!.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var maxLength = Math.Max(leftParts.Length, rightParts.Length);

        for (var i = 0; i < maxLength; i++)
        {
            if (i >= leftParts.Length)
                return -1;

            if (i >= rightParts.Length)
                return 1;

            var leftPart = leftParts[i];
            var rightPart = rightParts[i];
            var leftIsNumeric = int.TryParse(leftPart, out var leftNumber);
            var rightIsNumeric = int.TryParse(rightPart, out var rightNumber);

            if (leftIsNumeric && rightIsNumeric)
            {
                var comparison = leftNumber.CompareTo(rightNumber);
                if (comparison != 0)
                    return comparison;
                continue;
            }

            if (leftIsNumeric != rightIsNumeric)
                return leftIsNumeric ? -1 : 1;

            var textComparison = string.CompareOrdinal(leftPart, rightPart);
            if (textComparison != 0)
                return textComparison;
        }

        return 0;
    }
}
