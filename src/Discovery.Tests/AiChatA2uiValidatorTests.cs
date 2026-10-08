using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Regressão do erro visível "Não foi possível exibir a interface interativa
/// gerada.": o renderer (catálogo v0.9 estrito) rejeita referências que não são
/// IDs de componentes. Os payloads abaixo são REAIS (chat.db 2026-10-08 00:17Z),
/// quando o system prompt ainda ensinava `Button.child` com o rótulo.
/// </summary>
public class AiChatA2uiValidatorTests
{
    private static List<string> Lines(string content) =>
        content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    // Payload real que falhou: Button.child = "Clique aqui" (texto, não ID).
    private const string ProductionInvalidSurface = """
{"version":"v0.9","createSurface":{"surfaceId":"exemplo_a2ui","catalogId":"https://a2ui.org/specification/v0_9/basic_catalog.json"}}
{"version":"v0.9","updateComponents":{"surfaceId":"exemplo_a2ui","components":[{"id":"root","component":"Column","children":["titulo","botao"]},{"id":"titulo","component":"Text","text":"Exemplo"},{"id":"botao","component":"Button","child":"Clique aqui","action":{"event":{"name":"exemplo_clicado","context":{}}}}]}}
""";

    private const string ValidSurface = """
{"version":"v0.9","createSurface":{"surfaceId":"inventory_card","catalogId":"https://a2ui.org/specification/v0_9/basic_catalog.json"}}
{"version":"v0.9","updateComponents":{"surfaceId":"inventory_card","components":[{"id":"root","component":"Column","children":["title","installLabel","installBtn"]},{"id":"title","component":"Text","text":"# Inventário"},{"id":"installLabel","component":"Text","text":"Instalar"},{"id":"installBtn","component":"Button","child":"installLabel","action":{"event":{"name":"install_package","context":{"id":"Mozilla.Firefox"}}}}]}}
""";

    [Test]
    public void Validate_WhenButtonChildIsLabel_DropsWholeSurface()
    {
        var (valid, errors) = AiChatA2uiValidator.Validate(Lines(ProductionInvalidSurface));

        Assert.That(valid, Is.Empty, "a surface inválida deve ser descartada por inteiro");
        Assert.That(errors, Is.Not.Empty);
        Assert.That(string.Join(" | ", errors), Does.Contain("não é um ID de componente"));
    }

    [Test]
    public void Validate_WhenSurfaceFollowsTheCatalog_KeepsMessages()
    {
        var (valid, errors) = AiChatA2uiValidator.Validate(Lines(ValidSurface));

        Assert.That(errors, Is.Empty);
        Assert.That(valid, Has.Count.EqualTo(2));
    }

    /// <summary>
    /// O EXEMPLO DO SYSTEM PROMPT (receita de template de chamado) precisa
    /// continuar válido: se ele quebrar, todo modelo que copiar o exemplo gera
    /// interface descartada.
    /// </summary>
    [Test]
    public void Validate_WhenPromptRecipeExample_KeepsMessages()
    {
        const string content = """
{"version":"v0.9","createSurface":{"surfaceId":"ticket_template_picker","catalogId":"https://a2ui.org/specification/v0_9/basic_catalog.json"}}
{"version":"v0.9","updateComponents":{"surfaceId":"ticket_template_picker","components":[{"id":"root","component":"Column","children":["title","picker","submitLabel","submit"]},{"id":"title","component":"Text","text":"Escolha um modelo de chamado","variant":"h3"},{"id":"picker","component":"ChoicePicker","label":"Modelo","value":[],"options":[{"label":"Acesso / senha","value":"<templateId>"}]},{"id":"submitLabel","component":"Text","text":"Continuar"},{"id":"submit","component":"Button","child":"submitLabel","action":{"event":{"name":"template_selected","context":{}}}}]}}
""";

        var (valid, errors) = AiChatA2uiValidator.Validate(Lines(content));

        Assert.That(errors, Is.Empty);
        Assert.That(valid, Has.Count.EqualTo(2));
    }

    [Test]
    public void Validate_WhenComponentTypeIsUnknown_DropsSurface()
    {
        const string content = """
{"version":"v0.9","createSurface":{"surfaceId":"s","catalogId":"https://a2ui.org/specification/v0_9/basic_catalog.json"}}
{"version":"v0.9","updateComponents":{"surfaceId":"s","components":[{"id":"root","component":"Column","children":["status"]},{"id":"status","component":"StatusBar","text":"ok"}]}}
""";

        var (valid, errors) = AiChatA2uiValidator.Validate(Lines(content));

        Assert.That(valid, Is.Empty);
        Assert.That(string.Join(" | ", errors), Does.Contain("tipo desconhecido"));
    }

