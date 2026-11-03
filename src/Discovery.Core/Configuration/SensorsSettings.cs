using System.Text.Json;
using System.Text.Json.Serialization;

namespace Discovery.Core.Configuration;

/// <summary>
/// Configuração global de sensores, persistida em
/// <c>ServerConfiguration.SensorsSettingsJson</c>.
///
/// Fica num blob JSON (e não em colunas próprias) para não exigir mudanças no
/// catálogo/resolvedor de configuração — mesmo padrão de RetentionSettings e
/// ReportingOptions.
/// </summary>
public class SensorsSettings
{
    /// <summary>Kill switch: quando false o sync entrega 0 sensores aos agentes.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Intervalo default (segundos) para sensores com intervalSeconds = 0.</summary>
    public int DefaultIntervalSeconds { get; set; } = 300;

    /// <summary>Dispara auto-ticket a partir dos eventos de sensor (usa as regras existentes).</summary>
    public bool AutoTicketEnabled { get; set; } = true;

    /// <summary>Despacha notificações (e-mail/webhook/in-app) para alertas de sensor.</summary>
    public bool NotifyEnabled { get; set; } = true;

    /// <summary>Intervalo efetivo, sempre dentro do range aceito pelo domínio.</summary>
    [JsonIgnore]
    public int EffectiveDefaultIntervalSeconds
        => Math.Clamp(DefaultIntervalSeconds, 30, 86400);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Validação ESTRITA usada no PATCH de configuração: um JSON inválido não
    /// pode ser salvo (o parser tolerante só existe para leitura de dados
    /// antigos/corrompidos).
    /// </summary>
    public static bool TryParseStrict(string? json, out SensorsSettings settings, out string? error)
    {
        settings = new SensorsSettings();
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        try
        {
            settings = JsonSerializer.Deserialize<SensorsSettings>(json, Options) ?? new SensorsSettings();
            return true;
        }
        catch (JsonException ex)
        {
            error = $"SensorsSettingsJson inválido: {ex.Message}";
            return false;
        }
        catch (NotSupportedException ex)
        {
            error = $"SensorsSettingsJson inválido: {ex.Message}";
            return false;
        }
    }

    /// <summary>Lê o JSON com tolerância: vazio/inválido cai nos defaults.</summary>
    public static SensorsSettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
            return new SensorsSettings();

        try
        {
            return JsonSerializer.Deserialize<SensorsSettings>(json, Options) ?? new SensorsSettings();
        }
        catch (JsonException)
        {
            return new SensorsSettings();
        }
        catch (NotSupportedException)
        {
            return new SensorsSettings();
        }
    }
}
