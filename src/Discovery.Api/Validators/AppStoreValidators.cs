using FluentValidation;
using Discovery.Api.Controllers;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;

namespace Discovery.Api.Validators;

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

public class UpsertCustomAppCatalogPackageRequestValidator : AbstractValidator<UpsertCustomAppCatalogPackageRequest>
{
    public UpsertCustomAppCatalogPackageRequestValidator()
    {
        RuleFor(x => x.PackageId)
            .NotEmpty()
            .MaximumLength(300);

        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(500);

        RuleFor(x => x.Publisher)
            .MaximumLength(500);

        RuleFor(x => x.Version)
            .MaximumLength(100);

        RuleFor(x => x.IconUrl)
            .MaximumLength(2000);

        RuleFor(x => x.SiteUrl)
            .MaximumLength(2000);

        RuleFor(x => x.InstallCommand)
            .MaximumLength(1000);

        RuleFor(x => x.FileObjectKey)
            .MaximumLength(1000);

        RuleFor(x => x.FileBucket)
            .MaximumLength(200);

        RuleFor(x => x.FilePublicUrl)
            .MaximumLength(2000);

        RuleFor(x => x.FileContentType)
            .MaximumLength(200);

        RuleFor(x => x.FileChecksum)
            .MaximumLength(200);
    }
}
