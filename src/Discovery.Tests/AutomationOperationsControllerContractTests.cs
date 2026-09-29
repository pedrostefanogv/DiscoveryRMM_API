using Discovery.Api.Controllers;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Automation.Commands;
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
/// Contrato do controller de operações em massa: a rota define o escopo
/// (cliente/site) e o header X-Correlation-Id identifica o lote no histórico.
/// </summary>
[TestFixture]
public class AutomationOperationsControllerContractTests
{
    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SiteId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TaskId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ScriptId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static AutomationScopeDispatchResultDto Result()
        => new("site", SiteId, "lote-1", 3, 3, 2, 1, 0, 0, 0, []);

    [Test]
    public async Task RunTaskForClient_MapsScopeAndCorrelation()
    {
        var mediator = new CapturingMediator { Responder = _ => Discovery.Core.Cqrs.Result<AutomationScopeDispatchResultDto>.Success(Result()) };
        var controller = BuildController(mediator, "lote-header");

        var action = await controller.RunTaskForClient(ClientId, TaskId);

        var command = (RunAutomationTaskForScopeCommand)mediator.LastRequest!;
        Assert.That(command.ClientId, Is.EqualTo(ClientId));
        Assert.That(command.SiteId, Is.Null);
        Assert.That(command.TaskId, Is.EqualTo(TaskId));
        Assert.That(command.CorrelationId, Is.EqualTo("lote-header"));
        Assert.That(action, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task RunTaskForSite_KeepsClientAndSiteInScope()
    {
        var mediator = new CapturingMediator { Responder = _ => Discovery.Core.Cqrs.Result<AutomationScopeDispatchResultDto>.Success(Result()) };
        var controller = BuildController(mediator, correlationId: null);

        await controller.RunTaskForSite(ClientId, SiteId, TaskId);

        var command = (RunAutomationTaskForScopeCommand)mediator.LastRequest!;
        Assert.That(command.ClientId, Is.EqualTo(ClientId), "o clientId na rota é o parent do escopo de site");
        Assert.That(command.SiteId, Is.EqualTo(SiteId));
        Assert.That(command.CorrelationId, Is.Null);
    }

    [Test]
    public async Task RunScriptForSite_MapsScriptId()
    {
        var mediator = new CapturingMediator { Responder = _ => Discovery.Core.Cqrs.Result<AutomationScopeDispatchResultDto>.Success(Result()) };
        var controller = BuildController(mediator, "lote-script");

        await controller.RunScriptForSite(ClientId, SiteId, ScriptId);

        var command = (RunAutomationScriptForScopeCommand)mediator.LastRequest!;
        Assert.That(command.ScriptId, Is.EqualTo(ScriptId));
        Assert.That(command.SiteId, Is.EqualTo(SiteId));
        Assert.That(command.CorrelationId, Is.EqualTo("lote-script"));
    }

    [Test]
    public async Task ForceSyncForSite_MapsFlags()
    {
        var mediator = new CapturingMediator { Responder = _ => Discovery.Core.Cqrs.Result<AutomationScopeDispatchResultDto>.Success(Result()) };
        var controller = BuildController(mediator, "lote-sync");

        await controller.ForceSyncForSite(ClientId, SiteId, new ForceAutomationSyncScopeRequest(Policies: false, Inventory: true, Software: true, AppStore: false));

        var command = (ForceAutomationSyncForScopeCommand)mediator.LastRequest!;
        Assert.That(command.ClientId, Is.EqualTo(ClientId));
        Assert.That(command.SiteId, Is.EqualTo(SiteId));
        Assert.That(command.Policies, Is.False);
        Assert.That(command.Inventory, Is.True);
        Assert.That(command.Software, Is.True);
        Assert.That(command.AppStore, Is.False);
        Assert.That(command.CorrelationId, Is.EqualTo("lote-sync"));
    }

    [Test]
    public async Task ForceSyncForClient_WithoutBody_SendsNullFlags()
    {
        var mediator = new CapturingMediator { Responder = _ => Discovery.Core.Cqrs.Result<AutomationScopeDispatchResultDto>.Success(Result()) };
        var controller = BuildController(mediator, correlationId: null);

        await controller.ForceSyncForClient(ClientId, request: null);

        var command = (ForceAutomationSyncForScopeCommand)mediator.LastRequest!;
        Assert.That(command.Policies, Is.Null);
        Assert.That(command.Inventory, Is.Null);
        Assert.That(command.SiteId, Is.Null);
    }

    [TestCase("NotFound", typeof(NotFoundObjectResult))]
    [TestCase("Validation", typeof(BadRequestObjectResult))]
    public async Task Failure_MapsToHttpStatus(string errorCode, Type expected)
    {
        var mediator = new CapturingMediator
        {
            Responder = _ => Discovery.Core.Cqrs.Result<AutomationScopeDispatchResultDto>.Failure(
                errorCode == "NotFound"
                    ? Error.NotFound("Escopo não encontrado.")
                    : Error.Validation("scope", "Escopo inválido."))
        };
        var controller = BuildController(mediator, correlationId: null);

        var action = await controller.RunTaskForClient(ClientId, TaskId);

        Assert.That(action, Is.InstanceOf(expected));
    }

    private static AutomationOperationsController BuildController(IMediator mediator, string? correlationId)
    {
        var httpContext = new DefaultHttpContext();
        if (correlationId is not null)
            httpContext.Request.Headers["X-Correlation-Id"] = correlationId;

        return new AutomationOperationsController(mediator)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
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
