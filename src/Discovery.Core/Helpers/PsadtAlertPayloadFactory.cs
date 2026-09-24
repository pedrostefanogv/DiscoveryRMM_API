using System.Text.Json;
using System.Text.Json.Serialization;
using Discovery.Core.Enums;

namespace Discovery.Core.Helpers;

/// <summary>
/// Monta o payload JSON do comando <see cref="CommandType.ShowPsadtAlert"/> usado
/// por notificações avulsas (agent único) e por broadcasts de escopo
/// (cliente/site/label). Centraliza o clamp de timeout aceito pelo
/// Show-ADTDialogBox e a normalização do ícone para manter os dois caminhos
/// com exatamente o mesmo contrato de wire.
/// </summary>
public static class PsadtAlertPayloadFactory
{
    public const int MaxTitleLength = 120;
    public const int MaxMessageLength = 2000;

    // Show-ADTDialogBox rejeita -Timeout maior que UI.DefaultTimeout do
    // config.psd1 do PSADT (padrão 3300s) e, nesse caso, nenhum diálogo é
    // exibido. O agent ainda faz clamp/retry, mas limitar aqui mantém o
    // contrato da API coerente com o que o endpoint realmente consegue exibir.
    public const int MaxTimeoutSeconds = 3300;

    // Omite campos nulos (timeoutSeconds quando waitForUser, defaultAction sem
    // ação) para deixar o payload enxuto — o validator também os aceita nulos.
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Serializa o payload de exibição do alerta. <paramref name="alertId"/> é
    /// compartilhado por todos os agents de um mesmo broadcast para permitir
    /// correlação e deduplicação no lado do agent.
    /// </summary>
    public static string Build(
        Guid alertId,
        string title,
        string message,
        PsadtAlertType alertType,
        int? timeoutSeconds,
        string? icon,
        string? defaultAction = null)
    {
        // UpdateProgress é reservado ao self-update; avisos avulsos são modal/toast.
        var type = alertType == PsadtAlertType.Toast ? "toast" : "modal";
        var requestedTimeout = timeoutSeconds.GetValueOrDefault();

        int? effectiveTimeout;
        bool waitForUser;

        if (type == "toast")
        {
            // Toast sempre auto-fecha; default curto quando não informado.
            effectiveTimeout = requestedTimeout > 0 ? Math.Min(requestedTimeout, MaxTimeoutSeconds) : 15;
            waitForUser = false;
        }
        else if (requestedTimeout > 0)
        {
            effectiveTimeout = Math.Min(requestedTimeout, MaxTimeoutSeconds);
            waitForUser = false;
        }
        else
        {
            // Modal sem timeout informado: permanece aberto até o usuário clicar em OK.
            effectiveTimeout = null;
            waitForUser = true;
        }

        return JsonSerializer.Serialize(new
        {
            alertId = alertId.ToString(),
            type,
            title,
            message,
            timeoutSeconds = effectiveTimeout,
            waitForUser,
            icon = NormalizeIcon(icon),
            defaultAction = string.IsNullOrWhiteSpace(defaultAction) ? null : defaultAction.Trim()
        }, PayloadJsonOptions);
    }

    public static string NormalizeIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon))
            return "info";

        return icon.Trim().ToLowerInvariant() switch
        {
            "warning" or "warn" => "warning",
            "error" => "error",
            "question" => "question",
            // "success" não está no conjunto aceito pelo
            // SpecialCommandPayloadValidator (info|warning|error|question) e
            // cairia como payload inválido no dispatch — normaliza para info.
            _ => "info"
        };
    }
}
