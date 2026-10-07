using System.Text.Json;

namespace Discovery.Core.Helpers;

/// <summary>
/// Deriva a rota do console a partir do payload da notificacao. Espelha
/// deliberadamente a logica de src/components/notifications/notificationNavigation.ts
/// para que o clique na notificacao do navegador caia na mesma tela do sino in-app.
/// </summary>
public static class NotificationDeepLink
{
    public static string? Build(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var root = document.RootElement;

            var ticketId = ReadText(root, "ticketId", "ticket_id");
            if (ticketId is not null)
                return "/tickets/" + ticketId;

            var agentId = ReadText(root, "agentId", "agent_id");
            if (agentId is not null)
                return "/agents/" + agentId;

            var clientId = ReadText(root, "clientId", "client_id");
            if (clientId is not null)
                return "/clients/" + clientId;

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadText(JsonElement root, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
                continue;

            var text = value.GetString()?.Trim();
            if (!string.IsNullOrEmpty(text))
                return text;
        }

        return null;
    }
}
