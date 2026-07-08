using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Automation.Commands;

namespace Discovery.Core.Cqrs.Agents.Automation.Queries;

public sealed record GetAutomationExecutionsQuery(Guid AgentId, int Limit = 50) : IQuery<Result<IReadOnlyList<AutomationExecutionDto>>>;