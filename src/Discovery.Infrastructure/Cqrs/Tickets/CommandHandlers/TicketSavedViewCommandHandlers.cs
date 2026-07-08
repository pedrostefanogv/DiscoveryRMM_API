using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Tickets.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Tickets.CommandHandlers;

public sealed class CreateTicketSavedViewCommandHandler(ITicketSavedViewRepository repo) : IRequestHandler<CreateTicketSavedViewCommand, Result<TicketSavedView>>
{
    public async Task<Result<TicketSavedView>> Handle(CreateTicketSavedViewCommand cmd, CancellationToken ct)
    {
        var v = new TicketSavedView { Id = Guid.NewGuid(), UserId = cmd.UserId, Name = cmd.Name, FilterJson = cmd.FilterJson ?? "{}", IsShared = cmd.IsShared, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        return Result<TicketSavedView>.Success(await repo.CreateAsync(v));
    }
}

public sealed class UpdateTicketSavedViewCommandHandler(ITicketSavedViewRepository repo) : IRequestHandler<UpdateTicketSavedViewCommand, Result<TicketSavedView>>
{
    public async Task<Result<TicketSavedView>> Handle(UpdateTicketSavedViewCommand cmd, CancellationToken ct)
    {
        var v = await repo.GetByIdAsync(cmd.Id);
        if (v is null) return Result<TicketSavedView>.Failure(Error.NotFound("Saved view not found."));
        if (cmd.Name is not null) v.Name = cmd.Name;
        if (cmd.FilterJson is not null) v.FilterJson = cmd.FilterJson;
        if (cmd.IsShared.HasValue) v.IsShared = cmd.IsShared.Value;
        v.UpdatedAt = DateTime.UtcNow;
        await repo.UpdateAsync(v);
        return Result<TicketSavedView>.Success(v);
    }
}

public sealed class DeleteTicketSavedViewCommandHandler(ITicketSavedViewRepository repo) : IRequestHandler<DeleteTicketSavedViewCommand, Result<VoidResult>>
{ public async Task<Result<VoidResult>> Handle(DeleteTicketSavedViewCommand cmd, CancellationToken ct) { await repo.DeleteAsync(cmd.Id); return Result<VoidResult>.Success(VoidResult.Value); } }
