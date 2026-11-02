using Discovery.Core.Enums;

namespace Discovery.Core.DTOs;

/// <summary>
/// Quem é o dono da atualização de um pacote num escopo: define quem executa e
/// evita que dois motores (Loja × automação) atualizem o mesmo pacote.
/// </summary>
public enum AppUpdateOwner
{
    /// <summary>Ninguém automático: o operador atualiza pela aba Atualizações do agent.</summary>
    Manual = 0,

    /// <summary>Auto-atualização da Loja (executa dentro da janela de manutenção).</summary>
    AutoUpdate = 1,

    /// <summary>Uma task de automação atualiza o pacote — ela tem precedência.</summary>
    Automation = 2
}

/// <summary>Janela de manutenção configurada no servidor (maintenanceWindowJson).</summary>
public class AppMaintenanceWindowInfoDto
{
    public bool Enabled { get; set; }
    public string StartTime { get; set; } = string.Empty;
    public string TimeZone { get; set; } = "UTC";
    public string StartUtc { get; set; } = string.Empty;

    /// <summary>
    /// Instante UTC (ISO-8601) em que a ocorrência vigente fecha
    /// (abertura + duração). Vazio quando nenhuma janela está aberta agora.
    /// </summary>
    public string EndUtc { get; set; } = string.Empty;
    public int DurationMinutes { get; set; }
    public int RebootGraceSeconds { get; set; }
    public IReadOnlyList<int> DaysOfWeek { get; set; } = [];

    /// <summary>true quando a config não informou timeZone (janela em UTC),
    /// quando a timeZone é UTC ou quando a timeZone informada é inválida.</summary>
    public bool UsesUtc { get; set; }

    /// <summary>true quando utcNow está dentro da ocorrência vigente.</summary>
    public bool IsOpenNow { get; set; }

    /// <summary>Abertura UTC da próxima ocorrência (Kind=Utc); null quando não
    /// há abertura no horizonte de 8 dias.</summary>
    public DateTime? NextStartUtc { get; set; }
}

/// <summary>Intenção de atualização de um pacote no escopo consultado.</summary>
public class AppStoreUpdateIntentItemDto
{
    public string PackageId { get; set; } = string.Empty;
    public string? PackageName { get; set; }
    public AppApprovalActionType Action { get; set; }
    public bool AutoUpdateEnabled { get; set; }
    public AppUpdateOwner Owner { get; set; }

    public Guid? AutomationTaskId { get; set; }
    public string? AutomationTaskName { get; set; }

    /// <summary>Resumo dos gatilhos da task (ex.: "Recorrente (cron: 0 3 * * *)").</summary>
    public string? AutomationTriggerSummary { get; set; }

    /// <summary>true quando a Loja (autoUpdate) e a automação disputam o pacote.</summary>
    public bool Conflict { get; set; }

    public string? ConflictReason { get; set; }
}

/// <summary>
/// Prévia (somente leitura) de quem executaria a atualização de cada pacote
/// aprovado no escopo. Não executa nada: base para a tela "Simular atualizações".
/// </summary>
public class AppStoreUpdateIntentPageDto
{
    public AppApprovalScopeType ScopeType { get; set; }
    public Guid? ScopeId { get; set; }
    public AppInstallationType InstallationType { get; set; }
    public string? Search { get; set; }

    /// <summary>Política efetiva da Loja no escopo (informativa; null quando não resolvida).</summary>
    public AppStorePolicyType? Policy { get; set; }

    public AppMaintenanceWindowInfoDto MaintenanceWindow { get; set; } = new();
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public int Count { get; set; }
    public List<AppStoreUpdateIntentItemDto> Items { get; set; } = [];

    /// <summary>
    /// true quando alguma lista de tasks por escopo bateu o limite de consulta
    /// (500): pode haver tasks não consideradas na simulação.
    /// </summary>
    public bool Truncated { get; set; }
}
