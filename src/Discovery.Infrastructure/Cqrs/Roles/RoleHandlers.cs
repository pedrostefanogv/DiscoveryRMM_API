using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Roles.Commands;
using Discovery.Core.DTOs.Roles;
using Discovery.Core.Cqrs.Roles.Queries;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Identity;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Roles;

/// <summary>
/// Escopo ambiental que silencia a invalidação de cache dos handlers UNITÁRIOS enquanto um
/// handler em lote está dirigindo a operação.
/// </summary>
/// <remarks>
/// <para>
/// Por que existe: o lote reusa o caminho de gravação unitário (um comando por permissão),
/// e o unitário invalida o cache incondicionalmente. Como os dois recebem o MESMO
/// <c>IPermissionService</c> scoped (auto-registro <c>TryAddScoped</c>), um lote com N efeitos
/// reais produzia <c>1 + N</c> invalidações do cache GLOBAL — com o catálogo real de 100+
/// permissões, mais de 100 releituras de permissão para todos os usuários por clique.
/// </para>
/// <para>
/// Por que um flag ambiental e não um escopo DI isolado (<c>CreateScope</c>): um escopo novo
/// resolve OUTRA instância de <c>RoleService</c>/<c>RoleRepository</c> sobre outro
/// <c>DiscoveryDbContext</c>, e o unitário deixaria de enxergar o que o lote acabou de gravar
/// (além de perder a transação corrente). O flag não troca nenhuma dependência de escopo:
/// muda apenas QUEM invalida, mantendo a gravação no mesmo contexto relacional.
/// </para>
/// <para>
/// Seguro para concorrência: <see cref="AsyncLocal{T}"/> propaga pelo fluxo assíncrono da
/// MESMA requisição e não vaza entre requisições concorrentes. A restauração é feita em
/// <c>finally</c>, então uma exceção no meio do lote não deixa a supressão ligada.
/// </para>
/// </remarks>
internal static class RolePermissionCacheSuppression
{
    private static readonly AsyncLocal<int> _depth = new();

    /// <summary>Contador de profundidade: lotes aninhados não reativam a invalidação no meio.</summary>
    internal static bool IsSuppressed => _depth.Value > 0;

    internal static IDisposable Suppress()
    {
        _depth.Value++;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Nunca deixa o contador negativo, mesmo se Dispose for chamado duas vezes.
            if (_depth.Value > 0) _depth.Value--;
        }
    }
}

internal static class RoleMapping
{
    internal static RoleDto ToDto(Role r) => new(
        r.Id, r.Name, r.Description, r.IsSystem, r.MfaRequirement, r.IsActive, r.CreatedAt, r.UpdatedAt);

    /// <summary>
    /// O catálogo de permissões não tem coluna de código: o "código" é a composição
    /// Recurso.Ação (ex.: "Users.View"), exatamente o formato usado pelos gates do console.
    /// </summary>
    internal static PermissionDto ToDto(Permission p)
    {
        var code = $"{p.ResourceType}.{p.ActionType}";
        return new PermissionDto(p.Id, code, p.Description ?? code, p.Description,
            p.ResourceType.ToString(), p.ActionType.ToString());
    }
}

public sealed class CreateRoleCommandHandler(
    IRoleService service
) : IRequestHandler<CreateRoleCommand, Result<RoleDto>>
{
    public async Task<Result<RoleDto>> Handle(CreateRoleCommand cmd, CancellationToken ct)
    {
        var role = new Role
        {
            Name = cmd.Name,
            Description = cmd.Description,
            MfaRequirement = cmd.MfaRequirement,
            IsActive = true
        };
        var created = await service.CreateAsync(role, ct);
        return Result<RoleDto>.Success(RoleMapping.ToDto(created));
    }
}

