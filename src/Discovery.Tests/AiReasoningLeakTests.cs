using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/**
 * VAZAMENTO DE RACIOCINIO no chat (setima onda).
 *
 * O DEFEITO (medido no chat.db, turno 1046): a resposta chegou com 3890 chars, sendo
 * 2167 (56%) de CoT do modelo, MISTURADO no mesmo continuo de texto exibido.
 * Exemplo literal: '="memory.search" is not in my tool list. Actually I don't have
 * memory.search tool available... Let me check memory_list maybe?'
 *
 * CAUSA: o modelo em producao (deepseek/deepseek-v4.1-flash-1017) tem
 * reasoning.default_enabled = TRUE no catalogo do OpenRouter - ele PENSA por padrao.
 * O app nao enviava controle explicito (ReasoningEnabled default false), entao o
 * provedor decidiu a favor de devolver o CoT junto do texto.
 *
 * AS DUAS CAMADAS DA CORRECAO - e a distincao importa:
 *   CAMADA 1 (AddReasoningToPayload -> reasoning.exclude=true): e a CORRECAO DO BUG.
 *     O app passa a pedir explicitamente que o raciocinio NAO volte na resposta, sem
 *     desliga-lo (o raciocinio ajuda no diagnostico de logs - decisao do usuario).
 *   CAMADA 2 (TryGetReasoningDelta + descarte no parser): DEFESA EM PROFUNDIDADE.
 *     Garante que, se o provedor SEPARAR o CoT em delta.reasoning, ele nunca vire um
 *     evento "token" visivel. Ela NAO conserta o vazamento medido - ela protege o futuro.
 *
 * NAO ha teste heuristico por palavras-chave ("Let me", "I'll"): decisao explicita do
 * Lead. Heuristica em producao gera falso positivo.
 */
[TestFixture]
public class AiReasoningLeakTests
{
    // ── Fixtures das linhas SSE (o parser recebe o JSON ja sem o prefixo "data: ") ──

    /// <summary>Chunk de stream no shape do provedor OpenAI-compativel/OpenRouter.</summary>
    private static string Chunk(string deltaJson, string? finishReason = null, string? extraChoice = null) =>
        finishReason is null
            ? "{\"choices\":[{\"index\":0,\"delta\":" + deltaJson + (extraChoice ?? "") + "}]}"
            : "{\"choices\":[{\"index\":0,\"delta\":" + deltaJson + ",\"finish_reason\":\"" + finishReason + "\"" + (extraChoice ?? "") + "}]}";

    private const string CotText =
        "=\"memory.search\" is not in my tool list. Actually I don't have memory.search tool available. "
        + "Let me check memory_list maybe? I'll respond as if the user asked for a general health check.";
    private static readonly Dictionary<int, (string Id, string Name, StringBuilder Args)> NoPending = new();

    private static LlmStreamEvent? Parse(string data) =>
        OpenAiProvider.ParseStreamChunk(data, new Dictionary<int, (string Id, string Name, StringBuilder Args)>());

    // ── ITEM 1 (CAMADA 2): raciocinio NUNCA vira token visivel ──────────────

    [Test]
    public void ReasoningOnlyChunk_NeverProducesAVisibleToken()
    {
        // Se o provedor separar o CoT em delta.reasoning, ele NAO pode virar token.
        var reasoningOnly = "{\"reasoning\":\"" + CotText.Replace("\"", "\\\"") + "\"}";

        var evt = Parse(Chunk(reasoningOnly));

        Assert.Multiple(() =>
        {
            Assert.That(evt?.Type, Is.Not.EqualTo("token"), "raciocinio NAO pode virar token exibido");
            Assert.That(evt?.Content, Is.Null.Or.Not.Contains("memory.search"),
                "o texto do CoT nao pode aparecer em nenhum evento");
            Assert.That(evt, Is.Null, "sem content, o chunk vira null (nada a exibir)");
        });
    }

