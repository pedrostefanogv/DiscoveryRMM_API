using FluentValidation;
using Meduza.Api.Controllers;

namespace Meduza.Api.Validators;

public class CreateSiteRequestValidator : AbstractValidator<CreateSiteRequest>
{
    public CreateSiteRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().Length(2, 200);
        RuleFor(x => x.Address).MaximumLength(200);
        RuleFor(x => x.City).MaximumLength(100);
        RuleFor(x => x.State).MaximumLength(100);
        RuleFor(x => x.ZipCode).MaximumLength(20);
        RuleFor(x => x.Notes).MaximumLength(2000);
    }
}

public class UpdateSiteRequestValidator : AbstractValidator<UpdateSiteRequest>
{
    public UpdateSiteRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().Length(2, 200);
        RuleFor(x => x.Address).MaximumLength(200);
        RuleFor(x => x.City).MaximumLength(100);
        RuleFor(x => x.State).MaximumLength(100);
        RuleFor(x => x.ZipCode).MaximumLength(20);
        RuleFor(x => x.Notes).MaximumLength(2000);
    }
}
