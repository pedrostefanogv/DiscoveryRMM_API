using System.Diagnostics;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Cache de respostas rápidas para saudações e mensagens curtas.
/// Evita chamadas ao LLM para mensagens triviais, reduzindo latência e custo.
/// </summary>
public class AiChatQuickReply
{
    private readonly IAiChatMessageRepository _messageRepository;
    private readonly ILogger<AiChatService> _logger;

    private static readonly Dictionary<string, string> QuickReplies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["oi"] = "Olá! Como posso ajudar você hoje?",
        ["olá"] = "Olá! Em que posso ajudar?",
        ["ola"] = "Olá! Em que posso ajudar?",
        ["teste"] = "Olá! Como posso ajudar você hoje?",
        ["test"] = "Olá! Como posso ajudar você hoje?",
        ["bom dia"] = "Bom dia! Como posso ajudar você hoje?",
        ["boa tarde"] = "Boa tarde! Como posso ajudar você hoje?",
        ["boa noite"] = "Boa noite! Como posso ajudar você hoje?",
    };

    public AiChatQuickReply(IAiChatMessageRepository messageRepository, ILogger<AiChatService> logger)
    {
        _messageRepository = messageRepository;
        _logger = logger;
    }

    /// <summary>
    /// Tenta responder via cache rápido. Retorna null se não houver match.
    /// Match EXATO (saudações puras: "oi", "bom dia") funciona em qualquer
    /// ponto da conversa — responder "oi" com LLM completo é desperdício.
    /// Match PARCIAL ("oi, tudo bem?" → "oi") só na primeira mensagem (sem
    /// histórico): no meio da conversa, uma mensagem curta pode ser resposta
    /// a uma pergunta anterior e não uma saudação.
    /// </summary>
    public static string? TryGetReply(string message, IReadOnlyList<AiChatMessage>? history)
    {
        var trimmed = message.Trim().ToLowerInvariant();
        var hasHistory = history is { Count: > 0 };

        // Match exato: sempre válido (saudação pura é trivial em qualquer contexto).
        if (QuickReplies.TryGetValue(trimmed, out var quick)) return quick;

        // Match parcial: apenas primeira mensagem (sem histórico).
        if (!hasHistory && trimmed.Length <= 20 && trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: <= 3 } words)
        {
            // Limpa pontuação da primeira palavra ("oi," → "oi") para o match
            // com o dicionário de saudações.
            var firstWord = new string(words[0].Where(char.IsLetter).ToArray());
            if (firstWord.Length > 0 && QuickReplies.TryGetValue(firstWord, out var partial)) return partial;
        }
        return null;
    }

    public async Task PersistAsync(Guid sessionId, string userMessage, string reply,
        int seq, DateTime startTime, string traceId, AIIntegrationSettings aiSettings,
        Stopwatch stopwatch, CancellationToken ct)
    {
        try
        {
            var messages = new List<AiChatMessage>
            {
                new() { Id = Guid.NewGuid(), SessionId = sessionId, SequenceNumber = seq, Role = "user", Content = userMessage, CreatedAt = startTime, TraceId = traceId },
                new() { Id = Guid.NewGuid(), SessionId = sessionId, SequenceNumber = seq + 1, Role = "assistant", Content = reply, TokensUsed = reply.Split(' ').Length, LatencyMs = (int)stopwatch.ElapsedMilliseconds, ModelVersion = aiSettings.ChatModel + " (cached)", CreatedAt = DateTime.UtcNow, TraceId = traceId }
            };
            await _messageRepository.CreateBatchAsync(messages, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{TraceId}] Falha ao persistir quick-reply", traceId);
        }
    }
}
