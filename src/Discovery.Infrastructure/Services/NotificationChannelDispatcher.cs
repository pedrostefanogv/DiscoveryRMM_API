using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Despacha notificações para canais externos cadastrados (webhook HTTP e e-mail SMTP).
/// Best-effort: falha de um canal nunca interrompe o fluxo que publicou a notificação.
/// </summary>
public class NotificationChannelDispatcher(
    DiscoveryDbContext db,
    IHttpClientFactory httpClientFactory,
    ILogger<NotificationChannelDispatcher> logger) : INotificationChannelDispatcher
{
    public async Task DispatchAsync(NotificationPublishRequest request, CancellationToken ct = default)
    {
        List<NotificationChannel> channels;
        try
        {
            channels = await db.NotificationChannels.AsNoTracking()
                .Where(c => c.IsActive)
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao carregar canais de notificação");
            return;
        }

        foreach (var channel in channels)
        {
            if (!Matches(channel.EventsJson, request.EventType))
                continue;
            try
            {
                if (channel.Type == NotificationChannelType.Webhook)
                    await SendWebhookAsync(channel, request, ct);
                else if (channel.Type == NotificationChannelType.Email)
                    await SendEmailAsync(channel, request, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Falha ao despachar notificação pelo canal {ChannelId} ({Type})", channel.Id, channel.Type);
            }
        }
    }

    private static bool Matches(string eventsJson, string eventType)
    {
        if (string.IsNullOrWhiteSpace(eventsJson)) return false;
        try
        {
            var events = JsonSerializer.Deserialize<string[]>(eventsJson) ?? [];
            return events.Any(e => e == "*" || string.Equals(e, eventType, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private async Task SendWebhookAsync(NotificationChannel channel, NotificationPublishRequest request, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(channel.ConfigJson);
        var root = doc.RootElement;
        var url = root.TryGetProperty("url", out var u) ? u.GetString() : null;
        if (string.IsNullOrWhiteSpace(url))
        {
            logger.LogWarning("Canal webhook {ChannelId} sem url configurada", channel.Id);
            return;
        }

        var body = JsonSerializer.Serialize(new
        {
            eventType = request.EventType,
            topic = request.Topic,
            title = request.Title,
            message = request.Message,
            severity = request.Severity.ToString(),
            payload = request.Payload,
            timestampUtc = DateTime.UtcNow
        });

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var client = httpClientFactory.CreateClient();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (root.TryGetProperty("secret", out var secret) && !string.IsNullOrWhiteSpace(secret.GetString()))
            httpRequest.Headers.TryAddWithoutValidation("X-Discovery-Secret", secret.GetString());

        client.Timeout = TimeSpan.FromSeconds(15);
        using var response = await client.SendAsync(httpRequest, ct);
        if (!response.IsSuccessStatusCode)
            logger.LogWarning("Webhook {ChannelId} retornou HTTP {Status}", channel.Id, (int)response.StatusCode);
    }

    private async Task SendEmailAsync(NotificationChannel channel, NotificationPublishRequest request, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(channel.ConfigJson);
        var root = doc.RootElement;
        var host = root.TryGetProperty("host", out var h) ? h.GetString() : null;
        var from = root.TryGetProperty("from", out var f) ? f.GetString() : null;
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(from))
        {
            logger.LogWarning("Canal de e-mail {ChannelId} sem host/from configurado", channel.Id);
            return;
        }

        var port = root.TryGetProperty("port", out var p) && p.TryGetInt32(out var pv) ? pv : 587;
        var useSsl = !root.TryGetProperty("useSsl", out var ssl) || ssl.ValueKind != JsonValueKind.False;
        var username = root.TryGetProperty("username", out var un) ? un.GetString() : null;
        var password = root.TryGetProperty("password", out var pw) ? pw.GetString() : null;
        var recipients = ReadRecipients(root);
        if (recipients.Count == 0)
        {
            logger.LogWarning("Canal de e-mail {ChannelId} sem destinatários", channel.Id);
            return;
        }

#pragma warning disable SYSLIB0014 // SmtpClient é obsoleto, mas é a dependência disponível sem novo pacote.
        using var message = new MailMessage
        {
            From = new MailAddress(from),
            Subject = request.Title,
            Body = request.Message,
            IsBodyHtml = false
        };
        foreach (var recipient in recipients)
            message.To.Add(recipient);

        using var smtp = new SmtpClient(host, port)
        {
            EnableSsl = useSsl,
            Credentials = string.IsNullOrWhiteSpace(username) ? null : new NetworkCredential(username, password)
        };
        await smtp.SendMailAsync(message, ct);
#pragma warning restore SYSLIB0014
    }

    private static List<string> ReadRecipients(JsonElement root)
    {
        var result = new List<string>();
        if (!root.TryGetProperty("to", out var to))
            return result;
        if (to.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in to.EnumerateArray())
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value)) result.Add(value);
            }
        }
        else if (to.ValueKind == JsonValueKind.String)
        {
            var value = to.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                result.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        return result;
    }
}
