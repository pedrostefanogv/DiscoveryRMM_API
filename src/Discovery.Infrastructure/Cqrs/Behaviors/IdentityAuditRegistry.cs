using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Auth.Commands;
using Discovery.Core.Cqrs.Roles.Commands;
using Discovery.Core.Cqrs.UserGroups.Commands;
using Discovery.Core.Cqrs.Users.Commands;

namespace Discovery.Infrastructure.Cqrs.Behaviors;

/// <summary>
/// Metadados de auditoria de um comando de identidade.
/// </summary>
/// <param name="EventType">Ex.: <c>identity.user_created</c>.</param>
/// <param name="TargetType">"User", "Role", "UserGroup" ou "UserGroupRole".</param>
/// <param name="ResolveTargetId">Extrai o id do alvo do comando.</param>
/// <param name="ResolveTargetUserId">
/// Extrai o id do USUÁRIO afetado, quando aplicável (permite ver o histórico pela
/// pessoa, não só pelo recurso).
/// </param>
/// <param name="DescribePayload">Serializa os campos relevantes (sem segredos).</param>
internal sealed record IdentityAuditDescriptor(
    string EventType,
    string TargetType,
    Func<object, Guid?> ResolveTargetId,
    Func<object, Guid?> ResolveTargetUserId,
    Func<object, object?> DescribePayload);

