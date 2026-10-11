using System.Text.Json;
using System.Text.Json.Serialization;

namespace Discovery.Core.Helpers;

/// <summary>
/// Monta o payload JSON do comando <see cref="Enums.CommandType.TicketChatInvite"/>.
///
/// O payload carrega APENAS metadados de exibição e correlação (sessão, chamado,
/// título, nome do atendente e prazo). O conteúdo das mensagens trafega pelo
/// stream JetStream do chat de suporte, nunca pelo canal de comando.
/// </summary>
public static class TicketChatInvitePayloadFactory
{
    public const int MaxTicketTitleLength = 200;
    public const int MaxSupportUserNameLength = 120;

    // Omite o prazo quando ausente (o agent aplica o timeout padrão de UI).
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Build(
        Guid sessionId,
        Guid ticketId,
        string ticketTitle,
        string supportUserName,
        string? expiresAtUtc)
    {
        return JsonSerializer.Serialize(new
        {
            sessionId = sessionId.ToString(),
            ticketId = ticketId.ToString(),
            ticketTitle = Clamp(ticketTitle, MaxTicketTitleLength),
            supportUserName = Clamp(supportUserName, MaxSupportUserNameLength),
            expiresAtUtc = string.IsNullOrWhiteSpace(expiresAtUtc) ? null : expiresAtUtc.Trim()
        }, PayloadJsonOptions);
    }

    private static string Clamp(string? value, int maxLength)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
