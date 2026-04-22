using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

/// <summary>
/// Define um alerta PSADT a ser exibido no endpoint do agent.
/// Suporta dois modos: Toast (fecha automaticamente) e Modal (exige confirmação).
/// Pode ser entregue a um agent específico, site, cliente ou grupo por label.
/// </summary>
public class AgentAlertDefinition
{
    public Guid Id { get; set; }

    // ── Conteúdo do diálogo ───────────────────────────────────────────────

    /// <summary>Título exibido no diálogo PSADT.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Mensagem principal exibida no diálogo.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Toast (fecha sozinho) ou Modal (exige interação).</summary>
    public PsadtAlertType AlertType { get; set; } = PsadtAlertType.Toast;

    /// <summary>
    /// Tempo em segundos até o Toast fechar automaticamente.
    /// Padrão: 15. Valores comuns: 5, 15, 30.
    /// Ignorado para Modal.
    /// </summary>
    public int? TimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// JSON com botões de ação para Modal: [{"label":"Sim","value":"yes"},{"label":"Não","value":"no"}].
    /// Null para Toast.
    /// </summary>
    public string? ActionsJson { get; set; }

    /// <summary>
    /// Ação executada automaticamente quando o timeout expira em um Modal sem interação do usuário.
    /// Deve corresponder a um "value" em ActionsJson.
    /// </summary>
    public string? DefaultAction { get; set; }

    /// <summary>Ícone exibido: info | warning | error | success. Padrão: info.</summary>
    public string Icon { get; set; } = "info";

    // ── Escopo de entrega ─────────────────────────────────────────────────

    public AlertScopeType ScopeType { get; set; } = AlertScopeType.Agent;

    /// <summary>Preenchido quando ScopeType = Agent.</summary>
    public Guid? ScopeAgentId { get; set; }

    /// <summary>Preenchido quando ScopeType = Site.</summary>
    public Guid? ScopeSiteId { get; set; }

    /// <summary>Preenchido quando ScopeType = Client.</summary>
    public Guid? ScopeClientId { get; set; }

    /// <summary>Preenchido quando ScopeType = Label. Nome exato da label.</summary>
    public string? ScopeLabelName { get; set; }

    // ── Ciclo de vida ─────────────────────────────────────────────────────

    public AlertDefinitionStatus Status { get; set; } = AlertDefinitionStatus.Draft;

    /// <summary>
    /// Quando null, o alerta é despachado imediatamente após a criação.
    /// Quando definido, o scheduler dispara no momento correto.
    /// </summary>
    public DateTime? ScheduledAt { get; set; }

    /// <summary>
    /// Se atingido sem despacho, o alerta é marcado como Expired.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Quando o despacho foi efetivamente iniciado.</summary>
    public DateTime? DispatchedAt { get; set; }

    /// <summary>Quantidade de agents para os quais comandos foram criados.</summary>
    public int DispatchedCount { get; set; }

    // ── Rastreabilidade ───────────────────────────────────────────────────

    /// <summary>Ticket de suporte vinculado (opcional).</summary>
    public Guid? TicketId { get; set; }

    public string? CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