/// <summary>
/// Mapeia comandos de identidade para o que deve ser auditado.
///
/// <b>NUNCA inclua senhas/segredos</b> no payload audado: <c>DescribePayload</c>
/// é serializado para a tabela de auditoria e para consulta de operadores.
/// </summary>
internal static class IdentityAuditRegistry
{
    private static readonly Dictionary<Type, IdentityAuditDescriptor> Map = new()
    {
        // ── Usuários ─────────────────────────────────────────────────────────
        [typeof(CreateUserCommand)] = new(
            "identity.user_created", "User",
            cmd => null,
            cmd => null,
            cmd => new { Login = ((CreateUserCommand)cmd).Login, Email = ((CreateUserCommand)cmd).Email, FullName = ((CreateUserCommand)cmd).FullName, MfaRequired = ((CreateUserCommand)cmd).MfaRequired }),

        [typeof(UpdateUserCommand)] = new(
            "identity.user_updated", "User",
            cmd => ((UpdateUserCommand)cmd).Id,
            cmd => ((UpdateUserCommand)cmd).Id,
            cmd => new { Id = ((UpdateUserCommand)cmd).Id, Login = ((UpdateUserCommand)cmd).Login, Email = ((UpdateUserCommand)cmd).Email, FullName = ((UpdateUserCommand)cmd).FullName, IsActive = ((UpdateUserCommand)cmd).IsActive, MfaRequired = ((UpdateUserCommand)cmd).MfaRequired }),

        [typeof(DeleteUserCommand)] = new(
            "identity.user_deactivated", "User",
            cmd => ((DeleteUserCommand)cmd).Id,
            cmd => ((DeleteUserCommand)cmd).Id,
            cmd => new { Id = ((DeleteUserCommand)cmd).Id, Operation = "deactivate" }),

        [typeof(ResetUserPasswordCommand)] = new(
            "identity.user_password_reset", "User",
            cmd => ((ResetUserPasswordCommand)cmd).UserId,
            cmd => ((ResetUserPasswordCommand)cmd).UserId,
            // Senha NUNCA entra no payload audado.
            cmd => new { UserId = ((ResetUserPasswordCommand)cmd).UserId, RequestedBy = ((ResetUserPasswordCommand)cmd).RequestedBy }),

        [typeof(ForcePasswordChangeCommand)] = new(
            "identity.user_force_password_reset", "User",
            cmd => ((ForcePasswordChangeCommand)cmd).Id,
            cmd => ((ForcePasswordChangeCommand)cmd).Id,
            cmd => new { Id = ((ForcePasswordChangeCommand)cmd).Id }),

        [typeof(RevokeUserMfaCommand)] = new(
            "identity.user_mfa_revoked", "User",
            cmd => ((RevokeUserMfaCommand)cmd).Id,
            cmd => ((RevokeUserMfaCommand)cmd).Id,
            cmd => new { Id = ((RevokeUserMfaCommand)cmd).Id }),

        [typeof(RevokeUserMfaKeyCommand)] = new(
            "identity.user_mfa_key_revoked", "User",
            cmd => ((RevokeUserMfaKeyCommand)cmd).Id,
            cmd => ((RevokeUserMfaKeyCommand)cmd).Id,
            cmd => new { Id = ((RevokeUserMfaKeyCommand)cmd).Id, KeyId = ((RevokeUserMfaKeyCommand)cmd).KeyId }),

        [typeof(ChangeUserPasswordCommand)] = new(
            "identity.self_password_changed", "User",
            cmd => ((ChangeUserPasswordCommand)cmd).UserId,
            cmd => ((ChangeUserPasswordCommand)cmd).UserId,
            // Nem senha atual nem nova entram no payload audado.
            cmd => new { UserId = ((ChangeUserPasswordCommand)cmd).UserId }),

        [typeof(UpdateMyProfileCommand)] = new(
            "identity.self_profile_updated", "User",
            cmd => ((UpdateMyProfileCommand)cmd).Id,
            cmd => ((UpdateMyProfileCommand)cmd).Id,
            cmd => new { Id = ((UpdateMyProfileCommand)cmd).Id, Email = ((UpdateMyProfileCommand)cmd).Email, FullName = ((UpdateMyProfileCommand)cmd).FullName }),

        // ── Papéis e permissões ──────────────────────────────────────────────
        [typeof(CreateRoleCommand)] = new(
            "identity.role_created", "Role", cmd => null, cmd => null,
            cmd => new { Name = ((CreateRoleCommand)cmd).Name }),

        [typeof(UpdateRoleCommand)] = new(
            "identity.role_updated", "Role",
            cmd => ((UpdateRoleCommand)cmd).Id, cmd => null,
            cmd => new { Id = ((UpdateRoleCommand)cmd).Id, Name = ((UpdateRoleCommand)cmd).Name, IsActive = ((UpdateRoleCommand)cmd).IsActive, MfaRequirement = ((UpdateRoleCommand)cmd).MfaRequirement }),

        [typeof(DeleteRoleCommand)] = new(
            "identity.role_deleted", "Role",
            cmd => ((DeleteRoleCommand)cmd).Id, cmd => null,
            cmd => new { Id = ((DeleteRoleCommand)cmd).Id }),

        [typeof(AddRolePermissionCommand)] = new(
            "identity.role_permission_granted", "Role",
            cmd => ((AddRolePermissionCommand)cmd).RoleId, cmd => null,
            cmd => new { RoleId = ((AddRolePermissionCommand)cmd).RoleId, PermissionId = ((AddRolePermissionCommand)cmd).PermissionId }),

        [typeof(RemoveRolePermissionCommand)] = new(
            "identity.role_permission_revoked", "Role",
            cmd => ((RemoveRolePermissionCommand)cmd).RoleId, cmd => null,
            cmd => new { RoleId = ((RemoveRolePermissionCommand)cmd).RoleId, PermissionId = ((RemoveRolePermissionCommand)cmd).PermissionId }),

        // ── Grupos, vínculos e ATRIBUIÇÕES DE ESCOPO (o vetor de escalação) ──
        [typeof(CreateUserGroupCommand)] = new(
            "identity.group_created", "UserGroup", cmd => null, cmd => null,
            cmd => new { Name = ((CreateUserGroupCommand)cmd).Name }),

        [typeof(UpdateUserGroupCommand)] = new(
            "identity.group_updated", "UserGroup",
            cmd => ((UpdateUserGroupCommand)cmd).Id, cmd => null,
            cmd => new { Id = ((UpdateUserGroupCommand)cmd).Id, Name = ((UpdateUserGroupCommand)cmd).Name, IsActive = ((UpdateUserGroupCommand)cmd).IsActive }),

        [typeof(DeleteUserGroupCommand)] = new(
            "identity.group_deleted", "UserGroup",
            cmd => ((DeleteUserGroupCommand)cmd).Id, cmd => null,
            cmd => new { Id = ((DeleteUserGroupCommand)cmd).Id }),

        [typeof(AddGroupMemberCommand)] = new(
            "identity.group_member_added", "UserGroup",
            cmd => ((AddGroupMemberCommand)cmd).GroupId, cmd => ((AddGroupMemberCommand)cmd).UserId,
            cmd => new { GroupId = ((AddGroupMemberCommand)cmd).GroupId, UserId = ((AddGroupMemberCommand)cmd).UserId }),

        [typeof(RemoveGroupMemberCommand)] = new(
            "identity.group_member_removed", "UserGroup",
            cmd => ((RemoveGroupMemberCommand)cmd).GroupId, cmd => ((RemoveGroupMemberCommand)cmd).UserId,
            cmd => new { GroupId = ((RemoveGroupMemberCommand)cmd).GroupId, UserId = ((RemoveGroupMemberCommand)cmd).UserId }),

        [typeof(AssignGroupRoleCommand)] = new(
            "identity.role_scope_granted", "UserGroupRole",
            cmd => ((AssignGroupRoleCommand)cmd).GroupId, cmd => null,
            cmd => new { GroupId = ((AssignGroupRoleCommand)cmd).GroupId, RoleId = ((AssignGroupRoleCommand)cmd).RoleId, ScopeLevel = ((AssignGroupRoleCommand)cmd).ScopeLevel.ToString(), ScopeId = ((AssignGroupRoleCommand)cmd).ScopeId }),

        [typeof(RemoveGroupRoleAssignmentCommand)] = new(
            "identity.role_scope_revoked", "UserGroupRole",
            cmd => ((RemoveGroupRoleAssignmentCommand)cmd).GroupId, cmd => null,
            cmd => new { GroupId = ((RemoveGroupRoleAssignmentCommand)cmd).GroupId, AssignmentId = ((RemoveGroupRoleAssignmentCommand)cmd).AssignmentId })
    };

