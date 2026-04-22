using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

/// <summary>
/// Regra de alerta automático vinculada a um estado de workflow.
/// Quando um ticket transita para o WorkflowStateId configurado,
/// o servidor dispara automaticamente o alerta PSADT usando o
/// contexto do ticket (Agent, Site ou Client) como escopo.
/// </summary>
public class TicketAlertRule
{
    public Guid Id { get; set; }

    /// <summary>Estado do workflow que dispara o alerta.</summary>
    public Guid WorkflowStateId { get; set; }

    // ── Conteúdo do alerta ────────────────────────────────────────────────

    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;

    /// <summary>Toast (fecha sozinho) ou Modal (exige interação).</summary>
    public PsadtAlertType AlertType { get; set; } = PsadtAlertType.Toast;

    /// <summary>Segundos até fechar (Toast). Valores: 5, 15, 30. Padrão: 15.</summary>
    public int? TimeoutSeconds { get; set; } = 15;

    /// <summary>JSON com botões para Modal: [{"label":"Sim","value":"yes"}].</summary>
    public string? ActionsJson { get; set; }

    /// <summary>Ação automática quando não há interação do usuário (Modal).</summary>
    public string? DefaultAction { get; set; }

    /// <summary>Ícone: info | warning | error | success.</summary>
    public string Icon { get; set; } = "info";

    // ── Escopo de entrega ─────────────────────────────────────────────────

    /// <summary>
    /// Preferência de escopo ao disparar: Agent, Site ou Client.
    /// O sistema usa o campo disponível no ticket seguindo a preferência.
    /// Fallback: Agent → Site → Client.
    /// </summary>
    public AlertScopeType ScopePreference { get; set; } = AlertScopeType.Agent;

    // ── Controle ──────────────────────────────────────────────────────────

    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
