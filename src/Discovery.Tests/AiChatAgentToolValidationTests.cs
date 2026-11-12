using System.Text.Json;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Regressão B17: a validação de argumentos de tools do agent rejeitava "{}"
/// para TODA tool (exceto list_ticket_templates), inclusive tools sem
/// parâmetros (get_pending_updates, get_package_actions, ...). O modelo repetia
/// a chamada e o orquestrador caía no fallback seco, produzindo um turno 200
/// sem nenhum token. A validação agora é dirigida por schema (required).
/// </summary>
public class AiChatAgentToolValidationTests
{
    private static JsonElement Schema(params string[] required)
    {
        var reqJson = required.Length == 0
            ? string.Empty
            : ",\"required\":[" + string.Join(",", System.Array.ConvertAll(required, r => "\"" + r + "\"")) + "]";
        var json = "{\"type\":\"object\",\"properties\":{}" + reqJson + "}";
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Test]
    public void NoArgTool_WithEmptyObject_IsValid()
    {
        var (isValid, _) = AiChatToolOrchestrator.ValidateAgentToolArguments("get_pending_updates", "{}", Schema());
        Assert.That(isValid, Is.True);
    }

    [Test]
    public void NoArgTool_WithMissingSchema_IsValid()
    {
        var (isValid, _) = AiChatToolOrchestrator.ValidateAgentToolArguments("get_package_actions", "", null);
        Assert.That(isValid, Is.True);
    }

    [Test]
    public void EmptyKeyWrapper_IsNormalized()
    {
        var (isValid, _) = AiChatToolOrchestrator.ValidateAgentToolArguments(
            "search_packages", "{\"\":{\"query\":\"7-zip\"}}", Schema("query"));
        Assert.That(isValid, Is.True);
    }

    [Test]
    public void JsonEncodedString_IsNormalized()
    {
        var encoded = System.Text.Json.JsonSerializer.Serialize("{\"query\":\"chrome\"}");
        var (isValid, _) = AiChatToolOrchestrator.ValidateAgentToolArguments(
            "search_packages", encoded, Schema("query"));
        Assert.That(isValid, Is.True);
    }

    [Test]
    public void MissingRequired_IsInvalid()
    {
        var (isValid, _) = AiChatToolOrchestrator.ValidateAgentToolArguments("search_packages", "{}", Schema("query"));
        Assert.That(isValid, Is.False);
    }

    [Test]
    public void HardcodedFallback_WhenSchemaUnknown_RejectsMissingQuery()
    {
        var (isValid, _) = AiChatToolOrchestrator.ValidateAgentToolArguments("search_packages", "{}", null);
        Assert.That(isValid, Is.False);
    }

    [Test]
    public void MalformedJson_IsInvalid()
    {
        var (isValid, _) = AiChatToolOrchestrator.ValidateAgentToolArguments("search_packages", "{not json", Schema("query"));
        Assert.That(isValid, Is.False);
    }

    [Test]
    public void TrailingComma_IsRepaired()
    {
        var (isValid, _) = AiChatToolOrchestrator.ValidateAgentToolArguments(
            "create_ticket", "{\"title\":\"x\",\"description\":\"y\",}", Schema("title", "description"));
        Assert.That(isValid, Is.True);
    }

    [Test]
    public void ProviderSafeToolName_MatchesProviderPattern()
    {
        Assert.That(AiChatToolOrchestrator.IsProviderSafeToolName("memory_list"), Is.True);
        Assert.That(AiChatToolOrchestrator.IsProviderSafeToolName("capture_screenshot"), Is.True);
        Assert.That(AiChatToolOrchestrator.IsProviderSafeToolName("ask-user"), Is.True);
        Assert.That(AiChatToolOrchestrator.IsProviderSafeToolName("memory/list"), Is.False);
        Assert.That(AiChatToolOrchestrator.IsProviderSafeToolName("nome com espaco"), Is.False);
        Assert.That(AiChatToolOrchestrator.IsProviderSafeToolName("tool.name"), Is.False);
        Assert.That(AiChatToolOrchestrator.IsProviderSafeToolName(""), Is.False);
        Assert.That(AiChatToolOrchestrator.IsProviderSafeToolName(null), Is.False);
        Assert.That(AiChatToolOrchestrator.IsProviderSafeToolName(new string('a', 64)), Is.True);
        Assert.That(AiChatToolOrchestrator.IsProviderSafeToolName(new string('a', 65)), Is.False);
    }