    [Test]
    public void ReasoningContentChunk_VariantKeyAlsoNeverProducesAToken()
    {
        // Variante usada por alguns gateways compativeis.
        var evt = Parse(Chunk("{\"reasoning_content\":\"thinking...\"}"));

        Assert.That(evt?.Type, Is.Not.EqualTo("token"), "reasoning_content tambem e descartado");
    }

    [Test]
    public void ReasoningWithFinishReason_DoesNotLoseTheFinish()
    {
        // O descarte do reasoning NAO pode engolir o finish_reason: o round precisa encerrar.
        var evt = Parse(Chunk("{\"reasoning\":\"pensando\"}", finishReason: "stop"));

        Assert.Multiple(() =>
        {
            Assert.That(evt?.Type, Is.EqualTo("done"), "o finish_reason continua funcionando");
            Assert.That(evt?.FinishReason, Is.EqualTo("stop"));
        });
    }

    // ── ITEM 2: chunk com content E reasoning (o caso realista) ─────────────

    [Test]
    public void ChunkWithContentAndReasoning_EmitsOnlyTheContentToken()
    {
        // O provedor pode mandar os dois no MESMO chunk. So o content pode sair.
        var evt = Parse(Chunk("{\"content\":\"Resposta visivel.\",\"reasoning\":\"CoT interno que nao pode aparecer\"}"));

        Assert.Multiple(() =>
        {
            Assert.That(evt, Is.Not.Null);
            Assert.That(evt!.Type, Is.EqualTo("token"));
            Assert.That(evt.Content, Is.EqualTo("Resposta visivel."), "so o content sai");
            Assert.That(evt.Content, Does.Not.Contain("CoT interno"), "o raciocinio nao se mistura ao texto");
        });
    }

    [Test]
    public void ChunkWithReasoningBeforeContentInJson_StillEmitsOnlyContent()
    {
        // Ordem das chaves no JSON nao pode importar.
        var evt = Parse(Chunk("{\"reasoning\":\"CoT\",\"content\":\"Texto\"}"));

        Assert.Multiple(() =>
        {
            Assert.That(evt!.Type, Is.EqualTo("token"));
            Assert.That(evt.Content, Is.EqualTo("Texto"));
        });
    }

    [Test]
    public void EmptyReasoningWithContent_StillEmitsTheContent()
    {
        var evt = Parse(Chunk("{\"reasoning\":\"\",\"content\":\"ok\"}"));

        Assert.That(evt?.Content, Is.EqualTo("ok"));
    }
    // ── ITEM 3 (CAMADA 1 - A CORRECAO DO BUG): payload de reasoning ───────

    private static LlmOptions OpenRouterOptions(string? effort = null) => new(
        MaxTokens: 2000, Temperature: 0.3, Model: "deepseek/deepseek-v4.1-flash-1017",
        BaseUrl: "https://openrouter.ai/api/v1", Provider: AIIntegrationSettings.ProviderOpenRouter,
        ReasoningEffort: effort);

    private static LlmOptions OpenAiOptions() => new(
        MaxTokens: 2000, Temperature: 0.3, Model: "gpt-4o-mini",
        BaseUrl: AIIntegrationSettings.OpenAiDefaultBaseUrl, Provider: "openai");

    private static Dictionary<string, object?> BuildPayload(LlmOptions options, bool includeTools = false, bool stream = true) =>
        OpenAiProvider.BuildChatPayload(options, new List<object> { new { role = "user", content = "oi" } }, includeTools, stream);

