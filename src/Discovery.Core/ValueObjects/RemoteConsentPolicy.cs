using Discovery.Core.Enums;

namespace Discovery.Core.ValueObjects;

/// <summary>Origem de um campo da política de consentimento.</summary>
public sealed record RemoteConsentSource(
    string Source,
    Guid? ScopeId = null,
    string? SubjectName = null);

/// <summary>
/// Política efetiva de consentimento de acesso remoto (somente desktop).
/// Fontes por campo permitem explicar no console/audit de onde veio cada valor.
/// </summary>
public sealed class RemoteConsentPolicy
{
    public RemoteConsentMode Mode { get; set; }
    public int TimeoutSeconds { get; set; }
    public RemoteConsentTimeoutAction TimeoutAction { get; set; }
    public bool ShowBanner { get; set; }
    public string Message { get; set; } = string.Empty;
    public string BannerText { get; set; } = string.Empty;

    public Dictionary<string, RemoteConsentSource> Sources { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Somente sessões de desktop usam consentimento.</summary>
    public static bool AppliesTo(RemoteSessionKind kind) =>
        kind is RemoteSessionKind.Screen or RemoteSessionKind.All;

    public bool RequiresPrompt => Mode == RemoteConsentMode.Prompt;

    public bool IsOff => Mode == RemoteConsentMode.Off;

    /// <summary>
    /// Origem "de topo" (a mais específica que definiu algum campo), para
    /// gravar em remote_sessions.consent_policy_source.
    /// </summary>
    public string TopSource()
    {
        if (Sources.Count == 0) return "Server";
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Block"] = 0, ["Server"] = 1, ["Client"] = 2, ["Site"] = 3, ["UserGroup"] = 4, ["User"] = 5
        };
        return Sources.Values
            .Select(source => source.Source)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(source => rank.TryGetValue(source, out var value) ? value : 0)
            .FirstOrDefault() ?? "Server";
    }
}
