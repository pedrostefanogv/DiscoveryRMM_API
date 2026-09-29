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

/// <summary>
/// Item do historico de aplicacao/remocao de labels de um agente. Os dados ja eram
/// gravados em agent_label_change_logs, mas nao havia endpoint nem tela para consulta-los.
/// </summary>
public class AgentLabelChangeLogDto
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public string Label { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;

    /// <summary>"Added" ou "Removed".</summary>
    public string Action { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? Actor { get; set; }
    public DateTime OccurredAt { get; set; }
}
