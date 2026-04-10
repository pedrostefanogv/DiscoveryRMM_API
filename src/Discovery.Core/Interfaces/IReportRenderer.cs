using Discovery.Core.Enums;
using Discovery.Core.ValueObjects;

namespace Discovery.Core.Interfaces;

public interface IReportRenderer
{
    ReportFormat Format { get; }
    Task<ReportDocument> RenderAsync(ReportRenderContext context, ReportQueryResult data, CancellationToken cancellationToken = default);
}
