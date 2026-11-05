using Discovery.Core.Enums;

namespace Discovery.Core.ValueObjects;

/// <summary>
/// Uma sugestão da IA para um campo do chamado. É SEMPRE registrada em
/// auto_ticket_suggestions, aplicada ou não — daí a taxa de override ser medida.
/// </summary>
public class AutoTicketSuggestionItem
{
    public AutoTicketSuggestionField Field { get; init; }

    /// <summary>Valor sugerido, em texto (GUID serializado quando for id).</summary>
    public string? SuggestedValue { get; init; }

    /// <summary>Confiança que o modelo atribuiu a ESTA sugestão (0..1).</summary>
    public double Confidence { get; init; }

    /// <summary>Justificativa curta do modelo.</summary>
    public string? Rationale { get; init; }

    /// <summary>True quando a sugestão foi efetivamente aplicada ao chamado.</summary>
    public bool Applied { get; init; }

    /// <summary>Motivo de não ter sido aplicada (baixa confiança, inválida, modo Suggest).</summary>
    public string? NotAppliedReason { get; init; }

    /// <summary>Valor que acabou valendo no chamado.</summary>
    public string? AppliedValue { get; init; }
}

/// <summary>
/// Resultado do enriquecimento: o texto redigido e as sugestões de roteamento.
///
/// IMPORTANTE: nada aqui DECIDE se o chamado abre. A decisão é do avaliador de
/// sinal (determinístico). Este objeto apenas melhora o CONTEÚDO do chamado que
/// já foi decidido.
/// </summary>
public class AutoTicketEnrichment
{
    /// <summary>Título sugerido. Null = mantém o determinístico.</summary>
    public string? Title { get; init; }

    /// <summary>Descrição sugerida. Null = mantém a determinística.</summary>
    public string? Description { get; init; }

    /// <summary>Evidências factuais coletadas (métrica, valor, limiar, linha do tempo).</summary>
    public IReadOnlyList<string> Evidence { get; init; } = [];

    /// <summary>Hipóteses de causa sugeridas pelo modelo (claramente marcadas como hipóteses).</summary>
    public IReadOnlyList<string> Hypotheses { get; init; } = [];

    /// <summary>Sugestões de roteamento/prioridade, já validadas.</summary>
    public IReadOnlyList<AutoTicketSuggestionItem> Suggestions { get; init; } = [];

    /// <summary>Confiança global do enriquecimento (0..1).</summary>
    public double Confidence { get; init; }

    /// <summary>Modelo usado (auditoria de custo).</summary>
    public string? Model { get; init; }

    /// <summary>Tokens consumidos (auditoria de orçamento).</summary>
    public int TokensUsed { get; init; }

    /// <summary>
    /// True quando o enriquecimento caiu no determinístico — IA desligada,
    /// indisponível, timeout, JSON inválido ou circuito aberto.
    /// </summary>
    public bool UsedFallback { get; init; }

    /// <summary>Motivo do fallback, para diagnóstico.</summary>
    public string? FallbackReason { get; init; }

    /// <summary>Enriquecimento vazio (usado como fallback seguro).</summary>
    public static AutoTicketEnrichment Fallback(string reason) => new()
    {
        UsedFallback = true,
        FallbackReason = reason,
        Confidence = 0
    };
}
