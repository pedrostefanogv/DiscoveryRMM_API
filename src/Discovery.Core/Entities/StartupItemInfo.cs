namespace Discovery.Core.Entities;

/// <summary>
/// Item de inicialização do Windows coletado pelo agent (múltiplas origens:
/// Run/RunOnce HKLM/HKCU 64 e 32 bits, pastas Startup e serviços automáticos).
/// Persistido como parte do JSON de componentes de hardware do agent.
/// </summary>
public class StartupItemInfo
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Args { get; set; } = string.Empty;
    /// <summary>registry | folder | service</summary>
    public string Type { get; set; } = string.Empty;
    /// <summary>Origem coletada (ex.: "HKLM Run", "Pasta Startup (Usuário)", "Serviço").</summary>
    public string Source { get; set; } = string.Empty;
    /// <summary>enabled | disabled (StartupApproved / Start do serviço).</summary>
    public string Status { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    /// <summary>Detalhe de exibição (ex.: serviços: "Automático (Atrasado)").</summary>
    public string Detail { get; set; } = string.Empty;
}
