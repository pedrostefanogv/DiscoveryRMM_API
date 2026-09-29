using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Janelas da reentrega de comandos. Puro (sem I/O): valores hostis de
/// configuração não podem produzir loop apertado nem janela infinita.
/// </summary>
[TestFixture]
public class CommandRedeliveryOptionsTests
{
    [Test]
    public void Defaults_KeepRedeliveryEnabledWithSaneWindows()
    {
        var options = new NatsCommandRedeliveryOptions();

        Assert.That(options.Enabled, Is.True);
        Assert.That(options.IntervalSeconds, Is.EqualTo(120));
        Assert.That(options.RetentionHours, Is.EqualTo(24));
        Assert.That(options.BatchSize, Is.EqualTo(200));
    }

    [Test]
    public void ResolveWindow_ClampsHostileValues()
    {
        var options = new NatsCommandRedeliveryOptions
        {
            IntervalSeconds = 0,
            MinAgeSeconds = -10,
            RetryGraceSeconds = 0,
            RetentionHours = 0,
            BatchSize = 100_000
        };

        var window = options.ResolveWindow(DateTime.UtcNow);

        Assert.That(window.Interval, Is.EqualTo(TimeSpan.FromSeconds(10)));
        Assert.That(window.BatchSize, Is.EqualTo(1000));
        Assert.That(window.CreatedAfterUtc, Is.EqualTo(DateTime.UtcNow - TimeSpan.FromHours(1)).Within(TimeSpan.FromMinutes(1)));
    }

    [Test]
    public void ResolveWindow_UsesTheGreaterOfMinAgeAndRetryGrace()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var options = new NatsCommandRedeliveryOptions
        {
            MinAgeSeconds = 600,
            RetryGraceSeconds = 300,
            RetentionHours = 24
        };

        var window = options.ResolveWindow(now);

        Assert.That(window.StaleBeforeUtc, Is.EqualTo(now.AddSeconds(-600)));
        Assert.That(window.CreatedAfterUtc, Is.EqualTo(now.AddHours(-24)));
        Assert.That(window.Interval, Is.EqualTo(TimeSpan.FromSeconds(120)));
    }

    [Test]
    public void ResolveWindow_RespectsRetryGraceWhenItIsGreater()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var options = new NatsCommandRedeliveryOptions
        {
            MinAgeSeconds = 60,
            RetryGraceSeconds = 900
        };

        Assert.That(options.ResolveWindow(now).StaleBeforeUtc, Is.EqualTo(now.AddSeconds(-900)));
    }
}

/// <summary>
/// Seleção dos destinatários da reentrega.
/// </summary>
[TestFixture]
public class CommandRedeliveryAgentSelectionTests
{
    [Test]
    public void ExcludesTrashedAgents()
    {
        var online = new Agent { Id = Guid.NewGuid(), Hostname = "PC-1", Status = AgentStatus.Online };
        var trashed = new Agent { Id = Guid.NewGuid(), Hostname = "PC-2", Status = AgentStatus.Online, DeletedAt = DateTime.UtcNow };

        var ids = Discovery.Api.Services.PendingCommandRedeliveryService.SelectRedeliveryAgentIds([online, trashed]);

        Assert.That(ids, Is.EqualTo(new[] { online.Id }),
            "agente na lixeira não pode voltar a executar comandos");
    }

    [Test]
    public void EmptyOnlineListProducesNoRecipients()
    {
        Assert.That(Discovery.Api.Services.PendingCommandRedeliveryService.SelectRedeliveryAgentIds([]), Is.Empty);
    }
}

