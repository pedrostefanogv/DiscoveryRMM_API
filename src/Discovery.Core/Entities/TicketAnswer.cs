namespace Discovery.Core.Entities;

/// <summary>
/// Resposta de uma pergunta do mini questionário de um template de chamado.
/// Estruturada para permitir filtros e relatórios; o snapshot markdown do
/// chamado continua sendo o registro legível.
/// </summary>
public class TicketAnswer
{
    public Guid Id { get; set; }

    public Guid TicketId { get; set; }

    /// <summary>Template usado na abertura (null = sem template).</summary>
    public Guid? TemplateId { get; set; }

    /// <summary>Chave da pergunta (definida no template).</summary>
    public string QuestionKey { get; set; } = string.Empty;

    /// <summary>Rótulo da pergunta no momento da resposta (histórico).</summary>
    public string QuestionLabel { get; set; } = string.Empty;

    /// <summary>Valor achatado para filtros/exibição (texto, "Sim"/"Não", itens de lista).</summary>
    public string? ValueText { get; set; }

    /// <summary>Valor tipado original (JSON).</summary>
    public string ValueJson { get; set; } = "null";

    /// <summary>Ordem da pergunta no questionário (preserva a ordem de exibição).</summary>
    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; }
}