    [Test]
    public void MergeDistinctTools_RemovesDuplicateNames()
    {
        var kb = new List<LlmTool> { new("knowledge_search", "kb", new { }), new("get_inventory", "kb-inv", new { }) };
        var agent = new List<LlmTool> { new("get_inventory", "agent-dup", new { }), new("capture_screenshot", "cap", new { }) };

        var merged = AiChatToolOrchestrator.MergeDistinctTools(kb, agent);

        Assert.That(merged.Select(t => t.Name), Is.EqualTo(new[] { "knowledge_search", "get_inventory", "capture_screenshot" }));
        Assert.That(merged[1].Description, Is.EqualTo("kb-inv"), "a primeira ocorrência deve vencer");
        Assert.That(AiChatToolOrchestrator.MergeDistinctTools(null, null), Is.Empty);
        Assert.That(AiChatToolOrchestrator.MergeDistinctTools(null, new List<LlmTool> { new("", "x", new { }) }), Is.Empty);
    }

    // ── B18: corte de histórico no MEIO de um grupo de tool calls ────────────
    // O corte das últimas N mensagens por SequenceNumber caía no meio do grupo:
    // o payload passava a começar com function_call_output ÓRFÃOS (sem o
    // assistant que declarou os call_ids) e o provedor devolvia 400 "No function
    // call found for function call output with call_id '...'" -> HTTP 500 no
    // cliente, que só voltava "limpando o chat". Os testes abaixo reproduzem a
    // janela órfã e travam o invariante do payload.

    private static AiChatMessage HistoryRow(int seq, string role, string content = "",
        string? toolCallId = null, string? toolName = null, string? toolCallsJson = null) => new()
        {
            Id = System.Guid.NewGuid(),
            SessionId = System.Guid.NewGuid(),
            SequenceNumber = seq,
            Role = role,
            Content = content,
            ToolCallId = toolCallId,
            ToolName = toolName,
            ToolCallsJson = toolCallsJson,
            CreatedAt = System.DateTime.UtcNow,
        };

    private static string ToolCallsJsonOf(params string[] ids)
        => "[" + string.Join(",", System.Array.ConvertAll(ids,
            id => "{\"id\":\"" + id + "\",\"name\":\"get_pending_updates\",\"arguments\":\"{}\"}")) + "]";

    /// <summary>
    /// Invariante do provedor (as duas direções):
    ///   (a) toda mensagem role='tool' tem um assistant com o mesmo call_id ANTES;
    ///   (b) todo call_id declarado por um assistant tem exatamente UMA resposta.
    /// </summary>
    private static void AssertPayloadInvariant(List<LlmMessage> payload, string scenario)
    {
        var declared = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var answered = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var msg in payload)
        {
            if (msg.Role == "tool")
            {
                Assert.That(msg.ToolCallId, Is.Not.Null.And.Not.Empty,
                    $"[{scenario}] function_call_output sem call_id no payload");
                Assert.That(declared.ContainsKey(msg.ToolCallId!), Is.True,
                    $"[{scenario}] function_call_output ÓRFÃ: nenhum assistant declarou '{msg.ToolCallId}' antes dela");
                answered[msg.ToolCallId!] = answered.GetValueOrDefault(msg.ToolCallId!) + 1;
                Assert.That(answered[msg.ToolCallId!], Is.EqualTo(1),
                    $"[{scenario}] function_call_output DUPLICADA para '{msg.ToolCallId}'");
                continue;
            }

            if (msg.Role == "assistant" && msg.ToolCalls is { Count: > 0 })
            {
                foreach (var call in msg.ToolCalls)
                {
                    Assert.That(declared.ContainsKey(call.Id), Is.False,
                        $"[{scenario}] assistant redeclara o call_id '{call.Id}'");
                    declared[call.Id] = 1;
                }
            }
        }

        foreach (var callId in declared.Keys)
            Assert.That(answered.ContainsKey(callId), Is.True,
                $"[{scenario}] function_call SEM function_call_output para '{callId}'");

