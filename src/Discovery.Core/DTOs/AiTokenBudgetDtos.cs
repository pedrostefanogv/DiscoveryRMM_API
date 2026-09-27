namespace Discovery.Core.DTOs;

/// <summary>
/// Orçamento de tokens resolvido para uma chamada de IA: derivado da capacidade
/// real do modelo (catálogo), do teto do produto e de um teto opcional por
/// departamento. É o insumo que evita tetos fixos subutilizando os modelos.
/// </summary>
public sealed record AiTokenBudgetDto(
    string? Model,
    int? ContextLength,
    int? MaxCompletionTokens,
    int MaxOutputTokens,
    int MaxPromptChars,
    string Source);
