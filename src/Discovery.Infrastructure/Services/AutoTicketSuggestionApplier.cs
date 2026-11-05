using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Aplica as sugestões da IA conforme a política. É o ÚNICO ponto que decide o
/// que vira valor real do chamado — e é código nosso, com validação, nunca o LLM.
///
/// Três salvaguardas não negociáveis:
///
///  1. MODO. Suggest apenas REGISTRA; AutoApply aplica só se o campo estiver
///     habilitado E a confiança atingir o mínimo.
///
///  2. VALIDAÇÃO DE ROTEAMENTO. Departamento precisa existir e pertencer ao
///     cliente (ou ser global). Prioridade precisa ser um valor do enum.
///     Sugestão inválida é DESCARTADA, não aplicada.
///
///  3. PISO DE PRIORIDADE. Em escalação, a IA nunca REBAIXA a prioridade
///     determinada pelo sinal; só pode elevá-la. Sem isso, o modelo poderia
///     transformar um incidente crítico em chamado de baixa prioridade.
/// </summary>
public class AutoTicketSuggestionApplier(
    DiscoveryDbContext db,
    ILogger<AutoTicketSuggestionApplier> logger) : IAutoTicketSuggestionApplier
{
    public async Task<AutoTicketAppliedSuggestion> ApplyAsync(
        AgentMonitoringEvent monitoringEvent,
        ResolvedAutoTicketPolicy policy,
        AutoTicketEnrichment enrichment,
        TicketPriority deterministicPriority,
        bool isEscalation,
        CancellationToken cancellationToken = default)
    {
        var title = enrichment.Title;
        var description = enrichment.Description;

        // Começam NULOS de propósito: este método devolve apenas o que a IA
        // APLICOU. Semear com os defaults da política fazia o orchestrator
        // tratar um default como se fosse sugestão aceita, sobrepondo o que a
        // REGRA já havia definido (a regra é mais específica que o default).
        //
        // Precedência correta, aplicada pelo chamador:
        //   sugestão aplicada > valor da regra > default da política.
        var priority = deterministicPriority;
        Guid? departmentId = null;
        string? category = null;

        var results = new List<AutoTicketSuggestionItem>();
        var priorityFloored = false;

        var mode = policy.AiMode;
        var canApply = mode == AutoTicketAiMode.AutoApply;
        var suggestible = policy.SuggestibleFieldsSafe();

        foreach (var suggestion in enrichment.Suggestions)
        {
            var fieldName = suggestion.Field.ToString();

            // Campo não autorizado pela política: registra como não aplicada.
            if (!suggestible.Contains(fieldName, StringComparer.OrdinalIgnoreCase))
            {
                results.Add(Copy(suggestion, applied: false, reason: "Field is not enabled for AI suggestions."));
                continue;
            }

            // Modo Suggest: registra para o atendente decidir.
            if (!canApply)
            {
                results.Add(Copy(suggestion, applied: false, reason: "Suggestion mode: awaiting human review."));
                continue;
            }

            // Confiança abaixo do mínimo.
            if (suggestion.Confidence < policy.AiMinConfidence)
            {
                results.Add(Copy(suggestion, applied: false,
                    reason: $"Confidence {suggestion.Confidence:0.00} below threshold {policy.AiMinConfidence:0.00}."));
                continue;
            }

            switch (suggestion.Field)
            {
                case AutoTicketSuggestionField.Priority:
                {
                    if (!Enum.TryParse<TicketPriority>(suggestion.SuggestedValue, true, out var suggested))
                    {
                        results.Add(Copy(suggestion, applied: false, reason: "Not a valid priority value."));
                        break;
                    }

                    // ── PISO DE SEGURANÇA ──────────────────────────────────
                    // Em escalação, a IA só pode ELEVAR a prioridade.
                    if (isEscalation && suggested < deterministicPriority)
                    {
                        priorityFloored = true;
                        results.Add(Copy(suggestion, applied: false,
                            reason: "Blocked by the priority floor: cannot downgrade an escalated ticket."));
                        break;
                    }

                    priority = suggested;
                    results.Add(Copy(suggestion, applied: true, appliedValue: suggested.ToString()));
                    break;
                }

                case AutoTicketSuggestionField.Department:
                {
                    if (!Guid.TryParse(suggestion.SuggestedValue, out var suggestedDept))
                    {
                        results.Add(Copy(suggestion, applied: false, reason: "Not a valid department id."));
                        break;
                    }

                    // Valida existência E vínculo com o cliente (ou global).
                    var exists = await db.Departments.AsNoTracking()
                        .AnyAsync(d => d.Id == suggestedDept
                                       && (d.ClientId == null || d.ClientId == monitoringEvent.ClientId),
                                  cancellationToken);

                    if (!exists)
                    {
                        results.Add(Copy(suggestion, applied: false,
                            reason: "Department does not exist or does not belong to this client."));
                        break;
                    }

                    departmentId = suggestedDept;
                    results.Add(Copy(suggestion, applied: true, appliedValue: suggestedDept.ToString()));
                    break;
                }

                case AutoTicketSuggestionField.Category:
                {
                    var value = suggestion.SuggestedValue?.Trim();
                    if (string.IsNullOrWhiteSpace(value) || value.Length > 100)
                    {
                        results.Add(Copy(suggestion, applied: false, reason: "Category is empty or too long."));
                        break;
                    }

                    category = value;
                    results.Add(Copy(suggestion, applied: true, appliedValue: value));
                    break;
                }

                default:
                    // Title/Description já entraram como texto; nada a aplicar aqui.
                    results.Add(Copy(suggestion, applied: false, reason: "Field is content-only."));
                    break;
            }
        }

        if (priorityFloored)
        {
            logger.LogInformation(
                "Sugestão de prioridade da IA bloqueada pelo piso em escalação (evento {MonitoringEventId}).",
                monitoringEvent.Id);
        }

        return new AutoTicketAppliedSuggestion
        {
            Title = title,
            Description = description,
            Priority = priority,
            // Só preenchidos quando a IA realmente aplicou (ou quando o piso
            // determinístico definiu a prioridade).
            DepartmentId = departmentId,
            Category = category,
            Suggestions = results,
            PriorityFloored = priorityFloored
        };
    }

    private static AutoTicketSuggestionItem Copy(
        AutoTicketSuggestionItem source, bool applied, string? reason = null, string? appliedValue = null)
        => new()
        {
            Field = source.Field,
            SuggestedValue = source.SuggestedValue,
            Confidence = source.Confidence,
            Rationale = source.Rationale,
            Applied = applied,
            NotAppliedReason = reason,
            AppliedValue = appliedValue
        };
}
