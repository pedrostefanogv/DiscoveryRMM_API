using Meduza.Core.Enums;
using Meduza.Core.ValueObjects;

namespace Meduza.Core.Interfaces;

public interface IReportRenderer
{
    ReportFormat Format { get; }
    Task<ReportDocument> RenderAsync(ReportRenderContext context, ReportQueryResult data, CancellationToken cancellationToken = default);
}
