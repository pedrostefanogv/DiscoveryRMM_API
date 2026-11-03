namespace Discovery.Core.Enums;

/// <summary>
/// Estado do sensor. Diferente da severidade: um sensor pode estar
/// Pending (nunca executou), Unknown (erro de coleta) ou stale (sem dados
/// recentes — derivado na leitura, não persistido).
/// </summary>
public enum SensorStatus
{
    Pending = 0,
    Passing = 1,
    Warning = 2,
    Failing = 3,
    Unknown = 4
}
