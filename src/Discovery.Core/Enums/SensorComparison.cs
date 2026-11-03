namespace Discovery.Core.Enums;

/// <summary>
/// Direção do canal primário em relação ao limiar.
/// LowerIsWorse: disco livre (quanto menor, pior).
/// HigherIsWorse: CPU/memória usados, latência (quanto maior, pior).
/// </summary>
public enum SensorComparison
{
    LowerIsWorse = 0,
    HigherIsWorse = 1
}
