using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

public class AiChatA2uiExtractorTests
{
    [Test]
    public void Extract_WhenNoA2uiBlock_ReturnsContentUnchangedAndNoMessages()
    {
        const string content = "Olá! Aqui está a resposta em markdown.\n\n**Negrito** e *itálico*.";

        var (clean, messages) = AiChatA2uiExtractor.Extract(content);

        Assert.That(clean, Is.EqualTo(content));
        Assert.That(messages, Is.Empty);
    }

    [Test]
    public void Extract_WhenSingleA2uiBlock_RemovesBlockAndReturnsMessages()
    {
        const string content = "Aqui está o inventário:\n\n```a2ui\n" +
            "{\"version\":\"v0.9\",\"createSurface\":{\"surfaceId\":\"inv\",\"catalogId\":\"basic\"}}\n" +
            "{\"version\":\"v0.9\",\"updateComponents\":{\"surfaceId\":\"inv\",\"components\":[]}}\n" +
            "```\n\nEspero que ajude!";

        var (clean, messages) = AiChatA2uiExtractor.Extract(content);

        Assert.That(messages, Has.Count.EqualTo(2));
        Assert.That(messages[0], Does.Contain("\"createSurface\""));
        Assert.That(messages[1], Does.Contain("\"updateComponents\""));
        // O bloco a2ui é removido do texto visível
        Assert.That(clean, Does.Not.Contain("```a2ui"));
        Assert.That(clean, Does.Not.Contain("createSurface"));
        Assert.That(clean, Does.Contain("Aqui está o inventário"));
        Assert.That(clean, Does.Contain("Espero que ajude!"));
    }

    [Test]
    public void Extract_WhenInvalidJsonInBlock_IgnoresInvalidLines()
    {
        const string content = "```a2ui\n" +
            "{\"version\":\"v0.9\",\"createSurface\":{\"surfaceId\":\"x\"}}\n" +
            "isto não é json\n" +
            "{\"semVerbo\":true}\n" +
            "```";

        var (_, messages) = AiChatA2uiExtractor.Extract(content);

        Assert.That(messages, Has.Count.EqualTo(1));
        Assert.That(messages[0], Does.Contain("\"createSurface\""));
    }

    [Test]
    public void Extract_WhenUnclosedBlock_ProcessesRemainingLines()
    {
        const string content = "```a2ui\n" +
            "{\"version\":\"v0.9\",\"createSurface\":{\"surfaceId\":\"x\"}}\n";

        var (_, messages) = AiChatA2uiExtractor.Extract(content);

        Assert.That(messages, Has.Count.EqualTo(1));
    }

    [Test]
    public void Extract_WhenMultipleBlocks_ReturnsAllMessages()
    {
        const string content = "```a2ui\n" +
            "{\"version\":\"v0.9\",\"createSurface\":{\"surfaceId\":\"a\"}}\n" +
            "```\ntexto\n" +
            "```a2ui\n" +
            "{\"version\":\"v0.9\",\"updateComponents\":{\"surfaceId\":\"a\"}}\n" +
            "```";

        var (_, messages) = AiChatA2uiExtractor.Extract(content);

        Assert.That(messages, Has.Count.EqualTo(2));
    }

    [Test]
    public void Extract_WhenNullOrEmpty_ReturnsEmpty()
    {
        var (clean1, m1) = AiChatA2uiExtractor.Extract(null!);
        var (clean2, m2) = AiChatA2uiExtractor.Extract("");

        Assert.That(clean1, Is.EqualTo(string.Empty));
        Assert.That(m1, Is.Empty);
        Assert.That(clean2, Is.EqualTo(string.Empty));
        Assert.That(m2, Is.Empty);
    }

