using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.Tickets;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

public sealed class GetMyTicketsHandler() : IRequestHandler<GetMyTicketsQuery, Result<object>>
{ public Task<Result<object>> Handle(GetMyTicketsQuery q, CancellationToken ct) => Task.FromResult(Result<object>.Success(null!)); }

public sealed class GetMyTicketHandler() : IRequestHandler<GetMyTicketQuery, Result<object>>
{ public Task<Result<object>> Handle(GetMyTicketQuery q, CancellationToken ct) => Task.FromResult(Result<object>.Success(null!)); }

public sealed class CreateMyTicketHandler() : IRequestHandler<CreateMyTicketCommand, Result<object>>
{ public Task<Result<object>> Handle(CreateMyTicketCommand cmd, CancellationToken ct) => Task.FromResult(Result<object>.Success(null!)); }

public sealed class AddMyTicketCommentHandler() : IRequestHandler<AddMyTicketCommentCommand, Result<object>>
{ public Task<Result<object>> Handle(AddMyTicketCommentCommand cmd, CancellationToken ct) => Task.FromResult(Result<object>.Success(null!)); }

public sealed class GetMyTicketCommentsHandler() : IRequestHandler<GetMyTicketCommentsQuery, Result<object>>
{ public Task<Result<object>> Handle(GetMyTicketCommentsQuery q, CancellationToken ct) => Task.FromResult(Result<object>.Success(null!)); }

public sealed class UpdateMyTicketWorkflowStateHandler() : IRequestHandler<UpdateMyTicketWorkflowStateCommand, Result<object>>
{ public Task<Result<object>> Handle(UpdateMyTicketWorkflowStateCommand cmd, CancellationToken ct) => Task.FromResult(Result<object>.Success(null!)); }

public sealed class CloseAndRateMyTicketHandler() : IRequestHandler<CloseAndRateMyTicketCommand, Result<object>>
{ public Task<Result<object>> Handle(CloseAndRateMyTicketCommand cmd, CancellationToken ct) => Task.FromResult(Result<object>.Success(null!)); }