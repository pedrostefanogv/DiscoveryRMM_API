using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;

namespace Discovery.Tests;

/// <summary>
/// Resolver de política para testes: devolve sempre a mesma política resolvida,
/// opcionalmente marcando qual nível desligou.
/// </summary>
public sealed class FakeAutoTicketPolicyResolver : IAutoTicketPolicyResolver
{
    private readonly ResolvedAutoTicketPolicy _policy;

    public FakeAutoTicketPolicyResolver(ResolvedAutoTicketPolicy? policy = null)
        => _policy = policy ?? ActivePolicy();

    /// <summary>Política ativa (equivale a Enabled=true + Mode=Active).</summary>
    public static ResolvedAutoTicketPolicy ActivePolicy() => new()
    {
        Enabled = true,
        Mode = AutoTicketScopeMode.Active,
        MinConsecutiveFailures = 0,
        MinDurationSeconds = 0,
        FlapWindowMinutes = 0,
        FlapTransitionsThreshold = 0,
        SuppressWhenHostNotReachable = false,
        RespectAgentMaintenance = false,
        RespectMaintenanceWindow = false
    };

    /// <summary>Política desligada em um nível específico.</summary>
    public static ResolvedAutoTicketPolicy DisabledAt(string level) => new()
    {
        Enabled = false,
        Mode = AutoTicketScopeMode.Off,
        DisabledByLevel = level
    };

    /// <summary>Política em shadow (avalia mas nunca cria).</summary>
    public static ResolvedAutoTicketPolicy ShadowPolicy() => new()
    {
        Enabled = true,
        Mode = AutoTicketScopeMode.Shadow
    };

    public Task<ResolvedAutoTicketPolicy> ResolveAsync(
        Guid? clientId = null, Guid? siteId = null, CancellationToken cancellationToken = default)
        => Task.FromResult(_policy);

    public Task<string?> GetRawPolicyJsonAsync(
        string level, Guid? targetId = null, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);
}

/// <summary>
/// Avaliador de sinal para testes: devolve um veredito fixo, permitindo isolar o
/// pipeline de criação das regras de persistência/flapping/manutenção.
/// </summary>
public sealed class FakeAutoTicketSignalEvaluator : IAutoTicketSignalEvaluator
{
    private readonly AutoTicketSignalVerdict _verdict;

    public FakeAutoTicketSignalEvaluator(AutoTicketSignalVerdict? verdict = null)
        => _verdict = verdict ?? new AutoTicketSignalVerdict
        {
            Decision = AutoTicketSignalDecision.Open,
            Reason = "Test signal is open.",
            Score = 90,
            Impact = AutoTicketImpact.High
        };

    public static FakeAutoTicketSignalEvaluator Suppressing(AutoTicketSuppressionSource source)
        => new(new AutoTicketSignalVerdict
        {
            Decision = AutoTicketSignalDecision.Suppress,
            SuppressionSource = source,
            Reason = $"Suppressed by {source} (test)."
        });

    public static FakeAutoTicketSignalEvaluator Holding()
        => new(new AutoTicketSignalVerdict
        {
            Decision = AutoTicketSignalDecision.Hold,
            Reason = "Signal not persistent (test)."
        });

    public Task<AutoTicketSignalVerdict> EvaluateAsync(
        AgentMonitoringEvent monitoringEvent,
        ResolvedAutoTicketPolicy policy,
        string? sensorKey = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(_verdict);
}

/// <summary>
/// Enriquecimento de teste: por padrão devolve fallback (equivalente a "IA
/// desligada"), que é o estado de produção. Assim os testes existentes seguem
/// exercitando o caminho determinístico.
/// </summary>
public sealed class FakeAutoTicketEnrichmentService : IAutoTicketEnrichmentService
{
    private readonly AutoTicketEnrichment _result;

    public FakeAutoTicketEnrichmentService(AutoTicketEnrichment? result = null)
        => _result = result ?? AutoTicketEnrichment.Fallback("AI disabled in tests.");

    public Task<AutoTicketEnrichment> EnrichAsync(
        AgentMonitoringEvent monitoringEvent,
        ResolvedAutoTicketPolicy policy,
        CancellationToken cancellationToken = default)
        => Task.FromResult(_result);
}

/// <summary>
/// Aplicador de teste: resolve os valores finais sem tocar no banco, mas
/// mantendo a semântica do PISO DE PRIORIDADE — que é justamente o que os
/// testes precisam poder verificar.
/// </summary>
public sealed class FakeAutoTicketSuggestionApplier : IAutoTicketSuggestionApplier
{
    public List<AutoTicketSuggestionItem> LastSuggestions { get; } = [];

    public Task<AutoTicketAppliedSuggestion> ApplyAsync(
        AgentMonitoringEvent monitoringEvent,
        ResolvedAutoTicketPolicy policy,
        AutoTicketEnrichment enrichment,
        Discovery.Core.Enums.TicketPriority deterministicPriority,
        bool isEscalation,
        CancellationToken cancellationToken = default)
    {
        LastSuggestions.Clear();
        LastSuggestions.AddRange(enrichment.Suggestions);

        return Task.FromResult(new AutoTicketAppliedSuggestion
        {
            Title = enrichment.Title,
            Description = enrichment.Description,
            Priority = deterministicPriority,
            Suggestions = enrichment.Suggestions
        });
    }
}
