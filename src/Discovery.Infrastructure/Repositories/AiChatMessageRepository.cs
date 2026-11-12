using System.Text.Json;
using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class AiChatMessageRepository : IAiChatMessageRepository
{
    private readonly DiscoveryDbContext _db;

    public AiChatMessageRepository(DiscoveryDbContext db) => _db = db;

    public async Task<AiChatMessage> CreateAsync(AiChatMessage message, CancellationToken ct = default)
    {
        message.Id = IdGenerator.NewId();
        message.CreatedAt = DateTime.UtcNow;

        _db.AiChatMessages.Add(message);
        await _db.SaveChangesAsync(ct);
        return message;
    }

    /// <summary>
    /// Últimas <paramref name="limit"/> mensagens da sessão, em ordem crescente.
    /// <para>
    /// A fronteira é ALINHADA ao começo de um grupo de tool calls: o corte por
    /// SequenceNumber caía no MEIO do grupo e o payload enviado ao LLM começava
    /// com function_call_output ÓRFÃOS (sem o assistant que declarou os
    /// call_ids) — o provedor rejeitava com 400 "No function call found for
    /// function call output with call_id '...'". Era SISTEMÁTICO: toda sessão
    /// cuja janela truncada caía num role='tool' quebrava (3 de 3 sessões com
    /// >= 10 mensagens no incidente de 2026-11-11).
    /// </para>
    /// <para>
    /// A regra é CONSERVADORA por necessidade: as colunas tool_call_id e
    /// tool_calls_json são comparadas como TEXTO exato (sem parsing de prefixo
    /// 'call_' — o formato do id varia por provedor, ex. 'chatcmpl-tool-...').
    /// Quando as respostas de um grupo ficam com a declaração FORA da janela, a
    /// janela RECUA até incluí-la (a declaração fica a poucas linhas do corte);
    /// só quando ela não existe no histórico é que as respostas órfãs saem do
    /// recorte. Cadeia aberta no FIM também é completada. O normalizador de
    /// BuildLlmMessagesFromHistory continua sendo a rede de segurança final.
    /// </para>
    /// <para>
    /// O limite é respeitado: nenhum caminho devolve mais que <c>safeLimit</c>
    /// mensagens, então o custo de prompt continua limitado por
    /// maxHistoryMessages.
    /// </para>
    /// </summary>
    public async Task<List<AiChatMessage>> GetRecentBySessionAsync(Guid sessionId, int limit, CancellationToken ct = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 200);

        var window = await _db.AiChatMessages
            .AsNoTracking()
            .Where(m => m.SessionId == sessionId)
            .OrderByDescending(m => m.SequenceNumber)
            .Take(safeLimit)
            .OrderBy(m => m.SequenceNumber)
            .ToListAsync(ct);

        if (window.Count == 0) return window;

        // 1) Respostas com a declaração FORA da janela. Sem o assistant que
        //    declarou o call_id não existe payload válido contendo a resposta
        //    (function_call_output sem function_call = 400). A primeira tentativa
        //    é incluir a declaração: o corte é sempre próximo dela (no incidente,
        //    a declaração estava 3 linhas antes). Só se isso não resolver — a
        //    declaração não existe — as respostas é que saem do recorte.
        //    A resolução fica em NormalizeToolCallPairs como rede de segurança.
        var declaredInWindow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var msg in window)
            foreach (var callId in ExtractCallIds(msg.ToolCallsJson))
                declaredInWindow.Add(callId);

        var orphanIds = window
            .Where(m => m.Role == "tool" && !string.IsNullOrWhiteSpace(m.ToolCallId)
                && !declaredInWindow.Contains(m.ToolCallId!))
            .Select(m => m.ToolCallId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (orphanIds.Count > 0)
        {
            // Declaração anterior à janela que cobre TODAS as respostas órfãs: ela
            // é a cabeça legítima do grupo (mesma regra "respostas vêm logo depois
            // do assistant que as declarou" usada pelo normalizador).
            // Candidatas = últimas declarações antes da janela; a escolha da que
            // cobre TODAS as respostas órfãs é feita em memória (a tradução de
            // All(...Contains) não é suportada por todos os providers).
            var candidates = await _db.AiChatMessages.AsNoTracking()
                .Where(m => m.SessionId == sessionId
                    && m.Role == "assistant"
                    && m.ToolCallsJson != null
                    && m.SequenceNumber < window[0].SequenceNumber
                    && m.ToolCallsJson.Contains(window[0].ToolCallId!))
                .OrderByDescending(m => m.SequenceNumber)
                .Take(safeLimit)
                .ToListAsync(ct);

            var head = candidates.FirstOrDefault(m =>
                orphanIds.All(id => m.ToolCallsJson!.Contains(id, StringComparison.Ordinal)));

            var from = head?.SequenceNumber ?? window[0].SequenceNumber;
            var expanded = await _db.AiChatMessages.AsNoTracking()
                .Where(m => m.SessionId == sessionId && m.SequenceNumber >= from)
                .OrderBy(m => m.SequenceNumber)
                .Take(safeLimit)
                .ToListAsync(ct);

            var stillOrphan = expanded
                .Where(m => m.Role == "tool" && !string.IsNullOrWhiteSpace(m.ToolCallId))
                .Any(m => !expanded.Any(d => ExtractCallIds(d.ToolCallsJson)
                    .Contains(m.ToolCallId!, StringComparer.OrdinalIgnoreCase)));

            if (expanded.Count > 0 && !stillOrphan) return expanded;

            // Declaração inexistente: avançar para depois das respostas órfãs.
            var first = 0;
            while (first < window.Count
                && window[first].Role == "tool"
                && !string.IsNullOrWhiteSpace(window[first].ToolCallId)
                && !declaredInWindow.Contains(window[first].ToolCallId!))
            {
                first++;
            }
            return window.Skip(first).ToList();
        }

        // 2) Cadeia ABERTA no fim: a última mensagem que declara calls pode ter
        //    respostas fora da janela (corte no meio do grupo pelo fim). Incluir
        //    as mensagens seguintes dá ao normalizador as respostas que faltam.
        var lastDeclaring = window.LastOrDefault(m => !string.IsNullOrWhiteSpace(m.ToolCallsJson));
        if (lastDeclaring is not null)
        {
            var missing = new List<string>();
            foreach (var callId in ExtractCallIds(lastDeclaring.ToolCallsJson))
            {
                if (window.Any(m => string.Equals(m.ToolCallId, callId, StringComparison.OrdinalIgnoreCase))) continue;
                if (await ExistsAnswerAfterAsync(sessionId, lastDeclaring.SequenceNumber, callId, ct)) missing.Add(callId);
            }

            if (missing.Count > 0)
            {
                var declaration = await _db.AiChatMessages.AsNoTracking()
                    .Where(m => m.SessionId == sessionId
                        && m.SequenceNumber < lastDeclaring.SequenceNumber
                        && m.ToolCallsJson != null
                        && missing.Any(id => m.ToolCallsJson!.Contains(id)))
                    .OrderByDescending(m => m.SequenceNumber)
                    .FirstOrDefaultAsync(ct);

                // A declaração anterior (que a janela cortou) é incluída; a
                // ORIGEM não pode ser 'responses' — só linhas role='tool' contam
                // como resposta (uma linha assistant com ToolCallId pode ser eco).
                var from = declaration?.SequenceNumber ?? lastDeclaring.SequenceNumber;
                var extended = await _db.AiChatMessages.AsNoTracking()
                    .Where(m => m.SessionId == sessionId && m.SequenceNumber >= from)
                    .OrderBy(m => m.SequenceNumber)
                    .Take(safeLimit)
                    .ToListAsync(ct);

                // FIX-2: a extensão da janela podia INTRODUZIR respostas órfãs —
                // o recuo até 'from' trazia linhas role='tool' cujo declarer ficou
                // ainda mais atrás (fora do novo recorte). O caminho do passo 1
                // valida isso (:114-119); este não validava nada além da
                // contagem, e era por aqui que passava a janela quebrada do
                // incidente de 2026-11-12 (o normalizador então descartava as
                // respostas, e o round ficava sem elas). Reaplica a MESMA
                // validação; se ainda houver órfã, usa a estratégia do passo 1.
                if (extended.Count > window.Count)
                {
                    var stillOrphanExtended = extended
                        .Where(m => m.Role == "tool" && !string.IsNullOrWhiteSpace(m.ToolCallId))
                        .Any(m => !declaredInExtended(extended, m.ToolCallId!));

                    if (!stillOrphanExtended) return extended;

                    var firstClean = 0;
                    while (firstClean < extended.Count
                        && extended[firstClean].Role == "tool"
                        && !string.IsNullOrWhiteSpace(extended[firstClean].ToolCallId)
                        && !declaredInExtended(extended, extended[firstClean].ToolCallId!))
                    {
                        firstClean++;
                    }
                    return extended.Skip(firstClean).ToList();
                }
            }
        }

        return window;
    }

    /// <summary>
    /// O call_id é declarado por alguma mensagem do próprio recorte? Usado para
    /// validar as respostas role='tool' depois de estender a janela (FIX-2).
    /// </summary>
    private static bool declaredInExtended(List<AiChatMessage> messages, string toolCallId)
        => messages.Any(m => ExtractCallIds(m.ToolCallsJson)
            .Contains(toolCallId, StringComparer.OrdinalIgnoreCase));

    /// <summary>Existe resposta (role='tool') deste call_id DEPOIS da sequência informada?</summary>
    private Task<bool> ExistsAnswerAfterAsync(Guid sessionId, int sequenceNumber, string toolCallId, CancellationToken ct)
        => _db.AiChatMessages.AsNoTracking().AnyAsync(m => m.SessionId == sessionId
            && m.SequenceNumber > sequenceNumber
            && m.Role == "tool"
            && m.ToolCallId == toolCallId, ct);

    /// <summary>
    /// call_ids declarados em tool_calls_json. Leitura tolerante: o JSON é
    /// gravado pelo servidor, mas um payload corrompido não pode derrubar a
    /// leitura do histórico.
    /// </summary>
    private static IEnumerable<string> ExtractCallIds(string? toolCallsJson)
    {
        if (string.IsNullOrWhiteSpace(toolCallsJson)) yield break;

        List<string> ids;
        try
        {
            using var doc = JsonDocument.Parse(toolCallsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) yield break;
            ids = doc.RootElement.EnumerateArray()
                .Select(e => e.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .ToList();
        }
        catch (JsonException) { yield break; }

        foreach (var id in ids) yield return id;
    }

    public async Task<List<AiChatMessage>> SearchByAgentAsync(
        Guid agentId, string query, int limit, Guid? excludeSessionId = null, CancellationToken ct = default)
    {
        var term = (query ?? string.Empty).Trim();
        if (term.Length == 0)
            return new List<AiChatMessage>();
        if (term.Length > 200)
            term = term[..200];

        var safeLimit = Math.Clamp(limit, 1, 20);
        // Busca case-insensitive com lower() (traduz para lower(content) LIKE ...
        // no Postgres e funciona no provider InMemory dos testes).
        var lowered = term.ToLowerInvariant();

        return await _db.AiChatMessages
            .AsNoTracking()
            .Where(m => m.Role == "user" || m.Role == "assistant")
            .Where(m => m.Session.AgentId == agentId && m.Session.DeletedAt == null)
            .Where(m => !excludeSessionId.HasValue || m.SessionId != excludeSessionId.Value)
            .Where(m => m.Content.ToLower().Contains(lowered))
            .OrderByDescending(m => m.CreatedAt)
            .Take(safeLimit)
            .ToListAsync(ct);
    }

    public async Task CreateBatchAsync(IReadOnlyList<AiChatMessage> messages, CancellationToken ct = default)
    {
        foreach (var message in messages)
        {
            message.Id = IdGenerator.NewId();
            message.CreatedAt = DateTime.UtcNow;
            _db.AiChatMessages.Add(message);
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<(int MessageCount, int EstimatedTokens)> GetStatsAsync(Guid sessionId, CancellationToken ct = default)
    {
        var messages = await _db.AiChatMessages
            .AsNoTracking()
            .Where(m => m.SessionId == sessionId && m.Role != "tool")
            .Select(m => new { m.Role, m.Content, m.TokensUsed })
            .ToListAsync(ct);

        var tokenSum = messages
            .Where(m => m.TokensUsed.HasValue)
            .Sum(m => m.TokensUsed!.Value);

        // Se não temos tokens registrados, estimamos por palavras (1.3 tokens/palavra)
        if (tokenSum == 0)
        {
            tokenSum = (int)messages.Sum(m => (m.Content?.Length ?? 0) * 0.3);
        }

        return (messages.Count, tokenSum);
    }
}
