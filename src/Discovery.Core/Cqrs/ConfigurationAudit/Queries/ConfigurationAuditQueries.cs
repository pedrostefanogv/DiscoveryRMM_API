using Discovery.Core.Cqrs;
using ConfigurationAuditEntity = Discovery.Core.Entities.ConfigurationAudit;

namespace Discovery.Core.Cqrs.ConfigurationAudit.Queries;

public sealed record GetRecentAuditChangesQuery(int Days = 90, int Limit = 1000) : IQuery<Result<IReadOnlyList<ConfigurationAuditEntity>>>;
public sealed record GetEntityAuditHistoryQuery(string EntityType, Guid EntityId, int Limit = 100) : IQuery<Result<IReadOnlyList<ConfigurationAuditEntity>>>;
public sealed record GetFieldAuditHistoryQuery(string EntityType, Guid EntityId, string FieldName) : IQuery<Result<IReadOnlyList<ConfigurationAuditEntity>>>;
public sealed record GetAuditChangesByUserQuery(string Username, int Limit = 100) : IQuery<Result<IReadOnlyList<ConfigurationAuditEntity>>>;
public sealed record GetAuditReportQuery(DateTime StartDate, DateTime EndDate) : IQuery<Result<IReadOnlyList<ConfigurationAuditEntity>>>;
