using FluentValidation;
using Discovery.Api.Controllers;
using Discovery.Core.Enums;

namespace Discovery.Api.Validators;

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
    }
}

public class RegisterAgentInstallRequestValidator : AbstractValidator<RegisterAgentInstallRequest>
{
    public RegisterAgentInstallRequestValidator()
    {
        RuleFor(x => x.Hostname).NotEmpty().Length(2, 100);
        RuleFor(x => x.DisplayName).MaximumLength(100);
        RuleFor(x => x.OperatingSystem).MaximumLength(100);
        RuleFor(x => x.OsVersion).MaximumLength(100);
        RuleFor(x => x.AgentVersion).MaximumLength(100);
        RuleFor(x => x.MacAddress).MaximumLength(17);
    }
}
