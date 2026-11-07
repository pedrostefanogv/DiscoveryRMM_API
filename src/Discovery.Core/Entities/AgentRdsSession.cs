namespace Discovery.Core.Entities;

/// <summary>
/// Sessão do Remote Desktop Services ativa no host. Vazio quando a máquina não
/// é RDS Host (o campo <c>rdsHost</c> do payload é a fonte dessa decisão).
/// </summary>
public class AgentRdsSession
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }

    /// <summary>Identificador da sessão no host.</summary>
    public int SessionId { get; set; }

    /// <summary>Usuário da sessão (formato DOMÍNIO\\usuario).</summary>
    public string? Username { get; set; }

    public string? State { get; set; }

    /// <summary>Tipo da sessão (ex.: "rdp-tcp", "console").</summary>
    public string? SessionType { get; set; }

    public DateTime CollectedAt { get; set; }
}
