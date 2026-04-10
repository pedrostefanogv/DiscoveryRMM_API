namespace Discovery.Core.Helpers;

public static class DashboardCacheKeys
{
    public static readonly int[] SupportedWindowHours = [24, 24 * 7, 24 * 30];

    public static string GlobalSummary(int windowHours)
        => $"dashboard:global:summary:{windowHours}h";

    public static string ClientSummary(Guid clientId, int windowHours)
        => $"dashboard:client:{clientId}:summary:{windowHours}h";

    public static string SiteSummary(Guid clientId, Guid siteId, int windowHours)
        => $"dashboard:client:{clientId}:site:{siteId}:summary:{windowHours}h";
}
