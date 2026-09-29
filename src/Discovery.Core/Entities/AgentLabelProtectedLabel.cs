namespace Discovery.Core.Entities;

/// <summary>
/// Label protegida: nenhuma regra no modo Remover pode apaga-la.
///
/// Salvaguarda contra acidentes com rotulos criticos (ex.: PROD, NOTURNO). A regra
/// exata que aponte para uma protegida e rejeitada na validacao; o motor tambem
/// ignora a label no momento da remocao (defesa em profundidade).
/// </summary>
public class AgentLabelProtectedLabel
{
    public Guid Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}
