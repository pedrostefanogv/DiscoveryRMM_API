using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Tests;

public class SensorMuteTests
{
    [Test]
    public async Task MuteAsync_SetsAndClearsMutedUntil()
    {
        await using var db = CreateDb();
        var agentId = Guid.NewGuid();
        var sensorId = Guid.NewGuid();
        db.AgentSensorStates.Add(new AgentSensorState
        {
            Id = Guid.NewGuid(),
            AgentId = agentId,
            SensorId = sensorId,
            TargetKey = "C:",
            Status = SensorStatus.Warning,
        });
        await db.SaveChangesAsync();

        var service = new SensorOperationService(db, new NoopDispatcher(), new StubDefinitionService());

        var mutedUntil = await service.MuteAsync(agentId, sensorId, 60, "C:");
        Assert.That(mutedUntil, Is.Not.Null);
        Assert.That(mutedUntil!.Value, Is.GreaterThan(DateTime.UtcNow.AddMinutes(50)));

        var state = await db.AgentSensorStates.SingleAsync();
        Assert.That(state.MutedUntil, Is.EqualTo(mutedUntil));

        var cleared = await service.MuteAsync(agentId, sensorId, null, "C:");
        Assert.That(cleared, Is.Null);
        await db.Entry(state).ReloadAsync();
        Assert.That(state.MutedUntil, Is.Null);
    }

    private static TestDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"sensor-mute-{Guid.NewGuid():N}")
            .Options;
        return new TestDbContext(options);
    }

    private sealed class TestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                .Where(t => t.ClrType != typeof(AgentSensorState))
                .Select(t => t.ClrType)
                .ToList())
            {
                modelBuilder.Ignore(entityType);
            }
            modelBuilder.Entity<AgentSensorState>(e => e.HasKey(x => x.Id));
        }
    }

    private sealed class NoopDispatcher : IAgentCommandDispatcher
    {
        public Task<AgentCommand> DispatchAsync(AgentCommand command, CancellationToken cancellationToken = default)
            => Task.FromResult(command);

        public Task<AgentCommandDispatchResult> DispatchWithOutcomeAsync(AgentCommand command, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class StubDefinitionService : ISensorDefinitionService
    {
        public Task<Discovery.Core.DTOs.SensorDefinitionPageDto> ListAsync(AppApprovalScopeType? scopeType, Guid? clientId, Guid? siteId, Guid? agentId, bool includeInactive, string? search = null, SensorType? sensorType = null, bool? isActive = null, int? page = null, int? pageSize = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Discovery.Core.DTOs.SensorDefinitionDto?> GetAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Discovery.Core.DTOs.SensorDefinitionDto> CreateAsync(Discovery.Core.DTOs.SensorUpsertRequest request, string? actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Discovery.Core.DTOs.SensorDefinitionDto?> UpdateAsync(Guid id, Discovery.Core.DTOs.SensorUpsertRequest request, string? actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid id, string? actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> SetPinnedAsync(Guid id, bool isPinned, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SensorDefinition>> ResolveApplicableAsync(Guid agentId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SensorDefinition>>([]);
    }
}
