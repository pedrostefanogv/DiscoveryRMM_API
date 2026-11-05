using Discovery.Core.Configuration;
using Discovery.Core.Enums;

namespace Discovery.Core.ValueObjects;

/// <summary>
/// Nível hierárquico que decidiu um campo da política. Exposto na API para que
/// o operador entenda "por que este site não abre chamado".
/// </summary>
public static class AutoTicketPolicyLevel
{
    public const string Server = "Server";
    public const string Client = "Client";
    public const string Site = "Site";

    /// <summary>O campo está travado no nível superior e não pode ser sobrescrito.</summary>
    public const string Locked = "Locked";

    /// <summary>Valor veio do default de bootstrap (appsettings).</summary>
    public const string Default = "Default";
}

/// <summary>
/// Política de auto-ticket JÁ RESOLVIDA: todos os campos preenchidos, com a
/// proveniência de cada um. É isto que o orquestrador consome — ele não lê
/// mais o IOptions diretamente para decisões de política.
/// </summary>
public class ResolvedAutoTicketPolicy
{
    // ── Liga/desliga ───────────────────────────────────────────────────────
    public bool Enabled { get; init; }
    public AutoTicketScopeMode Mode { get; init; } = AutoTicketScopeMode.Off;
    public string[] CanaryClientIds { get; init; } = [];
    public string[] CanarySiteIds { get; init; } = [];

    /// <summary>
    /// Nível que desligou o auto-ticket, quando <see cref="Enabled"/> é false.
    /// Null quando está ligado. Vai para o Reason da execução (auditoria).
    /// </summary>
    public string? DisabledByLevel { get; init; }

    // ── Quando abrir ───────────────────────────────────────────────────────
    public int MinConsecutiveFailures { get; init; } = 3;
    public int MinDurationSeconds { get; init; } = 600;
    public AutoTicketImpact MinImpactToOpen { get; init; } = AutoTicketImpact.Medium;
    public AutoTicketImpact MinImpactToEscalate { get; init; } = AutoTicketImpact.High;
    public bool OpenOnWarning { get; init; } = true;
    public bool OpenOnPredictive { get; init; } = true;

    // ── Supressão ──────────────────────────────────────────────────────────
    public int MaintenanceGraceMinutes { get; init; } = 30;
    public bool ReNotifyAfterMaintenanceEnds { get; init; } = true;
    public bool RespectAgentMaintenance { get; init; } = true;
    public bool RespectMaintenanceWindow { get; init; } = true;
    public bool SuppressWhenHostNotReachable { get; init; } = true;
    public bool DependencySuppressionEnabled { get; init; }
    public int DependencyMaxDepth { get; init; } = 3;
    public bool CloseTicketResolvedDuringMaintenance { get; init; } = true;
    public int FlapWindowMinutes { get; init; } = 60;
    public int FlapTransitionsThreshold { get; init; } = 4;

    // ── Dedup e limites ────────────────────────────────────────────────────
    public int DedupWindowMinutes { get; init; } = 60;
    public int DedupWindowMaxHours { get; init; } = 24;
    public int ReopenWindowMinutes { get; init; }
    public int MaxCreatedTicketsPerHourPerAlertCode { get; init; }
    public bool IncrementOccurrenceCounter { get; init; } = true;

    // ── Vínculo por sensor ─────────────────────────────────────────────────
    public string[] AllowedSensorKeys { get; init; } = [];
    public string[] NeverTicketSensorKeys { get; init; } = [];

    // ── Roteamento e IA ────────────────────────────────────────────────────
    public Guid? DefaultDepartmentId { get; init; }
    public string? DefaultCategory { get; init; }
    public AutoTicketAiMode AiMode { get; init; } = AutoTicketAiMode.Off;
    public double AiMinConfidence { get; init; } = 0.60;
    public string[] AiSuggestibleFields { get; init; } = [];
    public string[] AiContentGateFields { get; init; } = [];

    /// <summary>Liga o envio de contexto do equipamento ao provedor de IA.</summary>
    public bool AiAllowEquipmentContext { get; init; } = true;

    /// <summary>Campos removidos/redigidos antes de montar o prompt.</summary>
    public string[] AiRedactFields { get; init; } =
        ["password", "secret", "token", "apikey", "api_key", "credential"];

    /// <summary>Pseudonimiza nomes de ativo antes de enviar ao provedor.</summary>
    public bool AiAnonymizeAssetNames { get; init; }

    /// <summary>Campo -> nível que decidiu (ver <see cref="AutoTicketPolicyLevel"/>).</summary>
    public IReadOnlyDictionary<string, string> Provenance { get; init; }
        = new Dictionary<string, string>();

    /// <summary>
    /// O modo efetivo permite criar chamado? Shadow nunca cria; Canary depende
    /// do escopo; Active cria. Off nunca cria.
    /// </summary>
    public bool AllowsCreation => Enabled && Mode is AutoTicketScopeMode.Active or AutoTicketScopeMode.Canary;

    /// <summary>Está em observação (avalia e registra, mas não cria)?</summary>
    public bool IsObserving => Enabled && Mode == AutoTicketScopeMode.Shadow;

    /// <summary>
    /// Campos que a IA pode sugerir, já normalizados. Quando a política não
    /// define nenhum, devolve a lista conservadora padrão (apenas os campos de
    /// texto, que não alteram roteamento).
    /// </summary>
    public IReadOnlyList<string> SuggestibleFieldsSafe()
    {
        if (AiSuggestibleFields.Length > 0) return AiSuggestibleFields;

        return [nameof(Enums.AutoTicketSuggestionField.Title),
                nameof(Enums.AutoTicketSuggestionField.Description)];
    }

    /// <summary>O escopo (cliente/site) está autorizado pelo canário?</summary>
    public bool IsInCanaryScope(Guid clientId, Guid? siteId)
    {
        if (Mode != AutoTicketScopeMode.Canary) return true;

        var clients = ParseGuidSet(CanaryClientIds);
        var sites = ParseGuidSet(CanarySiteIds);

        // Canário sem listas = tudo liberado (mesma semântica do código atual).
        if (clients.Count == 0 && sites.Count == 0) return true;

        return clients.Contains(clientId) || (siteId.HasValue && sites.Contains(siteId.Value));
    }

    private static HashSet<Guid> ParseGuidSet(IEnumerable<string> values)
    {
        var set = new HashSet<Guid>();
        foreach (var value in values)
        {
            if (Guid.TryParse(value, out var parsed)) set.Add(parsed);
        }
        return set;
    }
}
