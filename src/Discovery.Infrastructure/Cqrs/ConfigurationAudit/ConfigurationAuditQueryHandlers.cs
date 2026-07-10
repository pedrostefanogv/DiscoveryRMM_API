using ConfigurationAuditEntity = Discovery.Core.Entities.ConfigurationAudit;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.ConfigurationAudit.Queries;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.ConfigurationAudit;

public sealed class GetRecentAuditChangesQueryHandler(IConfigurationAuditService svc)
    : IRequestHandler<GetRecentAuditChangesQuery, Result<IReadOnlyList<ConfigurationAuditEntity>>>
{
    public async Task<Result<IReadOnlyList<ConfigurationAuditEntity>>> Handle(GetRecentAuditChangesQuery q, CancellationToken ct)
    {
        var changes = await svc.GetRecentChangesAsync(q.Days, q.Limit);
        return Result<IReadOnlyList<ConfigurationAuditEntity>>.Success(changes.ToList());
    }
}

public sealed class GetEntityAuditHistoryQueryHandler(IConfigurationAuditService svc)
    : IRequestHandler<GetEntityAuditHistoryQuery, Result<IReadOnlyList<ConfigurationAuditEntity>>>
{
    public async Task<Result<IReadOnlyList<ConfigurationAuditEntity>>> Handle(GetEntityAuditHistoryQuery q, CancellationToken ct)
    {
        var history = await svc.GetEntityHistoryAsync(q.EntityType, q.EntityId, q.Limit);
        return Result<IReadOnlyList<ConfigurationAuditEntity>>.Success(history.ToList());
    }
}

public sealed class GetFieldAuditHistoryQueryHandler(IConfigurationAuditService svc)
    : IRequestHandler<GetFieldAuditHistoryQuery, Result<IReadOnlyList<ConfigurationAuditEntity>>>
{
    public async Task<Result<IReadOnlyList<ConfigurationAuditEntity>>> Handle(GetFieldAuditHistoryQuery q, CancellationToken ct)
    {
        var history = await svc.GetFieldHistoryAsync(q.EntityType, q.EntityId, q.FieldName);
        return Result<IReadOnlyList<ConfigurationAuditEntity>>.Success(history.ToList());
    }
}

public sealed class GetAuditChangesByUserQueryHandler(IConfigurationAuditService svc)
    : IRequestHandler<GetAuditChangesByUserQuery, Result<IReadOnlyList<ConfigurationAuditEntity>>>
{
    public async Task<Result<IReadOnlyList<ConfigurationAuditEntity>>> Handle(GetAuditChangesByUserQuery q, CancellationToken ct)
    {
        var changes = await svc.GetChangesByUserAsync(q.Username, q.Limit);
        return Result<IReadOnlyList<ConfigurationAuditEntity>>.Success(changes.ToList());
    }
}

public sealed class GetAuditReportQueryHandler(IConfigurationAuditService svc)
    : IRequestHandler<GetAuditReportQuery, Result<IReadOnlyList<ConfigurationAuditEntity>>>
{
    public async Task<Result<IReadOnlyList<ConfigurationAuditEntity>>> Handle(GetAuditReportQuery q, CancellationToken ct)
    {
        var report = await svc.GetAuditReportAsync(q.StartDate, q.EndDate);
        return Result<IReadOnlyList<ConfigurationAuditEntity>>.Success(report.ToList());
    }
}
