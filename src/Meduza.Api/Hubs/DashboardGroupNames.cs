namespace Meduza.Api.Hubs;

public static class DashboardGroupNames
{
    public const string Global = "dashboard:global";

    public static string ForClient(Guid clientId)
        => $"dashboard:client:{clientId}";

    public static string ForSite(Guid siteId)
        => $"dashboard:site:{siteId}";
}
