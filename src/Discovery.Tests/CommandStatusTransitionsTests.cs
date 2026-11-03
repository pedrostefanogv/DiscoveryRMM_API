using Discovery.Core.Enums;
using Discovery.Core.Helpers;

namespace Discovery.Tests;

/// <summary>
/// Guarda de monotonicidade do status do comando. O result do agente chega mais
/// de uma vez (ack antecipado + resultado final + reentrega atrasada): o veredito
/// final não pode ser apagado por um ack tardio, mas o result final PRECISA
/// corrigir um ack antecipado otimista (Completed -> Failed).
/// </summary>
[TestFixture]
public class CommandStatusTransitionsTests
{
    [Test]
    public void NonTerminalProgressions_AreAllowed()
    {
        Assert.That(CommandStatusTransitions.ShouldApply(CommandStatus.Pending, CommandStatus.Sent), Is.True);
        Assert.That(CommandStatusTransitions.ShouldApply(CommandStatus.Pending, CommandStatus.Completed), Is.True);
        Assert.That(CommandStatusTransitions.ShouldApply(CommandStatus.Sent, CommandStatus.Running), Is.True);
        Assert.That(CommandStatusTransitions.ShouldApply(CommandStatus.Sent, CommandStatus.Completed), Is.True);
        Assert.That(CommandStatusTransitions.ShouldApply(CommandStatus.Running, CommandStatus.Failed), Is.True);
    }

    [Test]
    public void CompletedToFailed_IsAllowed_SoTheFinalResultCanCorrectTheEarlyAck()
    {
        Assert.That(CommandStatusTransitions.ShouldApply(CommandStatus.Completed, CommandStatus.Failed), Is.True,
            "é o result final corrigindo a confirmação antecipada do agente");
        Assert.That(CommandStatusTransitions.ShouldApply(CommandStatus.Completed, CommandStatus.Deferred), Is.True);
    }

    [Test]
    public void TerminalCannotGoBackToNonTerminal()
    {
        var terminals = new[]
        {
            CommandStatus.Completed, CommandStatus.Failed, CommandStatus.Cancelled,
            CommandStatus.Timeout, CommandStatus.Deferred
        };
        var nonTerminals = new[] { CommandStatus.Pending, CommandStatus.Sent, CommandStatus.Running };

        foreach (var terminal in terminals)
        {
            foreach (var nonTerminal in nonTerminals)
            {
                Assert.That(CommandStatusTransitions.ShouldApply(terminal, nonTerminal), Is.False,
                    $"{terminal} -> {nonTerminal} reabriria um comando encerrado");
            }
        }
    }

    [Test]
    public void NegativeVerdictCannotBecomeCompleted()
    {
        var negative = new[]
        {
            CommandStatus.Failed, CommandStatus.Cancelled, CommandStatus.Timeout, CommandStatus.Deferred
        };

        foreach (var status in negative)
        {
            Assert.That(CommandStatusTransitions.ShouldApply(status, CommandStatus.Completed), Is.False,
                $"{status} -> Completed apagaria a falha do histórico");
        }
    }

    [Test]
    public void NegativeVerdictsMayRefineEachOther()
    {
        Assert.That(CommandStatusTransitions.ShouldApply(CommandStatus.Deferred, CommandStatus.Failed), Is.True);
        Assert.That(CommandStatusTransitions.ShouldApply(CommandStatus.Completed, CommandStatus.Cancelled), Is.True);
    }

    [Test]
    public void IdempotentRepetition_IsAllowed()
    {
        foreach (var status in Enum.GetValues<CommandStatus>())
        {
            Assert.That(CommandStatusTransitions.ShouldApply(status, status), Is.True,
                $"{status} -> {status} não é regressão");
        }
    }

    [Test]
    public void TerminalAndNonTerminalSetsCoverTheWholeEnum()
    {
        foreach (var status in Enum.GetValues<CommandStatus>())
        {
            var terminal = CommandStatusTransitions.IsTerminal(status);
            var nonTerminal = CommandStatusTransitions.IsNonTerminal(status);
            Assert.That(terminal ^ nonTerminal, Is.True, $"{status} precisa ser exatamente um dos dois conjuntos");
        }
    }

    [Test]
    public void DeferredIsTerminal()
    {
        Assert.That(CommandStatusTransitions.IsTerminal(CommandStatus.Deferred), Is.True);
        Assert.That(CommandStatusTransitions.IsNegativeVerdict(CommandStatus.Deferred), Is.True);
    }
}
