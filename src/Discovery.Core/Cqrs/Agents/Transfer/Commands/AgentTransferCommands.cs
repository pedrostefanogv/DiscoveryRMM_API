using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Agents.Transfer.Commands;

public sealed record TransferAgentCommand(Guid AgentId, Guid TargetSiteId, Guid UserId) : ICommand<Result<AgentTransferDto>>;
public sealed record BulkTransferAgentsCommand(IReadOnlyList<Guid> AgentIds, Guid TargetSiteId, Guid UserId) : ICommand<Result<AgentTransferDto>>;
public sealed record ValidateAgentTransferQuery(Guid AgentId, Guid TargetSiteId) : IQuery<Result<AgentTransferDto>>;

public sealed record AgentTransferDto(bool Valid, string? Message, Guid? NewSiteId);