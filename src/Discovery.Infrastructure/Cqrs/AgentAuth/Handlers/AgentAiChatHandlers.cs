using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.AiChat;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

public sealed class ChatSyncHandler(
    IAiChatService aiChat
) : IRequestHandler<ChatSyncCommand, Result<object>>
{
    public async Task<Result<object>> Handle(ChatSyncCommand cmd, CancellationToken ct)
    {
        var response = await aiChat.ProcessSyncAsync(
            cmd.AgentId,
            cmd.Message,
            TryParseNullableGuid(cmd.SessionId),
            cmd.ClientIp,
            cmd.MaxTokens,
            cmd.DepartmentId,
            ct);

        return Result<object>.Success(response);
    }

    private static Guid? TryParseNullableGuid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Guid.TryParse(value, out var guid) ? guid : null;
    }
}

public sealed class ChatAsyncHandler(
    IAiChatService aiChat
) : IRequestHandler<ChatAsyncCommand, Result<object>>
{
    public async Task<Result<object>> Handle(ChatAsyncCommand cmd, CancellationToken ct)
    {
        var jobId = await aiChat.ProcessAsyncAsync(
            cmd.AgentId,
            cmd.Message,
            TryParseNullableGuid(cmd.SessionId),
            cmd.MaxTokens,
            cmd.DepartmentId,
            ct);

        return Result<object>.Success(new { jobId, status = "accepted" });
    }

    private static Guid? TryParseNullableGuid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Guid.TryParse(value, out var guid) ? guid : null;
    }
}

public sealed class GetAiChatJobHandler(
    IAiChatService aiChat
) : IRequestHandler<GetAiChatJobQuery, Result<object>>
{
    public async Task<Result<object>> Handle(GetAiChatJobQuery q, CancellationToken ct)
    {
        var status = await aiChat.GetJobStatusAsync(q.JobId, q.AgentId, ct);
        return Result<object>.Success(status);
    }
}