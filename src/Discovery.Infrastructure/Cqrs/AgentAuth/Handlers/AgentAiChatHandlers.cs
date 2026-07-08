using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.AiChat;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

public sealed class ChatSyncHandler() : IRequestHandler<ChatSyncCommand, Result<object>>
{ public Task<Result<object>> Handle(ChatSyncCommand cmd, CancellationToken ct) => Task.FromResult(Result<object>.Success(null!)); }

public sealed class ChatAsyncHandler() : IRequestHandler<ChatAsyncCommand, Result<object>>
{ public Task<Result<object>> Handle(ChatAsyncCommand cmd, CancellationToken ct) => Task.FromResult(Result<object>.Success(null!)); }

public sealed class GetAiChatJobHandler() : IRequestHandler<GetAiChatJobQuery, Result<object>>
{ public Task<Result<object>> Handle(GetAiChatJobQuery q, CancellationToken ct) => Task.FromResult(Result<object>.Success(null!)); }