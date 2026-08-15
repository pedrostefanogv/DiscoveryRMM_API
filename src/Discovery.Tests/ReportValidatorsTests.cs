using Discovery.Core.Cqrs.Reports.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Reports;

namespace Discovery.Tests;

/// <summary>
/// Testa a criação de templates de relatório via CreateReportTemplateCommandHandler
/// (substitui os antigos testes de validators FluentValidation, removidos junto com
/// os tipos CreateReportTemplateRequestValidator/CreateReportTemplateRequest).
/// </summary>
public class ReportValidatorsTests
{
    [Test]
    public void CreateTemplate_WhenDefaultFormatIsMarkdown_AcceptsRequest()
    {
        var repo = new FakeReportTemplateRepository();
        var handler = new CreateReportTemplateCommandHandler(repo);

        var result = handler.Handle(new CreateReportTemplateCommand(
            ClientId: null,
            Name: "Template markdown",
            Description: null,
            Instructions: null,
            ExecutionSchemaJson: null,
            DatasetType: (int)ReportDatasetType.AgentHardware,
            DefaultFormat: (int)ReportFormat.Markdown,
            LayoutJson: "{}",
            FiltersJson: null), CancellationToken.None).GetAwaiter().GetResult();

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.DefaultFormat, Is.EqualTo((int)ReportFormat.Markdown));
        Assert.That(result.Value.DatasetType, Is.EqualTo((int)ReportDatasetType.AgentHardware));
    }

    [Test]
    public void CreateTemplate_WhenPdfIsDefaultFormat_MapsPdf()
    {
        var repo = new FakeReportTemplateRepository();
        var handler = new CreateReportTemplateCommandHandler(repo);

        var result = handler.Handle(new CreateReportTemplateCommand(
            ClientId: null,
            Name: "Template pdf",
            Description: null,
            Instructions: null,
            ExecutionSchemaJson: null,
            DatasetType: (int)ReportDatasetType.AgentHardware,
            DefaultFormat: (int)ReportFormat.Pdf,
            LayoutJson: "{}",
            FiltersJson: null), CancellationToken.None).GetAwaiter().GetResult();

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.DefaultFormat, Is.EqualTo((int)ReportFormat.Pdf));
    }

    private sealed class FakeReportTemplateRepository : IReportTemplateRepository
    {
        public Task<ReportTemplate> CreateAsync(ReportTemplate template)
            => Task.FromResult(template);

        public Task<ReportTemplate?> GetByIdAsync(Guid id, Guid? clientId = null)
            => Task.FromResult<ReportTemplate?>(null);

        public Task<IReadOnlyList<ReportTemplate>> GetAllAsync(Guid? clientId = null, ReportDatasetType? datasetType = null, bool? isActive = true)
            => Task.FromResult<IReadOnlyList<ReportTemplate>>(Array.Empty<ReportTemplate>());

        public Task<IReadOnlyList<ReportTemplateHistory>> GetHistoryAsync(Guid templateId, int limit = 50)
            => Task.FromResult<IReadOnlyList<ReportTemplateHistory>>(Array.Empty<ReportTemplateHistory>());

        public Task UpdateAsync(ReportTemplate template) => Task.CompletedTask;

        public Task<bool> DeleteAsync(Guid id, Guid? clientId = null)
            => Task.FromResult(true);
    }
}

