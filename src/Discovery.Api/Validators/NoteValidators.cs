using FluentValidation;
using Discovery.Core.Cqrs.Notes.Commands;

namespace Discovery.Api.Validators;

/// <summary>
/// Valida o payload de criação de nota.
/// O conteúdo é obrigatório e deve ter ao menos 3 caracteres.
/// </summary>
public class CreateNoteRequestValidator : AbstractValidator<CreateNoteRequest>
{
    public CreateNoteRequestValidator()
    {
        RuleFor(x => x.Content)
            .NotEmpty().WithMessage("O conteúdo da nota é obrigatório.")
            .MinimumLength(3).WithMessage("O conteúdo da nota deve ter ao menos 3 caracteres.");
    }
}

/// <summary>
/// Valida o payload de edição de nota.
/// O conteúdo é opcional na edição, mas se enviado não pode ser vazio/em branco
/// e deve ter ao menos 3 caracteres.
/// </summary>
public class UpdateNoteRequestValidator : AbstractValidator<UpdateNoteRequest>
{
    public UpdateNoteRequestValidator()
    {
        RuleFor(x => x.Content)
            .NotEmpty().WithMessage("O conteúdo da nota não pode ser vazio.")
            .MinimumLength(3).WithMessage("O conteúdo da nota deve ter ao menos 3 caracteres.")
            .When(x => x.Content is not null);
    }
}