        // O payload pode terminar em function_call_output (assistant + tool é
        // uma cadeia FECHADA), mas nunca em função SEM par: nem assistant com
        // tool_calls como última mensagem, nem call_id sem declaração.
        if (payload.Count > 0)
        {
            var last = payload[^1];
            if (last.Role == "assistant" && last.ToolCalls is { Count: > 0 })
                Assert.Fail($"[{scenario}] payload termina com function_call sem function_call_output");
        }
    }

    /// <summary>
    /// CENÁRIO REAL do incidente (sessão 01a1cd68-31a4-716d-ad2c-637be5849a65):
    /// assistant declara 8 calls (_2.._9) e o corte em 10 mensagens joga a
    /// declaração FORA da janela — ela passa a começar na seq 7, que é um
    /// role='tool' respondendo '_4'.
    /// </summary>
    [Test]
    public void OrphanedToolWindow_IsRepaired()
    {
        var ids = new[] { "_2", "_3", "_4", "_5", "_6", "_7", "_8", "_9" };
        var history = new List<AiChatMessage> { HistoryRow(4, "assistant", "vou verificar", toolCallsJson: ToolCallsJsonOf(ids)) };
        for (var i = 0; i < ids.Length; i++)
            history.Add(HistoryRow(5 + i, "tool", "resultado", toolCallId: ids[i], toolName: "get_pending_updates"));
        history.Add(HistoryRow(13, "assistant", "resumo do que encontrei"));
        history.Add(HistoryRow(14, "user", "instale o foxit"));

        // GetRecentBySessionAsync(limit=10) = as 10 ÚLTIMAS: seq 7..16 (aqui 7..14).
        var window = history.OrderByDescending(m => m.SequenceNumber).Take(10).OrderBy(m => m.SequenceNumber).ToList();
        Assert.That(window[0].Role, Is.EqualTo("tool"), "pré-condição do cenário: a janela começa numa resposta");

        var payload = AiChatToolOrchestrator.BuildLlmMessagesFromHistory(window);

        // A declaração ficou fora da janela -> as respostas viram texto? Não:
        // elas são DESCARTADAS (nenhum assistant declara o call_id).
        Assert.That(payload.Any(m => m.Role == "tool"), Is.False,
            "resposta órfã não pode sobrar no payload");
        Assert.That(payload.Select(m => m.Content), Is.EqualTo(new[] { "resumo do que encontrei", "instale o foxit" }));
        AssertPayloadInvariant(payload, "janela órfã (corte no início do grupo)");
    }

    /// <summary>
    /// Janela que corta o FIM do grupo: o assistant declara _2 e _3, mas só a
    /// resposta de _2 está dentro da janela. O assistant NÃO pode ir ao payload
    /// declarando um call sem resposta (400 simétrico).
    /// </summary>
    [Test]
    public void WindowCuttingGroupTail_DoesNotLeaveUnansweredCall()
    {
        var history = new List<AiChatMessage>
        {
            HistoryRow(1, "user", "instale o foxit"),
            HistoryRow(2, "assistant", "vou consultar o catálogo", toolCallsJson: ToolCallsJsonOf("_2", "_3")),
            HistoryRow(3, "tool", "resultado _2", toolCallId: "_2", toolName: "search_packages"),
        };

        var payload = AiChatToolOrchestrator.BuildLlmMessagesFromHistory(history);

        AssertPayloadInvariant(payload, "corte no fim do grupo");
        Assert.That(payload.Select(m => m.Content), Is.EqualTo(new[] { "instale o foxit", "vou consultar o catálogo" }));
    }

    /// <summary>Histórico intacto (sessão nova) não pode ser alterado pelo fix.</summary>
    [Test]
    public void CompleteHistory_IsPreservedByteForByte()
    {
        var history = new List<AiChatMessage>
        {
            HistoryRow(1, "user", "instale o foxit"),
            HistoryRow(2, "assistant", "vou consultar o catálogo", toolCallsJson: ToolCallsJsonOf("_2", "_3")),
            HistoryRow(3, "tool", "resultado _2", toolCallId: "_2", toolName: "search_packages"),
            HistoryRow(4, "assistant", "", toolCallId: "_3", toolName: "install_package"),
            HistoryRow(5, "assistant", "instalado com sucesso"),
            HistoryRow(6, "user", "obrigado"),
        };

        var payload = AiChatToolOrchestrator.BuildLlmMessagesFromHistory(history);

        Assert.That(payload.Count, Is.EqualTo(history.Count), "histórico completo não pode perder mensagem");
        Assert.That(payload[1].ToolCalls!.Select(c => c.Id), Is.EqualTo(new[] { "_2", "_3" }));
        Assert.That(payload[2].ToolCallId, Is.EqualTo("_2"));
        Assert.That(payload[3].ToolCallId, Is.EqualTo("_3"));
        AssertPayloadInvariant(payload, "histórico intacto");
    }

    /// <summary>
    /// Caso assimétrico do incidente (B8-r2 + corte): a declaração de '_3' cai
    /// FORA da janela e outra resposta de '_3' entrou como role='assistant'. A
    /// cadeia completa de '_2' ADOTA o '_3' — a resposta boa continua no payload
    /// e as duplicatas saem.
    /// </summary>
    [Test]
    public void CompleteChainAdoptsCallsDeclaredOutsideTheWindow()
    {
        var history = new List<AiChatMessage>
        {
            HistoryRow(14, "assistant", "consultando", toolCallsJson: ToolCallsJsonOf("_2", "_3")),
            HistoryRow(15, "tool", "resultado _2", toolCallId: "_2", toolName: "search_packages"),
            HistoryRow(16, "assistant", "{resultado _3}", toolCallId: "_3", toolName: "install_package"),
        };

        var payload = AiChatToolOrchestrator.BuildLlmMessagesFromHistory(history);

        AssertPayloadInvariant(payload, "adoção de call declarado fora da janela");
        Assert.That(payload.Count, Is.EqualTo(3), "a cadeia completável deve ser preservada inteira");
        Assert.That(payload.Select(m => m.ToolCallId), Is.EqualTo(new string?[] { null, "_2", "_3" }));
    }

    /// <summary>
    /// Sessões reais do incidente (01a1cd7e first_seq=8 / 01a1cd8d first_seq=29):
    /// o call_id não segue o padrão 'call_' — o formato varia por provedor/upstream
    /// ('chatcmpl-tool-970b37aeb89ab8f2'). O pareamento é por igualdade EXATA do
    /// id contra tool_calls_json, sem parsing de prefixo.
    /// </summary>
    [Test]
    public void OrphanedToolWindow_WithProviderSpecificCallIdShape_IsRepaired()
    {
        var history = new List<AiChatMessage>
        {
            HistoryRow(29, "tool", "resultado", toolCallId: "chatcmpl-tool-970b37aeb89ab8f2", toolName: "get_pending_updates"),
            HistoryRow(30, "tool", "resultado", toolCallId: "call_c48bf232ecaad409_3", toolName: "search_packages"),
            HistoryRow(31, "assistant", "pronto"),
        };

        var payload = AiChatToolOrchestrator.BuildLlmMessagesFromHistory(history);

        AssertPayloadInvariant(payload, "call_id de formato não-'call_'");
        Assert.That(payload.Select(m => m.Content), Is.EqualTo(new[] { "pronto" }),
            "nenhuma resposta órfã pode sobrar, qualquer que seja o formato do id");
    }

    /// <summary>
    /// Resposta do MESMO call_id em DUAS linhas (B8-r2 + janela): a primeira é a
    /// resposta canônica; a segunda é duplicata e sairia como dois
    /// function_call_output do mesmo id (400 "Each function_call must have exactly
    /// one matching function_call_output").
    /// </summary>
    [Test]
    public void DuplicateAnswerForSameCallId_IsEmittedOnce()
    {
        var history = new List<AiChatMessage>
        {
            HistoryRow(1, "user", "instale o foxit"),
            HistoryRow(2, "assistant", "consultando", toolCallsJson: ToolCallsJsonOf("_2")),
            HistoryRow(3, "tool", "resultado", toolCallId: "_2", toolName: "search_packages"),
            HistoryRow(4, "assistant", "resultado repetido", toolCallId: "_2", toolName: "search_packages"),
            HistoryRow(5, "assistant", "instalado"),
        };

        var payload = AiChatToolOrchestrator.BuildLlmMessagesFromHistory(history);

        AssertPayloadInvariant(payload, "resposta duplicada do mesmo call_id");
        Assert.That(payload.Count(m => m.Role == "tool"), Is.EqualTo(1));
        Assert.That(payload.Count(m => m.ToolCallId == "_2"), Is.EqualTo(1));
    }

    /// <summary>
    /// Janela que começa exatamente na declaração e termina nos primeiros
    /// resultados (corte no MEIO do grupo pelo FIM): a cadeia fica INCOMPLETA e
    /// não pode ir ao payload como está — o assistant volta como texto puro.
    /// </summary>
    [Test]
    public void WindowEndingMidChain_DoesNotLeaveUnansweredCall()
    {
        var history = new List<AiChatMessage>
        {
            HistoryRow(9, "assistant", "vou verificar", toolCallsJson: ToolCallsJsonOf("_2", "_3", "_4")),
            HistoryRow(10, "tool", "resultado _2", toolCallId: "_2", toolName: "search_packages"),
        };

        var payload = AiChatToolOrchestrator.BuildLlmMessagesFromHistory(history);

        AssertPayloadInvariant(payload, "janela terminando no meio do grupo");
        Assert.That(payload.Count, Is.EqualTo(1), "a resposta _2 não serve sem a declaração de _3/_4");
        Assert.That(payload[0].ToolCalls, Is.Null);
        Assert.That(payload[0].Content, Is.EqualTo("vou verificar"));
    }

    /// <summary>
    /// O invariante vale também para o payload MONTADO EM MEMÓRIA no multi-round
    /// (que não relê o histórico do banco antes de acrescentar os tool results).
    /// </summary>
    [Test]
    public void InMemoryPayload_WithLeadingOrphanToolMessage_IsRepaired()
    {
        var payload = new List<LlmMessage>
        {
            new("tool", "resultado _4", "_4", "get_pending_updates"),
            new("assistant", "resumo"),
            new("user", "instale o foxit"),
        };

        var repaired = AiChatToolOrchestrator.NormalizeToolCallPairs(payload);

        AssertPayloadInvariant(repaired, "payload em memória");
        Assert.That(repaired.Select(m => m.Content), Is.EqualTo(new[] { "resumo", "instale o foxit" }));
    }
}
