using FluentValidation;
using Meduza.Core.DTOs.ApiTokens;

namespace Meduza.Api.Validators;

public class CreateApiTokenRequestDtoValidator : AbstractValidator<CreateApiTokenRequestDto>
{
    public CreateApiTokenRequestDtoValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(x => x.ExpiresAt)
            .Must(expiresAt => !expiresAt.HasValue || expiresAt.Value > DateTime.UtcNow)
            .WithMessage("ExpiresAt must be a future date/time when provided.");
    }
}
