using FluentValidation.TestHelper;
using Discovery.Api.Validators;
using Discovery.Core.Cqrs.Tickets.Commands;

namespace Discovery.Tests;

/// <summary>
/// Regressão: o validator de AddTicketCommentCommand não deve exigir UserName/UserId,
/// pois esses campos são preenchidos pelo controller a partir do token autenticado
/// APÓS o model binding. A FluentValidation auto-validation roda antes do controller,
/// então qualquer regra sobre esses campos rejeita todo comentário com HTTP 400.
/// </summary>
public class AddTicketCommentCommandValidatorTests
{
    private static AddTicketCommentCommand ValidCommand() => new(
        TicketId: Guid.NewGuid(),
        Content: "Comentário de teste",
        IsInternal: false,
        UserId: null,      // preenchido pelo controller
        UserName: null     // preenchido pelo controller
    );

    private readonly AddTicketCommentCommandValidator _validator = new();

    [Test]
    public void Should_Pass_When_UserName_And_UserId_Are_Null()
    {
        var result = _validator.TestValidate(ValidCommand());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Test]
    public void Should_Fail_When_Content_Is_Empty()
    {
        var cmd = ValidCommand() with { Content = "" };
        var result = _validator.TestValidate(cmd);
        result.ShouldHaveValidationErrorFor(x => x.Content);
    }

    [Test]
    public void Should_Fail_When_Content_Is_Too_Short()
    {
        var cmd = ValidCommand() with { Content = "ab" };
        var result = _validator.TestValidate(cmd);
        result.ShouldHaveValidationErrorFor(x => x.Content);
    }

    [Test]
    public void Should_Fail_When_Content_Exceeds_MaxLength()
    {
        var cmd = ValidCommand() with { Content = new string('x', 4001) };
        var result = _validator.TestValidate(cmd);
        result.ShouldHaveValidationErrorFor(x => x.Content);
    }

    [Test]
    public void Should_Pass_When_Content_Is_At_MinLength()
    {
        var cmd = ValidCommand() with { Content = "abc" };
        var result = _validator.TestValidate(cmd);
        result.ShouldNotHaveAnyValidationErrors();
    }
}