public sealed class UpdateRoleCommandHandler(
    IRoleService service,
    IPermissionService permissions
) : IRequestHandler<UpdateRoleCommand, Result<RoleDto>>
{
    public async Task<Result<RoleDto>> Handle(UpdateRoleCommand cmd, CancellationToken ct)
    {
        var role = await service.GetByIdAsync(cmd.Id, ct);
        if (role is null)
            return Result<RoleDto>.Failure(Error.NotFound($"Role {cmd.Id} not found"));
        if (role.IsSystem)
            return Result<RoleDto>.Failure(Error.Validation("Id", "System roles cannot be modified"));

        if (cmd.Name is not null) role.Name = cmd.Name;
        if (cmd.Description is not null) role.Description = cmd.Description;

        // Política de MFA por role; liberada também para roles de sistema seria ideal,
        // mas a regra atual bloqueia qualquer edição de role de sistema.
        if (cmd.MfaRequirement.HasValue) role.MfaRequirement = cmd.MfaRequirement.Value;

        var permissionsChanged = false;
        if (cmd.IsActive.HasValue && cmd.IsActive.Value != role.IsActive)
        {
            role.IsActive = cmd.IsActive.Value;
            // Role inativa deixa de conceder permissões (filtro em GetRolesWithPermissionsForUserAsync).
            permissionsChanged = true;
        }

        var updated = await service.UpdateAsync(role, ct);

        if (permissionsChanged)
            await permissions.InvalidateAllCacheAsync();

        return Result<RoleDto>.Success(RoleMapping.ToDto(updated));
    }
}

public sealed class DeleteRoleCommandHandler(
    IRoleService service,
    IPermissionService permissions
) : IRequestHandler<DeleteRoleCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteRoleCommand cmd, CancellationToken ct)
    {
        var role = await service.GetByIdAsync(cmd.Id, ct);
        if (role is null)
            return Result<VoidResult>.Failure(Error.NotFound($"Role {cmd.Id} not found"));
        if (role.IsSystem)
            return Result<VoidResult>.Failure(Error.Validation("Id", "System roles cannot be deleted"));

        var deleted = await service.DeleteAsync(cmd.Id, ct);
        if (deleted)
            await permissions.InvalidateAllCacheAsync();

        return deleted
            ? Result<VoidResult>.Success(VoidResult.Value)
            : Result<VoidResult>.Failure(Error.NotFound($"Role {cmd.Id} not found"));
    }
}

public sealed class ListRolesQueryHandler(
    IRoleService service
) : IRequestHandler<ListRolesQuery, Result<IReadOnlyList<RoleDto>>>
{
    public async Task<Result<IReadOnlyList<RoleDto>>> Handle(ListRolesQuery q, CancellationToken ct)
    {
        var roles = await service.GetAllAsync(ct);
        return Result<IReadOnlyList<RoleDto>>.Success(
            roles.Select(RoleMapping.ToDto).ToList().AsReadOnly());
    }
}

public sealed class GetRoleByIdQueryHandler(
    IRoleService service
) : IRequestHandler<GetRoleByIdQuery, Result<RoleDto>>
{
    public async Task<Result<RoleDto>> Handle(GetRoleByIdQuery q, CancellationToken ct)
    {
        var role = await service.GetByIdAsync(q.Id, ct);
        return role is null
            ? Result<RoleDto>.Failure(Error.NotFound($"Role {q.Id} not found"))
            : Result<RoleDto>.Success(RoleMapping.ToDto(role));
    }
}

// ── Permissões de role ───────────────────────────────────────────────────────

public sealed class ListRolePermissionsQueryHandler(
    IRoleService service
) : IRequestHandler<ListRolePermissionsQuery, Result<IReadOnlyList<PermissionDto>>>
{
    public async Task<Result<IReadOnlyList<PermissionDto>>> Handle(ListRolePermissionsQuery q, CancellationToken ct)
    {
        if (await service.GetByIdAsync(q.RoleId, ct) is null)
            return Result<IReadOnlyList<PermissionDto>>.Failure(Error.NotFound($"Role {q.RoleId} not found"));

        var permissions = await service.GetPermissionsAsync(q.RoleId, ct);
        return Result<IReadOnlyList<PermissionDto>>.Success(
            permissions.Select(RoleMapping.ToDto).ToList().AsReadOnly());
    }
}

