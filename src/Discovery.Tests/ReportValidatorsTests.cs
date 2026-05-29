using Discovery.Api.Controllers;
using Discovery.Api.Validators;
using Discovery.Core.Configuration;
using Discovery.Core.Enums;
using Microsoft.Extensions.Options;

namespace Discovery.Tests;

public class ReportValidatorsTests
{
    [Test]
    public void CreateReportTemplateValidator_WhenDefaultFormatIsMarkdown_AcceptsRequest()
    {
        var validator = new CreateReportTemplateRequestValidator(
            Options.Create(new ReportingOptions { EnablePdf = false }));

        var request = new CreateReportTemplateRequest(
            Name: "Template markdown",
            Description: null,
            Instructions: null,
            ExecutionSchemaJson: null,
            DatasetType: ReportDatasetType.AgentHardware,
            DefaultFormat: ReportFormat.Markdown,
            LayoutJson: "{}",
            FiltersJson: null,
            CreatedBy: "tester");

        var result = validator.Validate(request);

        Assert.That(result.Errors, Is.Empty);
    }

    [Test]
    public void CreateReportTemplateValidator_WhenPdfIsDisabled_RejectsPdfDefaultFormat()
    {
        var validator = new CreateReportTemplateRequestValidator(
            Options.Create(new ReportingOptions { EnablePdf = false }));

        var request = new CreateReportTemplateRequest(
            Name: "Template pdf",
            Description: null,
            Instructions: null,
            ExecutionSchemaJson: null,
            DatasetType: ReportDatasetType.AgentHardware,
            DefaultFormat: ReportFormat.Pdf,
            LayoutJson: "{}",
            FiltersJson: null,
            CreatedBy: "tester");

        var result = validator.Validate(request);

        Assert.That(result.Errors.Select(error => error.ErrorMessage),
            Has.Some.Contains("DefaultFormat must be one of"));
    }
}
