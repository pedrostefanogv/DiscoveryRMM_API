using Discovery.Core.Enums;

namespace Discovery.Core.DTOs;

public class AgentAutomationPolicySyncRequest
{
    public string? KnownPolicyFingerprint { get; set; }
    public bool IncludeScriptContent { get; set; }
}

public class AgentAutomationPolicySyncResponse
{
    public bool UpToDate { get; set; }
    public string PolicyFingerprint { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; }
    public int TaskCount { get; set; }
    public IReadOnlyList<AgentAutomationTaskPolicyDto> Tasks { get; set; } = [];

    /// <summary>
    /// Packages que o agent deve avaliar para PRÉ-CARGA no cache P2P
    /// (download+publish sem instalar), vindos das tasks ativas do escopo do
    /// agent com o ActionType de origem. O AGENT decide pela necessidade real:
    /// InstallPackage → baixa só se o pacote NÃO está instalado;
    /// UpdatePackage → baixa só se há update pendente;
    /// UpdateOrInstallPackage → baixa se não instalado OU há update pendente.
    /// Assim o instalador não é re-baixado quando o pacote já está em estado
    /// final (instalado/atualizado), evitando desperdiçar disco/banda — com o
    /// tempo o TTL local remove o arquivo e ele não volta.
    /// </summary>
    public IReadOnlyList<AgentPreloadPackageDto>? PreloadPackages { get; set; }
}

public class AgentPreloadPackageDto
{
    public string PackageId { get; set; } = string.Empty;
    public AutomationTaskActionType ActionType { get; set; }
}

public class AgentAutomationTaskPolicyDto
{
    public Guid TaskId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public AutomationTaskActionType ActionType { get; set; }
    public AppInstallationType? InstallationType { get; set; }
    public string? PackageId { get; set; }
    public Guid? ScriptId { get; set; }
    public string? CommandPayload { get; set; }
    public AppApprovalScopeType ScopeType { get; set; }
    public bool RequiresApproval { get; set; }
    public bool TriggerImmediate { get; set; }
    public bool TriggerRecurring { get; set; }
    public bool TriggerOnUserLogin { get; set; }
    public bool TriggerOnAgentCheckIn { get; set; }
    public string? ScheduleCron { get; set; }
    public IReadOnlyList<string> IncludeTags { get; set; } = [];
    public IReadOnlyList<string> ExcludeTags { get; set; } = [];
    public DateTime LastUpdatedAt { get; set; }
    public AgentAutomationScriptRefDto? Script { get; set; }
}

public class AgentAutomationScriptRefDto
{
    public Guid ScriptId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public AutomationScriptType ScriptType { get; set; }
    public DateTime LastUpdatedAt { get; set; }
    public string ContentHashSha256 { get; set; } = string.Empty;
    public string? Content { get; set; }
    public string? ParametersSchemaJson { get; set; }
    public string? MetadataJson { get; set; }
}
