using System.Diagnostics.Metrics;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Metricas do processo de auto-labeling.
///
/// Usa <see cref="System.Diagnostics.Metrics"/> (padrao do .NET): qualquer exporter
/// (OpenTelemetry/Prometheus) configurado no host coleta os valores automaticamente.
/// Serve para acompanhar custo (avaliacoes), efeito (labels aplicadas/removidas) e
/// saude (conflitos de unicidade entre avaliadores concorrentes).
/// </summary>
public static class LabelingMetrics
{
    public const string MeterName = "Discovery.AgentLabeling";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    /// <summary>Agentes avaliados pelo motor.</summary>
    public static readonly Counter<long> AgentsEvaluated = Meter.CreateCounter<long>(
        "agent_labeling.agents_evaluated", unit: "agents", description: "Agentes avaliados pelo motor de labels.");

    /// <summary>Labels automaticas aplicadas.</summary>
    public static readonly Counter<long> LabelsAdded = Meter.CreateCounter<long>(
        "agent_labeling.labels_added", unit: "labels", description: "Labels automaticas aplicadas.");

    /// <summary>Labels automaticas removidas.</summary>
    public static readonly Counter<long> LabelsRemoved = Meter.CreateCounter<long>(
        "agent_labeling.labels_removed", unit: "labels", description: "Labels automaticas removidas.");

    /// <summary>Labels MANUAIS removidas por regras no modo Remover.</summary>
    public static readonly Counter<long> ManualLabelsRemoved = Meter.CreateCounter<long>(
        "agent_labeling.manual_labels_removed", unit: "labels", description: "Labels manuais removidas pelo modo Remove.");

    /// <summary>Conflitos de concorrencia (linha ja removida por outro avaliador).</summary>
    public static readonly Counter<long> ConcurrencyConflicts = Meter.CreateCounter<long>(
        "agent_labeling.concurrency_conflicts", unit: "conflicts", description: "Conflitos de concorrencia ao gravar labels.");

    /// <summary>Conflitos de unicidade ao gravar um lote (corrida entre avaliadores).</summary>
    public static readonly Counter<long> UniqueConflicts = Meter.CreateCounter<long>(
        "agent_labeling.unique_conflicts", unit: "conflicts", description: "Conflitos de unicidade ao gravar labels.");
}