    [Test]
    public void Extract_WhenPromptExample_ExtractsCreateSurfaceAndUpdateComponents()
    {
        // Exemplo do system prompt corrigido: createSurface SEM components
        // (que são ignorados pelo renderer) + updateComponents com o root.
        const string content = "Aqui está o inventário:\n\n```a2ui\n" +
            "{\"version\":\"v0.9\",\"createSurface\":{\"surfaceId\":\"inventory_card\",\"catalogId\":\"https://a2ui.org/specification/v0_9/basic_catalog.json\"}}\n" +
            "{\"version\":\"v0.9\",\"updateComponents\":{\"surfaceId\":\"inventory_card\",\"components\":[{\"id\":\"root\",\"component\":\"Column\",\"children\":[\"title\"]},{\"id\":\"title\",\"component\":\"Text\",\"text\":\"# Inventário\"}]}}\n" +
            "```\n\nEspero que ajude!";

        var (clean, messages) = AiChatA2uiExtractor.Extract(content);

        Assert.That(messages, Has.Count.EqualTo(2));
        Assert.That(messages[0], Does.Contain("\"createSurface\""));
        Assert.That(messages[0], Does.Contain("https://a2ui.org/specification/v0_9/basic_catalog.json"));
        // createSurface não deve conter components (são ignorados pelo renderer)
        Assert.That(messages[0], Does.Not.Contain("\"components\""));
        Assert.That(messages[1], Does.Contain("\"updateComponents\""));
        Assert.That(messages[1], Does.Contain("\"id\":\"root\""));
        // O bloco a2ui é removido do texto visível
        Assert.That(clean, Does.Not.Contain("```a2ui"));
        Assert.That(clean, Does.Contain("Aqui está o inventário"));
        Assert.That(clean, Does.Contain("Espero que ajude!"));
    }

    [Test]
    public void Extract_WhenUpdateComponentsMissesClosingBraces_RepairsAndRecoversComponents()
    {
        // Reprodução do payload REAL de 2026-10-08 (surface paper_jam_wizard6):
        // o LLM esqueceu de fechar o objeto dos botões "Voltar" e "Avançar" — a
        // linha updateComponents INTEIRA virava JSON inválido, o servidor a
        // descartava em silêncio e o card ficava preso em "Loading surface...".
        const string valid =
            """{"version":"v0.9","updateComponents":{"surfaceId":"w6","components":[{"id":"root","component":"Column","children":["nav"]},{"id":"nav","component":"Row","children":["v","a"]},{"id":"vLabel","component":"Text","text":"Voltar"},{"id":"v","component":"Button","child":"vLabel","action":{"event":{"name":"ui.prev","context":{"target":"/etapa"}}}},{"id":"aLabel","component":"Text","text":"Avancar"},{"id":"a","component":"Button","child":"aLabel","action":{"event":{"name":"ui.next","context":{"target":"/etapa"}}}}]}}""";
        // Um "}" a menos no botão Voltar (antes do próximo componente) e no
        // botão Avançar (antes do fechamento da lista).
        var broken = valid
            .Replace("\"}}}},{\"id\":\"aLabel\"", "\"}}},{\"id\":\"aLabel\"")
            .Replace("}}}]}}\"", "}}]}}\"");
        Assert.That(broken, Is.Not.EqualTo(valid), "o payload de teste precisa estar corrompido");
        Assert.That(() => System.Text.Json.JsonDocument.Parse(broken), Throws.Exception, "payload de teste deve ser JSON inválido");

        var content = "Card:\n\n```a2ui\n" +
            "{\"version\":\"v0.9\",\"createSurface\":{\"surfaceId\":\"w6\",\"catalogId\":\"basic\"}}\n" +
            broken + "\n```";

        var diagnostics = new List<string>();
        var (_, messages) = AiChatA2uiExtractor.Extract(content, diagnostics.Add);

        Assert.That(messages, Has.Count.EqualTo(2), "createSurface + updateComponents reparado");
        Assert.That(messages[1], Does.Contain("\"id\":\"root\""));
        Assert.That(diagnostics, Has.Some.Contains("reparada"));

        // O reparo precisa devolver TODOS os componentes e continuar válido no
        // validador do renderer (referências child/children são IDs existentes).
        var (validMessages, errors) = AiChatA2uiValidator.Validate(messages);
        Assert.That(errors, Is.Empty);
        Assert.That(validMessages, Has.Count.EqualTo(2));
        using var doc = System.Text.Json.JsonDocument.Parse(messages[1]);
        var components = doc.RootElement.GetProperty("updateComponents").GetProperty("components");
        Assert.That(components.GetArrayLength(), Is.EqualTo(6));
    }

    [Test]
    public void Extract_WhenLineIsUnrepairable_ReportsDiagnosticInsteadOfSilentDrop()
    {
        const string content = "texto\n\n```a2ui\n" +
            "{\"version\":\"v0.9\",\"createSurface\":{\"surfaceId\":\"x\"}}\n" +
            "isto não é json\n" +
            "```";

        var diagnostics = new List<string>();
        var (_, messages) = AiChatA2uiExtractor.Extract(content, diagnostics.Add);

        Assert.That(messages, Has.Count.EqualTo(1));
        Assert.That(diagnostics, Has.Some.Contains("descartada"));
    }

