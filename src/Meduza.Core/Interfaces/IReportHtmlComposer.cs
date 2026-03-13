using Meduza.Core.ValueObjects;

namespace Meduza.Core.Interfaces;

public interface IReportHtmlComposer
{
    string Compose(ReportRenderContext context, ReportQueryResult data);
}