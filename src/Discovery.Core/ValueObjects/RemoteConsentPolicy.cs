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

    /// <summary>
    /// Sessões que exibem/acessam recursos interativos do endpoint usam consentimento.
    ///
    /// <b>Registry fica FORA de propósito</b> (decisão de produto: escrita exige
    /// confirmação explícita na UI do console + auditoria, sem prompt no
    /// endpoint). A razão é operacional: um editor de registro faz dezenas de
    /// operações por sessão (navegar, ler, editar), e um prompt local por escrita
    /// inviabilizaria o uso — o operador não está na frente da máquina. O
    /// controle de risco fica em três outras camadas: o guard fail-closed do
    /// agente (SAM/SECURITY/LSA nunca, Run/RunOnce só leitura), a confirmação
    /// obrigatória na UI e a auditoria por operação.
    /// </summary>
    public static bool AppliesTo(RemoteSessionKind kind) =>
        kind is RemoteSessionKind.Screen or RemoteSessionKind.Rdp or RemoteSessionKind.Proxy or RemoteSessionKind.All;

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
