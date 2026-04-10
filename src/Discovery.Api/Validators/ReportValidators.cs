using System.Text.Json;
using FluentValidation;
using Discovery.Api.Controllers;
using Discovery.Core.Configuration;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Microsoft.Extensions.Options;

namespace Discovery.Api.Validators;

internal static class ReportValidationHelpers
{

    public static bool BeValidJson(string json)
    {
        try
        {
            JsonDocument.Parse(json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool BeValidJsonOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return true;

        return BeValidJson(json);
    }

    public static HashSet<ReportFormat> GetEnabledFormats(ReportingOptions options)
    {
        var formats = new HashSet<ReportFormat>
        {
            ReportFormat.Xlsx,
            ReportFormat.Csv
        };

        if (options.EnablePdf)
            formats.Add(ReportFormat.Pdf);

        return formats;
    }
}

public class CreateReportTemplateRequestValidator : AbstractValidator<CreateReportTemplateRequest>
{
    public CreateReportTemplateRequestValidator(IOptions<ReportingOptions> options)
    {
        var enabledFormats = ReportValidationHelpers.GetEnabledFormats(options.Value);
        RuleFor(x => x.Name).NotEmpty().Length(2, 200);
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.Instructions).MaximumLength(4000);
        RuleFor(x => x.ExecutionSchemaJson)
            .Must(ReportValidationHelpers.BeValidJsonOrNull)
            .WithMessage("ExecutionSchemaJson must be valid JSON when informed.");
        RuleFor(x => x.DatasetType).IsInEnum();
        RuleFor(x => x.DefaultFormat)
            .Must(enabledFormats.Contains)
            .WithMessage($"DefaultFormat must be one of: {string.Join(", ", enabledFormats)}.");
        RuleFor(x => x.LayoutJson)
            .NotEmpty()
            .Must(layoutJson => ReportLayoutValidator.ValidateJson(layoutJson).Count == 0)
            .WithMessage(x => string.Join(" ", ReportLayoutValidator.ValidateJson(x.LayoutJson)));
        RuleFor(x => x.FiltersJson)
            .Must(ReportValidationHelpers.BeValidJsonOrNull)
            .WithMessage("FiltersJson must be valid JSON when informed.");
        RuleFor(x => x.CreatedBy).MaximumLength(256);
    }
}

public class UpdateReportTemplateRequestValidator : AbstractValidator<UpdateReportTemplateRequest>
{
    public UpdateReportTemplateRequestValidator(IOptions<ReportingOptions> options)
    {
        var enabledFormats = ReportValidationHelpers.GetEnabledFormats(options.Value);
        RuleFor(x => x.Name)
            .NotEmpty()
            .Length(2, 200)
            .When(x => x.Name is not null);
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.Instructions).MaximumLength(4000);
        RuleFor(x => x.ExecutionSchemaJson)
            .Must(ReportValidationHelpers.BeValidJsonOrNull)
            .WithMessage("ExecutionSchemaJson must be valid JSON when informed.");
        RuleFor(x => x.DatasetType!.Value)
            .IsInEnum()
            .When(x => x.DatasetType.HasValue);
        RuleFor(x => x.DefaultFormat)
            .Must(format => !format.HasValue || enabledFormats.Contains(format.Value))
            .WithMessage($"DefaultFormat must be one of: {string.Join(", ", enabledFormats)}.");
        RuleFor(x => x.LayoutJson)
            .Must(layoutJson =>
                layoutJson is null ||
                (!string.IsNullOrWhiteSpace(layoutJson) && ReportLayoutValidator.ValidateJson(layoutJson).Count == 0))
            .WithMessage(x => x.LayoutJson is null ? "" : string.Join(" ", ReportLayoutValidator.ValidateJson(x.LayoutJson)));
        RuleFor(x => x.FiltersJson)
            .Must(ReportValidationHelpers.BeValidJsonOrNull)
            .WithMessage("FiltersJson must be valid JSON when informed.");
        RuleFor(x => x.UpdatedBy).MaximumLength(256);
    }
}

public class PreviewReportRequestValidator : AbstractValidator<PreviewReportRequest>
{
    public PreviewReportRequestValidator(IOptions<ReportingOptions> options)
    {
        var enabledFormats = ReportValidationHelpers.GetEnabledFormats(options.Value);

        RuleFor(x => x)
            .Must(x => x.TemplateId.HasValue || x.Template is not null)
            .WithMessage("TemplateId or template draft must be informed for preview.");

        RuleFor(x => x.Format)
            .Must(format => !format.HasValue || enabledFormats.Contains(format.Value))
            .WithMessage($"Format must be one of: {string.Join(", ", enabledFormats)}.");

        RuleFor(x => x.FiltersJson)
            .Must(ReportValidationHelpers.BeValidJsonOrNull)
            .WithMessage("FiltersJson must be valid JSON when informed.");

        RuleFor(x => x.FileName)
            .MaximumLength(180);

        RuleFor(x => x.ResponseDisposition)
            .Must(value => string.IsNullOrWhiteSpace(value) || value.Equals("inline", StringComparison.OrdinalIgnoreCase) || value.Equals("attachment", StringComparison.OrdinalIgnoreCase))
            .WithMessage("ResponseDisposition must be 'inline' or 'attachment'.");

        RuleFor(x => x.PreviewMode)
            .Must(value => string.IsNullOrWhiteSpace(value) || value.Equals("document", StringComparison.OrdinalIgnoreCase) || value.Equals("html", StringComparison.OrdinalIgnoreCase))
            .WithMessage("PreviewMode must be 'document' or 'html'.");

        When(x => x.Template is not null, () =>
        {
            RuleFor(x => x.Template!.Name)
                .MaximumLength(200);
            RuleFor(x => x.Template!.Description)
                .MaximumLength(2000);
            RuleFor(x => x.Template!.Instructions)
                .MaximumLength(4000);
            RuleFor(x => x.Template!.ExecutionSchemaJson)
                .Must(ReportValidationHelpers.BeValidJsonOrNull)
                .WithMessage("Template.ExecutionSchemaJson must be valid JSON when informed.");
            RuleFor(x => x.Template!.LayoutJson)
                .Must(layoutJson => layoutJson is null || ReportLayoutValidator.ValidateJson(layoutJson).Count == 0)
                .WithMessage(x => x.Template?.LayoutJson is null ? "" : string.Join(" ", ReportLayoutValidator.ValidateJson(x.Template.LayoutJson)));
            RuleFor(x => x.Template!.FiltersJson)
                .Must(ReportValidationHelpers.BeValidJsonOrNull)
                .WithMessage("Template.FiltersJson must be valid JSON when informed.");
        });
    }
}

public class RunReportRequestValidator : AbstractValidator<RunReportRequest>
{
    public RunReportRequestValidator(IOptions<ReportingOptions> options)
    {
        var enabledFormats = ReportValidationHelpers.GetEnabledFormats(options.Value);
        RuleFor(x => x.TemplateId).NotEmpty();
        RuleFor(x => x.Format)
            .Must(format => !format.HasValue || enabledFormats.Contains(format.Value))
            .WithMessage($"Format must be one of: {string.Join(", ", enabledFormats)}.");
        RuleFor(x => x.FiltersJson)
            .Must(ReportValidationHelpers.BeValidJsonOrNull)
            .WithMessage("FiltersJson must be valid JSON when informed.");
        RuleFor(x => x.CreatedBy).MaximumLength(256);
        RuleFor(x => x.RunAsync).NotNull();
    }
}