public sealed class ListPermissionsCatalogQueryHandler(
    IRoleService service
) : IRequestHandler<ListPermissionsCatalogQuery, Result<IReadOnlyList<PermissionDto>>>
{
    public async Task<Result<IReadOnlyList<PermissionDto>>> Handle(ListPermissionsCatalogQuery q, CancellationToken ct)
    {
        var permissions = await service.GetAllPermissionsAsync(ct);
        return Result<IReadOnlyList<PermissionDto>>.Success(
            permissions.Select(RoleMapping.ToDto).OrderBy(p => p.Code, StringComparer.Ordinal).ToList().AsReadOnly());
    }
}

public sealed class AddRolePermissionCommandHandler(
    IRoleService service,
    IPermissionService permissions
) : IRequestHandler<AddRolePermissionCommand, Result<PermissionDto>>
{
    public async Task<Result<PermissionDto>> Handle(AddRolePermissionCommand cmd, CancellationToken ct)
    {
        var role = await service.GetByIdAsync(cmd.RoleId, ct);
        if (role is null)
            return Result<PermissionDto>.Failure(Error.NotFound($"Role {cmd.RoleId} not found"));

        // Papéis de sistema (Admin/Operator/Manager/Support/Viewer e as personas de
        // menor privilégio) têm seu conjunto de permissões fixado por migração.
        // O Update/Delete já bloqueavam IsSystem; add/remove de permissão NÃO —
        // era possível ampliar silenciosamente o papel Admin embutido.
        if (role.IsSystem)
            return Result<PermissionDto>.Failure(
                Error.Validation("RoleId", "Permissões de papéis de sistema não podem ser alteradas."));

        var catalog = await service.GetAllPermissionsAsync(ct);
        var permission = catalog.FirstOrDefault(p => p.Id == cmd.PermissionId);

        if (permission is null)
            return Result<PermissionDto>.Failure(Error.NotFound($"Permission {cmd.PermissionId} not found"));

        try
        {
            await service.AddPermissionAsync(cmd.RoleId, cmd.PermissionId, ct);
        }
        catch (InvalidOperationException ex)
        {
            // Invariante da camada de repositório (defesa em profundidade).
            return Result<PermissionDto>.Failure(Error.Validation("RoleId", ex.Message));
        }

        // Dirigido por um lote, quem invalida é o lote (uma vez, no final): aqui seria
        // invalidação redundante do cache global por item. Chamado avulso, invalida normal.
        if (!RolePermissionCacheSuppression.IsSuppressed)
            await permissions.InvalidateAllCacheAsync();

        return Result<PermissionDto>.Success(RoleMapping.ToDto(permission));
    }
}

