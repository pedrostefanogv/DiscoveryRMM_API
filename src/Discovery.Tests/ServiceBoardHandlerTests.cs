using Discovery.Core.Cqrs.Dashboard.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Cqrs.Dashboard.QueryHandlers;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Testes do <see cref="GetServiceBoardQueryHandler"/>: merge das fontes,
/// ordenação (prioridade desc, idade desc), truncamento e filtro por clientId.
/// </summary>
[TestFixture]
public class ServiceBoardHandlerTests
{
    private static (Discovery.Infrastructure.Data.DiscoveryDbContext Db, Guid ClientAId, Guid ClientBId, Guid SiteAId, Guid AgentAId) Seed()
    {
        var db = BoardTestDbContext.NewContext();
        var clientAId = Guid.NewGuid();
        var clientBId = Guid.NewGuid();
        var siteAId = Guid.NewGuid();
        var siteBId = Guid.NewGuid();
        var agentAId = Guid.NewGuid();

        db.Clients.Add(new Client { Id = clientAId, Name = "Cliente A" });
        db.Clients.Add(new Client { Id = clientBId, Name = "Cliente B" });
        db.Sites.Add(new Site { Id = siteAId, ClientId = clientAId, Name = "Site A" });
        db.Sites.Add(new Site { Id = siteBId, ClientId = clientBId, Name = "Site B" });
        db.Agents.Add(new Agent { Id = agentAId, SiteId = siteAId, Hostname = "HOST-A", DisplayName = "Servidor A" });
        db.SaveChanges();

        return (db, clientAId, clientBId, siteAId, agentAId);
    }

