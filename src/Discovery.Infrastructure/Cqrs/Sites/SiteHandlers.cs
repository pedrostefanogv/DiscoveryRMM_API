using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Sites.Commands;
using Discovery.Core.Cqrs.Sites.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Sites;

public sealed class GetSitesByClientQueryHandler(
    ISiteRepository repo
) : IRequestHandler<GetSitesByClientQuery, Result<IReadOnlyList<Site>>>
{
    public async Task<Result<IReadOnlyList<Site>>> Handle(GetSitesByClientQuery q, CancellationToken ct)
    {
        var sites = await repo.GetByClientIdAsync(q.ClientId, q.IncludeInactive);
        return Result<IReadOnlyList<Site>>.Success(sites.ToList());
    }
}

public sealed class GetSiteByIdQueryHandler(
    ISiteRepository repo
) : IRequestHandler<GetSiteByIdQuery, Result<Site>>
{
    public async Task<Result<Site>> Handle(GetSiteByIdQuery q, CancellationToken ct)
    {
        var site = await repo.GetByIdAsync(q.SiteId);
        if (site is null || site.ClientId != q.ClientId)
            return Result<Site>.Failure(Error.NotFound("Site not found."));
        return Result<Site>.Success(site);
    }
}

public sealed class CreateSiteCommandHandler(
    ISiteRepository repo
) : IRequestHandler<CreateSiteCommand, Result<Site>>
{
    public async Task<Result<Site>> Handle(CreateSiteCommand cmd, CancellationToken ct)
    {
        var site = new Site { ClientId = cmd.ClientId, Name = cmd.Name, Notes = cmd.Notes };
        var created = await repo.CreateAsync(site);
        return Result<Site>.Success(created);
    }
}

public sealed class UpdateSiteCommandHandler(
    ISiteRepository repo
) : IRequestHandler<UpdateSiteCommand, Result<Site>>
{
    public async Task<Result<Site>> Handle(UpdateSiteCommand cmd, CancellationToken ct)
    {
        var site = await repo.GetByIdAsync(cmd.SiteId);
        if (site is null || site.ClientId != cmd.ClientId)
            return Result<Site>.Failure(Error.NotFound("Site not found."));

        site.Name = cmd.Name;
        site.Notes = cmd.Notes;
        site.IsActive = cmd.IsActive;
        await repo.UpdateAsync(site);
        return Result<Site>.Success(site);
    }
}

public sealed class DeleteSiteCommandHandler(
    ISiteRepository repo
) : IRequestHandler<DeleteSiteCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteSiteCommand cmd, CancellationToken ct)
    {
        var site = await repo.GetByIdAsync(cmd.SiteId);
        if (site is null || site.ClientId != cmd.ClientId)
            return Result<VoidResult>.Failure(Error.NotFound("Site not found."));
        await repo.DeleteAsync(cmd.SiteId);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class GetSiteCustomFieldsQueryHandler(
    ISiteRepository repo,
    ICustomFieldService customFieldService
) : IRequestHandler<GetSiteCustomFieldsQuery, Result<IReadOnlyList<object>>>
{
    public async Task<Result<IReadOnlyList<object>>> Handle(GetSiteCustomFieldsQuery q, CancellationToken ct)
    {
        var site = await repo.GetByIdAsync(q.SiteId);
        if (site is null || site.ClientId != q.ClientId)
            return Result<IReadOnlyList<object>>.Failure(Error.NotFound("Site not found."));

        var values = await customFieldService.GetValuesAsync(CustomFieldScopeType.Site, q.SiteId, q.IncludeSecrets, ct);
        return Result<IReadOnlyList<object>>.Success(values);
    }
}

public sealed class UpsertSiteCustomFieldCommandHandler(
    ISiteRepository repo,
    ICustomFieldService customFieldService
) : IRequestHandler<UpsertSiteCustomFieldCommand, Result<object>>
{
    public async Task<Result<object>> Handle(UpsertSiteCustomFieldCommand cmd, CancellationToken ct)
    {
        var site = await repo.GetByIdAsync(cmd.SiteId);
        if (site is null || site.ClientId != cmd.ClientId)
            return Result<object>.Failure(Error.NotFound("Site not found."));

        try
        {
            var value = await customFieldService.UpsertValueAsync(
                new Core.DTOs.UpsertCustomFieldValueInput(
                    cmd.DefinitionId, CustomFieldScopeType.Site, cmd.SiteId, cmd.ValueJson, cmd.Username),
                ct);
            return Result<object>.Success(value);
        }
        catch (InvalidOperationException ex)
        {
            return Result<object>.Failure(Error.Validation("Value", ex.Message));
        }
    }
}
