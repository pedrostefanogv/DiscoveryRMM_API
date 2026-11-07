using Discovery.Core.Entities.Security;

namespace Discovery.Core.Interfaces.Security;

/// <summary>
/// Registro de auditoria de mutações de identidade (usuários, papéis, permissões,
/// grupos, vínculos e atribuições de escopo).
///
/// Existe separado do fluxo de autenticação porque a decisão de falhar é
/// diferente: um erro de auditoria de login não pode derrubar o login, mas uma
/// mudança de privilégio sem registro é justamente o que se quer evitar. O serviço
/// registra a falha de escrita em log e NUNCA propaga exceção — o comportamento
/// está documentado em <c>IdentityAuditService</c>.
/// </summary>
public interface IIdentityAuditService
{
    /// <summary>
    /// Registra uma mutação de identidade.
    /// </summary>
    /// <param name="eventType">Ex.: <c>identity.user_created</c>, <c>identity.role_scope_granted</c>.</param>
    /// <param name="actorUserId">Quem executou (Guid.Empty quando o sistema).</param>
    /// <param name="actorLogin">Login do ator, congelado no evento.</param>
    /// <param name="targetType">"User", "Role", "UserGroup" ou "UserGroupRole".</param>
    /// <param name="targetId">Id do alvo.</param>
    /// <param name="targetUserId">
    /// Quando o alvo é um usuário (ou o vínculo/atribuição de um usuário), o id dele —
    /// permite consultar o histórico pela pessoa afetada, não só por quem agiu.
    /// </param>
    /// <param name="before">Estado anterior (serializado). Null em criação.</param>
    /// <param name="after">Estado posterior (serializado). Null em exclusão.</param>
    /// <param name="success">False quando a operação foi recusada pelo servidor.</param>
    /// <param name="failureReason">Motivo da recusa, quando houver.</param>
    Task RecordAsync(
        string eventType,
        Guid actorUserId,
        string? actorLogin,
        string targetType,
        Guid? targetId = null,
        Guid? targetUserId = null,
        object? before = null,
        object? after = null,
        bool success = true,
        string? failureReason = null);

    /// <summary>Lista por ator (quem fez o quê).</summary>
    Task<IEnumerable<AuthAuditLog>> GetByActorAsync(Guid actorUserId, int limit = 100);

    /// <summary>Lista por alvo (o que aconteceu com este usuário/papel/grupo).</summary>
    Task<IEnumerable<AuthAuditLog>> GetByTargetAsync(string targetType, Guid targetId, int limit = 100);

    /// <summary>Lista mutações de identidade recentes (filtro por prefixo de EventType).</summary>
    Task<IEnumerable<AuthAuditLog>> GetRecentIdentityAsync(string? eventTypePrefix = null, int limit = 200);

    /// <summary>
    /// Histórico completo do usuário como SUJEITO (autenticação + identidade).
    /// Complementa <see cref="GetByTargetAsync"/>: eventos em que o usuário é o
    /// alvo direto não têm TargetType="User" (ex.: vínculo a grupo tem alvo
    /// UserGroup), mas continuam sendo do interesse dele.
    /// </summary>
    Task<IEnumerable<AuthAuditLog>> GetByUserAsyncForAuditView(Guid userId, int limit = 100);
}
