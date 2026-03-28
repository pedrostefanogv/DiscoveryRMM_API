using System.Security.Claims;
using Meduza.Api.Hubs;
using Meduza.Api.Services;
using Meduza.Core.Enums.Identity;
using Meduza.Core.Interfaces.Auth;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;

namespace Meduza.Tests;

public class RemoteDebugHubAuthorizationTests
{
    [Test]
    public async Task JoinSession_ShouldAllowOwnerWithoutPermission()
    {
        var manager = new RemoteDebugSessionManager();
        var session = manager.StartSession(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "info", "signalr", 10);
        var permissionService = new FakePermissionService(viewAllowed: false, executeAllowed: false);
        var hub = CreateHub(manager, permissionService, session.OwnerUserId);

        Assert.DoesNotThrowAsync(() => hub.JoinSession(session.SessionId));
        await Task.Delay(1);
        Assert.That(hub.Groups, Is.Not.Null);
    }

    [Test]
    public async Task JoinSession_ShouldAllowNonOwnerWithViewPermission()
    {
        var manager = new RemoteDebugSessionManager();
        var ownerId = Guid.NewGuid();
        var session = manager.StartSession(Guid.NewGuid(), ownerId, Guid.NewGuid(), Guid.NewGuid(), "info", "signalr", 10);
        var viewerId = Guid.NewGuid();
        var permissionService = new FakePermissionService(viewAllowed: true, executeAllowed: false);
        var hub = CreateHub(manager, permissionService, viewerId);

        Assert.DoesNotThrowAsync(() => hub.JoinSession(session.SessionId));
        await Task.Delay(1);
    }

    [Test]
    public void JoinSession_ShouldRejectNonOwnerWithoutViewPermission()
    {
        var manager = new RemoteDebugSessionManager();
        var ownerId = Guid.NewGuid();
        var session = manager.StartSession(Guid.NewGuid(), ownerId, Guid.NewGuid(), Guid.NewGuid(), "info", "signalr", 10);
        var viewerId = Guid.NewGuid();
        var permissionService = new FakePermissionService(viewAllowed: false, executeAllowed: false);
        var hub = CreateHub(manager, permissionService, viewerId);

        Assert.ThrowsAsync<HubException>(() => hub.JoinSession(session.SessionId));
    }

    [Test]
    public void CloseSession_ShouldRequireExecutePermissionForNonOwner()
    {
        var manager = new RemoteDebugSessionManager();
        var ownerId = Guid.NewGuid();
        var session = manager.StartSession(Guid.NewGuid(), ownerId, Guid.NewGuid(), Guid.NewGuid(), "info", "signalr", 10);
        var viewerId = Guid.NewGuid();
        var permissionService = new FakePermissionService(viewAllowed: true, executeAllowed: false);
        var hub = CreateHub(manager, permissionService, viewerId);

        Assert.ThrowsAsync<HubException>(() => hub.CloseSession(session.SessionId, "stop"));
    }

    [Test]
    public async Task CloseSession_ShouldAllowExecutePermission()
    {
        var manager = new RemoteDebugSessionManager();
        var ownerId = Guid.NewGuid();
        var session = manager.StartSession(Guid.NewGuid(), ownerId, Guid.NewGuid(), Guid.NewGuid(), "info", "signalr", 10);
        var viewerId = Guid.NewGuid();
        var permissionService = new FakePermissionService(viewAllowed: true, executeAllowed: true);
        var hub = CreateHub(manager, permissionService, viewerId);

        Assert.DoesNotThrowAsync(() => hub.CloseSession(session.SessionId, "stop"));
        await Task.Delay(1);
        Assert.That(manager.TryGetSession(session.SessionId, out _), Is.False);
    }

    private static RemoteDebugHub CreateHub(
        RemoteDebugSessionManager manager,
        IPermissionService permissionService,
        Guid userId)
    {
        var hub = new RemoteDebugHub(manager, permissionService)
        {
            Context = new TestHubCallerContext(userId),
            Clients = new TestHubCallerClients(),
            Groups = new TestGroupManager()
        };

        return hub;
    }

    private sealed class FakePermissionService : IPermissionService
    {
        private readonly bool _viewAllowed;
        private readonly bool _executeAllowed;

        public FakePermissionService(bool viewAllowed, bool executeAllowed)
        {
            _viewAllowed = viewAllowed;
            _executeAllowed = executeAllowed;
        }

        public Task<bool> HasPermissionAsync(
            Guid userId,
            ResourceType resource,
            ActionType action,
            ScopeLevel scopeLevel = ScopeLevel.Global,
            Guid? scopeId = null,
            Guid? parentScopeId = null)
        {
            return Task.FromResult(action switch
            {
                ActionType.View => _viewAllowed,
                ActionType.Execute => _executeAllowed,
                _ => false
            });
        }

        public Task<UserScopeAccess> GetScopeAccessAsync(Guid userId, ResourceType resource, ActionType action)
            => Task.FromResult(new UserScopeAccess());
    }

    private sealed class TestHubCallerContext : HubCallerContext
    {
        private readonly IDictionary<object, object?> _items = new Dictionary<object, object?>();

        public TestHubCallerContext(Guid userId)
        {
            _items["UserId"] = userId;
        }

        public override string ConnectionId { get; } = Guid.NewGuid().ToString("N");
        public override string? UserIdentifier { get; } = null;
        public override ClaimsPrincipal? User { get; } = null;
        public override IDictionary<object, object?> Items => _items;
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted { get; } = CancellationToken.None;
        public override void Abort()
        {
        }
    }

    private sealed class TestGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class TestHubCallerClients : IHubCallerClients
    {
        private readonly TestClientProxy _proxy = new();

        public IClientProxy All => _proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => _proxy;
        public IClientProxy Caller => _proxy;
        public IClientProxy Client(string connectionId) => _proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => _proxy;
        public IClientProxy Group(string groupName) => _proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => _proxy;
        public IClientProxy Others => _proxy;
        public IClientProxy OthersInGroup(string groupName) => _proxy;
        public IClientProxy User(string userId) => _proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => _proxy;
    }

    private sealed class TestClientProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
