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

public class TransferAgentRequestValidator : AbstractValidator<TransferAgentRequest>
{
    public TransferAgentRequestValidator()
    {
        RuleFor(x => x.TargetSiteId).NotEmpty().WithMessage("Target site ID is required.");
        RuleFor(x => x.Reason).MaximumLength(500).When(x => x.Reason is not null);
    }
}

public class BulkTransferAgentsRequestValidator : AbstractValidator<BulkTransferAgentsRequest>
{
    public BulkTransferAgentsRequestValidator()
    {
        RuleFor(x => x.AgentIds).NotNull().NotEmpty().WithMessage("At least one agent ID is required.");
        RuleFor(x => x.AgentIds).Must(ids => ids.Count <= 100).WithMessage("Maximum of 100 agents per bulk transfer.");
        RuleFor(x => x.TargetSiteId).NotEmpty().WithMessage("Target site ID is required.");
        RuleFor(x => x.Reason).MaximumLength(500).When(x => x.Reason is not null);
    }
}