    [Test]
    public void Payload_SendsReasoningWithExcludeTrue_ToOpenRouter()
    {
        // A CORRECAO: pedir explicitamente que o raciocinio NAO volte na resposta.
        // Sem "exclude", o modelo (default_enabled=true) devolve o CoT junto do texto.
        var payload = BuildPayload(OpenRouterOptions());

        Assert.That(payload.ContainsKey("reasoning"), Is.True,
            "o payload precisa enviar o controle de reasoning SEMPRE (a ausencia da chave era a causa do bug)");

        var reasoning = AsDict(payload["reasoning"]);
        Assert.Multiple(() =>
        {
            Assert.That(reasoning["exclude"], Is.EqualTo(true),
                "reasoning.exclude=true e o campo que impede o CoT de voltar (doc oficial do OpenRouter)");
            Assert.That(reasoning.ContainsKey("effort"), Is.True, "o esforco continua sendo enviado (raciocinio ATIVO)");
            Assert.That(reasoning["effort"], Is.EqualTo(OpenAiProvider.DefaultReasoningEffort));
            Assert.That(payload.ContainsKey("include_reasoning"), Is.False,
                "include_reasoning e legado; o shape atual usa reasoning.exclude");
        });
    }

    [Test]
    public void Payload_ReasoningIsSentEvenWhenReasoningEnabledIsFalse()
    {
        // O DEFAULT da config era false e o app nao mandava NADA - foi assim que o
        // provedor passou a devolver o CoT (turno 1046). O controle tem que ser enviado
        // INDEPENDENTE da flag: quem decide exibir e o "exclude", nao o "enabled".
        var payload = BuildPayload(OpenRouterOptions() with { ReasoningEnabled = false });

        Assert.Multiple(() =>
        {
            Assert.That(payload.ContainsKey("reasoning"), Is.True, "enviado mesmo com a flag false");
            Assert.That(AsDict(payload["reasoning"])["exclude"], Is.EqualTo(true));
        });
    }

    [Test]
    public void Payload_KeepsReasoningActive_NeverUsesEffortNone()
    {
        // DECISAO DO USUARIO: parar de EXIBIR, nao de PENSAR. "none" desligaria o
        // raciocinio, que ajuda no diagnostico de logs.
        var payload = BuildPayload(OpenRouterOptions());
        var reasoning = AsDict(payload["reasoning"]);

        Assert.Multiple(() =>
        {
            Assert.That(reasoning["effort"], Is.Not.EqualTo("none"), "o raciocinio NAO pode ser desligado");
            Assert.That(reasoning["effort"], Is.EqualTo("high"), "default medido do modelo em producao");
        });
    }

