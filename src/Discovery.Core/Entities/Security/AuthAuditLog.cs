namespace Discovery.Core.Entities.Security;

/// <summary>
/// Registro de auditoria de autenticação E de mutações de identidade.
///
/// <para>
/// <b>Autenticação</b> (sempre foi registrado): login, logout, falha de login,
/// MFA assertion e lockout — <see cref="UserId"/> é o sujeito do evento.
/// </para>
///
/// <para>
/// <b>Identidade</b> (auditoria 2026-11-07): criar/editar/excluir usuário, reset de
/// senha, desbloquear, revogar MFA, CRUD de papel, concessão de permissão, CRUD de
/// grupo, mudança de membro e atribuição de papel+escopo. Antes NADA disso era
/// registrado — um atacante que se autoconcedia papel Global não deixava rastro.
/// Nesses eventos <see cref="ActorUserId"/> é quem executou e <see cref="UserId"/>/
/// <see cref="TargetType"/>/<see cref="TargetId"/> identificam o alvo.
/// </para>
/// </summary>
public class AuthAuditLog
{
    public Guid Id { get; set; }

    /// <summary>
    /// Sujeito do evento: o próprio usuário em eventos de autenticação, ou o ALVO
    /// em eventos de administração de identidade.
    /// </summary>
    public Guid? UserId { get; set; }

    public string EventType { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? FailureReason { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? Detail { get; set; }
    public DateTime OccurredAt { get; set; }

    // ── Campos de auditoria de identidade (nulos em eventos de autenticação) ──

    /// <summary>Quem executou a mutação. Null em eventos originados pelo próprio usuário.</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>Login do ator, congelado no momento do evento (sobrevive a renomeação).</summary>
    public string? ActorLogin { get; set; }

    /// <summary>Tipo do alvo: "User", "Role", "UserGroup", "UserGroupRole".</summary>
    public string? TargetType { get; set; }

    /// <summary>Id do alvo da mutação.</summary>
    public Guid? TargetId { get; set; }

    /// <summary>Estado antes da mutação (JSON compacto). Null em criação.</summary>
    public string? BeforeState { get; set; }

    /// <summary>Estado depois da mutação (JSON compacto). Null em exclusão.</summary>
    public string? AfterState { get; set; }
}
