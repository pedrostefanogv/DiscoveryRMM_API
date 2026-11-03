namespace Discovery.Core.Enums;

/// <summary>
/// Severidade do sensor. Mapeia para <see cref="MonitoringEventSeverity"/> ao
/// emitir eventos: Info => Attention, Warning => Warning, Critical => Critical.
/// </summary>
public enum SensorSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2
}
