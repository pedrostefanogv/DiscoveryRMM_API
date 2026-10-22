using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Enforcement de orçamento mensal de IA (painel de custo): bloqueio no ponto
/// único de corte (TryAcquireAsync) e fail-open quando budget null/leitor indisponível.
/// </summary>
[TestFixture]
public class AiCostBudgetEnforcementTests
{
    [Test]
    public async Task BlocksNewCalls_WhenMonthlyCostReachesBudget()
    {
        var service = Build(reader: _ => Task.FromResult<double?>(10d), budgetUsdMonthly: 10);

        var allowed = await service.TryAcquireAsync(Guid.NewGuid(), Guid.NewGuid(), Settings(budgetUsdMonthly: 10, rateLimit: 10));

        Assert.That(allowed, Is.False, "custo do mês >= budget bloqueia novas chamadas");
    }

    [Test]
    public async Task FailsOpen_WhenBudgetNotConfigured()
    {
        var service = Build(reader: _ => Task.FromResult<double?>(9999d), budgetUsdMonthly: 10);

        var settings = Settings(budgetUsdMonthly: null, rateLimit: 10);
        Assert.That(await service.TryAcquireAsync(Guid.NewGuid(), Guid.NewGuid(), settings), Is.True,
            "budget null = fail-open (sem orçamento, sem bloqueio)");
    }

    [Test]
    public async Task FailsOpen_WhenMonthlyCostUnavailable()
    {
        var service = Build(reader: _ => Task.FromResult<double?>(null), budgetUsdMonthly: 10);

        Assert.That(await service.TryAcquireAsync(Guid.NewGuid(), Guid.NewGuid(), Settings(10, 10)), Is.True,
            "leitor de custo indisponível não deve bloquear o cliente");
    }

    [Test]
    public async Task Allows_WhenMonthlyCostBelowBudget()
    {
        var service = Build(reader: _ => Task.FromResult<double?>(9.99d), budgetUsdMonthly: 10);

        Assert.That(await service.TryAcquireAsync(Guid.NewGuid(), Guid.NewGuid(), Settings(10, 10)), Is.True);
    }

    private static AiCostControlService Build(Func<Guid, Task<double?>> reader, double? budgetUsdMonthly)
        => new(null, NullLogger<AiCostControlService>.Instance, new FakeMonthlyCostReader(reader));

    private static AIIntegrationSettings Settings(double? budgetUsdMonthly, int rateLimit) => new()
    {
        CostControlEnabled = true,
        RateLimitPerMinute = rateLimit,
        TokenBudgetDaily = int.MaxValue,
        BudgetUsdMonthly = budgetUsdMonthly
    };

    private sealed class FakeMonthlyCostReader(Func<Guid, Task<double?>> reader) : IAiMonthlyCostReader
    {
        public Task<double?> GetMonthToDateCostUsdAsync(Guid clientId, CancellationToken ct = default) => reader(clientId);
    }
}
