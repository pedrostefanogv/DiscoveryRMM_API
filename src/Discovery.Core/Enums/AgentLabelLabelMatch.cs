namespace Discovery.Core.Enums;

/// <summary>
/// Como o alvo de uma regra no modo <see cref="AgentLabelApplyMode.Remove"/> casa com
/// as labels do agente. Vale apenas para o modo Remove; os demais modos exigem Exact
/// (uma regra aditiva sempre produz uma label concreta).
/// </summary>
public enum AgentLabelLabelMatch
{
    /// <summary>Igualdade case-insensitive com o nome exato da label.</summary>
    Exact = 0,

    /// <summary>Prefixo (ex.: "TEMP-" remove "TEMP-2026", "TEMP-LAB").</summary>
    Prefix = 1,

    /// <summary>Expressao regular (case-insensitive, com timeout).</summary>
    Regex = 2
}