public sealed class RemoveRolePermissionCommandHandler(
    IRoleService service,
    IPermissionService permissions
) : IRequestHandler<RemoveRolePermissionCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(RemoveRolePermissionCommand cmd, CancellationToken ct)
    {
        var role = await service.GetByIdAsync(cmd.RoleId, ct);
        if (role is null)
            return Result<VoidResult>.Failure(Error.NotFound($"Role {cmd.RoleId} not found"));

        // Mesma invariante do Add: remover permissão de papel de sistema é alteração
        // silenciosa de privilégio (ex.: tirar Users:Edit do Admin).
        if (role.IsSystem)
            return Result<VoidResult>.Failure(
                Error.Validation("RoleId", "Permissões de papéis de sistema não podem ser alteradas."));

        try
        {
            await service.RemovePermissionAsync(cmd.RoleId, cmd.PermissionId, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Result<VoidResult>.Failure(Error.Validation("RoleId", ex.Message));
        }

        // Mesma regra do Add: suprimido durante um lote, que invalida uma vez ao final.
        if (!RolePermissionCacheSuppression.IsSuppressed)
            await permissions.InvalidateAllCacheAsync();

        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

// ── Permissões de role em lote ───────────────────────────────────────────────

/// <summary>
/// Base comum dos handlers em lote: valida role/catálogo uma vez e classifica os ids
/// pedidos em "aplicar" x "já no estado desejado".
/// </summary>
/// <remarks>
/// Decisões registradas:
/// <list type="bullet">
/// <item>A gravação efetiva é feita enviando os comandos UNITÁRIOS pelo mediator, um por
/// permissão. Duplicar aqui a checagem de <c>IsSystem</c> criaria uma segunda cópia da
/// invariante e o risco clássico de as duas divergirem; reenviando pelo mediator a regra
/// (inclusive a defesa em profundidade do repositório) permanece com um dono só.</item>
/// <item>O lote NÃO aborta no meio: os unitários são processados até o fim e um erro
/// inesperado é agregado para devolver Failure no final. Abortar na primeira falha deixaria
/// a role silenciosamente em estado parcial sem que os itens restantes fossem tentados.</item>
/// <item>A invalidação de cache acontece UMA vez por requisição, e não a cada unitário: o
/// cache é global e invalidá-lo N vezes faria o console (e os demais usuários) recarregarem
/// as permissões a cada item aplicado. Como os unitários reusados invalidariam por conta
/// própria (mesmo IPermissionService scoped), o loop roda dentro de
/// <see cref="RolePermissionCacheSuppression"/> — ver o comentário daquela classe.</item>
/// </list>
/// </remarks>
internal static class BulkRolePermissions
{
    internal static readonly Error SystemRoleError =
        Error.Validation("RoleId", "Permissões de papéis de sistema não podem ser alteradas.");

    /// <summary>
    /// Deduplica preservando a ordem de chegada — o console pode remontar a lista com ids
    /// repetidos e cada repetição seria contada como um "added" a mais.
    /// </summary>
    internal static List<Guid> Normalize(IReadOnlyList<Guid>? requested)
    {
        var seen = new HashSet<Guid>();
        var normalized = new List<Guid>(requested?.Count ?? 0);

        foreach (var id in requested ?? [])
            if (seen.Add(id))
                normalized.Add(id);

        return normalized;
    }

    /// <summary>
    /// Resolve role e catálogo antes de qualquer efeito colateral. Devolve o erro pronto
    /// (role inexistente ou id fora do catálogo) para o chamador apenas repassar.
    /// </summary>
    internal static async Task<(Role? Role, HashSet<Guid> CatalogIds, Error? Error)> LoadContextAsync(
        IRoleService service, Guid roleId, IReadOnlyList<Guid> permissionIds, CancellationToken ct)
    {
        var role = await service.GetByIdAsync(roleId, ct);
        if (role is null)
            return (null, [], Error.NotFound($"Role {roleId} not found"));

        // Papel de sistema é recusado antes de qualquer leitura de catálogo: a mensagem
        // precisa ser exatamente a do handler unitário.
        if (role.IsSystem)
            return (null, [], SystemRoleError);

        var catalog = await service.GetAllPermissionsAsync(ct);
        var catalogIds = catalog.Select(p => p.Id).ToHashSet();

        // Validação prévia: um id inválido no meio do lote não pode conceder "meio lote"
        // e falhar depois — o console mostra a matriz inteira, então ou vale tudo ou nada.
        var unknown = permissionIds.Where(id => !catalogIds.Contains(id)).ToList();
        if (unknown.Count > 0)
            return (null, [], Error.Validation("PermissionIds",
                $"Permissões inexistentes no catálogo: {string.Join(", ", unknown)}."));

        return (role, catalogIds, null);
    }
}

public sealed class AddRolePermissionsCommandHandler(
    IMediator mediator,
    IPermissionService permissions,
    IRoleService service
) : IRequestHandler<AddRolePermissionsCommand, Result<BulkAssignPermissionsResultDto>>
{
    public async Task<Result<BulkAssignPermissionsResultDto>> Handle(
        AddRolePermissionsCommand cmd, CancellationToken ct)
    {
        var requested = BulkRolePermissions.Normalize(cmd.PermissionIds);
        if (requested.Count == 0)
            return Result<BulkAssignPermissionsResultDto>.Success(new BulkAssignPermissionsResultDto(0, 0));

        var (role, _, error) = await BulkRolePermissions.LoadContextAsync(service, cmd.RoleId, requested, ct);
        if (error is not null)
            return Result<BulkAssignPermissionsResultDto>.Failure(error);

        var current = (await service.GetPermissionsAsync(cmd.RoleId, ct)).Select(p => p.Id).ToHashSet();

        var toApply = requested.Where(id => !current.Contains(id)).ToList();
        var skipped = requested.Count - toApply.Count;

        var added = 0;
        Error? firstFailure = null;

        // Supressão ativa durante todo o loop: cada unitário deixa de invalidar o cache
        // global (seriam N invalidações) e o lote invalida uma única vez logo abaixo.
        using (RolePermissionCacheSuppression.Suppress())
        {
            foreach (var permissionId in toApply)
            {
                ct.ThrowIfCancellationRequested();

                var result = await mediator.Send(new AddRolePermissionCommand(cmd.RoleId, permissionId), ct);

                if (result.IsSuccess)
                    added++;
                else
                    // Agrega e continua: os demais ids ainda são tentados, mas o resultado final
                    // é Failure para o console não achar que o lote inteiro foi aplicado.
                    firstFailure ??= result.Errors[0];
            }
        }

        // Invalidação SEMPRE ao final do lote, inclusive no caminho added=0/skipped=N: outro
        // operador pode ter alterado as permissões da role entre a leitura acima e agora, e o
        // handler unitário já invalida incondicionalmente. Condicionar a "added > 0" deixaria
        // cache obsoleto justamente no caso idempotente (reenvio do mesmo lote).
        await permissions.InvalidateAllCacheAsync();

        return firstFailure is not null
            ? Result<BulkAssignPermissionsResultDto>.Failure(firstFailure)
            : Result<BulkAssignPermissionsResultDto>.Success(new BulkAssignPermissionsResultDto(added, skipped));
    }
}

public sealed class RemoveRolePermissionsCommandHandler(
    IMediator mediator,
    IPermissionService permissions,
    IRoleService service
) : IRequestHandler<RemoveRolePermissionsCommand, Result<BulkRemovePermissionsResultDto>>
{
    public async Task<Result<BulkRemovePermissionsResultDto>> Handle(
        RemoveRolePermissionsCommand cmd, CancellationToken ct)
    {
        var requested = BulkRolePermissions.Normalize(cmd.PermissionIds);
        if (requested.Count == 0)
            return Result<BulkRemovePermissionsResultDto>.Success(new BulkRemovePermissionsResultDto(0, 0));

        var (role, _, error) = await BulkRolePermissions.LoadContextAsync(service, cmd.RoleId, requested, ct);
        if (error is not null)
            return Result<BulkRemovePermissionsResultDto>.Failure(error);

        var current = (await service.GetPermissionsAsync(cmd.RoleId, ct)).Select(p => p.Id).ToHashSet();

        var toApply = requested.Where(current.Contains).ToList();
        var skipped = requested.Count - toApply.Count;

        var removed = 0;
        Error? firstFailure = null;

        // Mesma supressão do add: ver RolePermissionCacheSuppression.
        using (RolePermissionCacheSuppression.Suppress())
        {
            foreach (var permissionId in toApply)
            {
                ct.ThrowIfCancellationRequested();

                var result = await mediator.Send(new RemoveRolePermissionCommand(cmd.RoleId, permissionId), ct);

                if (result.IsSuccess)
                    removed++;
                else
                    firstFailure ??= result.Errors[0];
            }
        }

        // Mesma regra do add: invalida sempre, inclusive em removed=0/skipped=N (ver comentário
        // no AddRolePermissionsCommandHandler).
        await permissions.InvalidateAllCacheAsync();

        return firstFailure is not null
            ? Result<BulkRemovePermissionsResultDto>.Failure(firstFailure)
            : Result<BulkRemovePermissionsResultDto>.Success(new BulkRemovePermissionsResultDto(removed, skipped));
    }
}
