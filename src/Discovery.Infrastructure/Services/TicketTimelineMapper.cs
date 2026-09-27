using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Converte o log de auditoria (`TicketActivityLog`) no DTO da timeline,
/// resolvendo rótulos e montando uma descrição legível (pt-BR).
///
/// Mantido puro/estático para ser testável sem banco: o handler entrega o mapa
/// de rótulos (`raw value` → nome resolvido) e o nome de quem alterou.
/// </summary>
public static class TicketTimelineMapper
{
    /// <summary>
    /// Teto para os valores crus/labels no payload. `DescriptionUpdated` guarda a
    /// descrição inteira em OldValue/NewValue; sem cap o evento infla a resposta.
    /// </summary>
    public const int MaxValueLength = 500;

    public static TicketTimelineEntryDto Map(
        TicketActivityLog log,
        string? changedByName,
        IReadOnlyDictionary<string, string>? labelByValue = null)
    {
        // Resolve pelo valor CRU (o mapa é indexado por ele) e só então capa o
        // texto exibido.
        var oldLabel = Truncate(ResolveLabel(log.OldValue, labelByValue));
        var newLabel = Truncate(ResolveLabel(log.NewValue, labelByValue));

        return new TicketTimelineEntryDto(
            log.Id,
            log.TicketId,
            log.Type,
            log.ChangedByUserId,
            changedByName,
            Truncate(log.OldValue),
            Truncate(log.NewValue),
            oldLabel,
            newLabel,
            log.Comment,
            BuildDescription(log.Type, log.Comment, oldLabel, newLabel),
            log.CreatedAt);
    }

    /// <summary>
    /// Usa o rótulo resolvido quando existir; senão devolve o valor cru
    /// (cobre prioridade/categoria, que já são texto, e GUIDs não resolvidos).
    /// </summary>
    public static string? ResolveLabel(
        string? raw,
        IReadOnlyDictionary<string, string>? labelByValue)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (labelByValue is not null
            && labelByValue.TryGetValue(raw, out var label)
            && !string.IsNullOrWhiteSpace(label))
        {
            return label;
        }

        // "none" é o marcador usado para "sem departamento" no log.
        return raw.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : raw;
    }

    public static string BuildDescription(
        TicketActivityType type,
        string? comment,
        string? oldLabel,
        string? newLabel)
    {
        string Change(string what)
        {
            if (!string.IsNullOrWhiteSpace(oldLabel) && !string.IsNullOrWhiteSpace(newLabel))
                return $"{what}: {oldLabel} → {newLabel}";
            if (!string.IsNullOrWhiteSpace(newLabel))
                return $"{what} definido: {newLabel}";
            if (!string.IsNullOrWhiteSpace(oldLabel))
                return $"{what} removido (era {oldLabel})";
            return $"{what} alterado";
        }

        return type switch
        {
            TicketActivityType.Created => "Chamado criado",
            // StateChanged também é gravado na troca de perfil de workflow
            // (comment fixo) — não é transição de estado, e os valores são GUIDs
            // de perfil (não resolvidos), então evitamos "Estado: <guid> → <guid>".
            TicketActivityType.StateChanged when IsWorkflowProfileChange(comment) => "Perfil de workflow alterado",
            TicketActivityType.StateChanged => Change("Estado"),
            TicketActivityType.Assigned => Change("Responsável"),
            TicketActivityType.PriorityChanged => Change("Prioridade"),
            TicketActivityType.DepartmentChanged => Change("Departamento"),
            TicketActivityType.RequesterChanged => Change("Solicitante"),
            TicketActivityType.AgentChanged => Change("Máquina"),
            // Triagem por IA: newValue é o usuário escolhido e o comment traz
            // dificuldade/origem/confiança.
            TicketActivityType.AiAssigned => CombineAi("Atribuído pela triagem por IA", newLabel, comment),
            TicketActivityType.AiAssignmentSuggested => CombineAi("Sugestão de responsável pela IA", newLabel, comment),
            TicketActivityType.CategoryChanged => Change("Categoria"),
            TicketActivityType.DescriptionUpdated => "Descrição atualizada",
            TicketActivityType.Commented => "Comentário adicionado",
            TicketActivityType.Rated => string.IsNullOrWhiteSpace(newLabel)
                ? "Chamado avaliado"
                : $"Chamado avaliado com nota {newLabel}",
            TicketActivityType.Reopened => "Chamado reaberto",
            TicketActivityType.Deleted => "Chamado excluído",
            TicketActivityType.SlaWarning => "Aviso de SLA",
            TicketActivityType.SlaBreached => "SLA violado",
            TicketActivityType.Escalated => "Chamado escalado",
            TicketActivityType.RemoteSessionStarted => "Sessão remota iniciada",
            TicketActivityType.RemoteSessionEnded => "Sessão remota encerrada",
            TicketActivityType.AutoCreatedFromAlert => "Criado automaticamente por alerta",
            TicketActivityType.TicketMerged => "Chamados mesclados",
            // Tipos cujo texto específico já vem no Comment (ex.: relação, KB,
            // automação) — preserva o detalhe em vez de generalizar.
            TicketActivityType.TicketRelationAdded => comment ?? "Relacionamento adicionado",
            TicketActivityType.TicketRelationRemoved => comment ?? "Relacionamento removido",
            TicketActivityType.KnowledgeLinked => comment ?? "Artigo vinculado",
            TicketActivityType.KnowledgeUnlinked => comment ?? "Artigo desvinculado",
            TicketActivityType.AutomationLinked => comment ?? "Automação vinculada",
            TicketActivityType.AutomationApproved => comment ?? "Automação aprovada",
            TicketActivityType.AutomationRejected => comment ?? "Automação rejeitada",
            _ => comment ?? type.ToString()
        };
    }

    private static string? Truncate(string? value)
        => value is null || value.Length <= MaxValueLength
            ? value
            : value[..MaxValueLength].TrimEnd() + "…";

    private static bool IsWorkflowProfileChange(string? comment)
        => comment is not null
           && comment.Contains("workflow", StringComparison.OrdinalIgnoreCase);

    private static string CombineAi(string prefix, string? newLabel, string? comment)
    {
        var who = string.IsNullOrWhiteSpace(newLabel) ? string.Empty : $": {newLabel}";
        var detail = string.IsNullOrWhiteSpace(comment) ? string.Empty : $" — {comment}";
        return $"{prefix}{who}{detail}";
    }
}
