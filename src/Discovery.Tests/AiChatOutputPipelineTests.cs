using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Regressão do bug "a interface A2UI não apareceu no chat": os dois caminhos
/// de streaming sanitizavam o texto ANTES de extrair o bloco ```a2ui. Como o
/// sanitizador remove esses blocos (fallback), o extractor não encontrava nada
/// e nenhum chunk "a2ui" era emitido. Estes testes cobrem a COMPOSIÇÃO usada
/// pelo orchestrator (AiChatOutputPipeline), não só os helpers isolados.
/// </summary>
public class AiChatOutputPipelineTests
{
    // Conteúdo real observado em produção (chat.db, 2026-10-07 23:53Z): o LLM
    // respondeu com um card A2UI de exemplo. A mensagem cita "O que você está
    // vendo acima", ou seja, o card DEVERIA aparecer entre o texto e a lista.
    private const string ProductionContent =
        "Claro! Aqui vai um exemplo prático — um card interativo com ações " +
        "relacionadas ao que conversamos. É assim que a A2UI aparece renderizada no chat:\n\n" +
        "```a2ui\n" +
        "{\"version\":\"v0.9\",\"createSurface\":{\"surfaceId\":\"exemplo_a2ui\",\"catalogId\":\"https://a2ui.org/specification/v0_9/basic_catalog.json\"}}\n" +
        "{\"version\":\"v0.9\",\"updateComponents\":{\"surfaceId\":\"exemplo_a2ui\",\"components\":[{\"id\":\"root\",\"component\":\"Column\"}]}}\n" +
        "```\n\n" +
        "O que você está vendo acima:\n- **Título e descrição** em texto formatado\n";

    [Test]
    public void Process_WithRealA2uiResponse_EmitsMessagesAndKeepsSurroundingText()
    {
        var (clean, messages, leaksRemoved) = AiChatOutputPipeline.Process(ProductionContent);

        Assert.That(messages, Has.Count.EqualTo(2), "as duas mensagens A2UI do bloco devem ser emitidas");
        Assert.That(messages[0], Does.Contain("createSurface"));
        Assert.That(messages[1], Does.Contain("updateComponents"));
        Assert.That(clean, Does.Not.Contain("```a2ui"));
        Assert.That(clean, Does.Contain("O que você está vendo acima"));
        Assert.That(leaksRemoved, Is.False);
    }

    [Test]
    public void Process_WhenLeakAndA2uiCoexist_KeepsA2uiMessagesAndRemovesLeak()
    {
        const string content =
            "Vou executar isso.\n\n" +
            "```a2ui\n" +
            "{\"version\":\"v0.9\",\"createSurface\":{\"surfaceId\":\"s1\",\"catalogId\":\"basic\"}}\n" +
            "```\n" +
            "<｜DSML｜tool_calls>{\"name\":\"get_logs\"}</｜DSML｜tool_calls>\n";

        var (clean, messages, leaksRemoved) = AiChatOutputPipeline.Process(content);

        Assert.That(messages, Has.Count.EqualTo(1));
        Assert.That(leaksRemoved, Is.True);
        Assert.That(clean, Does.Not.Contain("DSML"));
        Assert.That(clean, Does.Not.Contain("a2ui"));
    }

    [Test]
    public void Process_WhenNoA2ui_KeepsContentUntouched()
    {
        const string content = "Resposta normal em **markdown**, sem interface.";

        var (clean, messages, leaksRemoved) = AiChatOutputPipeline.Process(content);

        Assert.That(clean, Is.EqualTo(content));
        Assert.That(messages, Is.Empty);
        Assert.That(leaksRemoved, Is.False);
    }

    /// <summary>
    /// Guarda de ordem: se alguém voltar a sanitizar ANTES de extrair, o bloco
    /// é apagado e nenhuma mensagem sobra. Este teste documenta exatamente a
    /// causa raiz — ele deve continuar passando (a ordem errada realmente perde
    /// o A2UI), sinalizando que o pipeline NÃO pode usar essa sequência.
    /// </summary>
    [Test]
    public void ReverseOrder_SanitizeBeforeExtract_LosesA2uiMessages()
    {
        var (sanitized, _) = AiChatLeakSanitizer.Sanitize(ProductionContent);
        var (_, messages) = AiChatA2uiExtractor.Extract(sanitized);

        Assert.That(messages, Is.Empty,
            "com a ordem invertida o bloco a2ui é removido pelo sanitizador e o A2UI se perde — é exatamente o bug corrigido");
    }

    /// <summary>
    /// Regressão do teste real (chat.db 2026-10-08 00:17Z): o modelo emitiu
    /// `Button.child` com o rótulo e o renderer falhava com "Component not
    /// found", mostrando "Não foi possível exibir a interface interativa".
    /// O pipeline deve DESCARTAR a interface (o texto continua) e reportar o
    /// motivo, em vez de enviar um card que o renderer rejeita.
    /// </summary>
    [Test]
    public void Process_WhenInterfaceWouldFailInRenderer_DropsItAndReportsReason()
    {
        const string content =
            "Claro! Aqui vai um exemplo simples de interface interativa:\n\n```a2ui\n" +
            "{\"version\":\"v0.9\",\"createSurface\":{\"surfaceId\":\"exemplo_a2ui\",\"catalogId\":\"https://a2ui.org/specification/v0_9/basic_catalog.json\"}}\n" +
            "{\"version\":\"v0.9\",\"updateComponents\":{\"surfaceId\":\"exemplo_a2ui\",\"components\":[{\"id\":\"root\",\"component\":\"Column\",\"children\":[\"titulo\",\"botao\"]},{\"id\":\"titulo\",\"component\":\"Text\",\"text\":\"Exemplo\"},{\"id\":\"botao\",\"component\":\"Button\",\"child\":\"Clique aqui\",\"action\":{\"event\":{\"name\":\"exemplo_clicado\",\"context\":{}}}}]}}\n" +
            "```\n\nO que você está vendo acima é um card.";

        var reasons = new List<string>();
        var (clean, messages, _) = AiChatOutputPipeline.Process(content, reasons.Add);

        Assert.That(messages, Is.Empty, "interface inválida não pode ser emitida");
        Assert.That(reasons, Is.Not.Empty);
        Assert.That(clean, Does.Not.Contain("```a2ui"));
        Assert.That(clean, Does.Contain("O que você está vendo acima"));
    }
}