/// <summary>
/// Elegibilidade da reentrega: só comandos NÃO confirmados, de agentes online
/// agora, sem envio recente e dentro da janela de retenção. É o que impede
/// reenviar comando já concluído ou bombardeado a cada varredura.
/// </summary>
[TestFixture]
public class CommandRedeliveryRepositoryTests
{
    private static readonly Guid OnlineAgent = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OfflineAgent = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task ReturnsUnconfirmedCommandsOfOnlineAgents()
    {
        await using var db = NewDb();
        var stale = Now.AddMinutes(-10);
        var pending = NewCommand(OnlineAgent, CommandStatus.Sent, Now.AddHours(-2), stale);
        db.AgentCommands.Add(pending);
        await db.SaveChangesAsync();

        var result = await Repository(db).GetRedeliveryCandidatesAsync(
            [OnlineAgent], Now.AddHours(-24), Now.AddMinutes(-5), 100);

        Assert.That(result.Select(c => c.Id), Is.EqualTo(new[] { pending.Id }));
    }

    [Test]
    public async Task SkipsCommandsAlreadyConfirmed()
    {
        await using var db = NewDb();
        var stale = Now.AddMinutes(-10);
        db.AgentCommands.Add(NewCommand(OnlineAgent, CommandStatus.Completed, Now.AddHours(-2), stale));
        db.AgentCommands.Add(NewCommand(OnlineAgent, CommandStatus.Failed, Now.AddHours(-2), stale));
        db.AgentCommands.Add(NewCommand(OnlineAgent, CommandStatus.Cancelled, Now.AddHours(-2), stale));
        await db.SaveChangesAsync();

        var result = await Repository(db).GetRedeliveryCandidatesAsync(
            [OnlineAgent], Now.AddHours(-24), Now.AddMinutes(-5), 100);

        Assert.That(result, Is.Empty, "comando finalizado não pode ser reenviado");
    }

