namespace Discovery.Core.DTOs.Roles;

/// <summary>
/// Corpo de POST /api/v1/roles/{id}/permissions.
/// Os demais DTOs de role vivem em Discovery.Core.Cqrs.Roles (RoleDto/PermissionDto);
/// as classes duplicadas que existiam aqui nunca eram referenciadas.
/// </summary>
public class AssignPermissionToRoleDto
{
    public Guid PermissionId { get; set; }
}

/// <summary>
/// Corpo dos endpoints em lote (POST e DELETE /api/v1/roles/{id}/permissions/batch).
/// É uma lista e não um item único porque o console edita a matriz de permissões de uma
/// vez: enviar N requisições unitárias deixava a role visível em estado parcial enquanto
/// a tela recarregava, além de multiplicar as invalidações de cache.
/// </summary>
public class BulkRolePermissionsDto
{
    public List<Guid> PermissionIds { get; set; } = new();
}

/// <summary>
/// Resultado de POST /api/v1/roles/{id}/permissions/batch.
/// <c>Added</c> são as concessões efetivas e <c>Skipped</c> as que já existiam — separar
/// os dois permite ao console diferenciar "nada mudou" de "falhou" sem tratar erro.
/// </summary>
public sealed record BulkAssignPermissionsResultDto(int Added, int Skipped);

/// <summary>
/// Resultado de DELETE /api/v1/roles/{id}/permissions/batch.
/// Mesma semântica de <see cref="BulkAssignPermissionsResultDto"/> para revogação:
/// <c>Skipped</c> conta os ids que não estavam vinculados (idempotente).
/// </summary>
public sealed record BulkRemovePermissionsResultDto(int Removed, int Skipped);
