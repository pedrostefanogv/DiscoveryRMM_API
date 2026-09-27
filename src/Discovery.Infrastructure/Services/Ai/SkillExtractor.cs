using System.Text.Json;
using Discovery.Core.Entities;

namespace Discovery.Infrastructure.Services.Ai;

/// <summary>Chamado resolvido usado como evidência de competência (sem dados sensíveis).</summary>
public sealed record SkillEvidenceTicket(string Title, string Description, string? Category);

/// <summary>
/// Deriva competências do atendente a partir dos chamados que ele resolveu.
/// Reaproveita o extrator de sinais do chamado (categorias + tokens relevantes)
/// e mantém apenas tags com evidência mínima. Função pura para teste unitário.
/// </summary>
public static class SkillExtractor
{
    public static (IReadOnlyList<string> Tags, string EvidenceJson) Extract(
        IReadOnlyList<SkillEvidenceTicket> resolvedTickets,
        IReadOnlyList<string> existingTags,
        int minEvidence,
        int maxTags,
        int windowDays = 90)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var categoryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var ticket in resolvedTickets)
        {
            var tags = TicketSignalExtractor.ExtractTags(new Ticket
            {
                Title = ticket.Title,
                Description = ticket.Description,
                Category = ticket.Category
            });

            foreach (var tag in tags)
                counts[tag] = counts.GetValueOrDefault(tag) + 1;

            if (!string.IsNullOrWhiteSpace(ticket.Category))
            {
                var category = ticket.Category.Trim();
                categoryCounts[category] = categoryCounts.GetValueOrDefault(category) + 1;
            }
        }

        var existing = new HashSet<string>(
            existingTags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()),
            StringComparer.OrdinalIgnoreCase);

        var suggested = counts
            .Where(pair => pair.Value >= Math.Max(1, minEvidence))
            .Where(pair => !existing.Contains(pair.Key))
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key.ToLowerInvariant())
            .Take(Math.Max(1, maxTags))
            .ToList();

        var evidence = JsonSerializer.Serialize(new
        {
            windowDays,
            resolvedTickets = resolvedTickets.Count,
            minimumEvidence = minEvidence,
            tags = counts
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Take(15)
                .Select(pair => new { tag = pair.Key.ToLowerInvariant(), count = pair.Value }),
            categories = categoryCounts
                .OrderByDescending(pair => pair.Value)
                .Take(10)
                .Select(pair => new { category = pair.Key, count = pair.Value })
        });

        return (suggested, evidence);
    }
}
