using FluentValidation;
using Discovery.Api.Controllers;

namespace Discovery.Api.Validators;

public class CreateWorkflowStateRequestValidator : AbstractValidator<CreateWorkflowStateRequest>
{
    public CreateWorkflowStateRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().Length(2, 100);
        RuleFor(x => x.Color).MaximumLength(20);
        RuleFor(x => x.SortOrder).GreaterThanOrEqualTo(0);
    }
}

public class UpdateStateRequestValidator : AbstractValidator<UpdateStateRequest>
{
    public UpdateStateRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().Length(2, 100);
        RuleFor(x => x.Color).MaximumLength(20);
        RuleFor(x => x.SortOrder).GreaterThanOrEqualTo(0);
    }
}

public class CreateWorkflowTransitionRequestValidator : AbstractValidator<CreateWorkflowTransitionRequest>
{
    public CreateWorkflowTransitionRequestValidator()
    {
        RuleFor(x => x.FromStateId).NotEmpty();
        RuleFor(x => x.ToStateId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().Length(2, 100);
    }
}
