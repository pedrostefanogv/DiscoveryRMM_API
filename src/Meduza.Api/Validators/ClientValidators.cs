using FluentValidation;
using Meduza.Api.Controllers;

namespace Meduza.Api.Validators;

public class CreateClientRequestValidator : AbstractValidator<CreateClientRequest>
{
    public CreateClientRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().Length(2, 200);
        RuleFor(x => x.Document)
            .Must(d => d is null || !string.IsNullOrWhiteSpace(d))
            .WithMessage("Document must not be blank.")
            .MaximumLength(50);
        RuleFor(x => x.Email).EmailAddress().When(x => x.Email is not null).MaximumLength(200);
        RuleFor(x => x.Phone).MaximumLength(50);
        RuleFor(x => x.Notes).MaximumLength(2000);
    }
}

public class UpdateClientRequestValidator : AbstractValidator<UpdateClientRequest>
{
    public UpdateClientRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().Length(2, 200);
        RuleFor(x => x.Document)
            .Must(d => d is null || !string.IsNullOrWhiteSpace(d))
            .WithMessage("Document must not be blank.")
            .MaximumLength(50);
        RuleFor(x => x.Email).EmailAddress().When(x => x.Email is not null).MaximumLength(200);
        RuleFor(x => x.Phone).MaximumLength(50);
        RuleFor(x => x.Notes).MaximumLength(2000);
    }
}
