using FluentValidation;
using Meduza.Api.Controllers;
using Meduza.Core.Enums;

namespace Meduza.Api.Validators;

public class CreateAgentRequestValidator : AbstractValidator<CreateAgentRequest>
{
    public CreateAgentRequestValidator()
    {
        RuleFor(x => x.SiteId).NotEmpty();
        RuleFor(x => x.Hostname).NotEmpty().Length(2, 100);
        RuleFor(x => x.DisplayName).MaximumLength(100);
        RuleFor(x => x.OperatingSystem).MaximumLength(100);
        RuleFor(x => x.OsVersion).MaximumLength(100);
        RuleFor(x => x.AgentVersion).MaximumLength(100);
    }
}

public class UpdateAgentRequestValidator : AbstractValidator<UpdateAgentRequest>
{
    public UpdateAgentRequestValidator()
    {
        RuleFor(x => x.SiteId).NotEmpty();
        RuleFor(x => x.Hostname).NotEmpty().Length(2, 100);
        RuleFor(x => x.DisplayName).MaximumLength(100);
    }
}

public class SendCommandRequestValidator : AbstractValidator<SendCommandRequest>
{
    public SendCommandRequestValidator()
    {
        RuleFor(x => x.CommandType).IsInEnum();
        RuleFor(x => x.Payload).NotEmpty().MaximumLength(20000);
    }
}

public class CreateTokenRequestValidator : AbstractValidator<CreateTokenRequest>
{
    public CreateTokenRequestValidator()
    {
        RuleFor(x => x.Description).MaximumLength(200);
        RuleFor(x => x.ExpirationDays)
            .Must(d => !d.HasValue || d.Value is >= 1 and <= 3650)
            .WithMessage("ExpirationDays must be between 1 and 3650.");
    }
}
