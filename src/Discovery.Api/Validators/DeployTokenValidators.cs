using FluentValidation;
using Discovery.Api.Controllers;

namespace Discovery.Api.Validators;

public class CreateDeployTokenRequestValidator : AbstractValidator<CreateDeployTokenRequest>
{
    public CreateDeployTokenRequestValidator()
    {
        RuleFor(x => x.ClientId).NotEmpty();
        RuleFor(x => x.SiteId).NotEmpty();
        RuleFor(x => x.Description).MaximumLength(200);
        RuleFor(x => x.ExpiresInHours)
            .Must(h => !h.HasValue || h.Value is >= 1 and <= 720)
            .WithMessage("ExpiresInHours must be between 1 and 720.");

        RuleFor(x => x.Delivery)
            .Must(v =>
                string.IsNullOrWhiteSpace(v)
                || v.Equals("token", StringComparison.OrdinalIgnoreCase)
                || v.Equals("installer", StringComparison.OrdinalIgnoreCase)
                || v.Equals("full-installer", StringComparison.OrdinalIgnoreCase)
                || v.Equals("installer-full", StringComparison.OrdinalIgnoreCase)
                || v.Equals("offline", StringComparison.OrdinalIgnoreCase)
                || v.Equals("full", StringComparison.OrdinalIgnoreCase))
            .WithMessage("Delivery must be 'token', 'installer', or 'full-installer'.");
    }
}
