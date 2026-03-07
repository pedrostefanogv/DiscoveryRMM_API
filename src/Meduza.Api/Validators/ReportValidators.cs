using System.Text.Json;
using FluentValidation;
using Meduza.Api.Controllers;
using Meduza.Core.Configuration;
using Meduza.Core.Enums;
using Microsoft.Extensions.Options;

namespace Meduza.Api.Validators;

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
        RuleFor(x => x.DatasetType).IsInEnum();
        RuleFor(x => x.DefaultFormat)
            .Must(enabledFormats.Contains)
            .WithMessage($"DefaultFormat must be one of: {string.Join(", ", enabledFormats)}.");
        RuleFor(x => x.LayoutJson)
            .NotEmpty()
            .Must(ReportValidationHelpers.BeValidJson)
            .WithMessage("LayoutJson must be valid JSON.");
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
        RuleFor(x => x.DatasetType!.Value)
            .IsInEnum()
            .When(x => x.DatasetType.HasValue);
        RuleFor(x => x.DefaultFormat)
            .Must(format => !format.HasValue || enabledFormats.Contains(format.Value))
            .WithMessage($"DefaultFormat must be one of: {string.Join(", ", enabledFormats)}.");
        RuleFor(x => x.LayoutJson)
            .Must(layoutJson =>
                layoutJson is null ||
                (!string.IsNullOrWhiteSpace(layoutJson) && ReportValidationHelpers.BeValidJson(layoutJson)))
            .WithMessage("LayoutJson must be valid JSON.");
        RuleFor(x => x.FiltersJson)
            .Must(ReportValidationHelpers.BeValidJsonOrNull)
            .WithMessage("FiltersJson must be valid JSON when informed.");
        RuleFor(x => x.UpdatedBy).MaximumLength(256);
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
