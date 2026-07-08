using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Automation.Queries;
namespace Discovery.Core.Cqrs.Automation.Commands;
public sealed record CreateScriptCommand(string Name, string Description, string Type, string Content) : ICommand<Result<ScriptDetailDto>>;
public sealed record UpdateScriptCommand(Guid Id, string Name, string Description, string Content) : ICommand<Result<ScriptDetailDto>>;
public sealed record DeleteScriptCommand(Guid Id) : ICommand<Result<VoidResult>>;
public sealed record ExecuteScriptCommand(Guid ScriptId, Guid? AgentId) : ICommand<Result<VoidResult>>;
public sealed record CreateTaskCommand(string Name, Guid ScriptId, object? Schedule) : ICommand<Result<TaskDto>>;
public sealed record ExecuteTaskCommand(Guid TaskId) : ICommand<Result<VoidResult>>;
