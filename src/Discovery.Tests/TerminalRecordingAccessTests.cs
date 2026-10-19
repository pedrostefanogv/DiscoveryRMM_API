using System.Reflection;
using Discovery.Api.Controllers;
using Discovery.Api.Filters;
using Discovery.Core.Configuration;
using Discovery.Core.DTOs;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Infrastructure.Services.Remote.Recording;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Discovery.Tests;

/// <summary>
/// Regressão: os endpoints de gravação de terminal (GET metadados, GET download e
/// DELETE) exigiam permissão GLOBAL em Agents — um usuário com permissão apenas no
/// cliente/site da sessão recebia 403 — e não consideravam o tenant da sessão
/// (nenhum recorte de escopo). Agora usam ScopeSource.AccessList + decisão scoped.
/// </summary>
[TestFixture]
public class TerminalRecordingAccessPolicyTests
{
    private static readonly Guid ClientA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ClientB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid SiteA = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly TerminalRecordingTenant TenantA = new(ClientA, SiteA);

    [Test]
    public void Allows_global_access()
    {
        var access = new UserScopeAccess { HasGlobalAccess = true };

        Assert.That(TerminalRecordingAccessPolicy.Allows(access, TenantA), Is.True);
    }

    [Test]
    public void Allows_scoped_client_da_sessao()
    {
        var access = new UserScopeAccess { AllowedClientIds = [ClientA] };

        Assert.That(TerminalRecordingAccessPolicy.Allows(access, TenantA), Is.True);
    }

    [Test]
    public void Allows_scoped_site_da_sessao()
    {
        var access = new UserScopeAccess { AllowedSiteIds = [SiteA] };

        Assert.That(TerminalRecordingAccessPolicy.Allows(access, TenantA), Is.True);
    }

    [Test]
    public void Denies_scoped_em_outro_cliente()
    {
        var access = new UserScopeAccess
        {
            AllowedClientIds = [ClientB],
            AllowedSiteIds = [Guid.NewGuid()]
        };

        Assert.That(TerminalRecordingAccessPolicy.Allows(access, TenantA), Is.False);
    }

    [Test]
    public void Denies_sem_escopo()
    {
        Assert.That(TerminalRecordingAccessPolicy.Allows(new UserScopeAccess(), TenantA), Is.False);
        Assert.That(TerminalRecordingAccessPolicy.Allows(null, TenantA), Is.False);
    }

    [Test]
    public void Denies_site_vazio_sem_cliente()
    {
        var access = new UserScopeAccess { AllowedSiteIds = [Guid.Empty] };

        Assert.That(
            TerminalRecordingAccessPolicy.Allows(access, new TerminalRecordingTenant(ClientB, Guid.Empty)),
            Is.False,
            "site vazio não pode casar com AllowedSiteIds");
    }
}

/// <summary>
/// Wiring do controller: os três endpoints de gravação de terminal passam pela
/// decisão scoped e devolvem 404 (inexistente) antes de consultar o escopo, 403
/// (tenant errado) sem executar a operação e 200/204 quando o acesso é válido.
/// </summary>
[TestFixture]
public class RemoteSessionsControllerTerminalRecordingAccessTests
{
    private static readonly Guid AgentId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid SessionId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid ClientA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ClientB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid SiteA = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly TerminalRecordingTenant TenantA = new(ClientA, SiteA);

    // ── GET metadados ────────────────────────────────────────────────

