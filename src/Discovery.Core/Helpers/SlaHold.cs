using Discovery.Core.Entities;

namespace Discovery.Core.Helpers;

/// <summary>
/// Regra central de pausa do SLA (hold): entra em pausa ao assumir um estado
/// com <c>PausesSla=true</c>, acumula o tempo de pausa ao sair e mantém a pausa
/// enquanto permanecer. Também concentra o cálculo da expiração efetiva (SLA de
/// resolução e de primeira resposta), reutilizado pelo serviço de SLA e pelo KPI,
/// com o relógio congelado no fechamento (<c>ClosedAt</c>).
/// </summary>
public static class SlaHold
{
    /// <summary>
    /// Aplica a mudança de estado considerando a origem (pode ser nula em
    /// chamados órfãos/sanitização) e o hold já em andamento no chamado.
    /// </summary>
    public static void ApplyStateChange(Ticket ticket, WorkflowState? oldState, WorkflowState newState, DateTime now)
        => Apply(ticket, oldState?.PausesSla == true || ticket.SlaHoldStartedAt.HasValue, newState.PausesSla, now);

    /// <summary>
    /// Liga/desliga o hold. Se vai pausar, inicia o hold (preservando o atual).
    /// Se estava pausado e vai sair, acumula o tempo decorrido desde o início.
    /// </summary>
    public static void Apply(Ticket ticket, bool wasOnHold, bool willBeOnHold, DateTime now)
    {
        if (willBeOnHold)
        {
            ticket.SlaHoldStartedAt ??= now;
            return;
        }

        if (wasOnHold)
            ticket.SlaPausedSeconds += ElapsedSeconds(ticket.SlaHoldStartedAt, now);

        ticket.SlaHoldStartedAt = null;
    }

    /// <summary>Segundos decorridos desde o início do hold (0 se nulo/negativo).</summary>
    public static int ElapsedSeconds(DateTime? startedAt, DateTime now)
    {
        if (startedAt is not { } start) return 0;
        var seconds = (now - start).TotalSeconds;
        return seconds > 0 ? (int)seconds : 0;
    }

    /// <summary>Relógio do SLA: em chamado encerrado congela no <c>ClosedAt</c>.</summary>
    public static DateTime GetClockNow(Ticket ticket) => GetClockNow(ticket.ClosedAt);

    /// <summary>Relógio do SLA a partir do instante de fechamento.</summary>
    public static DateTime GetClockNow(DateTime? closedAt)
    {
        if (closedAt is not { } value) return DateTime.UtcNow;
        return value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    /// <summary>Total de segundos pausados, incluindo o hold corrente até o relógio.</summary>
    public static int TotalPausedSeconds(Ticket ticket, DateTime clockNow)
        => TotalPausedSeconds(ticket.SlaPausedSeconds, ticket.SlaHoldStartedAt, clockNow);

    /// <summary>Total de segundos pausados a partir dos campos crus do chamado.</summary>
    public static int TotalPausedSeconds(int slaPausedSeconds, DateTime? slaHoldStartedAt, DateTime clockNow)
    {
        var total = slaPausedSeconds;
        if (slaHoldStartedAt.HasValue)
        {
            var heldFor = (clockNow - slaHoldStartedAt.Value).TotalSeconds;
            if (heldFor > 0) total += (int)heldFor;
        }
        return total;
    }

    /// <summary>Expiração efetiva: prazo original + pausa acumulada (sempre UTC).</summary>
    public static DateTime? GetEffectiveExpiry(DateTime? expiresAt, Ticket ticket, DateTime clockNow)
        => GetEffectiveExpiry(expiresAt, ticket.SlaPausedSeconds, ticket.SlaHoldStartedAt, clockNow);

    /// <summary>Expiração efetiva a partir dos campos crus do chamado (sempre UTC).</summary>
    public static DateTime? GetEffectiveExpiry(DateTime? expiresAt, int slaPausedSeconds, DateTime? slaHoldStartedAt, DateTime clockNow)
    {
        if (!expiresAt.HasValue) return null;

        var paused = TotalPausedSeconds(slaPausedSeconds, slaHoldStartedAt, clockNow);
        var expiry = expiresAt.Value.AddSeconds(paused);
        return expiry.Kind == DateTimeKind.Utc ? expiry : DateTime.SpecifyKind(expiry, DateTimeKind.Utc);
    }

    /// <summary>Expiração efetiva do SLA de resolução.</summary>
    public static DateTime? GetEffectiveSlaExpiry(Ticket ticket)
        => GetEffectiveExpiry(ticket.SlaExpiresAt, ticket, GetClockNow(ticket));

    /// <summary>Expiração efetiva do SLA de primeira resposta (FRT).</summary>
    public static DateTime? GetEffectiveFrtExpiry(Ticket ticket)
        => GetEffectiveExpiry(ticket.SlaFirstResponseExpiresAt, ticket, GetClockNow(ticket));
}
