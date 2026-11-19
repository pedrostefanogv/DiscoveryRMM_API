using System.Reflection;
using System.Text.Json;
using Discovery.Api.Controllers;
using Discovery.Api.Filters;
using Discovery.Core.Configuration;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Automation.Commands;
using Discovery.Core.Cqrs.Agents.CommandsTokens.Commands;
using Discovery.Core.Cqrs.Agents.Crud.Commands;
using Discovery.Core.Cqrs.Agents.Crud.Queries;
using Discovery.Core.Cqrs.Agents.Fanout.Commands;
using Discovery.Core.Cqrs.Agents.Inventory.Queries;
using Discovery.Core.Cqrs.Agents.PowerManagement.Commands;
using Discovery.Core.Cqrs.Agents.Software.Commands;
using Discovery.Core.Cqrs.Alerts.Commands;
using Discovery.Core.Cqrs.Clients.Commands;
using Discovery.Core.Cqrs.Clients.Queries;
using Discovery.Core.Cqrs.CustomFields.Commands;
using Discovery.Core.Cqrs.CustomFields.Queries;
using Discovery.Core.Cqrs.Dashboard.Queries;
using Discovery.Core.Cqrs.Departments.Commands;
using Discovery.Core.Cqrs.SlaCalendars.Commands;
using Discovery.Core.Cqrs.SoftwareInventory.Queries;
using Discovery.Core.Cqrs.Support.Csat;
using Discovery.Core.Cqrs.WorkflowProfiles.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Agents.CommandHandlers;
using Discovery.Infrastructure.Cqrs.Agents.QueryHandlers;
using Discovery.Infrastructure.Cqrs.Clients;
using Discovery.Infrastructure.Cqrs.CustomFields;
using Discovery.Infrastructure.Cqrs.Dashboard.QueryHandlers;
using Discovery.Infrastructure.Cqrs.SoftwareInventory;
using Discovery.Infrastructure.Cqrs.Support;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Discovery.Tests;

[TestFixture]
public class F02TenantIsolationTests
{
    private static readonly Guid ClientA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ClientB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid SiteA1 = Guid.Parse("aaaaaaaa-1111-0000-0000-000000000001");
    private static readonly Guid SiteA2 = Guid.Parse("aaaaaaaa-2222-0000-0000-000000000002");
    private static readonly Guid SiteB1 = Guid.Parse("bbbbbbbb-1111-0000-0000-000000000001");
    private static readonly Guid AgentA1 = Guid.Parse("11111111-aaaa-0000-0000-000000000001");
    private static readonly Guid AgentA2 = Guid.Parse("22222222-aaaa-0000-0000-000000000002");
    private static readonly Guid AgentB1 = Guid.Parse("33333333-bbbb-0000-0000-000000000001");

    // -------------------------------------------------------------------------
    // 1. Reflection audit: zero implicit ScopeSource across all API controllers
    // -------------------------------------------------------------------------

    [Test]
    public void AllRequirePermissionAttributes_MustExplicitlySpecifyScopeSource()
    {
        var apiAssembly = typeof(AgentsController).Assembly;
        var controllerTypes = apiAssembly.GetTypes()
            .Where(t => !t.IsAbstract && t.IsClass && typeof(ControllerBase).IsAssignableFrom(t))
            .OrderBy(t => t.Name)
            .ToList();

        Assert.That(controllerTypes, Is.Not.Empty);

        var implicitOccurrences = new List<string>();
        var explicitCount = 0;

        foreach (var controller in controllerTypes)
        {
            foreach (var attr in CustomAttributeData.GetCustomAttributes(controller))
            {
                if (attr.AttributeType != typeof(RequirePermissionAttribute)) continue;
                if (attr.ConstructorArguments.Count < 3)
                    implicitOccurrences.Add($"{controller.Name} [class]");
                else
                    explicitCount++;
            }

            foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (var attr in CustomAttributeData.GetCustomAttributes(method))
                {
                    if (attr.AttributeType != typeof(RequirePermissionAttribute)) continue;
                    if (attr.ConstructorArguments.Count < 3 || attr.ConstructorArguments[2].ArgumentType != typeof(ScopeSource))
                        implicitOccurrences.Add($"{controller.Name}.{method.Name}");
                    else
                        explicitCount++;
                }
            }
        }

