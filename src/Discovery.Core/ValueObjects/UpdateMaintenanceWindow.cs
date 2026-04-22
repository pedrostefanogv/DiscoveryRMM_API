namespace Discovery.Core.ValueObjects;

/// <summary>
/// Configuração de uma janela de manutenção para atualizações automáticas.
/// </summary>
public class UpdateMaintenanceWindow
{
    /// <summary>Dia da semana (Monday, Tuesday, ..., Sunday)</summary>
    public string DayOfWeek { get; set; } = string.Empty;
    
    /// <summary>Hora de início (HH:mm, ex: "22:00")</summary>
    public string StartTime { get; set; } = string.Empty;
    
    /// <summary>Hora de término (HH:mm, ex: "23:59")</summary>
    public string EndTime { get; set; } = string.Empty;
    
    /// <summary>Resumo legível</summary>
    public string GetDisplayName() => $"{DayOfWeek} {StartTime} → {EndTime}";
}
