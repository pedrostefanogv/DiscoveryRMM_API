using Discovery.Core.Enums;

namespace Discovery.Core.ValueObjects;

/// <summary>
/// Veredito do avaliador de sinal: quem DECIDE se o chamado abre.
///
/// Determinístico e sem IA — segue a orientação do SRE ("rules that generate
/// alerts for humans should be simple to understand and represent a clear
/// failure"). A IA só entra DEPOIS, para redigir e sugerir roteamento.
/// </summary>
public class AutoTicketSignalVerdict
{
    public AutoTicketSignalDecision Decision { get; init; } = AutoTicketSignalDecision.Hold;

    /// <summary>Motivo legível, gravado no Reason da execução (auditoria).</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Origem da supressão, quando <see cref="Decision"/> = Suppress.</summary>
    public AutoTicketSuppressionSource SuppressionSource { get; init; } = AutoTicketSuppressionSource.None;

    /// <summary>Score 0-100 de "quanto este sinal merece um chamado".</summary>
    public int Score { get; init; }

    /// <summary>Impacto efetivo, já considerando o sensor e a severidade.</summary>
    public AutoTicketImpact Impact { get; init; } = AutoTicketImpact.Medium;

    /// <summary>Evidências coletadas — vão para o PayloadSnapshotJson.</summary>
    public IReadOnlyDictionary<string, object?> Evidence { get; init; }
        = new Dictionary<string, object?>();

    public bool ShouldCreate => Decision is AutoTicketSignalDecision.Open or AutoTicketSignalDecision.Escalate;
    public bool ShouldSuppress => Decision == AutoTicketSignalDecision.Suppress;
    public bool ShouldEscalate => Decision == AutoTicketSignalDecision.Escalate;
}
