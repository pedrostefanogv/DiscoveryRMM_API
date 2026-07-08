using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Jobs.Queries;

public sealed record ListBackgroundJobsQuery : IQuery<Result<IReadOnlyList<JobDto>>>;
public sealed record JobDto(string Name, string Group, string Status, DateTime? LastRun, DateTime? NextRun, string? LastError);
