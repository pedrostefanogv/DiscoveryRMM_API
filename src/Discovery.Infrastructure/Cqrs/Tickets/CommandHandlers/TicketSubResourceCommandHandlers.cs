using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Tickets.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Tickets.CommandHandlers;

public sealed class AddTicketWatcherCommandHandler(ITicketWatcherRepository repo) : IRequestHandler<AddTicketWatcherCommand, Result<TicketWatcher>>
{ public async Task<Result<TicketWatcher>> Handle(AddTicketWatcherCommand cmd, CancellationToken ct) => Result<TicketWatcher>.Success(await repo.AddAsync(cmd.TicketId, cmd.UserId, cmd.AddedBy)); }

public sealed class RemoveTicketWatcherCommandHandler(ITicketWatcherRepository repo) : IRequestHandler<RemoveTicketWatcherCommand, Result<VoidResult>>
{ public async Task<Result<VoidResult>> Handle(RemoveTicketWatcherCommand cmd, CancellationToken ct) { await repo.RemoveAsync(cmd.TicketId, cmd.UserId); return Result<VoidResult>.Success(VoidResult.Value); } }

public sealed class CreateTicketRemoteSessionCommandHandler(ITicketRemoteSessionRepository repo) : IRequestHandler<CreateTicketRemoteSessionCommand, Result<TicketRemoteSession>>
{
    public async Task<Result<TicketRemoteSession>> Handle(CreateTicketRemoteSessionCommand cmd, CancellationToken ct)
    {
        var session = new TicketRemoteSession { TicketId = cmd.TicketId, AgentId = cmd.AgentId, MeshNodeId = cmd.MeshNodeId, StartedBy = cmd.StartedBy, Note = cmd.Note, StartedAt = DateTime.UtcNow };
        return Result<TicketRemoteSession>.Success(await repo.CreateAsync(session, ct));
    }
}

public sealed class EndTicketRemoteSessionCommandHandler(ITicketRemoteSessionRepository repo) : IRequestHandler<EndTicketRemoteSessionCommand, Result<TicketRemoteSession>>
{
    public async Task<Result<TicketRemoteSession>> Handle(EndTicketRemoteSessionCommand cmd, CancellationToken ct)
    {
        var sessions = await repo.GetByTicketAsync(cmd.TicketId, ct);
        var session = sessions.FirstOrDefault(s => s.Id == cmd.SessionId);
        if (session is null) return Result<TicketRemoteSession>.Failure(Error.NotFound("Remote session not found."));
        session.EndedAt = DateTime.UtcNow;
        return Result<TicketRemoteSession>.Success(await repo.UpdateAsync(session, ct));
    }
}

public sealed class CreateTicketAutomationLinkCommandHandler(ITicketAutomationLinkRepository repo) : IRequestHandler<CreateTicketAutomationLinkCommand, Result<TicketAutomationLink>>
{
    public async Task<Result<TicketAutomationLink>> Handle(CreateTicketAutomationLinkCommand cmd, CancellationToken ct)
    {
        var link = new TicketAutomationLink { TicketId = cmd.TicketId, AutomationTaskDefinitionId = cmd.AutomationTaskDefinitionId, RequestedBy = cmd.RequestedBy, Note = cmd.Note, RequestedAt = DateTime.UtcNow };
        return Result<TicketAutomationLink>.Success(await repo.CreateAsync(link, ct));
    }
}

public sealed class CreateTicketKnowledgeLinkCommandHandler(ITicketKnowledgeLinkRepository repo) : IRequestHandler<CreateTicketKnowledgeLinkCommand, Result<TicketKnowledgeLink>>
{
    public async Task<Result<TicketKnowledgeLink>> Handle(CreateTicketKnowledgeLinkCommand cmd, CancellationToken ct)
    {
        var link = new TicketKnowledgeLink { TicketId = cmd.TicketId, ArticleId = cmd.ArticleId, LinkedBy = cmd.AddedByUserId?.ToString(), Note = cmd.Note, LinkedAt = DateTime.UtcNow };
        return Result<TicketKnowledgeLink>.Success(await repo.CreateAsync(link, ct));
    }
}

public sealed class DeleteTicketKnowledgeLinkCommandHandler(ITicketKnowledgeLinkRepository repo) : IRequestHandler<DeleteTicketKnowledgeLinkCommand, Result<VoidResult>>
{ public async Task<Result<VoidResult>> Handle(DeleteTicketKnowledgeLinkCommand cmd, CancellationToken ct) { await repo.DeleteAsync(cmd.LinkId, ct); return Result<VoidResult>.Success(VoidResult.Value); } }