    [Test]
    public async Task SkipsAgentsThatAreNotOnlineNow()
    {
        await using var db = NewDb();
        var stale = Now.AddMinutes(-10);
        db.AgentCommands.Add(NewCommand(OfflineAgent, CommandStatus.Sent, Now.AddHours(-2), stale));
        await db.SaveChangesAsync();

        var result = await Repository(db).GetRedeliveryCandidatesAsync(
            [OnlineAgent], Now.AddHours(-24), Now.AddMinutes(-5), 100);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task SkipsRecentlySentAndRecentNeverSentCommands()
    {
        await using var db = NewDb();
        // Enviado há 30s: pode estar em trânsito/executando.
        db.AgentCommands.Add(NewCommand(OnlineAgent, CommandStatus.Sent, Now.AddMinutes(-1), Now.AddSeconds(-30)));
        // Nunca enviado, mas criado agora (o dispatch inicial ainda pode ocorrer).
        db.AgentCommands.Add(NewCommand(OnlineAgent, CommandStatus.Pending, Now.AddSeconds(-30), null));
        await db.SaveChangesAsync();

        var result = await Repository(db).GetRedeliveryCandidatesAsync(
            [OnlineAgent], Now.AddHours(-24), Now.AddMinutes(-5), 100);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task IncludesNeverSentCommandsThatArePastTheMinAge()
    {
        await using var db = NewDb();
        var neverSent = NewCommand(OnlineAgent, CommandStatus.Pending, Now.AddMinutes(-10), null);
        db.AgentCommands.Add(neverSent);
        await db.SaveChangesAsync();

        var result = await Repository(db).GetRedeliveryCandidatesAsync(
            [OnlineAgent], Now.AddHours(-24), Now.AddMinutes(-5), 100);

        Assert.That(result.Select(c => c.Id), Is.EqualTo(new[] { neverSent.Id }),
            "comando criado quando o NATS estava fora precisa ser entregue");
    }

    [Test]
    public async Task SkipsCommandsOlderThanRetention()
    {
        await using var db = NewDb();
        db.AgentCommands.Add(NewCommand(OnlineAgent, CommandStatus.Sent, Now.AddHours(-30), Now.AddHours(-30)));
        await db.SaveChangesAsync();

        var result = await Repository(db).GetRedeliveryCandidatesAsync(
            [OnlineAgent], Now.AddHours(-24), Now.AddMinutes(-5), 100);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task OrdersByOldestAndHonorsTheBatchLimit()
    {
        await using var db = NewDb();
        db.AgentCommands.Add(NewCommand(OnlineAgent, CommandStatus.Sent, Now.AddHours(-3), Now.AddMinutes(-10)));
        db.AgentCommands.Add(NewCommand(OnlineAgent, CommandStatus.Sent, Now.AddHours(-4), Now.AddMinutes(-10)));
        await db.SaveChangesAsync();

        var result = await Repository(db).GetRedeliveryCandidatesAsync(
            [OnlineAgent], Now.AddHours(-24), Now.AddMinutes(-5), 1);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].CreatedAt, Is.EqualTo(Now.AddHours(-4)), "mais antigo primeiro");
    }

    [Test]
    public async Task ExpiredQuery_ReturnsOnlyUnconfirmedCommandsPastTheWindow()
    {
        await using var db = NewDb();
        var expired = NewCommand(OfflineAgent, CommandStatus.Sent, Now.AddHours(-30), Now.AddHours(-30));
        db.AgentCommands.Add(expired);
        // Recente: ainda está na janela de reentrega.
        db.AgentCommands.Add(NewCommand(OfflineAgent, CommandStatus.Pending, Now.AddHours(-1), null));
        // Já confirmado: nunca expira (já tem estado terminal).
        db.AgentCommands.Add(NewCommand(OfflineAgent, CommandStatus.Completed, Now.AddHours(-30), Now.AddHours(-30)));
        await db.SaveChangesAsync();

        var result = await Repository(db).GetExpiredUnconfirmedAsync(Now.AddHours(-24), 100);

        Assert.That(result.Select(c => c.Id), Is.EqualTo(new[] { expired.Id }));
    }

    [Test]
    public async Task ExpiredQuery_OrdersByOldestAndHonorsTheLimit()
    {
        await using var db = NewDb();
        db.AgentCommands.Add(NewCommand(OfflineAgent, CommandStatus.Sent, Now.AddHours(-25), Now.AddHours(-25)));
        db.AgentCommands.Add(NewCommand(OfflineAgent, CommandStatus.Sent, Now.AddHours(-40), Now.AddHours(-40)));
        await db.SaveChangesAsync();

        var result = await Repository(db).GetExpiredUnconfirmedAsync(Now.AddHours(-24), 1);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].CreatedAt, Is.EqualTo(Now.AddHours(-40)));
    }

    [Test]
    public async Task EmptyAgentListShortCircuits()
    {
        await using var db = NewDb();
        db.AgentCommands.Add(NewCommand(OnlineAgent, CommandStatus.Sent, Now.AddHours(-2), Now.AddMinutes(-10)));
        await db.SaveChangesAsync();

        var result = await Repository(db).GetRedeliveryCandidatesAsync(
            [], Now.AddHours(-24), Now.AddMinutes(-5), 100);

        Assert.That(result, Is.Empty);
    }

    private static AgentCommand NewCommand(Guid agentId, CommandStatus status, DateTime createdAt, DateTime? sentAt) => new()
    {
        Id = Guid.NewGuid(),
        AgentId = agentId,
        CommandType = CommandType.Script,
        Payload = "echo 1",
        Status = status,
        CreatedAt = createdAt,
        SentAt = sentAt
    };

    private static CommandRepository Repository(DiscoveryDbContext db) => new(db);

    private static CommandTestDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"command-redelivery-{Guid.NewGuid():N}")
            .Options;

        return new CommandTestDbContext(options);
    }

    /// <summary>
    /// Contexto restrito a AgentCommand: o DiscoveryDbContext completo mapeia
    /// dezenas de entidades cujas configurações o provider InMemory não suporta.
    /// </summary>
    private sealed class CommandTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            foreach (var entityType in typeof(Agent).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null
                             && type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => type != typeof(AgentCommand)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<AgentCommand>(entity => entity.HasKey(command => command.Id));
        }
    }
}