        Assert.That(explicitCount, Is.GreaterThanOrEqualTo(550), "All RequirePermission attributes should be inspected.");
        Assert.That(implicitOccurrences, Is.Empty,
            "Found implicit ScopeSource RequirePermission attributes: " + string.Join(", ", implicitOccurrences));
    }

    // -------------------------------------------------------------------------
    // 2. GetDeletedAgentsQueryHandler: AllowedSiteIds & AllowedClientIds
    // -------------------------------------------------------------------------

    [Test]
    public async Task GetDeletedAgents_SiteScopedTechnician_OnlySeesPermittedSiteDeletedAgents()
    {
        await using var db = CreateDb();
        await SeedClientsSitesAgentsAsync(db, deleted: true);

        var handler = new GetDeletedAgentsQueryHandler(db, new F02SiteRepositoryStub(
            new Site { Id = SiteA1, ClientId = ClientA, Name = "Site A1" },
            new Site { Id = SiteA2, ClientId = ClientA, Name = "Site A2" },
            new Site { Id = SiteB1, ClientId = ClientB, Name = "Site B1" }));

        var siteScoped = await handler.Handle(
            new GetDeletedAgentsQuery(null, null, 1, 50, null, HasGlobalAccess: false, AllowedClientIds: [], AllowedSiteIds: [SiteA1]),
            CancellationToken.None);

        Assert.That(siteScoped.IsSuccess, Is.True);
        Assert.That(siteScoped.Value!.Items.Select(i => i.Id).ToArray(), Is.EqualTo(new[] { AgentA1 }));

        var clientScoped = await handler.Handle(
            new GetDeletedAgentsQuery(null, null, 1, 50, null, HasGlobalAccess: false, AllowedClientIds: [ClientA], AllowedSiteIds: []),
            CancellationToken.None);

        Assert.That(clientScoped.IsSuccess, Is.True);
        Assert.That(clientScoped.Value!.Items.Select(i => i.Id).ToArray(), Is.EquivalentTo(new[] { AgentA1, AgentA2 }));

        var emptyScoped = await handler.Handle(
            new GetDeletedAgentsQuery(null, null, 1, 50, null, HasGlobalAccess: false, AllowedClientIds: [], AllowedSiteIds: []),
            CancellationToken.None);

        Assert.That(emptyScoped.IsSuccess, Is.True);
        Assert.That(emptyScoped.Value!.Items, Is.Empty);
    }

    // -------------------------------------------------------------------------
    // 3. Custom Fields: Agent & Entity cross-tenant rejection
    // -------------------------------------------------------------------------

    [Test]
    public async Task AgentCustomFieldHandlers_RejectCrossTenantAndFilterMap()
    {
        await using var db = CreateDb();
        await SeedClientsSitesAgentsAsync(db, deleted: false);

        var defId = Guid.NewGuid();
        db.CustomFieldDefinitions.Add(new CustomFieldDefinition
        {
            Id = defId,
            Name = "asset_tag",
            Label = "Asset Tag",
            ScopeType = CustomFieldScopeType.Agent,
            AllowWebRead = true,
            AllowWebEdit = true,
            IsActive = true
        });
        db.CustomFieldValues.AddRange(
            new CustomFieldValue
            {
                Id = Guid.NewGuid(),
                DefinitionId = defId,
                ScopeType = CustomFieldScopeType.Agent,
                EntityId = AgentA1,
                EntityKey = AgentA1.ToString("D"),
                ValueJson = "\"TAG-A1\"",
                UpdatedBy = "seed"
            },
            new CustomFieldValue
            {
                Id = Guid.NewGuid(),
                DefinitionId = defId,
                ScopeType = CustomFieldScopeType.Agent,
                EntityId = AgentB1,
                EntityKey = AgentB1.ToString("D"),
                ValueJson = "\"TAG-B1\"",
                UpdatedBy = "seed"
            });
        await db.SaveChangesAsync();

        var agentRepo = new F02AgentRepositoryStub(
            new Agent { Id = AgentA1, SiteId = SiteA1, Hostname = "PC-A1" },
            new Agent { Id = AgentB1, SiteId = SiteB1, Hostname = "PC-B1" });
        var siteRepo = new F02SiteRepositoryStub(
            new Site { Id = SiteA1, ClientId = ClientA, Name = "Site A1" },
            new Site { Id = SiteB1, ClientId = ClientB, Name = "Site B1" });
        var scopeA = F02ScopeContextStub.ForSites(SiteA1);
        var cfService = new CustomFieldService(db, agentRepo, siteRepo, F02NullProxy<ILogRepository>.Create(), NullLogger<CustomFieldService>.Instance);

        var getVals = new GetAgentCustomFieldsQueryHandler(agentRepo, siteRepo, scopeA, cfService);
        var deniedVals = await getVals.Handle(new GetAgentCustomFieldsQuery(AgentB1), CancellationToken.None);
        Assert.That(deniedVals.IsFailure, Is.True);
        Assert.That(deniedVals.Errors[0].Code, Is.AnyOf("Forbidden", "NotFound"));

        var getSchema = new GetAgentCustomFieldSchemaQueryHandler(agentRepo, siteRepo, scopeA, cfService);
        var deniedSchema = await getSchema.Handle(new GetAgentCustomFieldSchemaQuery(AgentB1), CancellationToken.None);
        Assert.That(deniedSchema.IsFailure, Is.True);
        Assert.That(deniedSchema.Errors[0].Code, Is.AnyOf("Forbidden", "NotFound"));

        var upsert = new UpsertAgentCustomFieldCommandHandler(agentRepo, siteRepo, scopeA, cfService);
        var deniedUpsert = await upsert.Handle(new UpsertAgentCustomFieldCommand(AgentB1, defId, "\"HACKED\"", "techA"), CancellationToken.None);
        Assert.That(deniedUpsert.IsFailure, Is.True);
        Assert.That(deniedUpsert.Errors[0].Code, Is.EqualTo("Forbidden"));

        var mapHandler = new GetAgentCustomFieldMapQueryHandler(db, scopeA);
        var mapResult = await mapHandler.Handle(new GetAgentCustomFieldMapQuery(), CancellationToken.None);
        Assert.That(mapResult.IsSuccess, Is.True);
        Assert.That(mapResult.Value!.Values.Select(v => v.AgentId).ToArray(), Is.EqualTo(new[] { AgentA1 }));
    }

    [Test]
    public async Task EntityCustomFieldHandlers_RejectCrossTenantClientSiteAgent()
    {
        await using var db = CreateDb();
        await SeedClientsSitesAgentsAsync(db, deleted: false);

        var defClient = new CustomFieldDefinition
        {
            Id = Guid.NewGuid(), Name = "contract", Label = "Contract",
            ScopeType = CustomFieldScopeType.Client, IsActive = true
        };
        db.CustomFieldDefinitions.Add(defClient);
        await db.SaveChangesAsync();

        var cfService = new CustomFieldService(
            db,
            new F02AgentRepositoryStub(new Agent { Id = AgentA1, SiteId = SiteA1 }, new Agent { Id = AgentB1, SiteId = SiteB1 }),
            new F02SiteRepositoryStub(new Site { Id = SiteA1, ClientId = ClientA }, new Site { Id = SiteA2, ClientId = ClientA }, new Site { Id = SiteB1, ClientId = ClientB }),
            F02NullProxy<ILogRepository>.Create(),
            NullLogger<CustomFieldService>.Instance);
        var scopeA = F02ScopeContextStub.ForSites(SiteA1);

        var listHandler = new ListCustomFieldValuesQueryHandler(cfService, db, scopeA);
        Assert.That((await listHandler.Handle(new ListCustomFieldValuesQuery(CustomFieldScopeType.Client, ClientB), CancellationToken.None)).Errors[0].Code, Is.EqualTo("Forbidden"));
        Assert.That((await listHandler.Handle(new ListCustomFieldValuesQuery(CustomFieldScopeType.Site, SiteA2), CancellationToken.None)).Errors[0].Code, Is.EqualTo("Forbidden"));
        Assert.That((await listHandler.Handle(new ListCustomFieldValuesQuery(CustomFieldScopeType.Agent, AgentB1), CancellationToken.None)).Errors[0].Code, Is.EqualTo("Forbidden"));
        Assert.That((await listHandler.Handle(new ListCustomFieldValuesQuery(CustomFieldScopeType.Site, SiteA1), CancellationToken.None)).IsSuccess, Is.True);

        var upsertHandler = new UpsertCustomFieldValueCommandHandler(cfService, db, scopeA);
        Assert.That((await upsertHandler.Handle(new UpsertCustomFieldValueCommand(defClient.Id, CustomFieldScopeType.Client, ClientB, "\"x\"", "tech"), CancellationToken.None)).Errors[0].Code, Is.EqualTo("Forbidden"));
        Assert.That((await upsertHandler.Handle(new UpsertCustomFieldValueCommand(defClient.Id, CustomFieldScopeType.Site, SiteB1, "\"x\"", "tech"), CancellationToken.None)).Errors[0].Code, Is.EqualTo("Forbidden"));
        Assert.That((await upsertHandler.Handle(new UpsertCustomFieldValueCommand(defClient.Id, CustomFieldScopeType.Agent, AgentB1, "\"x\"", "tech"), CancellationToken.None)).Errors[0].Code, Is.EqualTo("Forbidden"));
    }

    // -------------------------------------------------------------------------
    // 4. Operational Handlers & Controllers Cross-Tenant Negative Isolation
    // -------------------------------------------------------------------------

    [Test]
    public async Task AgentOperationalHandlers_RejectCrossTenantAccess()
    {
        await using var db = CreateDb();
        await SeedClientsSitesAgentsAsync(db, deleted: false);

        var agentRepo = new F02AgentRepositoryStub(
            new Agent { Id = AgentA1, SiteId = SiteA1, Hostname = "PC-A1" },
            new Agent { Id = AgentB1, SiteId = SiteB1, Hostname = "PC-B1", DeletedAt = DateTime.UtcNow });
        var siteRepo = new F02SiteRepositoryStub(
            new Site { Id = SiteA1, ClientId = ClientA, Name = "Site A1" },
            new Site { Id = SiteB1, ClientId = ClientB, Name = "Site B1" });
        var scopeA = F02ScopeContextStub.ForSites(SiteA1);

        var messaging = F02NullProxy<IAgentMessaging>.Create((m, _) => m.Name == "get_IsConnected" ? true : null);

        var approve = new ApproveZeroTouchCommandHandler(agentRepo, siteRepo, scopeA, messaging);
        Assert.That((await approve.Handle(new ApproveZeroTouchCommand(AgentB1), CancellationToken.None)).Errors[0].Code, Is.EqualTo("Forbidden"));

        var update = new UpdateAgentCommandHandler(agentRepo, F02NullProxy<IRedisService>.Create(), siteRepo, scopeA);
        Assert.That((await update.Handle(new UpdateAgentCommand(AgentB1, "hack", SiteB1, null, null, null), CancellationToken.None)).Errors[0].Code, Is.EqualTo("Forbidden"));
        Assert.That((await update.Handle(new UpdateAgentCommand(AgentA1, "move-out", SiteB1, null, null, null), CancellationToken.None)).Errors[0].Code, Is.EqualTo("Forbidden"));

        var delete = new DeleteAgentCommandHandler(agentRepo, F02NullProxy<IRedisService>.Create(), siteRepo, scopeA);
        Assert.That((await delete.Handle(new DeleteAgentCommand(AgentB1), CancellationToken.None)).Errors[0].Code, Is.EqualTo("Forbidden"));

        var restore = new RestoreAgentCommandHandler(db, F02NullProxy<IRedisService>.Create(), siteRepo, F02NullProxy<ICommandRepository>.Create(), scopeA, NullLogger<RestoreAgentCommandHandler>.Instance);
        Assert.That((await restore.Handle(new RestoreAgentCommand(AgentB1), CancellationToken.None)).Errors[0].Code, Is.EqualTo("Forbidden"));

        var hwReport = new GetAgentHardwareReportQueryHandler(agentRepo, F02NullProxy<IAgentHardwareRepository>.Create(), siteRepo, scopeA);
        Assert.That((await hwReport.Handle(new GetAgentHardwareReportQuery(AgentB1), CancellationToken.None)).Errors[0].Code, Is.EqualTo("Forbidden"));

        var swPage = new GetAgentSoftwarePageQueryHandler(agentRepo, F02NullProxy<IAgentSoftwareRepository>.Create(), siteRepo, scopeA);
        Assert.That((await swPage.Handle(new GetAgentSoftwarePageQuery(AgentB1), CancellationToken.None)).Errors[0].Code, Is.EqualTo("Forbidden"));

        var shutdown = new ShutdownAgentCommandHandler(agentRepo, F02NullProxy<IAgentCommandDispatcher>.Create(), siteRepo, scopeA);
        Assert.That((await shutdown.Handle(new ShutdownAgentCommand(AgentB1), CancellationToken.None)).Errors[0].Code, Is.EqualTo("Forbidden"));

        var createToken = new CreateAgentTokenCommandHandler(agentRepo, siteRepo, scopeA, F02NullProxy<IAgentAuthService>.Create());
        Assert.That((await createToken.Handle(new CreateAgentTokenCommand(AgentB1, "tok", null), CancellationToken.None)).Errors[0].Code, Is.EqualTo("Forbidden"));

        var siteFanout = new SendSiteFanoutCommandHandler(messaging, siteRepo, scopeA);
        Assert.That((await siteFanout.Handle(new SendSiteFanoutCommand(SiteB1, "PowerShell", "whoami", null), CancellationToken.None)).Errors[0].Code, Is.EqualTo("Forbidden"));

        var clientFanout = new SendClientFanoutCommandHandler(messaging, scopeA);
        Assert.That((await clientFanout.Handle(new SendClientFanoutCommand(ClientB, "PowerShell", "whoami", null), CancellationToken.None)).Errors[0].Code, Is.EqualTo("Forbidden"));

        var runTask = new RunAutomationTaskCommandHandler(
            agentRepo,
            F02NullProxy<IAutomationTaskService>.Create(),
            F02NullProxy<IAutomationScriptService>.Create(),
            F02NullProxy<IAgentCommandDispatcher>.Create(),
            F02NullProxy<IAutomationExecutionReportRepository>.Create(),
            F02NullProxy<IAppPackageRepository>.Create(),
            siteRepo,
            scopeA);
        Assert.That((await runTask.Handle(new RunAutomationTaskCommand(AgentB1, Guid.NewGuid()), CancellationToken.None)).Errors[0].Code, Is.EqualTo("Forbidden"));

        var cancelExec = new CancelAutomationExecutionCommandHandler(
            F02NullProxy<IAutomationExecutionReportRepository>.Create(),
            F02NullProxy<ICommandRepository>.Create(),
            agentRepo,
            siteRepo,
            scopeA);
        Assert.That((await cancelExec.Handle(new CancelAutomationExecutionCommand(AgentB1, Guid.NewGuid()), CancellationToken.None)).Errors[0].Code, Is.EqualTo("Forbidden"));

        var updateSw = new UpdateAgentSoftwareCommandHandler(
            agentRepo,
            siteRepo,
            F02NullProxy<IAgentSoftwareRepository>.Create(),
            F02NullProxy<IAppStoreService>.Create(),
            F02NullProxy<IAgentCommandDispatcher>.Create(),
            F02NullProxy<IAutomationExecutionReportRepository>.Create(),
            scopeA);
        Assert.That((await updateSw.Handle(new UpdateAgentSoftwareCommand(AgentB1, Guid.NewGuid()), CancellationToken.None)).Errors[0].Code, Is.EqualTo("Forbidden"));
    }

    [Test]
    public async Task ClientHandlersAndNotes_RejectCrossTenantAccess()
    {
        await using var db = CreateDb();
        await SeedClientsSitesAgentsAsync(db, deleted: false);

        var clientRepo = new StubClientRepo(
            new Client { Id = ClientA, Name = "Client A", IsActive = true },
            new Client { Id = ClientB, Name = "Client B", IsActive = true });
        var cfService = new CustomFieldService(
            db,
            new F02AgentRepositoryStub(),
            new F02SiteRepositoryStub(),
            F02NullProxy<ILogRepository>.Create(),
            NullLogger<CustomFieldService>.Instance);
        var scopeA = F02ScopeContextStub.ForClients(ClientA);

        var update = new UpdateClientCommandHandler(clientRepo, scopeA);
        Assert.That((await update.Handle(new UpdateClientCommand(ClientB, "Hacked", null, true), CancellationToken.None)).Errors[0].Code, Is.EqualTo("Forbidden"));

        var delete = new DeleteClientCommandHandler(clientRepo, scopeA);
        Assert.That((await delete.Handle(new DeleteClientCommand(ClientB), CancellationToken.None)).Errors[0].Code, Is.EqualTo("Forbidden"));

        var getCf = new GetClientCustomFieldsQueryHandler(clientRepo, scopeA, cfService);
        Assert.That((await getCf.Handle(new GetClientCustomFieldsQuery(ClientB), CancellationToken.None)).Errors[0].Code, Is.AnyOf("Forbidden", "NotFound"));

        var upsertCf = new UpsertClientCustomFieldCommandHandler(clientRepo, scopeA, cfService);
        Assert.That((await upsertCf.Handle(new UpsertClientCustomFieldCommand(ClientB, Guid.NewGuid(), "\"x\"", "tech"), CancellationToken.None)).Errors[0].Code, Is.EqualTo("Forbidden"));

        var controller = new ClientsController(new NoopMediator(), F02NullProxy<INoteService>.Create())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var notesResult = await controller.GetNotesPage(ClientB, scopeA);
        Assert.That((notesResult as ObjectResult)?.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
    }

    [Test]
    public async Task TicketCsat_And_ServiceBoard_And_SoftwareInventory_EnforceTenantScope()
    {
        await using var db = CreateDb();
        await SeedClientsSitesAgentsAsync(db, deleted: false);
        var now = DateTime.UtcNow;

        db.Departments.Add(new Department { Id = Guid.NewGuid(), Name = "TI", CreatedAt = now, UpdatedAt = now });
        db.Tickets.AddRange(
            new Ticket { Id = Guid.NewGuid(), ClientId = ClientA, SiteId = SiteA1, Title = "Ticket A1", Priority = TicketPriority.High, Rating = 5, RatedAt = now, ClosedAt = now, CreatedAt = now, UpdatedAt = now },
            new Ticket { Id = Guid.NewGuid(), ClientId = ClientA, SiteId = SiteA2, Title = "Ticket A2", Priority = TicketPriority.High, Rating = 1, RatedAt = now, ClosedAt = now, CreatedAt = now, UpdatedAt = now },
            new Ticket { Id = Guid.NewGuid(), ClientId = ClientB, SiteId = SiteB1, Title = "Ticket B1", Priority = TicketPriority.Critical, Rating = 1, RatedAt = now, ClosedAt = now, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();

        var scopeSiteA1 = F02ScopeContextStub.ForSites(SiteA1);

        var csatHandler = new GetTicketCsatSummaryQueryHandler(db, scopeSiteA1);
        var csatForbidden = await csatHandler.Handle(new GetTicketCsatSummaryQuery(ClientId: ClientB), CancellationToken.None);
        Assert.That(csatForbidden.IsFailure, Is.True);
        Assert.That(csatForbidden.Errors[0].Code, Is.EqualTo("Forbidden"));

        var csatScoped = await csatHandler.Handle(new GetTicketCsatSummaryQuery(), CancellationToken.None);
        Assert.That(csatScoped.IsSuccess, Is.True);
        Assert.That(csatScoped.Value!.Total, Is.EqualTo(1));
        Assert.That(csatScoped.Value!.Average, Is.EqualTo(5d));

        // Service Board (open ticket in each site)
        db.Tickets.AddRange(
            new Ticket { Id = Guid.NewGuid(), ClientId = ClientA, SiteId = SiteA1, Title = "Open A1", Priority = TicketPriority.High, CreatedAt = now, UpdatedAt = now },
            new Ticket { Id = Guid.NewGuid(), ClientId = ClientB, SiteId = SiteB1, Title = "Open B1", Priority = TicketPriority.Critical, CreatedAt = now, UpdatedAt = now });
        db.AgentAlertDefinitions.Add(new AgentAlertDefinition { Id = Guid.NewGuid(), Title = "Global Label Alert", Icon = "warning", Status = AlertDefinitionStatus.Dispatched, ScopeType = AlertScopeType.Label, ScopeLabelName = "VIP", CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();

        var boardHandler = new GetServiceBoardQueryHandler(db, scopeSiteA1);
        Assert.That((await boardHandler.Handle(new GetServiceBoardQuery(ClientB, null, 50), CancellationToken.None)).Errors[0].Code, Is.EqualTo("Forbidden"));
        Assert.That((await boardHandler.Handle(new GetServiceBoardQuery(null, SiteA2, 50), CancellationToken.None)).Errors[0].Code, Is.EqualTo("Forbidden"));

        var boardScoped = await boardHandler.Handle(new GetServiceBoardQuery(null, null, 50), CancellationToken.None);
        Assert.That(boardScoped.IsSuccess, Is.True);
        Assert.That(boardScoped.Value!.Items.Select(i => i.Title).ToArray(), Is.EqualTo(new[] { "Open A1" }));

        // SoftwareInventory
        var swSvc = F02NullProxy<ISoftwareInventoryService>.Create((m, _) =>
            m.Name switch
            {
                nameof(ISoftwareInventoryService.GetCatalogPagedAsync) => Task.FromResult<IReadOnlyList<SoftwareInventoryCatalogItem>>([]),
                nameof(ISoftwareInventoryService.GetCurrentByAgentIdAsync) => Task.FromResult<IReadOnlyList<AgentInstalledSoftware>>([]),
                nameof(ISoftwareInventoryService.GetSnapshotsByAgentIdAsync) => Task.FromResult<IReadOnlyList<AgentSoftwareSnapshot>>([]),
                _ => null
            });
        var siteRepo = new F02SiteRepositoryStub(new Site { Id = SiteA1, ClientId = ClientA, Name = "A1" }, new Site { Id = SiteB1, ClientId = ClientB, Name = "B1" });
        var agentRepo = new F02AgentRepositoryStub(new Agent { Id = AgentA1, SiteId = SiteA1 }, new Agent { Id = AgentB1, SiteId = SiteB1 });

        var listInv = new ListSoftwareInventoryQueryHandler(swSvc, scopeSiteA1, siteRepo);
        Assert.That((await listInv.Handle(new ListSoftwareInventoryQuery(SoftwareInventoryScope.Global, null, null, 10, null, false), CancellationToken.None)).Errors[0].Code, Is.EqualTo("Forbidden"));
        Assert.That((await listInv.Handle(new ListSoftwareInventoryQuery(SoftwareInventoryScope.Client, ClientB, null, 10, null, false), CancellationToken.None)).Errors[0].Code, Is.EqualTo("Forbidden"));
        Assert.That((await listInv.Handle(new ListSoftwareInventoryQuery(SoftwareInventoryScope.Site, SiteB1, null, 10, null, false), CancellationToken.None)).Errors[0].Code, Is.EqualTo("Forbidden"));
        Assert.That((await listInv.Handle(new ListSoftwareInventoryQuery(SoftwareInventoryScope.Site, SiteA1, null, 10, null, false), CancellationToken.None)).IsSuccess, Is.True);

        var listAgentSw = new ListAgentSoftwareQueryHandler(swSvc, agentRepo, siteRepo, scopeSiteA1);
        Assert.That((await listAgentSw.Handle(new ListAgentSoftwareQuery(AgentB1), CancellationToken.None)).Errors[0].Code, Is.EqualTo("Forbidden"));
    }

    [Test]
    public async Task AccessListControllers_EnforceTenantAndOwnershipIsolation()
    {
        await using var db = CreateDb();
        await SeedClientsSitesAgentsAsync(db, deleted: false);
        var now = DateTime.UtcNow;

        var viewId = Guid.NewGuid();
        var sharedViewId = Guid.NewGuid();
        var ownerId = Guid.Parse("99999999-0000-0000-0000-000000000001");
        var otherUserId = Guid.Parse("88888888-0000-0000-0000-000000000002");

        db.TicketSavedViews.AddRange(
            new TicketSavedView { Id = viewId, Name = "Private", UserId = ownerId, IsShared = false, CreatedAt = now, UpdatedAt = now },
            new TicketSavedView { Id = sharedViewId, Name = "Shared", UserId = ownerId, IsShared = true, CreatedAt = now, UpdatedAt = now });
        db.AgentAlertDefinitions.Add(new AgentAlertDefinition { Id = Guid.NewGuid(), Title = "Alert B", ScopeType = AlertScopeType.Client, ScopeClientId = ClientB, CreatedAt = now, UpdatedAt = now });
        var alertBId = db.AgentAlertDefinitions.Local.First().Id;
        await db.SaveChangesAsync();

        var agentRepo = new F02AgentRepositoryStub(new Agent { Id = AgentA1, SiteId = SiteA1 }, new Agent { Id = AgentB1, SiteId = SiteB1 });
        var siteRepo = new F02SiteRepositoryStub(
            new Site { Id = SiteA1, ClientId = ClientA, Name = "Site A1" },
            new Site { Id = SiteA2, ClientId = ClientA, Name = "Site A2" },
            new Site { Id = SiteB1, ClientId = ClientB, Name = "Site B1" });
        var scopeSiteA1 = F02ScopeContextStub.ForSites(SiteA1);
        var scopeClientA = F02ScopeContextStub.ForClients(ClientA);
        var mediator = new NoopMediator();

        // ConfigurationsController.GetSite / UpdateSite (Site-scoped tech allowed on A1, 403 on A2/B1)
        var configCtrl = new ConfigurationsController(mediator, null!, null!, null!, db, NullLogger<ConfigurationsController>.Instance);
        Assert.That((await configCtrl.GetSite(SiteA2, scopeSiteA1, siteRepo) as ObjectResult)?.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
        Assert.That(await configCtrl.GetSite(SiteA1, scopeSiteA1, siteRepo), Is.InstanceOf<OkObjectResult>());
        Assert.That(await configCtrl.GetSite(SiteA2, scopeClientA, siteRepo), Is.InstanceOf<OkObjectResult>());

        // AgentSensorsController (403 on AgentB1)
        var agentSensorsCtrl = new AgentSensorsController(null!, null!, null!);
        Assert.That((await agentSensorsCtrl.GetAll(AgentB1, scopeSiteA1, agentRepo, siteRepo, CancellationToken.None) as ObjectResult)?.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));

        // AgentUpdatesController (403 on AgentB1)
        var agentUpdatesCtrl = new AgentUpdatesController(mediator);
        Assert.That((await agentUpdatesCtrl.GetAgentEvents(AgentB1, scopeSiteA1, agentRepo, siteRepo) as ObjectResult)?.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));

        // AgentAlertsController (403 on cross-tenant Create / GetById / Notify)
        var alertB = db.AgentAlertDefinitions.Local.First();
        var alertRepo = F02NullProxy<IAgentAlertRepository>.Create((m, args) =>
            m.Name == "GetByIdAsync" && args is [Guid id] && id == alertBId
                ? Task.FromResult<AgentAlertDefinition?>(alertB)
                : null);
        var alertsCtrl = new AgentAlertsController(mediator, scopeSiteA1, siteRepo);
        Assert.That((await alertsCtrl.GetById(alertBId, alertRepo, agentRepo) as ObjectResult)?.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
        Assert.That((await alertsCtrl.Create(new CreateAlertCommand("t", "d", "info", null, ClientB, null), agentRepo) as ObjectResult)?.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
        Assert.That((await alertsCtrl.Notify(new SendAgentNotificationRequest(AgentB1, "t", "m"), agentRepo) as ObjectResult)?.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));

        // PatchesController (403 on Global / ClientB policy & approvals)
        var patchesCtrl = new PatchesController(mediator, scopeClientA);
        Assert.That((await patchesCtrl.CreateApproval(new PatchesController.CreateApprovalRequest(PatchApprovalScopeType.Global, null, "kb:1"), siteRepo, agentRepo) as ObjectResult)?.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
        Assert.That((await patchesCtrl.UpdatePolicy(new PatchesController.UpdatePolicyRequest(), siteRepo, agentRepo, PatchApprovalScopeType.Client, ClientB) as ObjectResult)?.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));

        // SensorsController (403 on Global / ClientB create)
        var sensorsCtrl = new SensorsController(F02NullProxy<ISensorDefinitionService>.Create(), F02NullProxy<ISensorReadService>.Create());
        Assert.That((await sensorsCtrl.Create(new SensorUpsertRequest { Name = "s", ScopeType = AppApprovalScopeType.Global }, scopeClientA, siteRepo, agentRepo, CancellationToken.None) as ObjectResult)?.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
        Assert.That((await sensorsCtrl.Create(new SensorUpsertRequest { Name = "s", ScopeType = AppApprovalScopeType.Client, ClientId = ClientB }, scopeClientA, siteRepo, agentRepo, CancellationToken.None) as ObjectResult)?.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));

        // TicketSavedViewsController (403 for other user on private view GetById and shared view Update)
        var savedViewsCtrl = new TicketSavedViewsController(mediator)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { Items = { ["UserId"] = otherUserId } }
            }
        };
        Assert.That((await savedViewsCtrl.GetById(viewId, scopeClientA, db) as ObjectResult)?.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
        Assert.That((await savedViewsCtrl.Update(sharedViewId, new Discovery.Api.Controllers.UpdateTicketSavedViewRequest("rename", null, null), scopeClientA, db) as ObjectResult)?.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));

        // WorkflowProfilesController & SlaCalendarsController (403 on Global/ClientB create)
        var wfProfilesCtrl = new WorkflowProfilesController(mediator);
        Assert.That((await wfProfilesCtrl.Create(new CreateWorkflowProfileCommand(null, Guid.NewGuid(), "Global", null, 8, null, 1, "Medium"), scopeClientA) as ObjectResult)?.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
        Assert.That((await wfProfilesCtrl.Create(new CreateWorkflowProfileCommand(ClientB, Guid.NewGuid(), "B", null, 8, null, 1, "Medium"), scopeClientA) as ObjectResult)?.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));

        var slaCtrl = new SlaCalendarsController(mediator);
        Assert.That((await slaCtrl.Create(new CreateSlaCalendarCommand("Global", null, "UTC", 8, 18, "[]"), scopeClientA) as ObjectResult)?.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
        Assert.That((await slaCtrl.Create(new CreateSlaCalendarCommand("B", ClientB, "UTC", 8, 18, "[]"), scopeClientA) as ObjectResult)?.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
    }

    // -------------------------------------------------------------------------
    // Helpers & minimal fakes
    // -------------------------------------------------------------------------

    private static F02TestDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"f02-{Guid.NewGuid():N}")
            .Options;
        return new F02TestDbContext(options);
    }

    private static async Task SeedClientsSitesAgentsAsync(F02TestDbContext db, bool deleted)
    {
        var now = DateTime.UtcNow;
        var deletedAt = deleted ? now : (DateTime?)null;

        db.Clients.AddRange(
            new Client { Id = ClientA, Name = "Client A", IsActive = true, CreatedAt = now, UpdatedAt = now },
            new Client { Id = ClientB, Name = "Client B", IsActive = true, CreatedAt = now, UpdatedAt = now });
        db.Sites.AddRange(
            new Site { Id = SiteA1, ClientId = ClientA, Name = "Site A1", IsActive = true, CreatedAt = now, UpdatedAt = now },
            new Site { Id = SiteA2, ClientId = ClientA, Name = "Site A2", IsActive = true, CreatedAt = now, UpdatedAt = now },
            new Site { Id = SiteB1, ClientId = ClientB, Name = "Site B1", IsActive = true, CreatedAt = now, UpdatedAt = now });
        db.Agents.AddRange(
            new Agent { Id = AgentA1, SiteId = SiteA1, Hostname = "PC-A1", DisplayName = "PC-A1", Status = AgentStatus.Online, DeletedAt = deletedAt, CreatedAt = now, UpdatedAt = now },
            new Agent { Id = AgentA2, SiteId = SiteA2, Hostname = "PC-A2", DisplayName = "PC-A2", Status = AgentStatus.Online, DeletedAt = deletedAt, CreatedAt = now, UpdatedAt = now },
            new Agent { Id = AgentB1, SiteId = SiteB1, Hostname = "PC-B1", DisplayName = "PC-B1", Status = AgentStatus.Online, DeletedAt = deletedAt, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
    }

    private sealed class F02TestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(Client), typeof(Site), typeof(Agent),
                typeof(User),
                typeof(CustomFieldDefinition), typeof(CustomFieldValue),
                typeof(Department), typeof(Ticket), typeof(WorkflowState),
                typeof(AutomationTaskDefinition), typeof(AutomationExecutionReport),
                typeof(AgentWindowsUpdate), typeof(AgentAlertDefinition),
                typeof(TicketSavedView)
            };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(t => t.IsClass && t.Namespace is not null && t.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(t => !allowed.Contains(t)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Client>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<Site>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<Agent>(e => { e.HasKey(x => x.Id); e.HasQueryFilter(x => x.DeletedAt == null); });
            modelBuilder.Entity<User>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<CustomFieldDefinition>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<CustomFieldValue>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<Department>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<Ticket>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<WorkflowState>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<AutomationTaskDefinition>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<AutomationExecutionReport>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<AgentWindowsUpdate>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<AgentAlertDefinition>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<TicketSavedView>(e => e.HasKey(x => x.Id));
        }
    }

    private sealed class NoopMediator : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default)
        {
            if (typeof(TResponse) == typeof(Result<SiteConfiguration>))
                return Task.FromResult((TResponse)(object)Result<SiteConfiguration>.Success(new SiteConfiguration()));
            throw new NotSupportedException($"Unexpected MediatR request {typeof(TResponse).Name}");
        }
        public Task Send<TRequest>(TRequest request, CancellationToken ct = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken ct = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken ct = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken ct = default) where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class StubClientRepo(params Client[] clients) : IClientRepository
    {
        private readonly Dictionary<Guid, Client> _map = clients.ToDictionary(c => c.Id);
        public Task<IEnumerable<Client>> GetAllAsync(bool includeInactive = false) => Task.FromResult(_map.Values.AsEnumerable());
        public Task<Client?> GetByIdAsync(Guid id) => Task.FromResult(_map.GetValueOrDefault(id));
        public Task<Client> CreateAsync(Client client) { _map[client.Id] = client; return Task.FromResult(client); }
        public Task UpdateAsync(Client client) { _map[client.Id] = client; return Task.CompletedTask; }
        public Task DeleteAsync(Guid id) { _map.Remove(id); return Task.CompletedTask; }
    }
}
