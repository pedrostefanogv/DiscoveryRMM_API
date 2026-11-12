using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;
using Discovery.Infrastructure.Services.Ai;

namespace Discovery.Tests;

/// <summary>
/// Regressão do incidente de 2026-11-12 (produção, modelo meta/muse-ember).
///
/// O provedor devolvia HTTP 400 para o payload do multi-round:
///   "No function call found for function call output with call_id 'call_01769fe00cf715c9_2'"
///   "Duplicate function_call_output for call_id 'call_856c0949dcd0b375_2'"
///
/// Causa: no multi-round o histórico é normalizado em
/// AiChatStreamingOrchestrator (passo 1) e os tool results que o agent acabou de
/// executar são ANEXADOS DEPOIS (passo 2), sem uma segunda normalização. O
/// payload que realmente ia ao provedor nunca passava pela invariante.
///
/// O mesmo payload NÃO quebrava o deepseek-v4.1-flash (tolerante), mas quebrava
/// o meta/muse-ember, que numera os call_ids por posição dentro do turno
/// (sufixo _2/_3/_4) e recusa a cadeia fora de ordem.
///
/// Estes testes travam o invariante no payload FINAL, montado como o
/// multi-round monta: histórico normalizado + tool results anexados.
/// </summary>
public class AiChatMultiRoundPayloadTests
{
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
            id => "{\"id\":\"" + id + "\",\"name\":\"system_info\",\"arguments\":\"{}\"}")) + "]";

    /// <summary>
    /// Reproduz o turno real: janela de histórico que começa no MEIO de um grupo
    /// de tool calls (a declaração caiu fora), seguida dos tool results que o
    /// agent anexou por cima. Exatamente o payload que gerou o 400 em produção.
    /// </summary>
    private static List<LlmMessage> BuildMultiRoundPayload(
        List<AiChatMessage> history, params (string CallId, string Name, string Result)[] appendedResults)
    {
        // Passo 1: histórico -> mensagens (normalizado), como o orquestrador faz.
        var llmMessages = AiChatToolOrchestrator.BuildLlmMessagesFromHistory(history);

        // Passo 2: o orchestrator ANEXA os tool results executados pelo agent
        // (AiChatStreamingOrchestrator.cs, "llmMessages.Add(new LlmMessage("tool", ...))").
        foreach (var tr in appendedResults)
            llmMessages.Add(new LlmMessage("tool", tr.Result, tr.CallId, tr.Name));

        // Passo 3: renormalização no ponto do append (AiChatStreamingOrchestrator.cs,
        // logo após o CreateBatchAsync dos tool results). O append acontece DEPOIS da
        // normalização do passo 1, então sem este passo o payload sairia do orquestrador
        // com call_ids duplicados (histórico + lote reenviado pelo agent) ou órfãos
        // (declaração cortada pela janela) — exatamente o 400 do meta/muse-ember em
        // produção. O helper espelha a produção, e a assertiva segue idêntica.
        return AiChatToolOrchestrator.NormalizeToolCallPairs(llmMessages);
    }

    /// <summary>
    /// Invariante exigido pelo provedor (as duas direções):
    ///   (a) toda mensagem role='tool' tem um assistant declarando o mesmo call_id ANTES;
    ///   (b) todo call_id declarado tem exatamente UMA resposta.
    /// </summary>
    private static void AssertProviderInvariant(List<LlmMessage> payload, string scenario)
    {
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var answered = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var msg in payload)
        {
            if (msg.Role == "tool")
            {
                Assert.That(msg.ToolCallId, Is.Not.Null.And.Not.Empty,
                    $"[{scenario}] function_call_output sem call_id");
                Assert.That(declared.Contains(msg.ToolCallId!), Is.True,
                    $"[{scenario}] 400 do provedor: function_call_output ÓRFÃ '{msg.ToolCallId}' — nenhum assistant a declarou antes");
                answered[msg.ToolCallId!] = answered.GetValueOrDefault(msg.ToolCallId!) + 1;
                Assert.That(answered[msg.ToolCallId!], Is.EqualTo(1),
                    $"[{scenario}] 400 do provedor: function_call_output DUPLICADA para '{msg.ToolCallId}'");
                continue;
            }

            if (msg.Role == "assistant" && msg.ToolCalls is { Count: > 0 })
                foreach (var call in msg.ToolCalls)
                    declared.Add(call.Id);
        }

        // (b) toda declaração presente no payload precisa de exatamente uma resposta.
        foreach (var id in declared)
            Assert.That(answered.GetValueOrDefault(id), Is.EqualTo(1),
                $"[{scenario}] function_call '{id}' sem exatamente uma resposta (o provedor recusa)");
    }

    // ── O caso de produção ───────────────────────────────────────────────────

    /// <summary>
    /// Sessão 01a1cf96 (produção): o round 2 reenvia os MESMOS call_ids que o
    /// round anterior já tinha respondido, depois de a janela ter cortado a
    /// declaração. Produzia "No function call found" / "Duplicate
    /// function_call_output" no meta/muse-ember.
    /// </summary>
    [Test]
    public void RepeatedToolBatch_AfterWindowCut_DoesNotProduceOrphanOrDuplicate()
    {
        var history = new List<AiChatMessage>
        {
            // A janela começa já dentro do grupo: a declaração de _2/_3 saiu.
            HistoryRow(20, "tool", "resultado anterior _2", toolCallId: "call_ec7ff980968b3427_2", toolName: "system_info"),
            HistoryRow(21, "tool", "resultado anterior _3", toolCallId: "call_ec7ff980968b3427_3", toolName: "disk"),
            HistoryRow(22, "assistant", "vou verificar de novo"),
        };

        // O agent reenvia o MESMO lote (é o livelock observado em produção).
        var payload = BuildMultiRoundPayload(history,
            ("call_ec7ff980968b3427_2", "system_info", "novo resultado _2"),
            ("call_ec7ff980968b3427_3", "disk", "novo resultado _3"));

        AssertProviderInvariant(payload, "lote repetido após corte de janela");
    }

    /// <summary>
    /// Variante do incidente com o lote completo declarado no histórico: aqui a
    /// declaração EXISTE, mas o agent anexa as respostas de novo. O resultado
    /// precisa continuar tendo exatamente uma resposta por call_id.
    /// </summary>
    [Test]
    public void RepeatedToolBatch_WithDeclarationInWindow_DoesNotDuplicate()
    {
        var history = new List<AiChatMessage>
        {
            HistoryRow(1, "user", "faca uma verificacao geral do pc"),
            HistoryRow(2, "assistant", "vou verificar", toolCallsJson: ToolCallsJsonOf("call_01769fe00cf715c9_2", "call_01769fe00cf715c9_3")),
            HistoryRow(3, "tool", "resultado _2", toolCallId: "call_01769fe00cf715c9_2", toolName: "system_info"),
            HistoryRow(4, "tool", "resultado _3", toolCallId: "call_01769fe00cf715c9_3", toolName: "disk"),
        };

        var payload = BuildMultiRoundPayload(history,
            ("call_01769fe00cf715c9_2", "system_info", "resultado repetido _2"),
            ("call_01769fe00cf715c9_3", "disk", "resultado repetido _3"));

        AssertProviderInvariant(payload, "lote repetido com declaração na janela");
    }

    /// <summary>
    /// Payload que TERMINA em um grupo de tool calls cuja declaração foi removida
    /// pelo trim de cauda: a cauda de tool messages ficava sem declarante.
    /// </summary>
    [Test]
    public void PayloadEndingWithToolRows_HasDeclarerBeforeThem()
    {
        var history = new List<AiChatMessage>
        {
            HistoryRow(1, "user", "verifique"),
            HistoryRow(2, "assistant", "consultando", toolCallsJson: ToolCallsJsonOf("call_b310c939816fbaad_2")),
            HistoryRow(3, "tool", "resultado _2", toolCallId: "call_b310c939816fbaad_2", toolName: "system_info"),
        };

        var payload = BuildMultiRoundPayload(history);

        AssertProviderInvariant(payload, "payload terminando em tool rows");
    }

    /// <summary>
    /// A normalização aplicada ao payload FINAL deve ser idempotente: o
    /// orquestrador já normalizou o histórico no passo 1, e o provedor normaliza
    /// de novo no passo 2. Aplicar duas vezes não pode alterar o resultado.
    /// </summary>
    [Test]
    public void NormalizationOfFinalPayload_IsIdempotent()
    {
        var history = new List<AiChatMessage>
        {
            HistoryRow(20, "tool", "orfa _2", toolCallId: "call_c48bf232ecaad409_2", toolName: "system_info"),
            HistoryRow(21, "assistant", "texto"),
        };

        var payload = BuildMultiRoundPayload(history,
            ("call_c48bf232ecaad409_3", "disk", "novo _3"));

        var once = AiChatToolOrchestrator.NormalizeToolCallPairs(payload);
        var twice = AiChatToolOrchestrator.NormalizeToolCallPairs(new List<LlmMessage>(once));

        AssertProviderInvariant(once, "normalizado uma vez");
        AssertProviderInvariant(twice, "normalizado duas vezes");
        Assert.That(twice.Count, Is.EqualTo(once.Count), "normalizar duas vezes não pode mudar o tamanho");
        Assert.That(twice.Select(m => m.Content), Is.EqualTo(once.Select(m => m.Content)),
            "normalizar duas vezes não pode mudar o conteúdo (idempotência)");
    }

    /// <summary>
    /// Sessão limpa (sem corte, sem repetição) não pode ser alterada.
    /// </summary>
    [Test]
    public void CleanMultiRoundPayload_IsPreserved()
    {
        var history = new List<AiChatMessage>
        {
            HistoryRow(1, "user", "verifique os chamados"),
            HistoryRow(2, "assistant", "vou consultar", toolCallsJson: ToolCallsJsonOf("call_27d7bbfd4536fadf_2")),
        };

        var payload = BuildMultiRoundPayload(history,
            ("call_27d7bbfd4536fadf_2", "list_tickets", "{\"abertos\":0}"));

        AssertProviderInvariant(payload, "payload limpo");
        Assert.That(payload.Count, Is.EqualTo(3), "payload limpo não pode perder mensagem");
        Assert.That(payload[1].ToolCalls!.Select(c => c.Id), Is.EqualTo(new[] { "call_27d7bbfd4536fadf_2" }));
        Assert.That(payload[2].ToolCallId, Is.EqualTo("call_27d7bbfd4536fadf_2"));
    }
}
