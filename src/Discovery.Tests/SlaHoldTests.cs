using Discovery.Core.Entities;
using Discovery.Core.Helpers;

namespace Discovery.Tests;

/// <summary>
/// Testa a regra central de pausa do SLA (SlaHold): entrar, permanecer, sair e
/// múltiplas passagens por estados que pausam o SLA.
/// </summary>
public class SlaHoldTests
{
    private static Ticket NewTicket() => new() { Id = Guid.NewGuid(), SlaPausedSeconds = 0 };

    [Test]
    public void Apply_EnterHold_StartsHoldAndKeepsAccumulated()
    {
        var ticket = NewTicket();
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        SlaHold.Apply(ticket, wasOnHold: false, willBeOnHold: true, now);

        Assert.That(ticket.SlaHoldStartedAt, Is.EqualTo(now));
        Assert.That(ticket.SlaPausedSeconds, Is.EqualTo(0));
    }

    [Test]
    public void Apply_StayOnHold_KeepsOriginalStart()
    {
        var ticket = NewTicket();
        var start = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        ticket.SlaHoldStartedAt = start;

        SlaHold.Apply(ticket, wasOnHold: true, willBeOnHold: true, start.AddMinutes(30));

        Assert.That(ticket.SlaHoldStartedAt, Is.EqualTo(start), "manter o hold não pode reiniciar o relógio da pausa");
        Assert.That(ticket.SlaPausedSeconds, Is.EqualTo(0));
    }

    [Test]
    public void Apply_ExitHold_AccumulatesElapsedAndClearsStart()
    {
        var ticket = NewTicket();
        var start = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        ticket.SlaHoldStartedAt = start;

        SlaHold.Apply(ticket, wasOnHold: true, willBeOnHold: false, start.AddSeconds(90));

        Assert.That(ticket.SlaHoldStartedAt, Is.Null);
        Assert.That(ticket.SlaPausedSeconds, Is.EqualTo(90));
    }

    [Test]
    public void Apply_MultiplePasses_AccumulateEveryPause()
    {
        var ticket = NewTicket();
        var t0 = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

        SlaHold.Apply(ticket, false, true, t0);
        SlaHold.Apply(ticket, true, false, t0.AddSeconds(60));
        SlaHold.Apply(ticket, false, true, t0.AddHours(1));
        SlaHold.Apply(ticket, true, false, t0.AddHours(1).AddSeconds(120));

        Assert.That(ticket.SlaHoldStartedAt, Is.Null);
        Assert.That(ticket.SlaPausedSeconds, Is.EqualTo(180));
    }

    [Test]
    public void ApplyStateChange_NewInitialStatePausesSla_StartsHold()
    {
        // Cobre a criação/reabertura em estado inicial com PausesSla=true.
        var ticket = NewTicket();
        var now = DateTime.UtcNow;
        var initial = new WorkflowState { Id = Guid.NewGuid(), Name = "Aguardando", IsInitial = true, PausesSla = true };

        SlaHold.ApplyStateChange(ticket, oldState: null, initial, now);

        Assert.That(ticket.SlaHoldStartedAt, Is.EqualTo(now));
        Assert.That(ticket.SlaPausedSeconds, Is.EqualTo(0));
    }

    [Test]
    public void ApplyStateChange_LeavingPausingState_AccumulatesPause()
    {
        var ticket = NewTicket();
        var start = DateTime.UtcNow.AddMinutes(-5);
        ticket.SlaHoldStartedAt = start;
        var oldState = new WorkflowState { Id = Guid.NewGuid(), PausesSla = true };
        var newState = new WorkflowState { Id = Guid.NewGuid(), PausesSla = false };
        var now = DateTime.UtcNow;

        SlaHold.ApplyStateChange(ticket, oldState, newState, now);

        Assert.That(ticket.SlaHoldStartedAt, Is.Null);
        Assert.That(ticket.SlaPausedSeconds, Is.InRange(299, 301));
    }

    [Test]
    public void ApplyStateChange_OrphanOriginToPausingState_PreservesExistingHoldStart()
    {
        var ticket = NewTicket();
        var existing = DateTime.UtcNow.AddSeconds(-10);
        ticket.SlaHoldStartedAt = existing;
        var newState = new WorkflowState { Id = Guid.NewGuid(), PausesSla = true };

        SlaHold.ApplyStateChange(ticket, oldState: null, newState, DateTime.UtcNow);

        Assert.That(ticket.SlaHoldStartedAt, Is.EqualTo(existing));
    }

    [Test]
    public void ApplyStateChange_OriginPausesButHoldMissing_LeavingStateStillClears()
    {
        // Sanitização/reparo: origem pausava, mas o hold não estava marcado.
        var ticket = NewTicket();
        var oldState = new WorkflowState { Id = Guid.NewGuid(), PausesSla = true };
        var newState = new WorkflowState { Id = Guid.NewGuid(), PausesSla = false };

        SlaHold.ApplyStateChange(ticket, oldState, newState, DateTime.UtcNow);

        Assert.That(ticket.SlaHoldStartedAt, Is.Null);
        Assert.That(ticket.SlaPausedSeconds, Is.EqualTo(0));
    }

    [Test]
    public void ElapsedSeconds_NullOrFutureStart_ReturnsZero()
    {
        Assert.That(SlaHold.ElapsedSeconds(null, DateTime.UtcNow), Is.EqualTo(0));
        Assert.That(SlaHold.ElapsedSeconds(DateTime.UtcNow.AddSeconds(5), DateTime.UtcNow), Is.EqualTo(0));
    }

    [Test]
    public void GetEffectiveExpiry_AddsAccumulatedAndCurrentHold()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var expires = now.AddHours(1);

        var effective = SlaHold.GetEffectiveExpiry(expires, 600, now.AddMinutes(-2), now);

        Assert.That(effective, Is.EqualTo(expires.AddSeconds(720)));
    }

    [Test]
    public void GetClockNow_ClosedTicket_FreezesAtClose()
    {
        var closedAt = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        var ticket = new Ticket { ClosedAt = closedAt };

        Assert.That(SlaHold.GetClockNow(ticket), Is.EqualTo(closedAt));
    }
}
