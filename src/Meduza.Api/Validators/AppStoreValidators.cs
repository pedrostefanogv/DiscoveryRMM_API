using FluentValidation;
using Meduza.Api.Controllers;
using Meduza.Core.Enums;

namespace Meduza.Api.Validators;

public class UpsertAppApprovalRuleRequestValidator : AbstractValidator<UpsertAppApprovalRuleRequest>
{
    public UpsertAppApprovalRuleRequestValidator()
    {
        RuleFor(x => x.PackageId)
            .NotEmpty()
            .MaximumLength(300);

        RuleFor(x => x.Reason)
            .MaximumLength(2000);

        RuleFor(x => x.ScopeId)
            .NotNull()
            .When(x => x.ScopeType is AppApprovalScopeType.Client or AppApprovalScopeType.Site or AppApprovalScopeType.Agent)
            .WithMessage("ScopeId is required for Client, Site and Agent scopes.");

        RuleFor(x => x.ScopeId)
            .Null()
            .When(x => x.ScopeType == AppApprovalScopeType.Global)
            .WithMessage("ScopeId must be null for Global scope.");
    }
}
