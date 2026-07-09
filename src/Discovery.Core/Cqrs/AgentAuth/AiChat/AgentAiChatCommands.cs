using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.AgentAuth.AiChat;

public sealed record ChatSyncCommand(Guid AgentId, string Message, string? SessionId, int? MaxTokens, Guid? DepartmentId, string? ClientIp) : ICommand<Result<object>>;
public sealed record ChatAsyncCommand(Guid AgentId, string Message, string? SessionId, int? MaxTokens, Guid? DepartmentId) : ICommand<Result<object>>;
public sealed record GetAiChatJobQuery(Guid AgentId, Guid JobId) : IQuery<Result<object>>;