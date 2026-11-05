using Discovery.Core.Configuration;
using Discovery.Core.ValueObjects;

namespace Discovery.Core.DTOs;

/// <summary>
/// Política de auto-ticket para a tela de configuração: o que ESTE nível
/// sobrescreve, o valor EFETIVO depois da herança e de ONDE cada campo veio.
///
/// A proveniência é o que permite ao operador responder "por que este site não
/// abre chamado?" sem inspecionar o banco.
/// </summary>
public class AutoTicketPolicyDto
{
    /// <summary>Nível consultado (Server/Client/Site).</summary>
    public string Level { get; set; } = AutoTicketPolicyLevel.Server;

    public Guid? TargetId { get; set; }

    /// <summary>Blob cru deste nível (null = herda tudo).</summary>
    public AutoTicketPolicy? Own { get; set; }

    /// <summary>Política efetiva após aplicar a herança.</summary>
    public ResolvedAutoTicketPolicyView? Effective { get; set; }

    /// <summary>Campo -> nível que decidiu (Server/Client/Site/Locked/Default).</summary>
    public IReadOnlyDictionary<string, string> Provenance { get; set; }
        = new Dictionary<string, string>();
}

/// <summary>Visão serializável da política resolvida.</summary>
public class ResolvedAutoTicketPolicyView
{
    public bool Enabled { get; set; }
    public string Mode { get; set; } = "Off";
    public string? DisabledByLevel { get; set; }
    public int MinConsecutiveFailures { get; set; }
    public int MinDurationSeconds { get; set; }
    public string MinImpactToOpen { get; set; } = "Medium";
    public string MinImpactToEscalate { get; set; } = "High";
    public bool OpenOnWarning { get; set; }
    public bool OpenOnPredictive { get; set; }
    public int MaintenanceGraceMinutes { get; set; }
    public bool ReNotifyAfterMaintenanceEnds { get; set; }
    public bool RespectAgentMaintenance { get; set; }
    public bool RespectMaintenanceWindow { get; set; }
    public bool SuppressWhenHostNotReachable { get; set; }
    public bool DependencySuppressionEnabled { get; set; }
    public int DependencyMaxDepth { get; set; }
    public bool CloseTicketResolvedDuringMaintenance { get; set; }
    public int FlapWindowMinutes { get; set; }
    public int FlapTransitionsThreshold { get; set; }
    public int DedupWindowMinutes { get; set; }
    public int DedupWindowMaxHours { get; set; }
    public int ReopenWindowMinutes { get; set; }
    public int MaxCreatedTicketsPerHourPerAlertCode { get; set; }
    public bool IncrementOccurrenceCounter { get; set; }
    public IReadOnlyList<string> AllowedSensorKeys { get; set; } = [];
    public IReadOnlyList<string> NeverTicketSensorKeys { get; set; } = [];
    public Guid? DefaultDepartmentId { get; set; }
    public string? DefaultCategory { get; set; }
    public string AiMode { get; set; } = "Off";
    public double AiMinConfidence { get; set; }
    public IReadOnlyList<string> AiSuggestibleFields { get; set; } = [];
    public bool AiAllowEquipmentContext { get; set; }
    public bool AiAnonymizeAssetNames { get; set; }

    public static ResolvedAutoTicketPolicyView From(ResolvedAutoTicketPolicy p) => new()
    {
        Enabled = p.Enabled,
        Mode = p.Mode.ToString(),
        DisabledByLevel = p.DisabledByLevel,
        MinConsecutiveFailures = p.MinConsecutiveFailures,
        MinDurationSeconds = p.MinDurationSeconds,
        MinImpactToOpen = p.MinImpactToOpen.ToString(),
        MinImpactToEscalate = p.MinImpactToEscalate.ToString(),
        OpenOnWarning = p.OpenOnWarning,
        OpenOnPredictive = p.OpenOnPredictive,
        MaintenanceGraceMinutes = p.MaintenanceGraceMinutes,
        ReNotifyAfterMaintenanceEnds = p.ReNotifyAfterMaintenanceEnds,
        RespectAgentMaintenance = p.RespectAgentMaintenance,
        RespectMaintenanceWindow = p.RespectMaintenanceWindow,
        SuppressWhenHostNotReachable = p.SuppressWhenHostNotReachable,
        DependencySuppressionEnabled = p.DependencySuppressionEnabled,
        DependencyMaxDepth = p.DependencyMaxDepth,
        CloseTicketResolvedDuringMaintenance = p.CloseTicketResolvedDuringMaintenance,
        FlapWindowMinutes = p.FlapWindowMinutes,
        FlapTransitionsThreshold = p.FlapTransitionsThreshold,
        DedupWindowMinutes = p.DedupWindowMinutes,
        DedupWindowMaxHours = p.DedupWindowMaxHours,
        ReopenWindowMinutes = p.ReopenWindowMinutes,
        MaxCreatedTicketsPerHourPerAlertCode = p.MaxCreatedTicketsPerHourPerAlertCode,
        IncrementOccurrenceCounter = p.IncrementOccurrenceCounter,
        AllowedSensorKeys = p.AllowedSensorKeys,
        NeverTicketSensorKeys = p.NeverTicketSensorKeys,
        DefaultDepartmentId = p.DefaultDepartmentId,
        DefaultCategory = p.DefaultCategory,
        AiMode = p.AiMode.ToString(),
        AiMinConfidence = p.AiMinConfidence,
        AiSuggestibleFields = p.AiSuggestibleFields,
        AiAllowEquipmentContext = p.AiAllowEquipmentContext,
        AiAnonymizeAssetNames = p.AiAnonymizeAssetNames
    };
}
