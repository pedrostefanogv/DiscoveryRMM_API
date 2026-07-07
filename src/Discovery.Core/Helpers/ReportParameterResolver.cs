using System.Text.RegularExpressions;

namespace Discovery.Core.Helpers;

public static partial class ReportParameterResolver
{
    private const string NowMarker = "now";
    private const string CurrentDateMarker = "currentDate";
    private const string ReportTitleMarker = "reportTitle";
    private const string ClientNameMarker = "clientName";

    /// <summary>
    /// Resolves dynamic placeholders in report metadata strings (titles, headers, footers, cover page).
    /// Supports: {{now}}, {{now-24h}}, {{now-7d}}, {{now-30d}}, {{currentDate}}, {{reportTitle}}, {{clientName}}, {{generatedAt}}
    /// </summary>
    public static string ResolveTemplate(string? template, string? reportTitle = null, string? clientName = null, DateTime? generatedAt = null)
    {
        if (string.IsNullOrWhiteSpace(template))
            return template ?? string.Empty;

        var resolved = template;
        var now = generatedAt ?? DateTime.UtcNow;

        resolved = resolved.Replace("{{now}}", now.ToString("yyyy-MM-dd HH:mm:ss"), StringComparison.OrdinalIgnoreCase);
        resolved = resolved.Replace("{{currentDate}}", now.ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase);
        resolved = resolved.Replace("{{generatedAt}}", now.ToString("yyyy-MM-dd HH:mm:ss"), StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(reportTitle))
            resolved = resolved.Replace("{{reportTitle}}", reportTitle, StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(clientName))
            resolved = resolved.Replace("{{clientName}}", clientName, StringComparison.OrdinalIgnoreCase);

        // Relative time offsets: {{now-24h}}, {{now-7d}}, {{now-30d}}
        resolved = ResolveRelativeTimeOffset(resolved, now);

        return resolved;
    }

    /// <summary>
    /// Resolves dynamic placeholders in report filter JSON.
    /// Supports: &lt;now-24h&gt;, &lt;now-7d&gt;, &lt;now-30d&gt; as from/to values.
    /// </summary>
    public static string? ResolveFiltersJson(string? filtersJson, DateTime? generatedAt = null)
    {
        if (string.IsNullOrWhiteSpace(filtersJson))
            return filtersJson;

        var now = generatedAt ?? DateTime.UtcNow;

        return RelativeTimeRegex().Replace(filtersJson, match =>
        {
            var offsetStr = match.Groups[1].Value.Trim('"');
            var offset = ParseRelativeOffset(offsetStr);
            if (offset.HasValue)
                return $"\"{now.Add(offset.Value):O}\"";
            return match.Value;
        });
    }

    private static string ResolveRelativeTimeOffset(string template, DateTime now)
    {
        return RelativeOffsetRegex().Replace(template, match =>
        {
            var value = match.Groups[1].Value;
            var offset = ParseRelativeOffset(value);
            if (offset.HasValue)
                return now.Add(offset.Value).ToString("yyyy-MM-dd HH:mm:ss");
            return match.Value;
        });
    }

    private static TimeSpan? ParseRelativeOffset(string value)
    {
        value = value.Trim();

        if (TryParseDuration(value, out var ts))
            return ts;

        return null;
    }

    private static bool TryParseDuration(string value, out TimeSpan result)
    {
        result = TimeSpan.Zero;

        // Format: "24h", "7d", "30d", "1h", "2d"
        var match = DurationRegex().Match(value);
        if (!match.Success)
            return false;

        if (!int.TryParse(match.Groups[1].Value, out var amount))
            return false;

        var unit = match.Groups[2].Value.ToLowerInvariant();
        result = unit switch
        {
            "h" => TimeSpan.FromHours(-amount),
            "d" => TimeSpan.FromDays(-amount),
            "w" => TimeSpan.FromDays(-amount * 7),
            "m" => TimeSpan.FromDays(-amount * 30),
            _ => TimeSpan.Zero
        };

        return true;
    }

    [GeneratedRegex("\"<now-(\\d+[hdwm])>\"", RegexOptions.IgnoreCase)]
    private static partial Regex RelativeTimeRegex();

    [GeneratedRegex("\\{\\{now-(\\d+[hdwm])\\}\\}", RegexOptions.IgnoreCase)]
    private static partial Regex RelativeOffsetRegex();

    [GeneratedRegex("^(\\d+)([hdwm])$", RegexOptions.IgnoreCase)]
    private static partial Regex DurationRegex();
}
