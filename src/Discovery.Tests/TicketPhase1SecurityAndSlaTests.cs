using Discovery.Api.Services;
using Discovery.Core.Cqrs.Tickets.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Tickets.CommandHandlers;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Discovery.Tests;

[TestFixture]
public class TicketPhase1SecurityAndSlaTests
{
    private sealed class MergeTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type> { typeof(Ticket), typeof(TicketMergeRecord) };
            foreach (var entityType in typeof(Ticket).Assembly.GetTypes()
                         .Where(t => t.IsClass && t.Namespace is not null
                                     && t.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(t => !allowed.Contains(t)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Ticket>(e => { e.HasKey(t => t.Id); e.Ignore(t => t.DaysOpen); });
            modelBuilder.Entity<TicketMergeRecord>(e => e.HasKey(r => r.Id));
        }
    }

    private static DiscoveryDbContext CreateInMemoryDb()
    {
        var opts = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"ticket-sec-{Guid.NewGuid()}")
            .Options;
        return new MergeTestDbContext(opts);
    }

    [Test]
    public async Task DeleteKnowledgeLink_ReturnsNotFound_WhenLinkBelongsToAnotherTicket()
    {
        var ticketA = Guid.NewGuid();
        var ticketB = Guid.NewGuid();
        var linkId = Guid.NewGuid();
        var repo = new FakeKnowledgeLinkRepo(ticketB, linkId);
        var handler = new DeleteTicketKnowledgeLinkCommandHandler(repo);

        var result = await handler.Handle(new DeleteTicketKnowledgeLinkCommand(ticketA, linkId), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(repo.Deleted, Is.False);
    }

    [Test]
    public async Task DeleteKnowledgeLink_Succeeds_WhenLinkBelongsToTicket()
    {
        var ticketId = Guid.NewGuid();
        var linkId = Guid.NewGuid();
        var repo = new FakeKnowledgeLinkRepo(ticketId, linkId);
        var handler = new DeleteTicketKnowledgeLinkCommandHandler(repo);

        var result = await handler.Handle(new DeleteTicketKnowledgeLinkCommand(ticketId, linkId), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(repo.Deleted, Is.True);
    }

    [Test]
    public async Task MergeTickets_RejectsCrossTenantSourceTicket()
    {
        await using var db = CreateInMemoryDb();
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        var stateId = Guid.NewGuid();
        var target = new Ticket { Id = Guid.NewGuid(), ClientId = clientA, Title = "Target", WorkflowStateId = stateId };
        var source = new Ticket { Id = Guid.NewGuid(), ClientId = clientB, Title = "Source", WorkflowStateId = stateId };
        db.Tickets.AddRange(target, source);
        await db.SaveChangesAsync();

        var handler = new MergeTicketsCommandHandler(
            new FakeWorkflowRepoEmpty(),
            db,
            NullLogger<MergeTicketsCommandHandler>.Instance);

        var result = await handler.Handle(
            new MergeTicketsCommand(target.Id, new[] { source.Id }, null, "dup"),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0].Message, Does.Contain("outro cliente"));
    }

    [Test]
    public async Task TicketKpiCache_InvalidateAsync_InvalidatesClientAndGlobalPrefixesWithoutDoubleWildcard()
    {
        var redis = new FakeRedisRecorder();
        var svc = new TicketKpiCacheService(redis, NullLogger<TicketKpiCacheService>.Instance);
        var clientId = Guid.NewGuid();

        await svc.InvalidateAsync(clientId);

        Assert.That(redis.DeletedPrefixes, Contains.Item($"tickets:kpi:{clientId}:"));
        Assert.That(redis.DeletedPrefixes, Contains.Item("tickets:kpi:all:"));
        Assert.That(redis.DeletedPrefixes.Any(p => p.Contains('*')), Is.False);
    }

    private sealed class FakeKnowledgeLinkRepo(Guid ownerTicketId, Guid linkId) : ITicketKnowledgeLinkRepository
    {
        public bool Deleted { get; private set; }
        public Task<List<TicketKnowledgeLink>> GetByTicketAsync(Guid ticketId, CancellationToken ct = default) => Task.FromResult(new List<TicketKnowledgeLink>());
        public Task<TicketKnowledgeLink> CreateAsync(TicketKnowledgeLink link, CancellationToken ct = default) => Task.FromResult(link);
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetFeedbackAsync(Guid id, bool useful, CancellationToken ct = default) => Task.CompletedTask;
        public Task<TicketKnowledgeLink?> GetByTicketAndArticleAsync(Guid ticketId, Guid articleId, CancellationToken ct = default) => Task.FromResult<TicketKnowledgeLink?>(null);
        public Task<bool> DeleteForTicketAsync(Guid ticketId, Guid id, CancellationToken ct = default)
        {
            if (ticketId == ownerTicketId && id == linkId)
            {
                Deleted = true;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }
    }

    private sealed class FakeWorkflowRepoEmpty : IWorkflowRepository
    {
        public Task<WorkflowState?> GetStateByIdAsync(Guid id) => Task.FromResult<WorkflowState?>(null);
        public Task<WorkflowState?> GetInitialStateAsync(Guid? clientId) => Task.FromResult<WorkflowState?>(null);
        public Task<bool> HasInitialStateAsync(Guid? clientId, Guid? excludeStateId = null) => Task.FromResult(false);
        public Task<int> CountTicketsInStateAsync(Guid stateId) => Task.FromResult(0);
        public Task<IEnumerable<WorkflowState>> GetStatesAsync(Guid? clientId = null) => Task.FromResult<IEnumerable<WorkflowState>>(Array.Empty<WorkflowState>());
        public Task<WorkflowState> CreateStateAsync(WorkflowState state) => Task.FromResult(state);
        public Task UpdateStateAsync(WorkflowState state) => Task.CompletedTask;
        public Task DeleteStateAsync(Guid id) => Task.CompletedTask;
        public Task<IEnumerable<WorkflowTransition>> GetTransitionsAsync(Guid? clientId = null) => Task.FromResult<IEnumerable<WorkflowTransition>>(Array.Empty<WorkflowTransition>());
        public Task<IEnumerable<WorkflowTransition>> GetTransitionsFromStateAsync(Guid fromStateId, Guid? clientId = null) => Task.FromResult<IEnumerable<WorkflowTransition>>(Array.Empty<WorkflowTransition>());
        public Task<bool> TransitionExistsAsync(Guid fromStateId, Guid toStateId, Guid? clientId = null) => Task.FromResult(false);
        public Task<WorkflowTransition> CreateTransitionAsync(WorkflowTransition transition) => Task.FromResult(transition);
        public Task DeleteTransitionAsync(Guid id) => Task.CompletedTask;
        public Task<bool> IsTransitionValidAsync(Guid fromStateId, Guid toStateId, Guid? clientId = null) => Task.FromResult(false);
    }

    private sealed class FakeRedisRecorder : IRedisService
    {
        public List<string> DeletedPrefixes { get; } = [];
        public bool IsConnected => true;
        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
        public Task SetAsync(string key, string value, int ttlSeconds = 300) => Task.CompletedTask;
        public Task DeleteAsync(string key) => Task.CompletedTask;
        public Task DeleteByPrefixAsync(string prefix)
        {
            DeletedPrefixes.Add(prefix);
            return Task.CompletedTask;
        }
        public Task<bool> AcquireLockAsync(string key, string value, TimeSpan expiry) => Task.FromResult(true);
        public Task ReleaseLockAsync(string key, string value) => Task.CompletedTask;
        public Task PublishAsync(string channel, string message) => Task.CompletedTask;
        public Task<long> IncrementAsync(string key) => Task.FromResult(1L);
        public Task<long> IncrementAsync(string key, int ttlSeconds = 60) => Task.FromResult(1L);
        public Task<long> IncrementByAsync(string key, long delta) => Task.FromResult(delta);
        public Task<bool> SetExpiryAsync(string key, int ttlSeconds) => Task.FromResult(true);
        public Task<int> GetTtlSecondsAsync(string key) => Task.FromResult(60);
        public Task SubscribeAsync(string channel, Action<string, string> handler) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetKeysByPrefixAsync(string prefix, int limit = 100) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<bool> SetIfNotExistsAsync(string key, string value, int ttlSeconds = 300) => Task.FromResult(true);
    }
}
