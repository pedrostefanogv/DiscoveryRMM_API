using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.AgentAuth.AiChat;

public sealed record ChatSyncCommand(string Message, string? SessionId, int? MaxTokens, Guid? DepartmentId, string? ClientIp) : ICommand<Result<object>>;
public sealed record ChatAsyncCommand(string Message, string? SessionId, int? MaxTokens, Guid? DepartmentId) : ICommand<Result<object>>;
public sealed record GetAiChatJobQuery(Guid JobId) : IQuery<Result<object>>;