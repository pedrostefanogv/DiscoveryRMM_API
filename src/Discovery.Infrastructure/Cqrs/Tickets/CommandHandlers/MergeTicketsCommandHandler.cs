using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Tickets.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Cqrs.Tickets.CommandHandlers;

/// <summary>
/// Handler para merge de tickets (consolida tickets fonte em um ticket alvo).
/// Move comentários e dados relevantes dos tickets fonte para o alvo,
/// e fecha os tickets fonte como "merged".
/// </summary>
public sealed class MergeTicketsCommandHandler(
    ITicketRepository ticketRepo,
    ILogger<MergeTicketsCommandHandler> logger
) : IRequestHandler<MergeTicketsCommand, Result<MergeTicketsResult>>
{
    public async Task<Result<MergeTicketsResult>> Handle(MergeTicketsCommand cmd, CancellationToken ct)
    {
        var target = await ticketRepo.GetByIdAsync(cmd.TargetTicketId);
        if (target is null)
            return Result<MergeTicketsResult>.Failure(Error.NotFound($"Target ticket {cmd.TargetTicketId} not found"));

        var mergedCount = 0;
        foreach (var sourceId in cmd.SourceTicketIds)
        {
            if (sourceId == cmd.TargetTicketId)
            {
                logger.LogWarning("Skipping self-merge: source ticket {SourceId} equals target {TargetId}", sourceId, cmd.TargetTicketId);
                continue;
            }

            var source = await ticketRepo.GetByIdAsync(sourceId);
            if (source is null)
            {
                logger.LogWarning("Source ticket {SourceId} not found, skipping merge", sourceId);
                continue;
            }

            // Merge: copiar comentários do source para o target
            var comments = await ticketRepo.GetCommentsAsync(sourceId);
            foreach (var comment in comments)
            {
                await ticketRepo.AddCommentAsync(new TicketComment
                {
                    Id = Guid.NewGuid(),
                    TicketId = cmd.TargetTicketId,
                    Author = $"[Merged from {sourceId}] {comment.Author}",
                    Content = comment.Content,
                    IsInternal = comment.IsInternal,
                    CreatedAt = comment.CreatedAt
                });
            }

            // Fechar ticket fonte como merged
            source.Description = $"**MERGED into {cmd.TargetTicketId}**\n\n" + source.Description;
            source.ClosedAt = DateTime.UtcNow;
            await ticketRepo.UpdateAsync(source);

            mergedCount++;
            logger.LogInformation("Merged ticket {SourceId} into {TargetId}", sourceId, cmd.TargetTicketId);
        }

        if (mergedCount == 0)
            return Result<MergeTicketsResult>.Failure(Error.Validation("SourceTicketIds", "No valid source tickets to merge"));

        return Result<MergeTicketsResult>.Success(new MergeTicketsResult(cmd.TargetTicketId, mergedCount));
    }
}
