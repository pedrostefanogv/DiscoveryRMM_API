using FluentValidation;
using Discovery.Api.Controllers;

namespace Discovery.Api.Validators;

public class SoftwareInventoryReportRequestValidator : AbstractValidator<SoftwareInventoryReportRequest>
{
    public SoftwareInventoryReportRequestValidator()
    {
        RuleFor(x => x.Software).NotNull();
        RuleForEach(x => x.Software!).SetValidator(new SoftwareInventoryItemRequestValidator())
            .When(x => x.Software is not null);
    }
}

public class SoftwareInventoryItemRequestValidator : AbstractValidator<SoftwareInventoryItemRequest>
{
    public SoftwareInventoryItemRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(300);
        RuleFor(x => x.Version).MaximumLength(120);
        RuleFor(x => x.Publisher).MaximumLength(300);
        RuleFor(x => x.InstallId).MaximumLength(1000);
        RuleFor(x => x.Serial).MaximumLength(1000);
        RuleFor(x => x.Source).MaximumLength(120);
    }
}
