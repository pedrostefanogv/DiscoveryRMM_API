using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Crud.Commands;

namespace Discovery.Core.Cqrs.Agents.Maintenance.Commands;

public sealed record SetAgentMaintenanceCommand(Guid AgentId, bool Enabled, string? Reason) : ICommand<Result<AgentDto>>;
