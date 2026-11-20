using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Discovery.Api.Controllers;
using Discovery.Api.Cqrs.RemoteSessions.CommandHandlers;
using Discovery.Api.Filters;
using Discovery.Api.Middleware;
using Discovery.Api.Services;
using Discovery.Core.Configuration;
using Discovery.Core.Cqrs.RemoteSessions.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Entities.Security;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Identity;
using Discovery.Core.Interfaces.Security;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services.Remote.Recording;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Discovery.Tests;

public sealed class RemoteProxyTunnelTests
{
    // ── 1. StartRemoteSessionCommandHandler: Proxy policy forwarding & Consent ──

    [Test]
    public async Task StartSession_Proxy_Forwards_RemoteAccessOptions_Proxy_Fields()
    {
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var agent = new Agent { Id = agentId, SiteId = siteId, Hostname = "LAN-HOST-1", AgentVersion = "9.9.9" };
        var site = new Site { Id = siteId, ClientId = clientId, Name = "Main Site" };
        var dispatcher = new RecordingCommandDispatcher();

        var options = Options.Create(new RemoteAccessOptions
        {
            Enabled = true,
            Proxy = new RemoteAccessProxyOptions
            {
                DefaultAllowlist = ["192.168.10.0/24", "10.0.0.5/32"],
                AllowedPorts = [22, 80, 443, 8080],
                MaxResponseBytes = 4_194_304,
                AllowLoopback = true
            }
        });

        var handler = CreateStartHandler(agent, site, dispatcher, options, RemoteConsentMode.Off);

        var result = await handler.Handle(new StartRemoteSessionCommand(
            AgentId: agentId,
            UserId: userId,
            TenantId: clientId,
            SiteId: siteId,
            Kind: RemoteSessionKind.Proxy,
            Transport: RemoteTransport.Nats,
            Quality: QualityProfile.High,
            Codec: RemoteCodec.Jpeg), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(dispatcher.Commands, Has.Count.EqualTo(1));

        using var doc = JsonDocument.Parse(dispatcher.Commands[0].Payload);
        var proxy = doc.RootElement.GetProperty("proxy");
        Assert.That(proxy.GetProperty("enabled").GetBoolean(), Is.True);
        Assert.That(proxy.GetProperty("maxResponseBytes").GetInt32(), Is.EqualTo(4_194_304));
        Assert.That(proxy.GetProperty("allowLoopback").GetBoolean(), Is.True);

        var allowlist = proxy.GetProperty("defaultAllowlist").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.That(allowlist, Is.EqualTo(new[] { "192.168.10.0/24", "10.0.0.5/32" }));

        var ports = proxy.GetProperty("allowedPorts").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.That(ports, Is.EqualTo(new[] { 22, 80, 443, 8080 }));
    }

    // ── 1b. Allowlist HERDÁVEL (config do banco vence o appsettings) ─────

    [Test]
    public async Task StartSession_Proxy_UsaAllowlistHerdavelDoBanco()
    {
        // O ponto da feature: a política vem da configuração herdável, e o
        // operador consegue liberar 192.168.10.250:8006 sem editar appsettings
        // nem reiniciar a API.
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        var agent = new Agent { Id = agentId, SiteId = siteId, Hostname = "LAN-HOST-1", AgentVersion = "9.9.9" };
        var site = new Site { Id = siteId, ClientId = clientId, Name = "Main Site" };
        var dispatcher = new RecordingCommandDispatcher();

        // appsettings continua vazio (bloqueio total): só o banco libera.
        var options = Options.Create(new RemoteAccessOptions
        {
            Enabled = true,
            Proxy = new RemoteAccessProxyOptions { DefaultAllowlist = [], AllowedPorts = [] }
        });

        var handler = CreateStartHandler(
            agent, site, dispatcher, options, RemoteConsentMode.Off,
            resolvedProxyPolicyJson: """
                {"entries":[{"cidr":"192.168.10.0/24","ports":[8006]}]}
                """);

        var result = await handler.Handle(new StartRemoteSessionCommand(
            AgentId: agentId, UserId: Guid.NewGuid(), TenantId: clientId, SiteId: siteId,
            Kind: RemoteSessionKind.Proxy, Transport: RemoteTransport.Nats,
            Quality: QualityProfile.High, Codec: RemoteCodec.Jpeg), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);

        using var doc = JsonDocument.Parse(dispatcher.Commands[0].Payload);
        var proxy = doc.RootElement.GetProperty("proxy");

        Assert.Multiple(() =>
        {
            Assert.That(proxy.GetProperty("enabled").GetBoolean(), Is.True);
            var allowlist = proxy.GetProperty("defaultAllowlist")
                .EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.That(allowlist, Is.EqualTo(new[] { "192.168.10.0/24" }),
                "a allowlist do banco tem de vencer o appsettings vazio");
        });
    }

    [Test]
    public async Task StartSession_Proxy_ModoOpen_EnviaEnabledFalse()
    {
        // "Liberado geral" NÃO vira 0.0.0.0/0: vira enabled=false, que desliga o
        // gate de CIDR/porta no agent mas preserva as proteções SSRF
        // incondicionais (link-local, multicast, IMDS).
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        var agent = new Agent { Id = agentId, SiteId = siteId, Hostname = "LAN-HOST-2", AgentVersion = "9.9.9" };
        var site = new Site { Id = siteId, ClientId = clientId, Name = "Main Site" };
        var dispatcher = new RecordingCommandDispatcher();

        var handler = CreateStartHandler(
            agent, site, dispatcher, Options.Create(new RemoteAccessOptions { Enabled = true }),
            RemoteConsentMode.Off,
            resolvedProxyPolicyJson: """{"mode":"open"}""");

        var result = await handler.Handle(new StartRemoteSessionCommand(
            AgentId: agentId, UserId: Guid.NewGuid(), TenantId: clientId, SiteId: siteId,
            Kind: RemoteSessionKind.Proxy, Transport: RemoteTransport.Nats,
            Quality: QualityProfile.High, Codec: RemoteCodec.Jpeg), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);

        using var doc = JsonDocument.Parse(dispatcher.Commands[0].Payload);
        var proxy = doc.RootElement.GetProperty("proxy");

        Assert.Multiple(() =>
        {
            Assert.That(proxy.GetProperty("enabled").GetBoolean(), Is.False,
                "open => enabled=false (o agent mantém as proteções SSRF incondicionais)");
            Assert.That(proxy.GetProperty("defaultAllowlist").EnumerateArray(), Is.Empty);
        });
    }

    [Test]
    public async Task StartSession_Proxy_SemPoliticaHerdaAppsettingsComoBootstrap()
    {
        // Compatibilidade: sem valor no banco, vale o appsettings (comportamento
        // anterior à UI) — quem só usa o arquivo não é afetado.
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        var agent = new Agent { Id = agentId, SiteId = siteId, Hostname = "LAN-HOST-3", AgentVersion = "9.9.9" };
        var site = new Site { Id = siteId, ClientId = clientId, Name = "Main Site" };
        var dispatcher = new RecordingCommandDispatcher();

        var options = Options.Create(new RemoteAccessOptions
        {
            Enabled = true,
            Proxy = new RemoteAccessProxyOptions
            {
                DefaultAllowlist = ["10.1.0.0/16"],
                AllowedPorts = [443],
                MaxResponseBytes = 2_097_152
            }
        });

        // resolvedProxyPolicyJson: null => o stub devolve "{}" (default da coluna,
        // como em produção) e o handler tem de cair no appsettings.
        var handler = CreateStartHandler(agent, site, dispatcher, options, RemoteConsentMode.Off);

        var result = await handler.Handle(new StartRemoteSessionCommand(
            AgentId: agentId, UserId: Guid.NewGuid(), TenantId: clientId, SiteId: siteId,
            Kind: RemoteSessionKind.Proxy, Transport: RemoteTransport.Nats,
            Quality: QualityProfile.High, Codec: RemoteCodec.Jpeg), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);

        using var doc = JsonDocument.Parse(dispatcher.Commands[0].Payload);
        var proxy = doc.RootElement.GetProperty("proxy");

        Assert.Multiple(() =>
        {
            var allowlist = proxy.GetProperty("defaultAllowlist")
                .EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.That(allowlist, Is.EqualTo(new[] { "10.1.0.0/16" }));
            Assert.That(proxy.GetProperty("maxResponseBytes").GetInt32(), Is.EqualTo(2_097_152));
        });
    }

    [Test]
    public async Task StartSession_Proxy_DefaultDaColunaNaoBloqueiaQuemUsaAppsettings()
    {
        // REGRESSÃO: a coluna do servidor é NOT NULL DEFAULT '{}', então a
        // resolução NUNCA devolve null. Se "{}" fosse tratado como política
        // explícita, toda instalação que só tem allowlist no appsettings (e nunca
        // abriu a tela) passaria a ser BLOQUEADA no rollout.
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        var agent = new Agent { Id = agentId, SiteId = siteId, Hostname = "LAN-HOST-4", AgentVersion = "9.9.9" };
        var site = new Site { Id = siteId, ClientId = clientId, Name = "Main Site" };
        var dispatcher = new RecordingCommandDispatcher();

        var options = Options.Create(new RemoteAccessOptions
        {
            Enabled = true,
            Proxy = new RemoteAccessProxyOptions { DefaultAllowlist = ["172.16.0.0/12"] }
        });

        // "{}" explícito = default da migration = ninguém configurou.
        var handler = CreateStartHandler(
            agent, site, dispatcher, options, RemoteConsentMode.Off,
            resolvedProxyPolicyJson: "{}");

        var result = await handler.Handle(new StartRemoteSessionCommand(
            AgentId: agentId, UserId: Guid.NewGuid(), TenantId: clientId, SiteId: siteId,
            Kind: RemoteSessionKind.Proxy, Transport: RemoteTransport.Nats,
            Quality: QualityProfile.High, Codec: RemoteCodec.Jpeg), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);

        using var doc = JsonDocument.Parse(dispatcher.Commands[0].Payload);
        var allowlist = doc.RootElement.GetProperty("proxy").GetProperty("defaultAllowlist")
            .EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.That(allowlist, Is.EqualTo(new[] { "172.16.0.0/12" }),
            "o default '{}' do banco não pode anular a allowlist do appsettings");
    }

    [Test]
    public async Task StartSession_Proxy_PortasPorEntradaViramRestricaoDaSessao()
    {
        // O agent tem UM conjunto de portas por sessão (não um por prefixo). As
        // portas digitadas numa entrada precisam virar o conjunto efetivo, senão
        // a UI promete restrição de porta e entrega todas as portas.
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        var agent = new Agent { Id = agentId, SiteId = siteId, Hostname = "LAN-HOST-5", AgentVersion = "9.9.9" };
        var site = new Site { Id = siteId, ClientId = clientId, Name = "Main Site" };
        var dispatcher = new RecordingCommandDispatcher();

        var handler = CreateStartHandler(
            agent, site, dispatcher, Options.Create(new RemoteAccessOptions { Enabled = true }),
            RemoteConsentMode.Off,
            resolvedProxyPolicyJson: """
                {"entries":[{"cidr":"192.168.10.0/24","ports":[8006]}]}
                """);

        var result = await handler.Handle(new StartRemoteSessionCommand(
            AgentId: agentId, UserId: Guid.NewGuid(), TenantId: clientId, SiteId: siteId,
            Kind: RemoteSessionKind.Proxy, Transport: RemoteTransport.Nats,
            Quality: QualityProfile.High, Codec: RemoteCodec.Jpeg), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);

        using var doc = JsonDocument.Parse(dispatcher.Commands[0].Payload);
        var ports = doc.RootElement.GetProperty("proxy").GetProperty("allowedPorts")
            .EnumerateArray().Select(e => e.GetInt32()).ToArray();

        Assert.That(ports, Is.EqualTo(new[] { 8006 }),
            "a porta da entrada tem de restringir a sessão; vazio liberaria TODAS as portas");
    }

    [TestCase(RemoteSessionKind.Proxy)]
    [TestCase(RemoteSessionKind.Rdp)]
    public async Task StartSession_ProxyOrRdp_WithPromptConsent_RequiresConsentAndOmitsImmediateCredentials(RemoteSessionKind kind)
    {
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var agent = new Agent { Id = agentId, SiteId = siteId, Hostname = "WIN-CONSENT", AgentVersion = "9.9.9" };
        var site = new Site { Id = siteId, ClientId = clientId, Name = "Main Site" };
        var dispatcher = new RecordingCommandDispatcher();

        var handler = CreateStartHandler(agent, site, dispatcher, Options.Create(new RemoteAccessOptions()), RemoteConsentMode.Prompt);

        var result = await handler.Handle(new StartRemoteSessionCommand(
            AgentId: agentId,
            UserId: userId,
            TenantId: clientId,
            SiteId: siteId,
            Kind: kind,
            Transport: RemoteTransport.Nats,
            Quality: QualityProfile.High,
            Codec: RemoteCodec.Jpeg), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.ConsentRequired, Is.True);
        Assert.That(result.Value.ConsentStatus, Is.EqualTo("pending"));
        Assert.That(result.Value.Status, Is.EqualTo(RemoteSessionStatus.PendingConsent));
        Assert.That(result.Value.ViewerCredentials, Is.Null);
    }

    // ── 2. TunnelTicketService: Redis + MemoryCache, Single-Use, Expiry, Binding ──

    [Test]
    public async Task TunnelTicketService_MemoryFallback_EnforcesSingleUseAndBindingAndExpiry()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var clock = new AdjustableTimeProvider(new DateTimeOffset(2026, 11, 18, 20, 0, 0, TimeSpan.Zero));
        var svc = new TunnelTicketService(cache, NullLogger<TunnelTicketService>.Instance, redisService: null, clock);

        var userId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var issued = await svc.IssueAsync(userId, agentId, sessionId);
        Assert.That(issued.Ticket, Is.Not.Empty);
        Assert.That(issued.ExpiresInSeconds, Is.EqualTo(TunnelTicketService.DefaultTtlSeconds));

        // Cross-session and cross-agent rejections do not grant access.
        Assert.That(await svc.ConsumeAsync(issued.Ticket, agentId, Guid.NewGuid()), Is.Null);
        Assert.That(await svc.ConsumeAsync(issued.Ticket, Guid.NewGuid(), sessionId), Is.Null);

        // First valid consumption succeeds.
        var consumed = await svc.ConsumeAsync(issued.Ticket, agentId, sessionId);
        Assert.That(consumed, Is.Not.Null);
        Assert.That(consumed!.UserId, Is.EqualTo(userId));

        // Replay is rejected.
        Assert.That(await svc.ConsumeAsync(issued.Ticket, agentId, sessionId), Is.Null);

        // Expired ticket is rejected.
        var issued2 = await svc.IssueAsync(userId, agentId, sessionId);
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.That(await svc.ConsumeAsync(issued2.Ticket, agentId, sessionId), Is.Null);
    }

