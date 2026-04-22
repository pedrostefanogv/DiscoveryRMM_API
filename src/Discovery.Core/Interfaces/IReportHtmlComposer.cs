using Discovery.Core.ValueObjects;

namespace Discovery.Core.Interfaces;

public interface IReportHtmlComposer
{
    string Compose(ReportRenderContext context, ReportQueryResult data);
}