    [Test]
    public async Task GetTerminalRecording_global_acessa()
    {
        var ctx = new FakeScopeContext(new UserScopeAccess { HasGlobalAccess = true });
        var service = new FakeTerminalRecordingService { Tenant = TenantA };
        var controller = BuildController(service, ctx);

        var result = await controller.GetTerminalRecording(AgentId, SessionId);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        Assert.That(service.GetCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task GetTerminalRecording_scoped_no_cliente_da_sessao_acessa()
    {
        var ctx = new FakeScopeContext(new UserScopeAccess { AllowedClientIds = [ClientA] });
        var service = new FakeTerminalRecordingService { Tenant = TenantA };
        var controller = BuildController(service, ctx);

        var result = await controller.GetTerminalRecording(AgentId, SessionId);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        Assert.That(ctx.RequestedActions, Has.All.EqualTo(ActionType.View));
        Assert.That(ctx.RequestedResources, Has.All.EqualTo(ResourceType.Agents));
    }

    [Test]
    public async Task GetTerminalRecording_scoped_em_outro_cliente_retorna_403()
    {
        var ctx = new FakeScopeContext(new UserScopeAccess { AllowedClientIds = [ClientB] });
        var service = new FakeTerminalRecordingService { Tenant = TenantA };
        var controller = BuildController(service, ctx);

        var result = await controller.GetTerminalRecording(AgentId, SessionId);

        Assert.That(result, Is.InstanceOf<ObjectResult>());
        Assert.That(((ObjectResult)result).StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
        Assert.That(service.GetCalls, Is.Zero, "sem acesso ao tenant não deve carregar os metadados");
    }

    [Test]
    public async Task GetTerminalRecording_inexistente_retorna_404_sem_consultar_escopo()
    {
        var ctx = new FakeScopeContext(new UserScopeAccess { HasGlobalAccess = true });
        var service = new FakeTerminalRecordingService { Tenant = null };
        var controller = BuildController(service, ctx);

        var result = await controller.GetTerminalRecording(AgentId, SessionId);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
        Assert.That(ctx.Calls, Is.Zero, "o tenant só é checado depois de achar a gravação");
        Assert.That(service.GetCalls, Is.Zero);
    }

    // ── GET download ─────────────────────────────────────────────────

    [Test]
    public async Task DownloadTerminalRecording_scoped_no_cliente_da_sessao_baixa()
    {
        var ctx = new FakeScopeContext(new UserScopeAccess { AllowedClientIds = [ClientA] });
        var service = new FakeTerminalRecordingService { Tenant = TenantA };
        var controller = BuildController(service, ctx);

        var result = await controller.DownloadTerminalRecording(AgentId, SessionId);

        Assert.That(result, Is.InstanceOf<FileStreamResult>());
        Assert.That(service.DownloadCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task DownloadTerminalRecording_scoped_em_outro_cliente_retorna_403()
    {
        var ctx = new FakeScopeContext(new UserScopeAccess { AllowedClientIds = [ClientB] });
        var service = new FakeTerminalRecordingService { Tenant = TenantA };
        var controller = BuildController(service, ctx);

        var result = await controller.DownloadTerminalRecording(AgentId, SessionId);

        Assert.That(((ObjectResult)result).StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
        Assert.That(service.DownloadCalls, Is.Zero, "sem acesso ao tenant não deve abrir o NDJSON");
    }

    [Test]
    public async Task DownloadTerminalRecording_inexistente_retorna_404()
    {
        var ctx = new FakeScopeContext(new UserScopeAccess { HasGlobalAccess = true });
        var service = new FakeTerminalRecordingService { Tenant = null };
        var controller = BuildController(service, ctx);

        var result = await controller.DownloadTerminalRecording(AgentId, SessionId);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
        Assert.That(service.DownloadCalls, Is.Zero);
    }

    // ── DELETE ───────────────────────────────────────────────────────

    [Test]
    public async Task DeleteTerminalRecording_global_exclui()
    {
        var ctx = new FakeScopeContext(new UserScopeAccess { HasGlobalAccess = true });
        var service = new FakeTerminalRecordingService { Tenant = TenantA };
        var controller = BuildController(service, ctx);

        var result = await controller.DeleteTerminalRecording(AgentId, SessionId);

        Assert.That(result, Is.InstanceOf<NoContentResult>());
        Assert.That(service.DeleteCalls, Is.EqualTo(1));
        Assert.That(ctx.RequestedActions, Has.All.EqualTo(ActionType.Execute));
    }

    [Test]
    public async Task DeleteTerminalRecording_scoped_no_site_da_sessao_exclui()
    {
        var ctx = new FakeScopeContext(new UserScopeAccess { AllowedSiteIds = [SiteA] });
        var service = new FakeTerminalRecordingService { Tenant = TenantA };
        var controller = BuildController(service, ctx);

        var result = await controller.DeleteTerminalRecording(AgentId, SessionId);

        Assert.That(result, Is.InstanceOf<NoContentResult>());
        Assert.That(service.DeleteCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task DeleteTerminalRecording_scoped_em_outro_cliente_retorna_403_sem_excluir()
    {
        var ctx = new FakeScopeContext(new UserScopeAccess { AllowedClientIds = [ClientB] });
        var service = new FakeTerminalRecordingService { Tenant = TenantA };
        var controller = BuildController(service, ctx);

        var result = await controller.DeleteTerminalRecording(AgentId, SessionId);

        Assert.That(((ObjectResult)result).StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
        Assert.That(service.DeleteCalls, Is.Zero, "a autorização precisa vir antes de finalizar/apagar");
    }

    [Test]
    public async Task DeleteTerminalRecording_inexistente_retorna_404()
    {
        var ctx = new FakeScopeContext(new UserScopeAccess { HasGlobalAccess = true });
        var service = new FakeTerminalRecordingService { Tenant = null };
        var controller = BuildController(service, ctx);

        var result = await controller.DeleteTerminalRecording(AgentId, SessionId);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
        Assert.That(service.DeleteCalls, Is.Zero);
    }

    // ── Wiring dos atributos ─────────────────────────────────────────

    [TestCase(nameof(RemoteSessionsController.GetTerminalRecording))]
    [TestCase(nameof(RemoteSessionsController.DownloadTerminalRecording))]
    [TestCase(nameof(RemoteSessionsController.DeleteTerminalRecording))]
    public void Endpoint_usa_ScopeSource_AccessList(string actionName)
    {
        var method = typeof(RemoteSessionsController).GetMethod(actionName, BindingFlags.Public | BindingFlags.Instance);
        Assert.That(method, Is.Not.Null);

        var attribute = method!.GetCustomAttributes(typeof(RequirePermissionAttribute), inherit: false)
            .Cast<RequirePermissionAttribute>()
            .SingleOrDefault();

        Assert.That(attribute, Is.Not.Null, "o endpoint precisa manter o RequirePermission");

        // O escopo fica em campo privado do atributo (não há getter público).
        var scopeField = typeof(RequirePermissionAttribute)
            .GetField("_scopeSource", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(scopeField, Is.Not.Null);

        Assert.That(
            scopeField!.GetValue(attribute),
            Is.EqualTo(ScopeSource.AccessList),
            "sem AccessList o filtro volta a exigir permissão global e o usuário scoped perde o acesso");
    }

    private static RemoteSessionsController BuildController(
        ITerminalRecordingService service,
        IScopeContext scopeContext)
        => new(new ThrowingMediator(), service, scopeContext, null!, Options.Create(new RemoteAccessOptions()), NullLogger<RemoteSessionsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private static TerminalRecordingDto Metadata()
        => new(
            Guid.NewGuid(),
            SessionId,
            AgentId,
            ClientA,
            "completed",
            DateTime.UtcNow.AddMinutes(-5),
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow,
            Frames: 10,
            Bytes: 1024,
            DroppedFrames: 0,
            MaxFps: 30,
            StorageProvider: 1,
            RetentionExpiresAtUtc: DateTime.UtcNow.AddDays(30),
            LastError: null,
            DownloadUrl: $"/api/v1/remote-sessions/{AgentId}/recording/terminal/download?sessionId={SessionId}");

    /// <summary>Serviço fake: devolve o tenant e conta chamadas; nunca toca disco/banco.</summary>
    private sealed class FakeTerminalRecordingService : ITerminalRecordingService
    {
        public TerminalRecordingTenant? Tenant { get; init; }
        public int GetCalls { get; private set; }
        public int DownloadCalls { get; private set; }
        public int DeleteCalls { get; private set; }

        public Task<TerminalRecordingTenant?> GetTenantAsync(Guid agentId, Guid sessionId, CancellationToken ct = default)
            => Task.FromResult(Tenant);

        public Task<TerminalRecordingDto?> GetAsync(Guid agentId, Guid sessionId, CancellationToken ct = default)
        {
            GetCalls++;
            return Task.FromResult<TerminalRecordingDto?>(Metadata());
        }

        public Task<TerminalRecordingDownloadDto?> OpenDownloadAsync(Guid agentId, Guid sessionId, CancellationToken ct = default)
        {
            DownloadCalls++;
            var stream = new MemoryStream("{}"u8.ToArray());
            return Task.FromResult<TerminalRecordingDownloadDto?>(
                new TerminalRecordingDownloadDto(stream, $"terminal-{sessionId:N}.ndjson", stream.Length));
        }

        public Task<bool> DeleteAsync(Guid agentId, Guid sessionId, CancellationToken ct = default)
        {
            DeleteCalls++;
            return Task.FromResult(true);
        }

        public Task FinalizeAsync(Guid sessionId, string status, string? lastError = null, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeScopeContext(UserScopeAccess access) : IScopeContext
    {
        public List<ActionType> RequestedActions { get; } = [];
        public List<ResourceType> RequestedResources { get; } = [];
        public int Calls => RequestedActions.Count;

        public Task<UserScopeAccess> GetAccessAsync(ResourceType resource, ActionType action)
        {
            RequestedResources.Add(resource);
            RequestedActions.Add(action);
            return Task.FromResult(access);
        }

        public Task<bool> HasGlobalAccessAsync(ResourceType resource, ActionType action)
            => Task.FromResult(access.HasGlobalAccess);

        public void SetUserId(Guid userId) { }

        public Guid? ResolvedClientId { get; set; }
        public Guid? ResolvedSiteId { get; set; }
    }

    private sealed class ThrowingMediator : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
            => throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => throw new NotSupportedException();
    }
}
