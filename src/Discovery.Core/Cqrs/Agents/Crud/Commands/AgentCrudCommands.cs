using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Agents.Crud.Commands;

public sealed record ApproveZeroTouchCommand(Guid AgentId) : ICommand<Result<AgentDto>>;
public sealed record CreateAgentCommand(string Name, Guid ClientId, Guid SiteId, Guid? DepartmentId, string? MacAddress, string? Notes) : ICommand<Result<AgentDto>>;
public sealed record UpdateAgentCommand(Guid Id, string? Name, Guid? SiteId, Guid? DepartmentId, string? MacAddress, string? Notes) : ICommand<Result<AgentDto>>;
public sealed record DeleteAgentCommand(Guid Id) : ICommand<Result<VoidResult>>;

public sealed record AgentDto(Guid Id, string Name, Guid ClientId, Guid SiteId, string Status, string? Version, string? MacAddress, DateTime CreatedAt, DateTime? LastSeenAt);