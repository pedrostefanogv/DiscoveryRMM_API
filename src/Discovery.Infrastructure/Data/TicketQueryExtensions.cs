using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Data;

/// <summary>
/// Predicados compartilhados de busca por resposta do questionário, usados pela
/// listagem, pelo KPI e pela busca universal — para que os três tenham a mesma
/// semântica (chave, valor, "contém" e busca livre).
/// </summary>
public static class TicketQueryExtensions
{
    /// <summary>Filtra por resposta do questionário (chave e/ou valor).</summary>
    public static IQueryable<Ticket> WhereHasAnswer(
        this IQueryable<Ticket> query,
        IQueryable<TicketAnswer> answers,
        string? answerKey,
        string? answerValue,
        TicketAnswerMatch match)
    {
        var key = string.IsNullOrWhiteSpace(answerKey) ? null : answerKey.Trim();
        var value = string.IsNullOrWhiteSpace(answerValue) ? null : answerValue.Trim();

        if (key is null && value is null) return query;

        if (key is not null)
        {
            if (value is null)
                return query.Where(t => answers.Any(a => a.TicketId == t.Id && a.QuestionKey == key));

            if (match == TicketAnswerMatch.Contains)
            {
                var pattern = LikePatternHelper.Contains(value);
                return query.Where(t => answers.Any(a => a.TicketId == t.Id
                    && a.QuestionKey == key
                    && a.ValueText != null
                    && EF.Functions.ILike(a.ValueText, pattern)));
            }

            return query.Where(t => answers.Any(a => a.TicketId == t.Id
                && a.QuestionKey == key
                && a.ValueText == value));
        }

        // Sem chave informada: busca no rótulo da pergunta OU no valor.
        if (match == TicketAnswerMatch.Contains)
        {
            var pattern = LikePatternHelper.Contains(value!);
            return query.Where(t => answers.Any(a => a.TicketId == t.Id
                && (EF.Functions.ILike(a.QuestionLabel, pattern)
                    || (a.ValueText != null && EF.Functions.ILike(a.ValueText, pattern)))));
        }

        return query.Where(t => answers.Any(a => a.TicketId == t.Id
            && (a.QuestionLabel == value || a.ValueText == value)));
    }

    /// <summary>
    /// Busca livre do chamado: título, descrição, categoria OU respostas do
    /// questionário (pergunta/valor). O termo é escapado internamente via
    /// <see cref="LikePatternHelper"/> (curingas não viram coringa).
    /// </summary>
    public static IQueryable<Ticket> WhereMatchesText(
        this IQueryable<Ticket> query,
        IQueryable<TicketAnswer> answers,
        string term)
    {
        var pattern = LikePatternHelper.Contains(term);
        return query.Where(t =>
            EF.Functions.ILike(t.Title, pattern)
            || EF.Functions.ILike(t.Description, pattern)
            || (t.Category != null && EF.Functions.ILike(t.Category, pattern))
            || answers.Any(a => a.TicketId == t.Id
                && (EF.Functions.ILike(a.QuestionLabel, pattern)
                    || (a.ValueText != null && EF.Functions.ILike(a.ValueText, pattern)))));
    }
}
