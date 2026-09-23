namespace Discovery.Core.DTOs;

/// <summary>
/// Supressao visivel ao usuario: uma label automatica que o reconcile nao esta aplicando
/// porque foi removida manualmente. Vale apenas enquanto a condicao da regra continuar
/// verdadeira; quando ela deixa de valer, a supressao e liberada automaticamente.
/// </summary>
public class AgentLabelSuppressionDto
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public string Label { get; set; } = string.Empty;
    public DateTime SuppressedAt { get; set; }
    public string? SuppressedBy { get; set; }

    /// <summary>Nome da regra que produz esta label, quando identificavel.</summary>
    public string? RuleName { get; set; }
}
