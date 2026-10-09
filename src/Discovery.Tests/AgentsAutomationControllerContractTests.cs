using Discovery.Api.Controllers;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Automation.Commands;
using Discovery.Core.Cqrs.Agents.Automation.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Contrato do controller de operações de automação.
///
/// Regressão coberta: a UI gera um correlation id e o envia no header
/// X-Correlation-Id, mas o backend nunca o lia — o histórico de execuções
/// ficava sem o vínculo com a operação disparada (coluna Correlation vazia,
/// auditoria incompleta).
/// </summary>
[TestFixture]
public class AgentsAutomationControllerContractTests
{
    private static readonly Guid AgentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TaskId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ScriptId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static AutomationExecutionDto ExecutionDto()
        => new(Guid.NewGuid(), AutomationExecutionStatus.Dispatched.ToString(), DateTime.UtcNow);

    [Test]
    public async Task RunTaskNow_PropagatesCorrelationHeaderToCommand()
    {
        var mediator = new CapturingMediator
        {
            Responder = _ => Result<AutomationExecutionDto>.Success(ExecutionDto())
        };
        var controller = BuildController(mediator, "corr-task");

        await controller.RunAutomationTaskNow(AgentId, TaskId);

        var command = mediator.LastRequest as RunAutomationTaskCommand;
        Assert.That(command, Is.Not.Null, "A action deve enviar RunAutomationTaskCommand.");
        Assert.That(command!.AgentId, Is.EqualTo(AgentId));
        Assert.That(command.TaskId, Is.EqualTo(TaskId));
        Assert.That(command.CorrelationId, Is.EqualTo("corr-task"));
    }

    [Test]
    public async Task RunScriptNow_PropagatesCorrelationHeaderToCommand()
    {
        var mediator = new CapturingMediator
        {
            Responder = _ => Result<AutomationExecutionDto>.Success(ExecutionDto())
        };
        var controller = BuildController(mediator, "corr-script");

        await controller.RunAutomationScriptNow(AgentId, ScriptId);

        var command = mediator.LastRequest as RunAutomationScriptCommand;
        Assert.That(command, Is.Not.Null);
        Assert.That(command!.ScriptId, Is.EqualTo(ScriptId));
        Assert.That(command.CorrelationId, Is.EqualTo("corr-script"));
    }

    [Test]
    public async Task RunTaskNow_WithoutHeader_LeavesCorrelationNull()
    {
        var mediator = new CapturingMediator
        {
            Responder = _ => Result<AutomationExecutionDto>.Success(ExecutionDto())
        };
        var controller = BuildController(mediator, correlationId: null);

        await controller.RunAutomationTaskNow(AgentId, TaskId);

        Assert.That(((RunAutomationTaskCommand)mediator.LastRequest!).CorrelationId, Is.Null);
    }

    [Test]
    public async Task GetExecutions_MapsFiltersIncludingBatchCorrelation()
    {
        var mediator = new CapturingMediator
        {
            Responder = _ => Result<IReadOnlyList<AutomationExecutionDto>>.Success([])
        };
        var controller = BuildController(mediator, correlationId: null);

        await controller.GetAutomationExecutionHistory(
            AgentId,
            limit: 25,
            status: AutomationExecutionStatus.Failed,
            sourceType: AutomationExecutionSourceType.RunNow,
            taskId: TaskId,
            scriptId: null,
            correlationId: "lote-1");

        var query = (GetAutomationExecutionsQuery)mediator.LastRequest!;
        Assert.That(query.AgentId, Is.EqualTo(AgentId));
        Assert.That(query.Limit, Is.EqualTo(25));
        Assert.That(query.Status, Is.EqualTo(AutomationExecutionStatus.Failed));
        Assert.That(query.SourceType, Is.EqualTo(AutomationExecutionSourceType.RunNow));
        Assert.That(query.TaskId, Is.EqualTo(TaskId));
        Assert.That(query.CorrelationId, Is.EqualTo("lote-1"));
    }

    [Test]
    public async Task GetPolicies_SendsQueryForAgent()
    {
        var mediator = new CapturingMediator
        {
            Responder = _ => Result<AgentAutomationPolicyPreviewDto>.Success(new AgentAutomationPolicyPreviewDto())
        };
        var controller = BuildController(mediator, correlationId: null);

        await controller.GetAutomationPolicies(AgentId);

        var query = mediator.LastRequest as GetAgentAutomationPoliciesQuery;
        Assert.That(query, Is.Not.Null, "A action deve enviar GetAgentAutomationPoliciesQuery.");
        Assert.That(query!.AgentId, Is.EqualTo(AgentId));
    }

    [Test]
    public async Task ForceSync_HeaderWinsOverBodyAndKeepsFlags()
    {
        var mediator = new CapturingMediator
        {
            Responder = _ => Result<VoidResult>.Success(VoidResult.Value)
        };
        var controller = BuildController(mediator, "corr-header");

        // Body sem flags = default do handler; o header manda no correlation.
        var body = new ForceAutomationSyncCommand(AgentId, TaskIds: null, Policies: false, Inventory: true, Software: true, AppStore: false, CorrelationId: "corr-body");
        await controller.ForceAutomationSync(AgentId, body);

        var sent = (ForceAutomationSyncCommand)mediator.LastRequest!;
        Assert.That(sent.AgentId, Is.EqualTo(AgentId));
        Assert.That(sent.CorrelationId, Is.EqualTo("corr-header"));
        Assert.That(sent.Policies, Is.False);
        Assert.That(sent.Inventory, Is.True);
        Assert.That(sent.Software, Is.True);
        Assert.That(sent.AppStore, Is.False);
    }

    [Test]
    public async Task ForceSync_WithoutHeader_KeepsBodyCorrelation()
    {
        var mediator = new CapturingMediator
        {
            Responder = _ => Result<VoidResult>.Success(VoidResult.Value)
        };
        var controller = BuildController(mediator, correlationId: null);

        await controller.ForceAutomationSync(AgentId, new ForceAutomationSyncCommand(AgentId, CorrelationId: "corr-body"));

        Assert.That(((ForceAutomationSyncCommand)mediator.LastRequest!).CorrelationId, Is.EqualTo("corr-body"));
    }

    private static AgentsController BuildController(IMediator mediator, string? correlationId)
    {
        var httpContext = new DefaultHttpContext();
        if (correlationId is not null)
            httpContext.Request.Headers["X-Correlation-Id"] = correlationId;

        return new AgentsController(mediator, new NoopNoteService(), new NoopScopeContext())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    private sealed class NoopNoteService : INoteService
    {
        public Task<CursorPageDto<EntityNote>> GetPageAsync(Guid? clientId, Guid? siteId, Guid? agentId, string? cursor, int limit, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<EntityNote?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<EntityNote> CreateAsync(EntityNote note, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(EntityNote note, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class NoopScopeContext : IScopeContext
    {
        public Task<UserScopeAccess> GetAccessAsync(ResourceType resource, ActionType action) => throw new NotSupportedException();
        public Task<bool> HasGlobalAccessAsync(ResourceType resource, ActionType action) => throw new NotSupportedException();
        public void SetUserId(Guid userId) { }
        public Guid? ResolvedClientId { get; set; }
        public Guid? ResolvedSiteId { get; set; }
    }

    private sealed class CapturingMediator : IMediator
    {
        public object? LastRequest { get; private set; }
        public Func<object, object>? Responder { get; set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            var response = Responder?.Invoke(request!);
            return Task.FromResult((TResponse)(response ?? default(TResponse))!);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
        {
            LastRequest = request;
            return Task.CompletedTask;
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult<object?>(null);
        }

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
