using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Clients.Commands;
using Discovery.Core.Cqrs.Clients.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Clients;

public sealed class GetAllClientsQueryHandler(
    IClientRepository repo,
    IScopeContext scopeContext
) : IRequestHandler<GetAllClientsQuery, Result<IReadOnlyList<Client>>>
{
    public async Task<Result<IReadOnlyList<Client>>> Handle(GetAllClientsQuery q, CancellationToken ct)
    {
        var scope = await scopeContext.GetAccessAsync(ResourceType.Clients, ActionType.View);
        List<Client> clients;
        if (scope.HasGlobalAccess)
        {
            clients = (await repo.GetAllAsync(q.IncludeInactive)).ToList();
        }
        else
        {
            var allowedIds = scope.AllowedClientIds.ToHashSet();
            clients = (await repo.GetAllAsync(q.IncludeInactive))
                .Where(c => allowedIds.Contains(c.Id))
                .ToList();
        }
        return Result<IReadOnlyList<Client>>.Success(clients);
    }
}

public sealed class GetClientByIdQueryHandler(
    IClientRepository repo,
    IScopeContext scopeContext
) : IRequestHandler<GetClientByIdQuery, Result<Client>>
{
    public async Task<Result<Client>> Handle(GetClientByIdQuery q, CancellationToken ct)
    {
        var scope = await scopeContext.GetAccessAsync(ResourceType.Clients, ActionType.View);
        if (!scope.HasGlobalAccess && !scope.AllowedClientIds.Contains(q.Id))
            return Result<Client>.Failure(Error.NotFound("Client not found."));

        var client = await repo.GetByIdAsync(q.Id);
        return client is null
            ? Result<Client>.Failure(Error.NotFound("Client not found."))
            : Result<Client>.Success(client);
    }
}

public sealed class CreateClientCommandHandler(
    IClientRepository repo
) : IRequestHandler<CreateClientCommand, Result<Client>>
{
    public async Task<Result<Client>> Handle(CreateClientCommand cmd, CancellationToken ct)
    {
        var client = new Client { Name = cmd.Name, Notes = cmd.Notes };
        var created = await repo.CreateAsync(client);
        return Result<Client>.Success(created);
    }
}

public sealed class UpdateClientCommandHandler(
    IClientRepository repo
) : IRequestHandler<UpdateClientCommand, Result<Client>>
{
    public async Task<Result<Client>> Handle(UpdateClientCommand cmd, CancellationToken ct)
    {
        var client = await repo.GetByIdAsync(cmd.Id);
        if (client is null)
            return Result<Client>.Failure(Error.NotFound("Client not found."));

        client.Name = cmd.Name;
        client.Notes = cmd.Notes;
        client.IsActive = cmd.IsActive;
        await repo.UpdateAsync(client);
        return Result<Client>.Success(client);
    }
}

public sealed class DeleteClientCommandHandler(
    IClientRepository repo
) : IRequestHandler<DeleteClientCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteClientCommand cmd, CancellationToken ct)
    {
        await repo.DeleteAsync(cmd.Id);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class GetClientCustomFieldsQueryHandler(
    IClientRepository repo,
    ICustomFieldService customFieldService
) : IRequestHandler<GetClientCustomFieldsQuery, Result<IReadOnlyList<object>>>
{
    public async Task<Result<IReadOnlyList<object>>> Handle(GetClientCustomFieldsQuery q, CancellationToken ct)
    {
        var client = await repo.GetByIdAsync(q.ClientId);
        if (client is null)
            return Result<IReadOnlyList<object>>.Failure(Error.NotFound("Client not found."));

        var values = await customFieldService.GetValuesAsync(CustomFieldScopeType.Client, q.ClientId, q.IncludeSecrets, ct);
        return Result<IReadOnlyList<object>>.Success(values);
    }
}

public sealed class UpsertClientCustomFieldCommandHandler(
    IClientRepository repo,
    ICustomFieldService customFieldService
) : IRequestHandler<UpsertClientCustomFieldCommand, Result<object>>
{
    public async Task<Result<object>> Handle(UpsertClientCustomFieldCommand cmd, CancellationToken ct)
    {
        var client = await repo.GetByIdAsync(cmd.ClientId);
        if (client is null)
            return Result<object>.Failure(Error.NotFound("Client not found."));

        try
        {
            var value = await customFieldService.UpsertValueAsync(
                new Core.DTOs.UpsertCustomFieldValueInput(
                    cmd.DefinitionId, CustomFieldScopeType.Client, cmd.ClientId, cmd.ValueJson, cmd.Username),
                ct);
            return Result<object>.Success(value);
        }
        catch (InvalidOperationException ex)
        {
            return Result<object>.Failure(Error.Validation("Value", ex.Message));
        }
    }
}
