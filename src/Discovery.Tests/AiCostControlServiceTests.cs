using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// Cost control de IA (fallback local, sem Redis): budget diário e rate limit.
/// </summary>
public class AiCostControlServiceTests
{
    private static AiCostControlService Build()
        => new(null, NullLogger<AiCostControlService>.Instance);

    private static AIIntegrationSettings Settings(int rateLimitPerMinute, int tokenBudgetDaily) => new()
    {
        CostControlEnabled = true,
        RateLimitPerMinute = rateLimitPerMinute,
        TokenBudgetDaily = tokenBudgetDaily
    };

    [Test]
    public async Task BudgetBlock_DoesNotConsumeRateSlot()
    {
        var service = Build();
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();

        var blocked = await service.TryAcquireAsync(clientId, siteId, Settings(1, 0));
        Assert.That(blocked, Is.False, "budget zero bloqueia a requisição");

        var allowed = await service.TryAcquireAsync(clientId, siteId, Settings(1, 100000));
        Assert.That(allowed, Is.True,
            "o bloqueio por budget não pode consumir a quota de rate limit do mesmo escopo");
    }

    [Test]
    public async Task RateLimit_BlocksAfterTheConfiguredCalls()
    {
        var service = Build();
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var settings = Settings(2, 100000);

        Assert.That(await service.TryAcquireAsync(clientId, siteId, settings), Is.True);
        Assert.That(await service.TryAcquireAsync(clientId, siteId, settings), Is.True);
        Assert.That(await service.TryAcquireAsync(clientId, siteId, settings), Is.False);
    }

    [Test]
    public async Task RecordUsage_AccumulatesTokens_AndBlocksBudget()
    {
        var service = Build();
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var settings = Settings(100, 150);

        Assert.That(await service.TryAcquireAsync(clientId, siteId, settings), Is.True);
        await service.RecordUsageAsync(clientId, siteId, 100);
        Assert.That(await service.TryAcquireAsync(clientId, siteId, settings), Is.True);
        await service.RecordUsageAsync(clientId, siteId, 60);
        Assert.That(await service.TryAcquireAsync(clientId, siteId, settings), Is.False);
    }

    [Test]
    public async Task DisabledCostControl_AlwaysAllows()
    {
        var service = Build();
        var settings = new AIIntegrationSettings { CostControlEnabled = false, TokenBudgetDaily = 0 };

        Assert.That(await service.TryAcquireAsync(Guid.NewGuid(), Guid.NewGuid(), settings), Is.True);
    }

    [Test]
    public async Task Scopes_AreIsolated()
    {
        var service = Build();
        var settings = Settings(1, 100000);
        var siteId = Guid.NewGuid();

        Assert.That(await service.TryAcquireAsync(Guid.NewGuid(), siteId, settings), Is.True);
        Assert.That(await service.TryAcquireAsync(Guid.NewGuid(), siteId, settings), Is.True,
            "clientes diferentes têm escopos de rate limit independentes");
    }
}
