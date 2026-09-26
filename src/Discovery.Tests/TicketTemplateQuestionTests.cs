using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;

namespace Discovery.Tests;

/// <summary>
/// Mini questionário do template de chamado: parse/serialização e validação das
/// respostas (obrigatoriedade, tipo, regex, opções e limites).
/// </summary>
public class TicketTemplateQuestionTests
{
    private static TicketTemplateQuestion Question(
        string key = "nome",
        string label = "Nome",
        CustomFieldDataType dataType = CustomFieldDataType.Text,
        bool required = false,
        string[]? options = null,
        string? regex = null,
        int? minLength = null,
        int? maxLength = null)
        => new(key, label, dataType, required, options ?? Array.Empty<string>(), regex,
            null, minLength, maxLength, null, null, null);

    [Test]
    public void Parse_ShouldIgnoreInvalidJsonAndIncompleteQuestions()
    {
        Assert.That(TicketTemplateQuestions.Parse("{invalido"), Is.Empty);
        Assert.That(TicketTemplateQuestions.Parse(null), Is.Empty);
        Assert.That(
            TicketTemplateQuestions.Parse("[{\"key\":\"\",\"label\":\"X\"}]"),
            Is.Empty);
    }

    [Test]
    public void Serialize_ShouldRoundTrip()
    {
        var json = TicketTemplateQuestions.Serialize(new[] { Question(regex: "^\\d{11}$") });
        var parsed = TicketTemplateQuestions.Parse(json);

        Assert.Multiple(() =>
        {
            Assert.That(parsed, Has.Count.EqualTo(1));
            Assert.That(parsed[0].Key, Is.EqualTo("nome"));
            Assert.That(parsed[0].ValidationRegex, Is.EqualTo("^\\d{11}$"));
        });
        Assert.That(TicketTemplateQuestions.Serialize(Array.Empty<TicketTemplateQuestion>()), Is.EqualTo("[]"));
    }

    [Test]
    public void ValidateDefinitions_ShouldRejectInvalidRegexOptionsDuplicatesAndRanges()
    {
        // Regex inválida cadastrada derrubava a criação do chamado (500) antes
        // de a definição ser validada ao salvar o template.
        var invalidRegex = new[] { Question(key: "cpf", label: "CPF", regex: "([") };
        Assert.That(TicketTemplateQuestions.ValidateDefinitions(invalidRegex), Is.Not.Empty);

        var dropdownWithoutOptions = new[]
        {
            Question(key: "sistema", label: "Sistema", dataType: CustomFieldDataType.Dropdown, options: Array.Empty<string>()),
        };
        Assert.That(TicketTemplateQuestions.ValidateDefinitions(dropdownWithoutOptions), Is.Not.Empty);

        var duplicated = new[]
        {
            Question(key: "nome", label: "Nome"),
            Question(key: "NOME", label: "Nome 2"),
        };
        Assert.That(TicketTemplateQuestions.ValidateDefinitions(duplicated), Is.Not.Empty);

        var badRange = new[] { Question(key: "idade", label: "Idade", minLength: 10, maxLength: 2) };
        Assert.That(TicketTemplateQuestions.ValidateDefinitions(badRange), Is.Not.Empty);

        var valid = new[]
        {
            Question(key: "nome", label: "Nome", required: true, minLength: 2, maxLength: 80),
            Question(key: "cpf", label: "CPF", regex: "^\\d{11}$"),
            Question(key: "sistema", label: "Sistema", dataType: CustomFieldDataType.Dropdown, options: new[] { "Interno" }),
        };
        Assert.That(TicketTemplateQuestions.ValidateDefinitions(valid), Is.Empty);
    }

    [Test]
    public void ValidateAnswers_ShouldNotThrowOnInvalidConfiguredRegex()
    {
        // Defesa em profundidade: mesmo com regex inválida (dado legado), a
        // validação devolve erro tratado em vez de exceção.
        var questions = new[] { Question(key: "cpf", label: "CPF", regex: "([") };
        var answers = new Dictionary<string, JsonElement>
        {
            ["cpf"] = JsonSerializer.SerializeToElement("123"),
        };

        Assert.That(() => TicketTemplateQuestions.ValidateAnswers(questions, answers), Throws.Nothing);
        Assert.That(TicketTemplateQuestions.ValidateAnswers(questions, answers), Is.Not.Empty);
    }

    [Test]
    public void ValidateAnswers_ShouldEnforceRequired()
    {
        var questions = new[] { Question(required: true) };
        var errors = TicketTemplateQuestions.ValidateAnswers(questions, null);

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0].Key, Is.EqualTo("nome"));
    }

    [Test]
    public void ValidateAnswers_ShouldEnforceTypeRegexAndOptions()
    {
        var questions = new[]
        {
            Question(key: "idade", label: "Idade", dataType: CustomFieldDataType.Integer),
            Question(key: "cpf", label: "CPF", regex: "^\\d{11}$"),
            Question(key: "sistema", label: "Sistema", dataType: CustomFieldDataType.Dropdown, options: new[] { "Interno", "Terceiros" }),
        };

        var answers = new Dictionary<string, JsonElement>
        {
            ["idade"] = JsonSerializer.SerializeToElement("abc"),
            ["cpf"] = JsonSerializer.SerializeToElement("123"),
            ["sistema"] = JsonSerializer.SerializeToElement("Inexistente"),
        };

        var errors = TicketTemplateQuestions.ValidateAnswers(questions, answers);
        Assert.That(errors, Has.Count.EqualTo(3));
    }

    [Test]
    public void ValidateAnswers_ShouldAcceptValidAnswers()
    {
        var questions = new[]
        {
            Question(key: "nome", label: "Nome", required: true, minLength: 2),
            Question(key: "email", label: "E-mail", regex: "^[^\\s@]+@[^\\s@]+$"),
            Question(key: "aceite", label: "Aceite", dataType: CustomFieldDataType.Boolean),
        };

        var answers = new Dictionary<string, JsonElement>
        {
            ["nome"] = JsonSerializer.SerializeToElement("Ana"),
            ["email"] = JsonSerializer.SerializeToElement("ana@empresa.com"),
            ["aceite"] = JsonSerializer.SerializeToElement(true),
        };

        Assert.That(TicketTemplateQuestions.ValidateAnswers(questions, answers), Is.Empty);
        Assert.That(
            TicketTemplateQuestions.ValidateAnswers(questions, new Dictionary<string, JsonElement> { ["nome"] = JsonSerializer.SerializeToElement("A") }),
            Is.Not.Empty);
    }
}
