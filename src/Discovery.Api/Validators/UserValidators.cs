using Discovery.Core.Cqrs.Users.Commands;
using Discovery.Core.DTOs.Users;
using FluentValidation;

namespace Discovery.Api.Validators;

/// <summary>
/// Validação de payload dos endpoints de usuário (antes não havia nenhuma: campos vazios
/// ou maiores que a coluna chegavam ao banco e retornavam 500 em vez de 400).
/// A unicidade de login/e-mail continua sendo verificada nos handlers (409).
/// </summary>
public class CreateUserCommandValidator : AbstractValidator<CreateUserCommand>
{
    public CreateUserCommandValidator()
    {
        RuleFor(x => x.Login).NotEmpty().WithMessage("Informe o login.").MaximumLength(100);
        RuleFor(x => x.Email).NotEmpty().WithMessage("Informe o e-mail.")
            .EmailAddress().WithMessage("Informe um e-mail válido.").MaximumLength(256);
        RuleFor(x => x.FullName).NotEmpty().WithMessage("Informe o nome completo.").MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().WithMessage("Informe a senha inicial.");
    }
}

public class UpdateUserCommandValidator : AbstractValidator<UpdateUserCommand>
{
    public UpdateUserCommandValidator()
    {
        RuleFor(x => x.Login).NotEmpty().WithMessage("O login não pode ser vazio.")
            .MaximumLength(100).When(x => x.Login is not null);

        RuleFor(x => x.Email).NotEmpty().WithMessage("O e-mail não pode ser vazio.")
            .EmailAddress().WithMessage("Informe um e-mail válido.")
            .MaximumLength(256).When(x => x.Email is not null);

        RuleFor(x => x.FullName).NotEmpty().WithMessage("O nome completo não pode ser vazio.")
            .MaximumLength(256).When(x => x.FullName is not null);
    }
}

public class UpdateMyProfileDtoValidator : AbstractValidator<UpdateMyProfileDto>
{
    public UpdateMyProfileDtoValidator()
    {
        RuleFor(x => x.Email).NotEmpty().WithMessage("Informe o e-mail.")
            .EmailAddress().WithMessage("Informe um e-mail válido.").MaximumLength(256);
        RuleFor(x => x.FullName).NotEmpty().WithMessage("Informe o nome completo.").MaximumLength(256);
    }
}

/// <summary>
/// O mesmo DTO é usado na troca da própria senha (com senha atual) e no reset
/// administrativo (sem senha atual), por isso apenas a nova senha é validada aqui —
/// a política de complexidade é aplicada por IUserPasswordManagementService.
/// </summary>
public class ChangePasswordDtoValidator : AbstractValidator<ChangePasswordDto>
{
    public ChangePasswordDtoValidator()
    {
        RuleFor(x => x.NewPassword).NotEmpty().WithMessage("Informe a nova senha.");
    }
}