    /// <summary>Descreve o comando, ou null quando ele não é uma mutação de identidade.</summary>
    internal static IdentityAuditDescriptor? Describe(object request)
        => Map.TryGetValue(request.GetType(), out var descriptor) ? descriptor : null;

    /// <summary>
    /// Lê o desfecho de um <c>Result&lt;T&gt;</c> via reflexão leve (o behavior é
    /// genérico e não conhece TResponse). Tipos que não sejam Result são tratados
    /// como sucesso — comandos de identidade hoje sempre devolvem Result.
    /// </summary>
    internal static (bool Success, string? FailureReason) ReadOutcome(object? response)
    {
        if (response is null)
            return (true, null);

        var type = response.GetType();
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(Result<>))
            return (true, null);

        var isSuccess = type.GetProperty("IsSuccess")?.GetValue(response) as bool?;
        if (isSuccess is null or true)
            return (true, null);

        var errors = type.GetProperty("Errors")?.GetValue(response) as System.Collections.IEnumerable;
        var first = errors?.Cast<object>().FirstOrDefault();
        var code = first?.GetType().GetProperty("Code")?.GetValue(first)?.ToString();
        var message = first?.GetType().GetProperty("Message")?.GetValue(first)?.ToString();

        return (false, message ?? code ?? "operation_failed");
    }

    /// <summary>Ator declarado no próprio comando (execução fora de request HTTP).</summary>
    internal static Guid ReadActorFromCommand(object request) => request switch
    {
        AssignGroupRoleCommand cmd => cmd.RequestedByUserId,
        RemoveGroupRoleAssignmentCommand cmd => cmd.RequestedByUserId,
        AddGroupMemberCommand cmd => cmd.RequestedByUserId,
        RemoveGroupMemberCommand cmd => cmd.RequestedByUserId,
        _ => Guid.Empty
    };
}
