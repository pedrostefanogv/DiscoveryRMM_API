namespace Discovery.Core.Helpers;

public static class DashboardCacheKeys
{
    /// <summary>Menor janela aceita pelo dashboard (1 hora).</summary>
    public const int MinWindowHours = 1;

    /// <summary>
    /// Maior janela aceita pelo dashboard: 30 dias.
    /// O seletor do dashboard oferece 24h/7d/30d; limitar a 168h (7 dias) fazia
    /// "30 dias" compartilhar a MESMA chave de cache que "7 dias" — bug de dados
    /// silencioso (a janela consultada era 720h, mas o cache devolvia 7 dias).
    /// </summary>
    public const int MaxWindowHours = 24 * 30;

    public static readonly int[] SupportedWindowHours = [24, 24 * 7, 24 * 30];

    /// <summary>
    /// Normaliza uma janela (TimeSpan) para horas inteiras dentro da faixa suportada.
    /// Usada tanto no cache key quanto no filtro de banco, garantindo que a chave
    /// e a janela efetiva nunca divirjam.
    /// </summary>
    public static int NormalizeWindowHours(TimeSpan window)
        => (int)Math.Clamp(Math.Round(window.TotalHours), MinWindowHours, MaxWindowHours);

    public static string GlobalSummary(int windowHours)
        => $"dashboard:global:summary:{windowHours}h";

    public static string ClientSummary(Guid clientId, int windowHours)
        => $"dashboard:client:{clientId}:summary:{windowHours}h";

    public static string SiteSummary(Guid clientId, Guid siteId, int windowHours)
        => $"dashboard:client:{clientId}:site:{siteId}:summary:{windowHours}h";
}
