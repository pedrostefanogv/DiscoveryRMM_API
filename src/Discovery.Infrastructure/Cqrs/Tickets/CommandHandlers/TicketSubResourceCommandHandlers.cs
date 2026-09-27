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

public sealed class SetTicketKnowledgeLinkFeedbackCommandHandler(ITicketKnowledgeLinkRepository repo)
    : IRequestHandler<SetTicketKnowledgeLinkFeedbackCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(SetTicketKnowledgeLinkFeedbackCommand cmd, CancellationToken ct)
    {
        var link = await repo.GetByTicketAndArticleAsync(cmd.TicketId, cmd.ArticleId, ct);
        if (link is null)
            return Result<VoidResult>.Failure(Error.NotFound($"Knowledge link for article {cmd.ArticleId} not found on ticket {cmd.TicketId}."));
        await repo.SetFeedbackAsync(link.Id, cmd.Useful, ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}
