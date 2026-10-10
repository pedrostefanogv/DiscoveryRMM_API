using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Crud.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Agents.CommandHandlers;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Descomissionamento remoto pós-commit: o comando que desinstala o agente do
/// PC só é enviado quando ele está online, e a falha do envio nunca derruba o
/// fluxo (best-effort — a lixeira já está commitada quando isto roda).
/// </summary>
[TestFixture]
public class AgentDeleteDecommissionTests
{
    private static readonly Guid SiteId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static async Task<Guid> SeedAgentAsync(DiscoveryDbContext db, AgentStatus status, DateTime? deletedAt)
    {
        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            SiteId = SiteId,
            Hostname = "host",
            DisplayName = "host",
            Status = status,
            DeletedAt = deletedAt,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Agents.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static RequestAgentDecommissionCommandHandler NewHandler(DiscoveryDbContext db, FakeDispatcher dispatcher)
        => new(db, dispatcher, NullLogger<RequestAgentDecommissionCommandHandler>.Instance);

    [Test]
    public async Task RequestDecommission_OnlineTrashedAgent_DispatchesDecommission()
    {
        await using var db = NewDb();
        var agentId = await SeedAgentAsync(db, AgentStatus.Online, DateTime.UtcNow);
        var dispatcher = new FakeDispatcher();

        var result = await NewHandler(db, dispatcher)
            .Handle(new RequestAgentDecommissionCommand(agentId, "trash"), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(dispatcher.Dispatched, Has.Count.EqualTo(1));
        Assert.That(dispatcher.Dispatched[0].CommandType, Is.EqualTo(CommandType.DecommissionAgent));
        Assert.That(dispatcher.Dispatched[0].AgentId, Is.EqualTo(agentId));

        using var doc = JsonDocument.Parse(dispatcher.Dispatched[0].Payload);
        Assert.That(doc.RootElement.GetProperty("reason").GetString(), Is.EqualTo("trash"));
    }

    [Test]
    public async Task RequestDecommission_OfflineAgent_DoesNotDispatch()
    {
        await using var db = NewDb();
        var agentId = await SeedAgentAsync(db, AgentStatus.Offline, DateTime.UtcNow);
        var dispatcher = new FakeDispatcher();

        var result = await NewHandler(db, dispatcher)
            .Handle(new RequestAgentDecommissionCommand(agentId), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(dispatcher.Dispatched, Is.Empty);
    }

    [Test]
    public async Task RequestDecommission_DispatchFailure_StillSucceeds()
    {
        await using var db = NewDb();
        var agentId = await SeedAgentAsync(db, AgentStatus.Online, DateTime.UtcNow);
        var dispatcher = new FakeDispatcher { ThrowOnDispatch = true };

        var result = await NewHandler(db, dispatcher)
            .Handle(new RequestAgentDecommissionCommand(agentId), CancellationToken.None);

        // O agente já está na lixeira: uma falha de NATS não pode virar erro.
        Assert.That(result.IsSuccess, Is.True);
    }

    [Test]
    public async Task RequestDecommission_UnknownAgent_ReturnsNotFound()
    {
        await using var db = NewDb();
        var dispatcher = new FakeDispatcher();

        var result = await NewHandler(db, dispatcher)
            .Handle(new RequestAgentDecommissionCommand(Guid.NewGuid()), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0].Code, Is.EqualTo("NotFound"));
        Assert.That(dispatcher.Dispatched, Is.Empty);
    }

    // -------------------------------------------------------------------------
    // Fakes / contexto de teste
    // -------------------------------------------------------------------------

    private sealed class FakeDispatcher : IAgentCommandDispatcher
    {
        public List<AgentCommand> Dispatched { get; } = [];
        public bool ThrowOnDispatch { get; init; }

        public Task<AgentCommand> DispatchAsync(AgentCommand command, CancellationToken cancellationToken = default)
        {
            if (ThrowOnDispatch)
                throw new InvalidOperationException("NATS indisponível (teste)");

            command.Id = Guid.NewGuid();
            Dispatched.Add(command);
            return Task.FromResult(command);
        }
    }

    private static DiscoveryDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"agent-decommission-{Guid.NewGuid():N}")
            .Options;
        return new DecommissionDispatchTestDbContext(options);
    }

    /// <summary>Contexto restrito a Agent/Site (InMemory não suporta o modelo completo).</summary>
    private sealed class DecommissionDispatchTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            foreach (var entityType in typeof(Agent).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null
                             && type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => type != typeof(Agent) && type != typeof(Site)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Agent>(entity => entity.HasKey(agent => agent.Id));
            modelBuilder.Entity<Site>(entity => entity.HasKey(site => site.Id));
        }
    }
}
