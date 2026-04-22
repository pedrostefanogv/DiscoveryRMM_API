using System.Text.Json.Serialization;

namespace Discovery.Core.ValueObjects;

/// <summary>
/// Configurações de atualização automática de software nos agents.
/// </summary>
public class AutoUpdateSettings
{
    /// <summary>Habilita atualizações automáticas</summary>
    public bool Enabled { get; set; } = true;
    
    /// <summary>Intervalo de verificação (horas)</summary>
    public int CheckEveryHours { get; set; } = 4;
    
    /// <summary>Permite que o usuário atrase a atualização</summary>
    public bool AllowUserDelay { get; set; } = true;
    
    /// <summary>Máximo de horas que o usuário pode adiar</summary>
    public int MaxDelayHours { get; set; } = 24;
    
    /// <summary>Força reinicialização após atualização (sem opção)</summary>
    public bool ForceRestartDelay { get; set; } = false;
    
    /// <summary>Delay para reinicialização após atualização (horas)</summary>
    public int RestartDelayHours { get; set; } = 8;
    
    /// <summary>Procura atualizações ao fazer login do usuário</summary>
    public bool UpdateOnLogon { get; set; } = true;
    
    /// <summary>Janelas de manutenção para atualizações programadas</summary>
    [JsonPropertyName("maintenanceWindows")]
    public UpdateMaintenanceWindow[] MaintenanceWindows { get; set; } = [];
    
    /// <summary>Desabilita notificações ao usuário durante atualização</summary>
    public bool SilentInstall { get; set; } = false;
    
    /// <summary>Rollback automático se falhar (requer snapshot)</summary>
    public bool AutoRollbackOnFailure { get; set; } = false;
}
