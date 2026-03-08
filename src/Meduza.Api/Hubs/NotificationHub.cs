using Microsoft.AspNetCore.SignalR;

namespace Meduza.Api.Hubs;

public class NotificationHub : Hub
{
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
