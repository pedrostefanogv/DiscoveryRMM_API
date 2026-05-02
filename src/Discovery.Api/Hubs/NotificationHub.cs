using Microsoft.AspNetCore.SignalR;

namespace Discovery.Api.Hubs;

public class NotificationHub : Hub
{
    private readonly ILogger<NotificationHub> _logger;

    public NotificationHub(ILogger<NotificationHub> logger)
    {
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var userId = Context.Items["UserId"] as Guid?;

        if (!userId.HasValue)
        {
            _logger.LogWarning(
                "NotificationHub: conexao anonima rejeitada. ConnectionId={ConnectionId}",
                Context.ConnectionId);
            Context.Abort();
            return;
        }

        _logger.LogInformation(
            "NotificationHub: usuario conectado. UserId={UserId}, ConnectionId={ConnectionId}",
            userId, Context.ConnectionId);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = Context.Items["UserId"] as Guid?;
        _logger.LogInformation(
            "NotificationHub: usuario desconectado. UserId={UserId}, ConnectionId={ConnectionId}",
            userId, Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public Task SubscribeAll()
        => Groups.AddToGroupAsync(Context.ConnectionId, "notifications:all");

    public Task SubscribeTopic(string topic)
        => Groups.AddToGroupAsync(Context.ConnectionId, $"notifications:topic:{topic}");

    public Task SubscribeUser(Guid userId)
        => Groups.AddToGroupAsync(Context.ConnectionId, $"notifications:user:{userId}");

    public Task SubscribeAgent(Guid agentId)
        => Groups.AddToGroupAsync(Context.ConnectionId, $"notifications:agent:{agentId}");

    public Task SubscribeKey(string recipientKey)
        => Groups.AddToGroupAsync(Context.ConnectionId, $"notifications:key:{recipientKey}");

    public Task UnsubscribeTopic(string topic)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, $"notifications:topic:{topic}");

    public Task UnsubscribeUser(Guid userId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, $"notifications:user:{userId}");

    public Task UnsubscribeAgent(Guid agentId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, $"notifications:agent:{agentId}");

    public Task UnsubscribeKey(string recipientKey)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, $"notifications:key:{recipientKey}");
}
