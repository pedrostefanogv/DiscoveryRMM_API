using FluentValidation;
using Meduza.Api.Controllers;

namespace Meduza.Api.Validators;

public class CreateClientRequestValidator : AbstractValidator<CreateClientRequest>
{
    public CreateClientRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().Length(2, 200);
        RuleFor(x => x.Notes).MaximumLength(2000);
    }
}

public class UpdateClientRequestValidator : AbstractValidator<UpdateClientRequest>
{
    public UpdateClientRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().Length(2, 200);
        RuleFor(x => x.Notes).MaximumLength(2000);
    }
}
