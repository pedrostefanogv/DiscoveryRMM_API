namespace Discovery.Core.Enums;

/// <summary>Evento da timeline do sensor (transições e erros).</summary>
public enum SensorEventKind
{
    FirstRun = 0,
    StatusChanged = 1,
    SeverityChanged = 2,
    AlertRaised = 3,
    AlertResolved = 4,
    RunError = 5,
    Reset = 6
}