    [Test]
    public void Validate_WhenDefinitionReferencesMissingComponent_DropsSurface()
    {
        const string content = """
{"version":"v0.9","createSurface":{"surfaceId":"s","catalogId":"https://a2ui.org/specification/v0_9/basic_catalog.json"}}
{"version":"v0.9","updateComponents":{"surfaceId":"s","components":[{"id":"root","component":"Column","children":["missing"]},{"id":"title","component":"Text","text":"x"}]}}
""";

        var (valid, errors) = AiChatA2uiValidator.Validate(Lines(content));

        Assert.That(valid, Is.Empty);
        Assert.That(string.Join(" | ", errors), Does.Contain("não existe na definição"));
    }

    [Test]
    public void Validate_WhenChildrenItemIsNotString_DropsSurface()
    {
        const string content = """
{"version":"v0.9","createSurface":{"surfaceId":"s","catalogId":"https://a2ui.org/specification/v0_9/basic_catalog.json"}}
{"version":"v0.9","updateComponents":{"surfaceId":"s","components":[{"id":"root","component":"Column","children":[123]},{"id":"a","component":"Text","text":"x"}]}}
""";

        var (valid, _) = AiChatA2uiValidator.Validate(Lines(content));

        Assert.That(valid, Is.Empty);
    }

    [Test]
    public void Validate_WhenIncrementalUpdateReferencesExistingComponent_KeepsMessages()
    {
        const string content = """
{"version":"v0.9","createSurface":{"surfaceId":"s","catalogId":"https://a2ui.org/specification/v0_9/basic_catalog.json"}}
{"version":"v0.9","updateComponents":{"surfaceId":"s","components":[{"id":"root","component":"Column","children":["a"]},{"id":"a","component":"Text","text":"x"}]}}
{"version":"v0.9","updateComponents":{"surfaceId":"s","components":[{"id":"b","component":"Text","text":"novo"}]}}
""";

        var (valid, errors) = AiChatA2uiValidator.Validate(Lines(content));

        Assert.That(errors, Is.Empty);
        Assert.That(valid, Has.Count.EqualTo(3));
    }

    /// <summary>
    /// Update incremental (sem root) pode referenciar componente definido em
    /// resposta anterior — não há como distinguir de um id inexistente aqui,
    /// então a validação de referência só vale para a DEFINIÇÃO COMPLETA.
    /// </summary>
    [Test]
    public void Validate_WhenIncrementalWithoutRoot_HasUnknownReference_IsPermissive()
    {
        // Sem createSurface: a surface veio de uma resposta anterior, então não
        // há como validar referências aqui.
        const string content = """
{"version":"v0.9","updateComponents":{"surfaceId":"s","components":[{"id":"btn","component":"Button","child":"someLabel","action":{"event":{"name":"x","context":{}}}}]}}
""";

        var (valid, _) = AiChatA2uiValidator.Validate(Lines(content));

        Assert.That(valid, Has.Count.EqualTo(1));
    }

    [Test]
    public void Validate_WhenCreatedSurfaceDefinesComponentsWithoutRoot_DropsSurface()
    {
        const string content = """
{"version":"v0.9","createSurface":{"surfaceId":"s","catalogId":"https://a2ui.org/specification/v0_9/basic_catalog.json"}}
{"version":"v0.9","updateComponents":{"surfaceId":"s","components":[{"id":"title","component":"Text","text":"sem root"}]}}
""";

        var (valid, errors) = AiChatA2uiValidator.Validate(Lines(content));

        Assert.That(valid, Is.Empty);
        Assert.That(string.Join(" | ", errors), Does.Contain("sem o componente raiz"));
    }

    // createSurface sozinho é válido: a definição pode chegar em outro turno.
    [Test]
    public void Validate_WhenCreateSurfaceOnly_IsKept()
    {
        const string content = """
{"version":"v0.9","createSurface":{"surfaceId":"s","catalogId":"https://a2ui.org/specification/v0_9/basic_catalog.json"}}
""";

        var (valid, errors) = AiChatA2uiValidator.Validate(Lines(content));

        Assert.That(errors, Is.Empty);
        Assert.That(valid, Has.Count.EqualTo(1));
    }

