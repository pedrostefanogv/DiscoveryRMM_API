namespace Discovery.Core.Interfaces;

/// <summary>Estado de agendamento de um processo em segundo plano.</summary>
public sealed record BackgroundScheduleProcessState(
    string Process,
    string JobName,
    string JobGroup,
    /// <summary>Tick desejado (configuração global do banco).</summary>
    int TickSeconds,
    /// <summary>Tick efetivamente aplicado no scheduler (null = ainda não aplicado).</summary>
    int? AppliedTickSeconds,
    /// <summary>Kill switch de operação (appsettings).</summary>
    bool Enabled,
    DateTimeOffset? NextFireTimeUtc);

/// <summary>Resultado da sincronização/aplicação do agendamento.</summary>
public sealed record BackgroundScheduleSnapshot(
    DateTimeOffset AppliedAt,
    IReadOnlyList<BackgroundScheduleProcessState> Processes);

/// <summary>
/// Abstração mínima do scheduler usada para reprogramar os triggers dos
/// processamentos em segundo plano (permite testar a regra sem o IScheduler).
/// </summary>
public interface IBackgroundProcessingScheduler
{
    /// <summary>
    /// Reprograma o trigger do job para o intervalo informado. Devolve o próximo
    /// disparo ou null quando o job não está agendado.
    /// </summary>
    Task<DateTimeOffset?> RescheduleAsync(
        string jobName, string jobGroup, int intervalSeconds, int startupDelaySeconds,
        CancellationToken ct = default);

    /// <summary>Próximo disparo do job (null quando não há trigger).</summary>
    Task<DateTimeOffset?> GetNextFireTimeAsync(
        string jobName, string jobGroup, CancellationToken ct = default);
}

/// <summary>
/// Aplica no Quartz o tick definido na configuração global (banco), sem
/// restart. O appsettings permanece como kill switch de operação e como
/// fallback do delay de startup.
/// </summary>
public interface IBackgroundProcessingScheduleService
{
    /// <summary>
    /// Sincroniza os triggers com a configuração. Reprograma apenas o que mudou
    /// (a não ser com force = true, usado no startup).
    /// </summary>
    Task<BackgroundScheduleSnapshot> ApplyAsync(bool force = false, CancellationToken ct = default);

    /// <summary>Estado atual do agendamento (tick desejado, aplicado e próximo disparo).</summary>
    Task<BackgroundScheduleSnapshot> GetStatusAsync(CancellationToken ct = default);
}