    [Test]
    public void Payload_HonorsConfiguredEffort_AndFallsBackForUnsupportedValues()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AsDict(BuildPayload(OpenRouterOptions("low"))["reasoning"])["effort"], Is.EqualTo("low"));
            Assert.That(AsDict(BuildPayload(OpenRouterOptions("MAX"))["reasoning"])["effort"], Is.EqualTo("max"),
                "case-insensitive e normalizado");
            // Valor invalido (texto livre vindo da UI) NAO pode virar 400 no provedor.
            Assert.That(AsDict(BuildPayload(OpenRouterOptions("turbo"))["reasoning"])["effort"],
                Is.EqualTo(OpenAiProvider.DefaultReasoningEffort), "valor nao suportado cai no default");
        });
    }

    [Test]
    public void Payload_ForNonOpenRouterProvider_HasNoReasoningKey()
    {
        // Os demais provedores nao conhecem esses campos e devolveriam 400.
        var payload = BuildPayload(OpenAiOptions());

        Assert.That(payload.ContainsKey("reasoning"), Is.False,
            "reasoning e exclusivo do OpenRouter");
    }

    private static Dictionary<string, object?> AsDict(object? value)
    {
        Assert.That(value, Is.Not.Null, "o objeto reasoning precisa existir");
        return value switch
        {
            Dictionary<string, object?> dict => dict,
            _ => throw new AssertionException($"reasoning deveria ser um Dictionary, mas veio {value!.GetType().Name}"),
        };
    }
    // ── ITEM 4: REGRESSAO (o caminho feliz e a ordem sensivel do parser) ────

    [Test]
    public void ChunkWithoutReasoning_StillEmitsTheToken()
    {
        var evt = Parse(Chunk("{\"content\":\"Resposta normal.\"}"));

        Assert.Multiple(() =>
        {
            Assert.That(evt, Is.Not.Null, "o caminho feliz nao pode quebrar");
            Assert.That(evt!.Type, Is.EqualTo("token"));
            Assert.That(evt.Content, Is.EqualTo("Resposta normal."));
        });
    }

    [Test]
    public void ReasoningWithToolCalls_KeepsTheToolCallDeltasIntact()
    {
        // ORDEM SENSIVEL (B16-r2): o parser acumula tool_calls ANTES de olhar o
        // content. O bloco novo de reasoning nao pode quebrar essa ordem.
        var pending = new Dictionary<int, (string Id, string Name, StringBuilder Args)>();
        var chunk = "{\"choices\":[{\"index\":0,\"delta\":{\"reasoning\":\"CoT\","
            + "\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"get_inventory\",\"arguments\":\"{\\\"a\\\":\"}}]},\"finish_reason\":\"tool_calls\"}]}";

        var evt = OpenAiProvider.ParseStreamChunk(chunk, pending);

        Assert.Multiple(() =>
        {
            Assert.That(evt?.Type, Is.EqualTo("tool_calls"), "o finish de tool_calls continua funcionando");
            Assert.That(evt!.ToolCalls, Has.Count.EqualTo(1));
            Assert.That(evt.ToolCalls![0].Name, Is.EqualTo("get_inventory"));
            Assert.That(evt.ToolCalls[0].ArgumentsJson, Is.EqualTo("{\"a\":"), "os argumentos acumulados chegam intactos");
        });
    }

    [Test]
    public void TryGetReasoningDelta_RecognizesBothKeysAndRejectsEverythingElse()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Read("{\"reasoning\":\"a\"}"), Is.EqualTo("a"));
            Assert.That(Read("{\"reasoning_content\":\"b\"}"), Is.EqualTo("b"));
            Assert.That(Read("{\"content\":\"c\"}"), Is.Null, "content NAO e reasoning");
            Assert.That(Read("{\"reasoning\":123}"), Is.Null, "reasoning nao-string nao e texto");
            Assert.That(Read("{}"), Is.Null);
        });
    }

    private static string? Read(string deltaJson)
    {
        using var doc = JsonDocument.Parse(deltaJson);
        return OpenAiProvider.TryGetReasoningDelta(doc.RootElement, out var reasoning) ? reasoning : null;
    }

    // ── ITEM NOVO (verificador): max_tokens gasto pensando => content vazio ─

    [Test]
    public void AllBudgetSpentOnReasoning_ReturnsDoneWithLength_AndNoToken()
    {
        // Cenario levantado pelo verificador: com exclude=true o CoT nao volta, mas se o
        // modelo gastar TODO o max_tokens pensando, o chunk final pode vir SEM content e
        // com finish_reason=length. O parser precisa emitir "done" com o finish correto
        // (o orquestrador usa isso para a nota de sistema + sintese forcada).
        // O que NAO pode acontecer: nenhum token falso - o usuario nao pode ver CoT.
        var evt = Parse(Chunk("{\"reasoning\":\"pensou muito e nao escreveu nada\"}", finishReason: "length"));

        Assert.Multiple(() =>
        {
            Assert.That(evt, Is.Not.Null, "o round precisa encerrar - nao pode ficar pendurado");
            Assert.That(evt!.Type, Is.EqualTo("done"), "finish_reason=length vira done (o orquestrador trata)");
            Assert.That(evt.FinishReason, Is.EqualTo("length"), "o finish precisa chegar para o aviso de truncamento");
            Assert.That(evt.Content, Is.Null, "nenhum conteudo: muito menos o CoT");
        });
    }
}