    [Test]
    public void Validate_WhenCreateSurfaceHasInlineComponents_DropsSurface()
    {
        const string content = """
{"version":"v0.9","createSurface":{"surfaceId":"s","catalogId":"https://a2ui.org/specification/v0_9/basic_catalog.json","components":[{"id":"root","component":"Column","children":["a"]},{"id":"a","component":"Text","text":"x"}]}}
""";

        var (valid, errors) = AiChatA2uiValidator.Validate(Lines(content));

        Assert.That(valid, Is.Empty);
        Assert.That(string.Join(" | ", errors), Does.Contain("createSurface não deve conter"));
    }

    [Test]
    public void Validate_WhenUpdateComponentsIsEmpty_DropsSurface()
    {
        const string content = """
{"version":"v0.9","createSurface":{"surfaceId":"s","catalogId":"https://a2ui.org/specification/v0_9/basic_catalog.json"}}
{"version":"v0.9","updateComponents":{"surfaceId":"s","components":[]}}
""";

        var (valid, errors) = AiChatA2uiValidator.Validate(Lines(content));

        Assert.That(valid, Is.Empty);
        Assert.That(string.Join(" | ", errors), Does.Contain("sem componentes"));
    }

    [Test]
    public void Validate_WhenEmpty_ReturnsEmpty()
    {
        var (valid, errors) = AiChatA2uiValidator.Validate(new List<string>());
        Assert.That(valid, Is.Empty);
        Assert.That(errors, Is.Empty);
        var (validNull, errorsNull) = AiChatA2uiValidator.Validate(null);
        Assert.That(validNull, Is.Empty);
        Assert.That(errorsNull, Is.Empty);
    }

    /// <summary>
    /// Select é componente PRÓPRIO do Discovery (dropdown real), registrado no
    /// bundle (components/Select.js) sob o MESMO catalogId do basic. Se ele não
    /// estiver em KnownComponents, o validador descarta a surface inteira e o
    /// dropdown nunca aparece.
    /// </summary>
    [Test]
    public void Validate_WhenSelectDropdown_KeepsMessages()
    {
        const string content = """
{"version":"v0.9","createSurface":{"surfaceId":"printer_picker","catalogId":"https://a2ui.org/specification/v0_9/basic_catalog.json"}}
{"version":"v0.9","updateComponents":{"surfaceId":"printer_picker","components":[{"id":"root","component":"Column","children":["title","printer","applyLabel","apply"]},{"id":"title","component":"Text","text":"Impressora padrão","variant":"h3"},{"id":"printer","component":"Select","label":"Impressora","value":"","placeholder":"Escolha...","options":[{"label":"Microsoft Print to PDF","value":"pdf"},{"label":"OneNote (Desktop)","value":"onenote"}]},{"id":"applyLabel","component":"Text","text":"Aplicar"},{"id":"apply","component":"Button","child":"applyLabel","action":{"event":{"name":"printer_selected","context":{}}}}]}}
""";

        var (valid, errors) = AiChatA2uiValidator.Validate(Lines(content));

        Assert.That(errors, Is.Empty);
        Assert.That(valid, Has.Count.EqualTo(2));
    }

    /// <summary>
    /// O schema do renderer é ESTRITO: Select/ChoicePicker sem 'options' e Text
    /// sem 'text' nem são criados — o componente sumia do card sem aviso. O
    /// validador descarta a surface e o usuário recebe o fallback.
    /// </summary>
    [Test]
    public void Validate_WhenSelectWithoutOptions_DropsSurface()
    {
        const string content = """
{"version":"v0.9","createSurface":{"surfaceId":"s","catalogId":"https://a2ui.org/specification/v0_9/basic_catalog.json"}}
{"version":"v0.9","updateComponents":{"surfaceId":"s","components":[{"id":"root","component":"Column","children":["sel"]},{"id":"sel","component":"Select","label":"Impressora"}]}}
""";

        var (valid, errors) = AiChatA2uiValidator.Validate(Lines(content));

        Assert.That(valid, Is.Empty);
        Assert.That(string.Join(" | ", errors), Does.Contain("propriedade obrigatória 'options'"));
    }

    [Test]
    public void Validate_WhenTextWithoutText_DropsSurface()
    {
        const string content = """
{"version":"v0.9","createSurface":{"surfaceId":"s","catalogId":"https://a2ui.org/specification/v0_9/basic_catalog.json"}}
{"version":"v0.9","updateComponents":{"surfaceId":"s","components":[{"id":"root","component":"Column","children":["t"]},{"id":"t","component":"Text","variant":"h3"}]}}
""";

        var (valid, _) = AiChatA2uiValidator.Validate(Lines(content));
        Assert.That(valid, Is.Empty);
    }
}