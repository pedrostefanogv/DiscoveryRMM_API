using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Testes do sanitizador de vazamentos de tool calls (DSML, blocos ```json
/// com invokes, ações A2UI cruas) emitidos como TEXTO pelo LLM.
/// Contexto: o modelo (DeepSeek) às vezes emite tool calls na marcação DSML
/// nativa em vez de function call nativa — ver chat_logs.jsonl de 2026-08-30.
/// </summary>
public class AiChatLeakSanitizerTests
{
    [Test]
    public void Sanitize_WhenDsmlBlock_RemovesBlockKeepsUsefulText()
    {
        const string content = "Perfeito! Vou fazer um diagnóstico geral. Só um instante...\n\n" +
            "<｜DSML｜tool_invokes>\n<invoke name=\"get_inventory\">\n<parameter name=\"agentId\">abc</parameter>\n</invoke>\n</｜DSML｜tool_invokes>";

        var (clean, removed) = AiChatLeakSanitizer.Sanitize(content);

        Assert.That(removed, Is.True);
        Assert.That(clean, Does.Not.Contain("DSML"));
        Assert.That(clean, Does.Not.Contain("invoke"));
        Assert.That(clean, Does.Contain("diagnóstico geral"));
    }

    [Test]
    public void Sanitize_WhenDsmlWithAsciiPipe_RemovesBlock()
    {
        const string content = "Texto antes <|DSML|tool_invokes><invoke name=\"x\">y</invoke></|DSML|tool_invokes> texto depois";

        var (clean, removed) = AiChatLeakSanitizer.Sanitize(content);

        Assert.That(removed, Is.True);
        Assert.That(clean, Does.Not.Contain("DSML"));
        Assert.That(clean, Does.Contain("Texto antes"));
        Assert.That(clean, Does.Contain("texto depois"));
    }

    [Test]
    public void Sanitize_WhenJsonFenceWithInvokeArray_RemovesBlock()
    {
        const string content = "Vou buscar o contexto.\n\n```json\n[\n  {\"name\": \"memory.search\", \"arguments\": {\"query\": \"perfil\"}}\n]\n```\n\nVou olhar o inventário.";

        var (clean, removed) = AiChatLeakSanitizer.Sanitize(content);

        Assert.That(removed, Is.True);
        Assert.That(clean, Does.Not.Contain("memory.search"));
        Assert.That(clean, Does.Not.Contain("```json"));
        Assert.That(clean, Does.Contain("Vou buscar o contexto"));
        Assert.That(clean, Does.Contain("Vou olhar o inventário"));
    }

    [Test]
    public void Sanitize_WhenJsonFenceWithA2uiAction_RemovesBlock()
    {
        const string content = "Vou verificar o que sei sobre você.\n\n```json{\"version\":\"a2ui\",\"action\":\"search\",\"query\":\"perfil\"}```\n\nOlá! Como posso ajudar?";

        var (clean, removed) = AiChatLeakSanitizer.Sanitize(content);

        Assert.That(removed, Is.True);
        Assert.That(clean, Does.Not.Contain("a2ui"));
        Assert.That(clean, Does.Contain("Como posso ajudar"));
    }

    [Test]
    public void Sanitize_WhenLegitimateJsonFence_KeepsBlock()
    {
        const string content = "Aqui está um exemplo:\n\n```json\n{\"status\": \"ok\", \"count\": 3}\n```\n\nFim.";

        var (clean, removed) = AiChatLeakSanitizer.Sanitize(content);

        Assert.That(removed, Is.False);
        Assert.That(clean, Is.EqualTo(content));
    }

    [Test]
    public void Sanitize_WhenOnlyLeak_ReturnsEmpty()
    {
        const string content = "<｜DSML｜tool_invokes><invoke name=\"get_inventory\"></invoke></｜DSML｜tool_invokes>";

        var (clean, removed) = AiChatLeakSanitizer.Sanitize(content);

        Assert.That(removed, Is.True);
        Assert.That(clean, Is.Empty);
    }

    [Test]
    public void Sanitize_WhenNormalText_ReturnsUnchanged()
    {
        const string content = "Resposta normal com **markdown** e `código`.\n\n- item 1\n- item 2";

        var (clean, removed) = AiChatLeakSanitizer.Sanitize(content);

        Assert.That(removed, Is.False);
        Assert.That(clean, Is.EqualTo(content));
    }

    [Test]
    public void Sanitize_WhenOrphanDsmlTags_RemovesTags()
    {
        // Stream cortado no meio: só a tag de abertura chegou.
        const string content = "Texto visível.\n\n<｜DSML｜tool_invokes>\n<invoke name=\"get_inv";

        var (clean, removed) = AiChatLeakSanitizer.Sanitize(content);

        Assert.That(removed, Is.True);
        Assert.That(clean, Does.Not.Contain("DSML"));
        Assert.That(clean, Does.Contain("Texto visível"));
    }

    [Test]
    public void Sanitize_WhenEmpty_ReturnsUnchanged()
    {
        var (clean, removed) = AiChatLeakSanitizer.Sanitize(string.Empty);

        Assert.That(removed, Is.False);
        Assert.That(clean, Is.Empty);
    }
}
