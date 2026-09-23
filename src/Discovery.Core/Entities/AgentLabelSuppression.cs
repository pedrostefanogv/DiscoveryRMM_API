namespace Discovery.Core.Entities;

/// <summary>
/// Registra que o usuario removeu manualmente uma label AUTOMATICA de um agente.
///
/// Sem isso, a remocao manual nao era duradoura: o match da regra continuava existindo
/// e a reconciliacao seguinte recriava a label. A supressao e limpa quando a regra
/// deixa de casar, de modo que um novo match volte a aplicar a label.
/// </summary>
public class AgentLabelSuppression
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public string Label { get; set; } = string.Empty;
    public DateTime SuppressedAt { get; set; }
    public string? SuppressedBy { get; set; }
}
