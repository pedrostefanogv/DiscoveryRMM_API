using FluentValidation;
using Discovery.Core.Cqrs.Tickets.Commands;

namespace Discovery.Api.Validators;

public class CreateTicketCommandValidator : AbstractValidator<CreateTicketCommand>
{
    public CreateTicketCommandValidator()
    {
        RuleFor(x => x.ClientId).NotEmpty();
        RuleFor(x => x.Title).NotEmpty().Length(3, 200);
        RuleFor(x => x.Description).NotEmpty().Length(3, 10000);
        RuleFor(x => x.Priority).IsInEnum();
        RuleFor(x => x.Category).MaximumLength(100);
    }
}

public class UpdateTicketCommandValidator : AbstractValidator<UpdateTicketCommand>
{
    public UpdateTicketCommandValidator()
    {
        // Title e Description só são validados quando enviados (PATCH parcial para transferência não os envia)
        RuleFor(x => x.Title)
            .Length(3, 200)
            .When(x => !string.IsNullOrEmpty(x.Title));
        RuleFor(x => x.Description)
            .Length(3, 10000)
            .When(x => !string.IsNullOrEmpty(x.Description));
        RuleFor(x => x.Priority).IsInEnum().When(x => x.Priority.HasValue);
        RuleFor(x => x.Category).MaximumLength(100);
    }
}

public class TransitionTicketStateCommandValidator : AbstractValidator<TransitionTicketStateCommand>
{
    public TransitionTicketStateCommandValidator()
    {
        RuleFor(x => x.TargetStateId).NotEmpty();
    }
}

public class AddTicketCommentCommandValidator : AbstractValidator<AddTicketCommentCommand>
{
    public AddTicketCommentCommandValidator()
    {
        RuleFor(x => x.UserName).NotEmpty().Length(2, 100);
        RuleFor(x => x.Content).NotEmpty().Length(3, 4000);
    }
}
