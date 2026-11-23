using System.Reflection;
using Discovery.Api.Controllers;
using Discovery.Api.Filters;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AutomationScripts.Commands;
using Discovery.Core.Cqrs.AutomationScripts.Queries;
using Discovery.Core.Cqrs.AutomationTasks.Commands;
using Discovery.Core.Cqrs.AutomationTasks.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Infrastructure.Cqrs.AutomationScripts;
using Discovery.Infrastructure.Cqrs.AutomationTasks;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;

namespace Discovery.Tests;

[TestFixture]
public class AutomationHardeningAndFixesTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ClientA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ClientB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TaskId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ScriptId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [TestCase(nameof(AutomationScriptsController.GetAll))]
    [TestCase(nameof(AutomationScriptsController.GetById))]
    [TestCase(nameof(AutomationScriptsController.Create))]
    [TestCase(nameof(AutomationScriptsController.Update))]
    [TestCase(nameof(AutomationScriptsController.Delete))]
    [TestCase(nameof(AutomationScriptsController.GetAudit))]
    [TestCase(nameof(AutomationScriptsController.Consume))]
    [TestCase(nameof(AutomationScriptsController.GetCustomFields))]
    public void ScriptsController_Actions_RequireAutomationPermission(string methodName)
    {
        var method = typeof(AutomationScriptsController).GetMethod(methodName);
        Assert.That(method, Is.Not.Null);
        var attrs = method!.GetCustomAttributes<RequirePermissionAttribute>().ToList();
        Assert.That(attrs, Is.Not.Empty, $"{methodName} deve exigir RequirePermissionAttribute(ResourceType.Automation, ...)");
    }

    [TestCase(nameof(AutomationTasksController.GetAll))]
    [TestCase(nameof(AutomationTasksController.GetById))]
    [TestCase(nameof(AutomationTasksController.GetAudit))]
    [TestCase(nameof(AutomationTasksController.GetExecutions))]
    [TestCase(nameof(AutomationTasksController.PreviewTargets))]
    [TestCase(nameof(AutomationTasksController.Create))]
    [TestCase(nameof(AutomationTasksController.Update))]
    [TestCase(nameof(AutomationTasksController.Delete))]
    [TestCase(nameof(AutomationTasksController.Restore))]
    [TestCase(nameof(AutomationTasksController.GetCustomFields))]
    public void TasksController_Actions_RequireAutomationPermission(string methodName)
    {
        var method = typeof(AutomationTasksController).GetMethod(methodName);
        Assert.That(method, Is.Not.Null);
        var attrs = method!.GetCustomAttributes<RequirePermissionAttribute>().ToList();
        Assert.That(attrs, Is.Not.Empty, $"{methodName} deve exigir RequirePermissionAttribute(ResourceType.Automation, ...)");
    }

    [Test]
    public async Task ScriptsController_Create_ForwardsActorIpAndCorrelation()
    {
        var mediator = new RecordingMediator
        {
            Handler = _ => Result<AutomationScriptDetailDto>.Success(new AutomationScriptDetailDto
            {
                Id = ScriptId,
                Name = "Test",
                Summary = "Sum",
                ScriptType = AutomationScriptType.PowerShell,
                Version = "1.0.0",
                IsActive = true,
                Content = "Write-Output 1"
            })
        };
        var controller = new AutomationScriptsController(mediator)
        {
            ControllerContext = new ControllerContext { HttpContext = BuildHttpContext("corr-123") }
        };

        await controller.Create(new CreateAutomationScriptCommand("Test", "Sum", AutomationScriptType.PowerShell, "1.0.0", "Write-Output 1", true));

        var sent = (CreateAutomationScriptCommand)mediator.LastRequest!;
        Assert.That(sent.ChangedBy, Is.EqualTo($"user:{UserId}"));
        Assert.That(sent.CorrelationId, Is.EqualTo("corr-123"));
    }

    [Test]
    public async Task TasksController_Restore_ForwardsActorAndCorrelation()
    {
        var mediator = new RecordingMediator
        {
            Handler = _ => Result<AutomationTaskDetailDto>.Success(new AutomationTaskDetailDto { Id = TaskId, Name = "Task" })
        };
        var controller = new AutomationTasksController(mediator)
        {
            ControllerContext = new ControllerContext { HttpContext = BuildHttpContext("corr-restore") }
        };

        var result = await controller.Restore(TaskId, "confirmed");

        var sent = (RestoreAutomationTaskCommand)mediator.LastRequest!;
        Assert.That(sent.ChangedBy, Is.EqualTo($"user:{UserId}"));
        Assert.That(sent.CorrelationId, Is.EqualTo("corr-restore"));
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task ListAutomationScriptsQueryHandler_ForwardsActiveOnlySearchAndScriptType()
    {
        var svc = new StubScriptService();
        var handler = new ListAutomationScriptsQueryHandler(svc);

        await handler.Handle(
            new ListAutomationScriptsQuery(ClientA, null, 25, ActiveOnly: false, Search: "dns", ScriptType: AutomationScriptType.Python),
            CancellationToken.None);

        Assert.That(svc.LastActiveOnly, Is.False);
        Assert.That(svc.LastSearch, Is.EqualTo("dns"));
        Assert.That(svc.LastScriptType, Is.EqualTo(AutomationScriptType.Python));
    }

    [Test]
    public async Task CreateScript_ScopedUser_CannotCreateGlobalScript()
    {
        var svc = new StubScriptService();
        var scope = new StubScopeContext(UserId, hasGlobal: false, allowedClients: [ClientA]);
        var handler = new CreateAutomationScriptCommandHandler(svc, scope);

        var res = await handler.Handle(
            new CreateAutomationScriptCommand("Global", "Sum", AutomationScriptType.PowerShell, "1.0.0", "echo 1", true, ClientId: null),
            CancellationToken.None);

        Assert.That(res.IsFailure, Is.True);
        Assert.That(res.Errors[0].Code, Is.EqualTo("Forbidden"));
    }

    [Test]
    public async Task CreateTask_ScopedUser_CannotCreateTaskForOtherClient()
    {
        var svc = new StubTaskService();
        var scope = new StubScopeContext(UserId, hasGlobal: false, allowedClients: [ClientA]);
        var handler = new CreateAutomationTaskCommandHandler(svc, new NoOpSyncPublisher(), scope);

        var res = await handler.Handle(
            new CreateAutomationTaskCommand(
                "T", null, AutomationTaskActionType.RunScript, null, null, ScriptId, null,
                AppApprovalScopeType.Client, ClientB, [], [], true, false, false, false, null, false, true),
            CancellationToken.None);

        Assert.That(res.IsFailure, Is.True);
        Assert.That(res.Errors[0].Code, Is.EqualTo("Forbidden"));
    }

    [Test]
    public async Task CreateAndUpdateTask_ForwardsExecutionOrder_AndCleansIncompatibleFieldsOnActionChange()
    {
        var svc = new StubTaskService
        {
            ExistingTask = new AutomationTaskDetailDto
            {
                Id = TaskId,
                Name = "Coletora",
                Description = "Desc antiga",
                ActionType = AutomationTaskActionType.RunScript,
                ScriptId = ScriptId,
                CollectorFieldDefinitionId = Guid.NewGuid(),
                ScopeType = AppApprovalScopeType.Global,
                TriggerImmediate = true,
                TriggerRecurring = true,
                ScheduleCron = "0 8 * * *",
                ExecutionOrder = 40,
                IsActive = true
            }
        };

        var createHandler = new CreateAutomationTaskCommandHandler(svc, new NoOpSyncPublisher());
        await createHandler.Handle(
            new CreateAutomationTaskCommand(
                "Nova", null, AutomationTaskActionType.RunScript, null, null, ScriptId, null,
                AppApprovalScopeType.Global, null, [], [], true, false, false, false, null, false, true,
                ExecutionOrder: 15),
            CancellationToken.None);

        Assert.That(svc.LastCreateRequest!.ExecutionOrder, Is.EqualTo(15));

        var updateHandler = new UpdateAutomationTaskCommandHandler(svc, new NoOpSyncPublisher());
        await updateHandler.Handle(
            new UpdateAutomationTaskCommand(
                TaskId, "Atualizada", "", AutomationTaskActionType.InstallPackage, AppInstallationType.Winget,
                "Google.Chrome", null, null, null, null, null, null,
                true, false, false, false, null, null, true,
                ExecutionOrder: 12),
            CancellationToken.None);

        Assert.That(svc.LastUpdateRequest!.ExecutionOrder, Is.EqualTo(12));
        Assert.That(svc.LastUpdateRequest.Description, Is.Null);
        Assert.That(svc.LastUpdateRequest.CollectorFieldDefinitionId, Is.Null);
        Assert.That(svc.LastUpdateRequest.ScriptId, Is.Null);
        Assert.That(svc.LastUpdateRequest.ScheduleCron, Is.Null);
    }

    [Test]
    public async Task PreviewAutomationTaskTargetsQueryHandler_ReturnsPreview()
    {
        var svc = new StubTaskService();
        var handler = new PreviewAutomationTaskTargetsQueryHandler(svc);

        var res = await handler.Handle(new PreviewAutomationTaskTargetsQuery(TaskId, 20, 0), CancellationToken.None);

        Assert.That(res.IsSuccess, Is.True);
        Assert.That(res.Value!.Total, Is.EqualTo(1));
    }

    [Test]
    public async Task CreateTask_ClientScopedUser_CanCreateSiteTaskViaSiteRepo()
    {
        var siteId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var svc = new StubTaskService();
        var scope = new StubScopeContext(UserId, hasGlobal: false, allowedClients: [ClientA]);
        var siteRepo = new StubSiteRepository(new Discovery.Core.Entities.Site { Id = siteId, ClientId = ClientA, Name = "Matriz" });
        var handler = new CreateAutomationTaskCommandHandler(svc, new NoOpSyncPublisher(), scope, siteRepo);

        var res = await handler.Handle(
            new CreateAutomationTaskCommand(
                "SiteTask", null, AutomationTaskActionType.RunScript, null, null, ScriptId, null,
                AppApprovalScopeType.Site, siteId, [], [], true, false, false, false, null, false, true),
            CancellationToken.None);

        Assert.That(res.IsSuccess, Is.True);
    }

    [Test]
    public async Task UpdateTask_ClientScopedUser_CannotMoveTaskToOtherClientsSite()
    {
        var siteB = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var svc = new StubTaskService
        {
            ExistingTask = new AutomationTaskDetailDto
            {
                Id = TaskId,
                Name = "Task A",
                ActionType = AutomationTaskActionType.RunScript,
                ScriptId = ScriptId,
                ScopeType = AppApprovalScopeType.Client,
                ScopeId = ClientA,
                ScopeClientId = ClientA,
                TriggerImmediate = true,
                IsActive = true
            }
        };
        var scope = new StubScopeContext(UserId, hasGlobal: false, allowedClients: [ClientA]);
        var siteRepo = new StubSiteRepository(new Discovery.Core.Entities.Site { Id = siteB, ClientId = ClientB, Name = "Filial B" });
        var handler = new UpdateAutomationTaskCommandHandler(svc, new NoOpSyncPublisher(), scope, siteRepo);

        var res = await handler.Handle(
            new UpdateAutomationTaskCommand(
                TaskId, null, null, null, null, null, null, null,
                AppApprovalScopeType.Site, siteB, null, null,
                null, null, null, null, null, null, null),
            CancellationToken.None);

        Assert.That(res.IsFailure, Is.True);
        Assert.That(res.Errors[0].Code, Is.EqualTo("Forbidden"));
    }

    private sealed class StubSiteRepository(Discovery.Core.Entities.Site site) : ISiteRepository
    {
        public Task<Discovery.Core.Entities.Site?> GetByIdAsync(Guid id) => Task.FromResult<Discovery.Core.Entities.Site?>(id == site.Id ? site : null);
        public Task<IEnumerable<Discovery.Core.Entities.Site>> GetByClientIdAsync(Guid clientId, bool includeInactive = false) => throw new NotSupportedException();
        public Task<IEnumerable<Discovery.Core.Entities.Site>> GetByClientIdsAsync(IEnumerable<Guid> clientIds, bool includeInactive = false) => throw new NotSupportedException();
        public Task<IEnumerable<Discovery.Core.Entities.Site>> GetAllAsync(bool includeInactive = false) => throw new NotSupportedException();
        public Task<IEnumerable<Discovery.Core.Entities.Site>> GetByIdsAsync(IEnumerable<Guid> siteIds, bool includeInactive = false) => throw new NotSupportedException();
        public Task<Discovery.Core.Entities.Site> CreateAsync(Discovery.Core.Entities.Site s) => throw new NotSupportedException();
        public Task UpdateAsync(Discovery.Core.Entities.Site s) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id) => throw new NotSupportedException();
    }

    private static DefaultHttpContext BuildHttpContext(string correlationId)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["UserId"] = UserId;
        ctx.Request.Headers["X-Correlation-Id"] = correlationId;
        return ctx;
    }

    private sealed class RecordingMediator : IMediator
    {
        public object? LastRequest { get; private set; }
        public Func<object, object>? Handler { get; set; }
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult((TResponse)Handler!(request)!);
        }
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class StubScopeContext(Guid userId, bool hasGlobal, IReadOnlyList<Guid> allowedClients) : IScopeContext
    {
        public Guid? UserId => userId;
        public Guid? ResolvedClientId { get; set; }
        public Guid? ResolvedSiteId { get; set; }
        public Task<UserScopeAccess> GetAccessAsync(ResourceType resource, ActionType action)
            => Task.FromResult(new UserScopeAccess { HasGlobalAccess = hasGlobal, AllowedClientIds = allowedClients, AllowedSiteIds = [] });
        public Task<bool> HasGlobalAccessAsync(ResourceType resource, ActionType action) => Task.FromResult(hasGlobal);
        public void SetUserId(Guid id) { }
    }

    private sealed class NoOpSyncPublisher : ISyncInvalidationPublisher
    {
        public Task PublishGlobalAsync(SyncResourceType resource, string reason, AppInstallationType? installationType = null, string? correlationId = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task PublishByScopeAsync(SyncResourceType resource, AppApprovalScopeType scopeType, Guid? scopeId, string reason, AppInstallationType? installationType = null, string? correlationId = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task PublishByScopeAsync(SyncResourceType resource, AppApprovalScopeType scopeType, Guid? scopeId, string reason, string? revision = null, string? correlationId = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task PublishForAgentAsync(SyncResourceType resource, Guid agentId, string reason, string? revision = null, string? correlationId = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task PublishForAgentsAsync(SyncResourceType resource, IEnumerable<Guid> agentIds, string reason, string? revision = null, string? correlationId = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class StubScriptService : IAutomationScriptService
    {
        public bool? LastActiveOnly { get; private set; }
        public string? LastSearch { get; private set; }
        public AutomationScriptType? LastScriptType { get; private set; }

        public Task<CursorPageDto<AutomationScriptSummaryDto>> GetListPageAsync(Guid? clientId, bool activeOnly, string? cursor, int limit, CancellationToken cancellationToken = default)
            => GetListPageAsync(clientId, activeOnly, cursor, limit, cancellationToken, null, null);

        public Task<CursorPageDto<AutomationScriptSummaryDto>> GetListPageAsync(Guid? clientId, bool activeOnly, string? cursor, int limit, CancellationToken cancellationToken, string? search, AutomationScriptType? scriptType)
        {
            LastActiveOnly = activeOnly;
            LastSearch = search;
            LastScriptType = scriptType;
            return Task.FromResult(new CursorPageDto<AutomationScriptSummaryDto>([], 0, null, null, false, limit));
        }

        public Task<AutomationScriptDetailDto?> GetByIdAsync(Guid id, bool includeInactive = false, CancellationToken cancellationToken = default) => Task.FromResult<AutomationScriptDetailDto?>(null);
        public Task<AutomationScriptDetailDto> CreateAsync(CreateAutomationScriptRequest request, string? changedBy, string? ipAddress, string correlationId, CancellationToken cancellationToken = default) => Task.FromResult(new AutomationScriptDetailDto());
        public Task<AutomationScriptDetailDto?> UpdateAsync(Guid id, UpdateAutomationScriptRequest request, string? changedBy, string? ipAddress, string correlationId, CancellationToken cancellationToken = default) => Task.FromResult<AutomationScriptDetailDto?>(null);
        public Task<bool> DeleteAsync(Guid id, string? changedBy, string? ipAddress, string correlationId, string? reason, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IReadOnlyList<AutomationScriptAuditDto>> GetAuditAsync(Guid id, int limit = 100, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AutomationScriptAuditDto>>([]);
        public Task<AutomationScriptConsumeDto?> GetConsumePayloadAsync(Guid id, string? changedBy, string? ipAddress, string correlationId, CancellationToken cancellationToken = default) => Task.FromResult<AutomationScriptConsumeDto?>(null);
    }

    private sealed class StubTaskService : IAutomationTaskService
    {
        public AutomationTaskDetailDto? ExistingTask { get; set; }
        public CreateAutomationTaskRequest? LastCreateRequest { get; private set; }
        public UpdateAutomationTaskRequest? LastUpdateRequest { get; private set; }

        public Task<CursorPageDto<AutomationTaskSummaryDto>> GetListPageAsync(AppApprovalScopeType? scopeType, Guid? scopeId, bool activeOnly, bool deletedOnly, bool includeDeleted, string? search, Guid? clientId, Guid? siteId, Guid? agentId, IReadOnlyList<AppApprovalScopeType>? scopeTypes, IReadOnlyList<AutomationTaskActionType>? actionTypes, IReadOnlyList<string>? labels, IReadOnlyList<string>? triggers, bool? isActive, string? cursor, int limit, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AutomationTaskDetailDto?> GetByIdAsync(Guid id, bool includeInactive = false, CancellationToken cancellationToken = default)
            => Task.FromResult(ExistingTask);

        public Task<AutomationTaskDetailDto> CreateAsync(CreateAutomationTaskRequest request, string? changedBy, string? ipAddress, string correlationId, CancellationToken cancellationToken = default)
        {
            LastCreateRequest = request;
            return Task.FromResult(new AutomationTaskDetailDto { Id = TaskId, Name = request.Name, ScopeType = request.ScopeType });
        }

        public Task<AutomationTaskDetailDto?> UpdateAsync(Guid id, UpdateAutomationTaskRequest request, string? changedBy, string? ipAddress, string correlationId, CancellationToken cancellationToken = default)
        {
            LastUpdateRequest = request;
            return Task.FromResult<AutomationTaskDetailDto?>(new AutomationTaskDetailDto { Id = id, Name = request.Name, ScopeType = request.ScopeType });
        }

        public Task<bool> DeleteAsync(Guid id, string? changedBy, string? ipAddress, string correlationId, string? reason, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<AutomationTaskDetailDto?> RestoreAsync(Guid id, string? changedBy, string? ipAddress, string correlationId, string? reason, CancellationToken cancellationToken = default) => Task.FromResult<AutomationTaskDetailDto?>(null);
        public Task<IReadOnlyList<AutomationTaskAuditDto>> GetAuditAsync(Guid id, int limit = 100, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AutomationTaskAuditDto>>([]);
        public Task<AutomationTaskTargetPreviewPageDto?> PreviewTargetAgentsAsync(Guid taskId, int limit = 50, int offset = 0, CancellationToken cancellationToken = default)
            => Task.FromResult<AutomationTaskTargetPreviewPageDto?>(new AutomationTaskTargetPreviewPageDto { TaskName = "Preview", Total = 1, Count = 1 });
        public Task<string> GetPolicyFingerprintForAgentAsync(Guid agentId, CancellationToken cancellationToken = default) => Task.FromResult("fp");
        public Task<AgentAutomationPolicyPreviewDto> GetApplicablePoliciesForAgentAsync(Guid agentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentAutomationPolicySyncResponse> SyncPolicyForAgentAsync(Guid agentId, AgentAutomationPolicySyncRequest request, string? changedBy, string? ipAddress, string correlationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
