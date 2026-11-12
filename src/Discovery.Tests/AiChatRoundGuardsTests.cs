using Discovery.Core.DTOs;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Cobertura das guardas de round introduzidas no incidente de 2026-11-12
/// (produção, modelo meta/muse-ember): teto de tools por round, guarda de
/// capacidade de function calling e detecção do livelock de reenvio.
///
/// Contexto medido: 58 schemas de tool eram enviados em 78 de 78 rodadas, sem
/// teto e sem `tool_choice`; e o agent reenviava o MESMO lote já respondido,
/// fazendo o modelo reemitir as mesmas chamadas (3 rounds idênticos, cada um
/// terminando em HTTP 400 "No function call found for function call output").
/// </summary>
public class AiChatRoundGuardsTests
{
    private static LlmTool Tool(string name) => new(name, "desc", new { type = "object" });

    private static AiModelInfo Model(params string[] supportedParameters) => new(
        Id: "meta/muse-ember",
        Name: "Muse Ember",
        Description: null,
        Provider: "Meta",
        Capabilities: [],
        InputModalities: ["text"],
        OutputModalities: ["text"],
        SupportedParameters: [.. supportedParameters],
        ContextLength: null,
        MaxCompletionTokens: null,
        Pricing: null,
        IsFree: false,
        IsRecommendedForChat: false,
        IsRecommendedForEmbedding: false,
        EmbeddingDimensions: null,
        IncompatibleReason: null);

    // ── CapToolsPerRound ─────────────────────────────────────────────────────

    [Test]
    public void CapToolsPerRound_AtOrBelowCeiling_IsUnchanged()
    {
        var tools = Enumerable.Range(0, AiChatHelpers.MaxToolsPerRound).Select(i => Tool($"t{i}")).ToList();

        var capped = AiChatHelpers.CapToolsPerRound(tools);

        Assert.That(capped, Is.SameAs(tools), "no limite não pode recortar nem realocar");
    }

    [Test]
    public void CapToolsPerRound_AboveCeiling_TrimsToCeiling()
    {
        var tools = Enumerable.Range(0, 58).Select(i => Tool($"t{i}")).ToList();

        var capped = AiChatHelpers.CapToolsPerRound(tools);

        Assert.That(capped.Count, Is.EqualTo(AiChatHelpers.MaxToolsPerRound));
    }

    /// <summary>
    /// knowledge_search/knowledge_list nunca saem pelo teto: cortá-las tiraria o
    /// caminho de resposta mais usado do produto.
    /// </summary>
    [Test]
    public void CapToolsPerRound_AlwaysKeepsKnowledgeTools()
    {
        // As essenciais entram no FIM da lista, além do teto.
        var tools = Enumerable.Range(0, 58).Select(i => Tool($"t{i}"))
            .Concat(AiChatHelpers.AlwaysKeptToolNames.Select(Tool))
            .ToList();

        var capped = AiChatHelpers.CapToolsPerRound(tools);

        Assert.That(capped.Count, Is.EqualTo(AiChatHelpers.MaxToolsPerRound));
        foreach (var essential in AiChatHelpers.AlwaysKeptToolNames)
            Assert.That(capped.Select(t => t.Name), Does.Contain(essential),
                $"'{essential}' é essencial e não pode sair pelo teto");
    }

    [Test]
    public void CapToolsPerRound_IsDeterministic()
    {
        var tools = Enumerable.Range(0, 58).Select(i => Tool($"t{i}")).ToList();

        var first = AiChatHelpers.CapToolsPerRound(tools).Select(t => t.Name).ToList();
        var second = AiChatHelpers.CapToolsPerRound(tools).Select(t => t.Name).ToList();

        Assert.That(second, Is.EqualTo(first),
            "o mesmo conjunto tem de produzir o mesmo recorte (cache de prompt do provedor)");
    }

    [Test]
    public void CapToolsPerRound_DoesNotDuplicateTools()
    {
        var tools = Enumerable.Range(0, 58).Select(i => Tool($"t{i}"))
            .Concat(AiChatHelpers.AlwaysKeptToolNames.Select(Tool))
            .ToList();

        var capped = AiChatHelpers.CapToolsPerRound(tools);

        Assert.That(capped.Select(t => t.Name).Distinct().Count(), Is.EqualTo(capped.Count),
            "a tool essencial já presente na entrada não pode entrar duas vezes");
    }

    // ── ResolveToolsAllowed ──────────────────────────────────────────────────

    [Test]
    public void ResolveToolsAllowed_ModelWithoutCatalogInfo_KeepsOffering()
    {
        Assert.That(AiChatHelpers.ResolveToolsAllowed(null), Is.True,
            "sem informação do catálogo mantém o comportamento (oferece)");
        Assert.That(AiChatHelpers.ResolveToolsAllowed(Model()), Is.True,
            "modelo sem parâmetros declarados mantém o comportamento");
    }

    [Test]
    public void ResolveToolsAllowed_ModelWithToolsSupport_IsAllowed()
    {
        Assert.That(AiChatHelpers.ResolveToolsAllowed(Model("tools", "tool_choice")), Is.True);
        Assert.That(AiChatHelpers.ResolveToolsAllowed(Model("TOOLS")), Is.True, "comparação é case-insensitive");
    }

    /// <summary>
    /// O caso que motivou a guarda: modelo que declara parâmetros e NÃO suporta
    /// tools (ex.: llama-3.1-70b) recebia 58 schemas e devolvia 400 todo round.
    /// </summary>
    [Test]
    public void ResolveToolsAllowed_ModelWithoutToolSupport_IsBlocked()
    {
        Assert.That(AiChatHelpers.ResolveToolsAllowed(Model("temperature", "max_tokens", "top_p")), Is.False,
            "modelo com capacidades declaradas e sem 'tools' não pode receber schemas");
    }

    // ── IsFullyRepeatedToolBatch ─────────────────────────────────────────────

    [Test]
    public void IsFullyRepeatedToolBatch_AllAlreadyAnswered_IsTrue()
    {
        Assert.That(AiChatHelpers.IsFullyRepeatedToolBatch(
            ["call_a_2", "call_b_2"], ["call_a_2", "call_b_2", "call_c_2"]), Is.True);
    }

    [Test]
    public void IsFullyRepeatedToolBatch_AnyNew_IsFalse()
    {
        Assert.That(AiChatHelpers.IsFullyRepeatedToolBatch(
            ["call_a_2", "call_novo_2"], ["call_a_2"]), Is.False,
            "um call_id novo significa trabalho novo — não é livelock");
    }

    [Test]
    public void IsFullyRepeatedToolBatch_EmptyBatch_IsFalse()
    {
        Assert.That(AiChatHelpers.IsFullyRepeatedToolBatch([], ["call_a_2"]), Is.False);
        Assert.That(AiChatHelpers.IsFullyRepeatedToolBatch(["", "  "], ["call_a_2"]), Is.False,
            "ids vazios não contam como lote");
    }

    [Test]
    public void IsFullyRepeatedToolBatch_NothingAnswered_IsFalse()
    {
        Assert.That(AiChatHelpers.IsFullyRepeatedToolBatch(["call_a_2"], []), Is.False);
    }
}