    [Test]
    public async Task TunnelTicketService_RedisBacked_ConcurrentConsumeAllowsExactlyOneWinner()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var redis = new InMemoryRedisStub();
        var clock = new AdjustableTimeProvider(new DateTimeOffset(2026, 11, 18, 20, 0, 0, TimeSpan.Zero));
        var svc = new TunnelTicketService(cache, NullLogger<TunnelTicketService>.Instance, redis, clock);

        var userId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var issued = await svc.IssueAsync(userId, agentId, sessionId);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 12).Select(_ => svc.ConsumeAsync(issued.Ticket, agentId, sessionId)));

        var winners = results.Where(r => r is not null).ToList();
        Assert.That(winners, Has.Count.EqualTo(1));
        Assert.That(winners[0]!.UserId, Is.EqualTo(userId));
    }

    // ── 3. RemoteSessionsController.IssueTunnelTicket ──

    [Test]
    public void IssueTunnelTicket_DeclaresRequiredSecurityAttributes()
    {
        var method = typeof(RemoteSessionsController).GetMethod(nameof(RemoteSessionsController.IssueTunnelTicket));
        Assert.That(method, Is.Not.Null);

        var sessionAuth = method!.GetCustomAttribute<RemoteSessionAuthorizeAttribute>();
        Assert.That(sessionAuth, Is.Not.Null);
        Assert.That(sessionAuth!.RequiredAction, Is.EqualTo(ActionType.Execute));

        var perm = method.GetCustomAttribute<RequirePermissionAttribute>();
        Assert.That(perm, Is.Not.Null);
        var scopeField = typeof(RequirePermissionAttribute).GetField("_scopeSource", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(scopeField!.GetValue(perm), Is.EqualTo(ScopeSource.AccessList));
    }

    [Test]
    public async Task IssueTunnelTicket_ReturnsTicketForOwnerAndRejectsCrossUserOrOutOfScope()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var siteA = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();

        var session = new RemoteSession
        {
            Id = sessionId,
            AgentId = agentId,
            UserId = ownerId,
            TenantId = tenantA,
            SiteId = siteA,
            Status = RemoteSessionStatus.Active,
            NatsSubject = $"tenant.{tenantA:N}.site.{siteA:N}.agent.{agentId:N}.remote.session.{sessionId:N}"
        };

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var ticketSvc = new TunnelTicketService(cache, NullLogger<TunnelTicketService>.Instance);

        var controller = BuildTunnelController(session, F02ScopeContextStub.ForClients(tenantA), ticketSvc, ownerId);
        var okResult = await controller.IssueTunnelTicket(agentId, sessionId) as OkObjectResult;
        Assert.That(okResult, Is.Not.Null);
        var dto = okResult!.Value as TunnelTicketResponseDto;
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.Ticket, Is.Not.Empty);
        Assert.That(dto.ExpiresInSeconds, Is.EqualTo(60));

        // Cross-user rejected with 404.
        var otherUserCtrl = BuildTunnelController(session, F02ScopeContextStub.ForClients(tenantA), ticketSvc, otherUserId);
        var otherResult = await otherUserCtrl.IssueTunnelTicket(agentId, sessionId);
        Assert.That(otherResult, Is.TypeOf<NotFoundObjectResult>());

        // Out-of-scope tenant rejected with 404.
        var outOfScopeCtrl = BuildTunnelController(session, F02ScopeContextStub.ForClients(tenantB), ticketSvc, ownerId);
        var outOfScopeResult = await outOfScopeCtrl.IssueTunnelTicket(agentId, sessionId);
        Assert.That(outOfScopeResult, Is.TypeOf<NotFoundObjectResult>());
    }

    // ── 4. UserAuthMiddleware on /tunnel/ws ──

    [Test]
    public async Task UserAuthMiddleware_TunnelWs_AcceptsSingleUseTicketOnceAndRejectsAccessTokenQuery()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var ticketSvc = new TunnelTicketService(cache, NullLogger<TunnelTicketService>.Instance);
        var userId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var issued = await ticketSvc.IssueAsync(userId, agentId, sessionId);

        var middleware = new UserAuthMiddleware(_ => Task.CompletedTask, NullLogger<UserAuthMiddleware>.Instance);
        var path = $"/api/v1/remote-sessions/{agentId}/{sessionId}/tunnel/ws";

        // 1. Valid ?ticket= authenticates user and runs next.
        var next1 = false;
        var ctx1 = CreateTunnelHttpContext(path, $"?ticket={Uri.EscapeDataString(issued.Ticket)}", ticketSvc);
        var mw1 = new UserAuthMiddleware(_ => { next1 = true; return Task.CompletedTask; }, NullLogger<UserAuthMiddleware>.Instance);
        await mw1.InvokeAsync(ctx1, F02NullProxy<IJwtService>.Create(), F02NullProxy<IUserSessionRepository>.Create());
        Assert.That(next1, Is.True);
        Assert.That(ctx1.Items["UserId"], Is.EqualTo(userId));

        // 2. Reusing the same ticket returns 401 and blocks next.
        var next2 = false;
        var ctx2 = CreateTunnelHttpContext(path, $"?ticket={Uri.EscapeDataString(issued.Ticket)}", ticketSvc);
        var mw2 = new UserAuthMiddleware(_ => { next2 = true; return Task.CompletedTask; }, NullLogger<UserAuthMiddleware>.Instance);
        await mw2.InvokeAsync(ctx2, F02NullProxy<IJwtService>.Create(), F02NullProxy<IUserSessionRepository>.Create());
        Assert.That(next2, Is.False);
        Assert.That(ctx2.Response.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));

        // 3. Long-lived JWT in ?access_token= is NEVER accepted on /tunnel/ws.
        var ctx3 = CreateTunnelHttpContext(path, "?access_token=eyJhbGciOiJIUzI1NiJ9.longlived.sig", ticketSvc);
        await middleware.InvokeAsync(ctx3, F02NullProxy<IJwtService>.Create(), F02NullProxy<IUserSessionRepository>.Create());
        Assert.That(ctx3.Items.ContainsKey("UserId"), Is.False);
    }

    // ── 5. WS->NATS Multi-Frame Reassembly & MessageTooBig ──

    [Test]
    public async Task RelayWebSocketToNats_ReassemblesMultiFrameMessageIntoSinglePublish()
    {
        var part1 = Encoding.UTF8.GetBytes("{\"streamId\":1,\"op\":\"open\",");
        var part2 = Encoding.UTF8.GetBytes("\"protocol\":\"rdp\",\"targetPort\":3389}");
        var binPart1 = new byte[] { 0x01, 0x02, 0x00, 0x00 };
        var binPart2 = new byte[] { 0x00, 0x01, 0xAA, 0xBB };

        var socket = new ScriptedWebSocket(
        [
            new(part1, WebSocketMessageType.Text, EndOfMessage: false),
            new(part2, WebSocketMessageType.Text, EndOfMessage: true),
            new(binPart1, WebSocketMessageType.Binary, EndOfMessage: false),
            new(binPart2, WebSocketMessageType.Binary, EndOfMessage: true),
            new([], WebSocketMessageType.Close, EndOfMessage: true)
        ]);

        var published = new List<byte[]>();
        var closedWithError = await RemoteSessionsController.RelayWebSocketToNatsAsync(
            socket,
            maxPayloadBytes: 1024,
            (frame, _) => { published.Add(frame); return ValueTask.CompletedTask; },
            CancellationToken.None);

        Assert.That(closedWithError, Is.False);
        Assert.That(published, Has.Count.EqualTo(2));
        Assert.That(Encoding.UTF8.GetString(published[0]), Is.EqualTo("{\"streamId\":1,\"op\":\"open\",\"protocol\":\"rdp\",\"targetPort\":3389}"));
        Assert.That(published[1], Is.EqualTo(new byte[] { 0x01, 0x02, 0x00, 0x00, 0x00, 0x01, 0xAA, 0xBB }));
    }

    [Test]
    public async Task RelayWebSocketToNats_MultiFrameExceedingMaxPayload_ClosesWithMessageTooBigWithoutPublishing()
    {
        var chunk1 = new byte[600];
        var chunk2 = new byte[500];
        Array.Fill(chunk1, (byte)0x41);
        Array.Fill(chunk2, (byte)0x42);

        var socket = new ScriptedWebSocket(
        [
            new(chunk1, WebSocketMessageType.Binary, EndOfMessage: false),
            new(chunk2, WebSocketMessageType.Binary, EndOfMessage: true)
        ]);

        var published = new List<byte[]>();
        var closedWithError = await RemoteSessionsController.RelayWebSocketToNatsAsync(
            socket,
            maxPayloadBytes: 1000,
            (frame, _) => { published.Add(frame); return ValueTask.CompletedTask; },
            CancellationToken.None);

        Assert.That(closedWithError, Is.True);
        Assert.That(published, Is.Empty);
        Assert.That(socket.CloseStatus, Is.EqualTo(WebSocketCloseStatus.MessageTooBig));
    }

    [Test]
    public async Task RelayWebSocketToNats_CloseMidFragment_DiscardsPartialFrame()
    {
        var socket = new ScriptedWebSocket(
        [
            new([0x01, 0x02, 0x03], WebSocketMessageType.Binary, EndOfMessage: false),
            new([], WebSocketMessageType.Close, EndOfMessage: true)
        ]);

        var published = new List<byte[]>();
        var closedWithError = await RemoteSessionsController.RelayWebSocketToNatsAsync(
            socket,
            maxPayloadBytes: 1024,
            (frame, _) => { published.Add(frame); return ValueTask.CompletedTask; },
            CancellationToken.None);

        Assert.That(closedWithError, Is.False);
        Assert.That(published, Is.Empty);
    }

    // ── Helpers & Fakes ──

    private static DefaultHttpContext CreateTunnelHttpContext(string path, string queryString, ITunnelTicketService ticketSvc)
    {
        var sp = new ServiceCollection()
            .AddSingleton(ticketSvc)
            .BuildServiceProvider();

        var ctx = new DefaultHttpContext
        {
            RequestServices = sp
        };
        ctx.Request.Path = path;
        ctx.Request.QueryString = new QueryString(queryString);
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static RemoteSessionsController BuildTunnelController(
        RemoteSession session,
        IScopeContext scopeContext,
        ITunnelTicketService ticketSvc,
        Guid userId)
    {
        var sessionManager = F02NullProxy<IRemoteSessionManager>.Create((m, args) =>
            m.Name == nameof(IRemoteSessionManager.GetRawSessionAsync)
                ? Task.FromResult<RemoteSession?>(args?[0] is Guid id && id == session.Id ? session : null)
                : null);

        var http = new DefaultHttpContext();
        http.Items["UserId"] = userId;

        return new RemoteSessionsController(
            F02NullProxy<IMediator>.Create(),
            F02NullProxy<ITerminalRecordingService>.Create(),
            scopeContext,
            sessionManager,
            Options.Create(new RemoteAccessOptions { Enabled = true }),
            null!,
            NullLogger<RemoteSessionsController>.Instance,
            ticketSvc)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static StartRemoteSessionCommandHandler CreateStartHandler(
        Agent agent,
        Site site,
        RecordingCommandDispatcher dispatcher,
        IOptions<RemoteAccessOptions> options,
        RemoteConsentMode consentMode,
        string? resolvedProxyPolicyJson = null)
    {
        var agentRepo = new F02AgentRepositoryStub(agent);
        var siteRepo = new F02SiteRepositoryStub(site);

        var sessionManager = F02NullProxy<IRemoteSessionManager>.Create((m, args) =>
        {
            if (m.Name == nameof(IRemoteSessionManager.CreateSessionAsync))
            {
                var created = new RemoteSession
                {
                    Id = (Guid)(args![14] ?? Guid.NewGuid()),
                    AgentId = (Guid)args[0]!,
                    UserId = (Guid)args[1]!,
                    TenantId = (Guid)args[2]!,
                    SiteId = (Guid)args[3]!,
                    Kind = (RemoteSessionKind)args[4]!,
                    Transport = (RemoteTransport)args[5]!,
                    QualityProfile = (QualityProfile)args[6]!,
                    Codec = (RemoteCodec)args[7]!,
                    NatsSubject = (string)args[8]!,
                    Status = (bool)args[11]! ? RemoteSessionStatus.PendingConsent : RemoteSessionStatus.Active,
                    ConsentRequired = (bool)args[11]!,
                    StartedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(30)
                };
                return Task.FromResult(created);
            }
            return null;
        });

        var configService = F02NullProxy<IConfigurationService>.Create((m, _) =>
            m.Name == nameof(IConfigurationService.GetServerConfigAsync)
                ? Task.FromResult(new ServerConfiguration())
                : null);

        var credentialsService = F02NullProxy<INatsCredentialsService>.Create((m, _) =>
            m.Name == nameof(INatsCredentialsService.IssueSessionCredentialsAsync)
                ? Task.FromResult(("jwt-viewer", "SU-SEED", DateTime.UtcNow.AddMinutes(40)))
                : null);

        var consentResolver = F02NullProxy<IRemoteConsentPolicyResolver>.Create((m, _) =>
            m.Name == nameof(IRemoteConsentPolicyResolver.ResolveAsync)
                ? Task.FromResult(new RemoteConsentPolicy
                {
                    Mode = consentMode,
                    TimeoutSeconds = 60,
                    TimeoutAction = RemoteConsentTimeoutAction.Deny
                })
                : null);

        // GetEffectiveValueAsync<T> é genérico: o valor devolvido tem de ser um
        // Task<T> do tipo pedido, senão o DispatchProxy lança no cast.
        //
        // O gate de RemoteAccessEnabled pede bool (true = habilitado). A
        // resolução da allowlist de proxy pede string, e devolver null nesse
        // caso faz o handler cair no fallback do appsettings
        // (RemoteAccessOptions) — que é exatamente o que estes testes verificam,
        // preservando o comportamento anterior à configuração herdável.
        var configResolver = F02NullProxy<IConfigurationResolver>.Create((m, _) =>
        {
            if (m.Name != nameof(IConfigurationResolver.GetEffectiveValueAsync))
                return null;

            var requested = m.GetGenericArguments();
            if (requested.Length != 1)
                return null;

            var inner = requested[0];
            // Espelha a PRODUÇÃO: a coluna do servidor é NOT NULL DEFAULT '{}',
            // então a resolução de string nunca devolve null — devolve "{}"
            // quando nenhum escopo definiu política. Um stub que devolvesse null
            // aqui daria falsa confiança sobre o caminho de fallback.
            object? value = inner == typeof(bool)
                ? true
                : inner == typeof(string)
                    ? resolvedProxyPolicyJson ?? "{}"
                    : null;
            var fromResult = typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner);
            return fromResult.Invoke(null, [value]);
        });

        return new StartRemoteSessionCommandHandler(
            agentRepo,
            siteRepo,
            sessionManager,
            dispatcher,
            new SpecialCommandPayloadValidator(),
            configService,
            credentialsService,
            consentResolver,
            F02NullProxy<IUserRepository>.Create(),
            F02NullProxy<IClientRepository>.Create(),
            options,
            F02ScopeContextStub.Global(),
            configResolver,
            NullLogger<StartRemoteSessionCommandHandler>.Instance);
    }

    private sealed class RecordingCommandDispatcher : IAgentCommandDispatcher
    {
        public List<AgentCommand> Commands { get; } = [];
        public Task<AgentCommand> DispatchAsync(AgentCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return Task.FromResult(command);
        }

        public Task<AgentCommandDispatchResult> DispatchWithOutcomeAsync(AgentCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return Task.FromResult(new AgentCommandDispatchResult(command, CommandDispatchOutcome.Sent));
        }
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class InMemoryRedisStub : IRedisService
    {
        private readonly ConcurrentDictionary<string, string> _data = new(StringComparer.Ordinal);
        public bool IsConnected => true;
        public Task<string?> GetAsync(string key) => Task.FromResult(_data.TryGetValue(key, out var val) ? val : null);
        public Task<long> IncrementAsync(string key) => Task.FromResult(0L);
        public Task<long> IncrementByAsync(string key, long amount) => Task.FromResult(0L);
        public Task SetAsync(string key, string value, int expirySeconds = 3600) { _data[key] = value; return Task.CompletedTask; }
        public Task<bool> SetExpiryAsync(string key, int expirySeconds) => Task.FromResult(true);
        public Task<int> GetTtlSecondsAsync(string key) => Task.FromResult(60);
        public Task DeleteAsync(string key) { _data.TryRemove(key, out _); return Task.CompletedTask; }
        public Task DeleteByPrefixAsync(string prefix) => Task.CompletedTask;
        public Task PublishAsync(string channel, string message) => Task.CompletedTask;
        public Task SubscribeAsync(string channel, Action<string, string> handler) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetKeysByPrefixAsync(string prefix, int maxResults = 10000) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<bool> SetIfNotExistsAsync(string key, string value, int expirySeconds) => Task.FromResult(_data.TryAdd(key, value));
    }

    private sealed record ScriptedFrame(byte[] Bytes, WebSocketMessageType MessageType, bool EndOfMessage);

    private sealed class ScriptedWebSocket(IEnumerable<ScriptedFrame> frames) : WebSocket
    {
        private readonly Queue<ScriptedFrame> _frames = new(frames);
        private WebSocketState _state = WebSocketState.Open;
        private WebSocketCloseStatus? _closeStatus;
        private string? _closeDescription;

        public override WebSocketCloseStatus? CloseStatus => _closeStatus;
        public override string? CloseStatusDescription => _closeDescription;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _closeStatus = closeStatus;
            _closeDescription = statusDescription;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override void Dispose() { }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (_frames.Count == 0)
            {
                _state = WebSocketState.CloseReceived;
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, endOfMessage: true));
            }

            var next = _frames.Dequeue();
            if (next.MessageType == WebSocketMessageType.Close)
            {
                _state = WebSocketState.CloseReceived;
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, endOfMessage: true, WebSocketCloseStatus.NormalClosure, "client close"));
            }

            next.Bytes.AsSpan().CopyTo(buffer.AsSpan());
            return Task.FromResult(new WebSocketReceiveResult(next.Bytes.Length, next.MessageType, next.EndOfMessage));
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