    [Test]
    public void Extract_WhenWizardOverflowsCap_KeepsCreateSurfaceAndRootDefinition()
    {
        // Bug da revisão de 2026-10-08: o teto era aplicado cegamente por ordem
        // e um wizard que emitia updateDataModel por etapa estourava o limite
        // ANTES do updateComponents — card vazio mesmo com JSON 100% válido.
        var block = new System.Text.StringBuilder();
        block.Append("```a2ui\n");
        block.Append("{\"version\":\"v0.9\",\"createSurface\":{\"surfaceId\":\"w\",\"catalogId\":\"basic\"}}\n");
        for (var i = 1; i <= 6; i++)
        {
            block.Append("{\"version\":\"v0.9\",\"updateDataModel\":{\"surfaceId\":\"w\",\"path\":\"/p")
                .Append(i).Append("\",\"value\":").Append(i).Append("}}\n");
        }
        block.Append("{\"version\":\"v0.9\",\"updateComponents\":{\"surfaceId\":\"w\",\"components\":[{\"id\":\"root\",\"component\":\"Column\",\"children\":[\"t\"]},{\"id\":\"t\",\"component\":\"Text\",\"text\":\"ok\"}]}}\n");
        block.Append("```\n");

        var diagnostics = new List<string>();
        var (_, messages) = AiChatA2uiExtractor.Extract(block.ToString(), diagnostics.Add);

        Assert.That(messages, Has.Count.EqualTo(AiChatA2uiExtractor.MaxA2uiMessagesPerResponse));
        Assert.That(messages, Has.Some.Contains("\"createSurface\""), "o createSurface precisa sobreviver ao teto");
        Assert.That(messages, Has.Some.Contains("\"id\":\"root\""), "a definição com root precisa sobreviver ao teto");
        Assert.That(diagnostics, Has.Some.Contains("teto"), "o descarte por teto precisa de diagnóstico");
    }

    [Test]
    public void SurfacesMissingDefinition_ReportsSurfaceCreatedWithoutRoot()
    {
        var messages = new List<string>
        {
            "{\"version\":\"v0.9\",\"createSurface\":{\"surfaceId\":\"w\",\"catalogId\":\"basic\"}}",
            "{\"version\":\"v0.9\",\"updateDataModel\":{\"surfaceId\":\"w\",\"path\":\"/etapa\",\"value\":1}}",
        };
        Assert.That(AiChatA2uiExtractor.SurfacesMissingDefinition(messages), Is.EqualTo(new[] { "w" }));

        messages.Add("{\"version\":\"v0.9\",\"updateComponents\":{\"surfaceId\":\"w\",\"components\":[{\"id\":\"root\",\"component\":\"Column\"}]}}");
        Assert.That(AiChatA2uiExtractor.SurfacesMissingDefinition(messages), Is.Empty);
    }

    [Test]
    public void Extract_WhenManyInvalidLines_LimitsDiagnostics()
    {
        // Um bloco corrompido com dezenas de linhas gerava dezenas de LogWarning
        // por turno; o diagnóstico é limitado a 3 + 1 resumo.
        var block = new System.Text.StringBuilder();
        block.Append("```a2ui\n");
        for (var i = 0; i < 10; i++) block.Append("linha invalida ").Append(i).Append('\n');
        block.Append("```\n");

        var diagnostics = new List<string>();
        AiChatA2uiExtractor.Extract(block.ToString(), diagnostics.Add);

        Assert.That(diagnostics, Has.Count.EqualTo(4), "3 diagnósticos + 1 resumo");
        Assert.That(diagnostics[^1], Does.Contain("omitidos"));
    }

    [Test]
    public void BuildIncompleteDiagnostic_ReportsSurfacesWithoutRootAndNullWhenComplete()
    {
        var messages = new List<string>
        {
            "{\"version\":\"v0.9\",\"createSurface\":{\"surfaceId\":\"w6\",\"catalogId\":\"basic\"}}",
            "{\"version\":\"v0.9\",\"updateDataModel\":{\"surfaceId\":\"w6\",\"path\":\"/etapa\",\"value\":1}}",
        };

        Assert.That(AiChatA2uiExtractor.BuildIncompleteDiagnostic(messages), Is.EqualTo("w6"));

        messages.Add("{\"version\":\"v0.9\",\"updateComponents\":{\"surfaceId\":\"w6\",\"components\":[{\"id\":\"root\",\"component\":\"Column\"}]}}");
        Assert.That(AiChatA2uiExtractor.BuildIncompleteDiagnostic(messages), Is.Null);
        Assert.That(AiChatA2uiExtractor.BuildIncompleteDiagnostic(null), Is.Null);
    }
}