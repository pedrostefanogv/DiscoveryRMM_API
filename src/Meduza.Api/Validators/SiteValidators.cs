using FluentValidation;
using Meduza.Api.Controllers;

namespace Meduza.Api.Validators;

public class CreateSiteRequestValidator : AbstractValidator<CreateSiteRequest>
{
    public CreateSiteRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().Length(2, 200);
        RuleFor(x => x.Notes).MaximumLength(2000);
    }
}

public class UpdateSiteRequestValidator : AbstractValidator<UpdateSiteRequest>
{
    public UpdateSiteRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().Length(2, 200);
        RuleFor(x => x.Notes).MaximumLength(2000);
    }
}
