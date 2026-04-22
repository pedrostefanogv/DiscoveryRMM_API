using FluentValidation;
using Discovery.Api.Controllers;

namespace Discovery.Api.Validators;

public class CreateTicketRequestValidator : AbstractValidator<CreateTicketRequest>
{
    public CreateTicketRequestValidator()
    {
        RuleFor(x => x.ClientId).NotEmpty();
        RuleFor(x => x.Title).NotEmpty().Length(3, 200);
        RuleFor(x => x.Description).NotEmpty().Length(3, 10000);
        RuleFor(x => x.Priority).IsInEnum();
        RuleFor(x => x.Category).MaximumLength(100);
    }
}

public class UpdateTicketRequestValidator : AbstractValidator<UpdateTicketRequest>
{
    public UpdateTicketRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().Length(3, 200);
        RuleFor(x => x.Description).NotEmpty().Length(3, 10000);
        RuleFor(x => x.Priority).IsInEnum();
        RuleFor(x => x.Category).MaximumLength(100);
    }
}

public class UpdateWorkflowStateRequestValidator : AbstractValidator<UpdateWorkflowStateRequest>
{
    public UpdateWorkflowStateRequestValidator()
    {
        RuleFor(x => x.WorkflowStateId).NotEmpty();
    }
}

public class AddCommentRequestValidator : AbstractValidator<AddCommentRequest>
{
    public AddCommentRequestValidator()
    {
        RuleFor(x => x.Author).NotEmpty().Length(2, 100);
        RuleFor(x => x.Content).NotEmpty().Length(3, 4000);
    }
}