    [Test]
    public async Task Merge_OrdenaPrioridadeDepoisIdade_TruncaEmLimit()
    {
        var (db, clientAId, _, siteAId, agentAId) = Seed();
        var now = DateTime.UtcNow;

        // Ticket crítico aberto há 10h (rank 5, idade 10).
        db.Tickets.Add(new Ticket
        {
            Id = Guid.NewGuid(), ClientId = clientAId, SiteId = siteAId,
            Title = "Servidor fora", Priority = TicketPriority.Critical,
            CreatedAt = now.AddHours(-10), UpdatedAt = now
        });
        // Alerta ativo error há 1h (rank 5, idade 1).
        db.AgentAlertDefinitions.Add(new AgentAlertDefinition
        {
            Id = Guid.NewGuid(), Title = "Disco cheio", Icon = "error",
            Status = AlertDefinitionStatus.Dispatched, ScopeType = AlertScopeType.Client,
            ScopeClientId = clientAId, CreatedAt = now.AddHours(-1), UpdatedAt = now
        });
        // Automação em falha há 2h (rank 4).
        var taskId = Guid.NewGuid();
        db.AutomationTaskDefinitions.Add(new AutomationTaskDefinition { Id = taskId, Name = "Backup Noturno" });
        db.AutomationExecutionReports.Add(new AutomationExecutionReport
        {
            Id = Guid.NewGuid(), AgentId = agentAId, TaskId = taskId,
            Status = AutomationExecutionStatus.Failed, CreatedAt = now.AddHours(-2), UpdatedAt = now
        });
        // Agente com patches pendentes (rank 3).
        db.AgentWindowsUpdates.Add(new AgentWindowsUpdate
        {
            Id = Guid.NewGuid(), AgentId = agentAId, UpdateKey = "kb:KB1", GroupKey = "kb:KB1",
            Title = "CU", IsPresent = true, IsPending = true,
            Status = PatchUpdateStatus.Pending,
            ReportedAt = now, FirstSeenAt = now, LastSeenAt = now, CreatedAt = now, UpdatedAt = now
        });
        await db.SaveChangesAsync();

        var handler = new GetServiceBoardQueryHandler(db);

        // Sem truncamento: ordem esperada por rank desc e idade desc.
        var full = await handler.Handle(new GetServiceBoardQuery(clientAId, null, Limit: 10), default);
        var board = full.Value!;
        Assert.Multiple(() =>
        {
            Assert.That(board.Counts, Is.EqualTo(new Discovery.Core.Cqrs.Dashboard.Queries.ServiceBoardCountsDto(1, 1, 1, 1)));
            Assert.That(board.Items.Select(i => i.Kind).ToArray(),
                Is.EqualTo(new[] { "ticket", "alert", "automation", "patch" }));
            Assert.That(board.Items[0].Priority, Is.EqualTo("Critical"));
            Assert.That(board.Items[0].DeepLink, Is.EqualTo("/tickets/" + board.Items[0].Id));
            Assert.That(board.Items[1].Priority, Is.EqualTo("error"));
            Assert.That(board.Items[2].Title, Is.EqualTo("Backup Noturno"));
            Assert.That(board.Items[3].Title, Is.EqualTo("1 patches pendentes — Servidor A"));
            Assert.That(board.Items[3].DeepLink, Is.EqualTo("/patches"));
        });

        // Com limit=2: patch e automation saem, counts permanecem totais.
        var truncated = await handler.Handle(new GetServiceBoardQuery(clientAId, null, Limit: 2), default);
        Assert.Multiple(() =>
        {
            Assert.That(truncated.Value!.Items, Has.Count.EqualTo(2));
            Assert.That(truncated.Value!.Items.Select(i => i.Kind).ToArray(), Is.EqualTo(new[] { "ticket", "alert" }));
            Assert.That(truncated.Value!.Counts.Patch, Is.EqualTo(1));
            Assert.That(truncated.Value!.Counts.Automation, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task FiltroPorClientId_ExcluiItensDeOutroCliente()
    {
        var (db, clientAId, clientBId, siteAId, agentAId) = Seed();
        var now = DateTime.UtcNow;
        var otherAgent = Guid.NewGuid();

        db.Tickets.Add(new Ticket
        {
            Id = Guid.NewGuid(), ClientId = clientAId, SiteId = siteAId,
            Title = "Do cliente A", Priority = TicketPriority.Medium,
            CreatedAt = now, UpdatedAt = now
        });
        db.Tickets.Add(new Ticket
        {
            Id = Guid.NewGuid(), ClientId = clientBId,
            Title = "Do cliente B", Priority = TicketPriority.Medium,
            CreatedAt = now, UpdatedAt = now
        });
        db.AutomationTaskDefinitions.Add(new AutomationTaskDefinition { Id = Guid.NewGuid(), Name = "T" });
        db.AutomationExecutionReports.Add(new AutomationExecutionReport
        {
            Id = Guid.NewGuid(), AgentId = agentAId, TaskId = null,
            Status = AutomationExecutionStatus.Failed, CreatedAt = now, UpdatedAt = now
        });
        // Patches pendentes em agente do cliente B → não pode aparecer no board do A.
        db.Agents.Add(new Agent { Id = otherAgent, SiteId = Guid.NewGuid(), Hostname = "HOST-B2" });
        await db.SaveChangesAsync();

        var handler = new GetServiceBoardQueryHandler(db);
        var result = await handler.Handle(new GetServiceBoardQuery(clientAId, null, Limit: 50), default);

        var board = result.Value!;
        Assert.Multiple(() =>
        {
            Assert.That(board.Items, Has.Count.EqualTo(2));
            Assert.That(board.Items.Where(i => i.Kind == "ticket").All(i => i.ClientId == clientAId), Is.True);
            Assert.That(board.Items.Count(i => i.Kind == "automation"), Is.EqualTo(1));
            Assert.That(board.Items.All(i => i.ClientId == clientAId), Is.True);
        });
    }

    [Test]
    public async Task AgeHours_DeepLink_E_Status_Corretos()
    {
        var (db, clientAId, _, siteAId, agentAId) = Seed();
        var now = DateTime.UtcNow;
        var ticketId = Guid.NewGuid();

        db.Tickets.Add(new Ticket
        {
            Id = ticketId, ClientId = clientAId, SiteId = siteAId,
            Title = "Ticket X", Priority = TicketPriority.High,
            CreatedAt = now.AddHours(-3), UpdatedAt = now
        });
        await db.SaveChangesAsync();

        var handler = new GetServiceBoardQueryHandler(db);
        var result = await handler.Handle(new GetServiceBoardQuery(null, siteAId, Limit: 50), default);

        var item = result.Value!.Items.Single(i => i.Kind == "ticket");
        Assert.Multiple(() =>
        {
            Assert.That(item.Id, Is.EqualTo(ticketId));
            Assert.That(item.Status, Is.EqualTo("open"));
            Assert.That(item.Priority, Is.EqualTo("High"));
            Assert.That(item.AgeHours, Is.EqualTo(3).Within(0.01));
            Assert.That(item.DeepLink, Is.EqualTo($"/tickets/{ticketId}"));
            Assert.That(item.SiteId, Is.EqualTo(siteAId));
            Assert.That(item.ClientName, Is.EqualTo("Cliente A"));
        });
    }

    [Test]
    public async Task AlertasCanceladosOuExpirados_NaoEntramNaFila()
    {
        var (db, clientAId, _, _, _) = Seed();
        var now = DateTime.UtcNow;

        db.AgentAlertDefinitions.Add(new AgentAlertDefinition
        {
            Id = Guid.NewGuid(), Title = "Ativo", Icon = "warning",
            Status = AlertDefinitionStatus.Dispatched, ScopeType = AlertScopeType.Client,
            ScopeClientId = clientAId, CreatedAt = now, UpdatedAt = now
        });
        db.AgentAlertDefinitions.Add(new AgentAlertDefinition
        {
            Id = Guid.NewGuid(), Title = "Cancelado", Icon = "warning",
            Status = AlertDefinitionStatus.Cancelled, ScopeType = AlertScopeType.Client,
            ScopeClientId = clientAId, CreatedAt = now, UpdatedAt = now
        });
        db.AgentAlertDefinitions.Add(new AgentAlertDefinition
        {
            Id = Guid.NewGuid(), Title = "Expirado", Icon = "warning",
            Status = AlertDefinitionStatus.Expired, ScopeType = AlertScopeType.Client,
            ScopeClientId = clientAId, CreatedAt = now, UpdatedAt = now
        });
        await db.SaveChangesAsync();

        var handler = new GetServiceBoardQueryHandler(db);
        var result = await handler.Handle(new GetServiceBoardQuery(null, null, Limit: 50), default);

        Assert.Multiple(() =>
        {
            Assert.That(result.Value!.Counts.Alert, Is.EqualTo(1));
            Assert.That(result.Value!.Items.Single(i => i.Kind == "alert").Title, Is.EqualTo("Ativo"));
        });
    }
}
