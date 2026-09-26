using Pgvector;

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

    /// <summary>
    /// Embedding pgvector da resposta ("{pergunta}: {valor}") para busca semântica.
    /// Null = ainda não indexada (o ciclo do job processa em lote).
    /// </summary>
    public Vector? Embedding { get; set; }

    /// <summary>
    /// Preenchido quando a resposta foi processada (embedada) OU deliberadamente
    /// pulada. Null + embedding null = pendente de processamento.
    /// </summary>
    public DateTime? EmbeddingGeneratedAt { get; set; }

    /// <summary>
    /// Pergunta marcada como sensível no template: nunca é enviada ao provedor de
    /// embeddings nem retornada na busca semântica (segue em filtros exato/contém).
    /// </summary>
    public bool IsSensitive { get; set; }

    public DateTime CreatedAt { get; set; }
}
