using System.Text.RegularExpressions;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;

namespace Discovery.Infrastructure.Services.Ai;

/// <summary>
/// Extrai sinais determinísticos do chamado (tags e dificuldade estimada) usados
/// pela triagem por IA. É a versão sem LLM: sempre disponível e sem custo.
/// </summary>
public static partial class TicketSignalExtractor
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "para", "com", "sem", "sobre", "pelo", "pela", "dos", "das", "uma", "que", "nao", "não",
        "esta", "está", "esse", "essa", "isso", "aqui", "quando", "onde", "como", "porque", "mais",
        "menos", "muito", "pouco", "favor", "urgente", "ajuda", "problema", "erro", "favor",
        "the", "and", "for", "with", "when", "this", "that", "from"
    };

    private static readonly (string Keyword, int Delta)[] DifficultyKeywords =
    [
        ("servidor", 1), ("banco de dados", 1), ("firewall", 1), ("vpn", 1), ("virtualizacao", 1),
        ("virtualização", 1), ("cluster", 1), ("storage", 1), ("backup", 1), ("rede", 1),
        ("dominio", 1), ("domínio", 1), ("active directory", 1), ("migracao", 1), ("migração", 1),
        ("instabilidade", 1), ("queda", 1), ("lentidao", 1), ("lentidão", 1), ("travando", 1),
        ("impressora", -1), ("senha", -1), ("reset", -1), ("acesso", -1), ("instalar", -1)
    ];

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}\-_\.]{3,}", RegexOptions.Compiled)]
    private static partial Regex TokenRegex();

    /// <summary>Tags derivadas da categoria e do texto do chamado (tokens relevantes).</summary>
    public static IReadOnlyList<string> ExtractTags(Ticket ticket, int maxTags = 12)
    {
        var tags = new List<string>();

        if (!string.IsNullOrWhiteSpace(ticket.Category))
            tags.Add(ticket.Category!.Trim().ToLowerInvariant());

        var text = (ticket.Title + " " + ticket.Description).ToLowerInvariant();
        foreach (Match match in TokenRegex().Matches(text))
        {
            var token = match.Value;
            if (token.Length < 4 || StopWords.Contains(token)) continue;
            if (tags.Contains(token, StringComparer.OrdinalIgnoreCase)) continue;

            tags.Add(token);
            if (tags.Count >= maxTags) break;
        }

        return tags;
    }

    /// <summary>
    /// Dificuldade 1..5 a partir da prioridade, do tamanho da descrição, de
    /// palavras-chave técnicas e do histórico de chamados semelhantes.
    /// </summary>
    public static (int Level, string Rationale) EstimateDifficulty(
        Ticket ticket,
        IReadOnlyList<TechnicianAffinityHit> similarTickets)
    {
        var level = ticket.Priority switch
        {
            TicketPriority.Low => 1,
            TicketPriority.Medium => 2,
            TicketPriority.High => 4,
            TicketPriority.Critical => 5,
            _ => 3
        };

        var reasons = new List<string> { $"prioridade {ticket.Priority}" };

        var text = (ticket.Title + " " + ticket.Description).ToLowerInvariant();
        foreach (var (keyword, delta) in DifficultyKeywords)
        {
            if (!text.Contains(keyword)) continue;
            level += delta;
            reasons.Add(keyword);
        }

        if (ticket.Description.Length > 1500)
        {
            level += 1;
            reasons.Add("descrição extensa");
        }

        // Histórico: muitos chamados semelhantes já resolvidos sugerem um caso
        // recorrente; nenhum histórico sugere um caso novo (mais difícil).
        if (similarTickets.Count >= 3 && level > 1)
            level -= 1;
        else if (similarTickets.Count == 0)
            reasons.Add("sem histórico semelhante");

        return (Math.Clamp(level, 1, 5), string.Join(", ", reasons));
    }
}
