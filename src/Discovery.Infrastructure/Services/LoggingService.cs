using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using LogLevelEnum = Discovery.Core.Enums.LogLevel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Implementação do serviço centralizado de logging automático
/// Persiste os logs no banco de dados com fallback para ILogger
/// </summary>
public class LoggingService : ILoggingService
{
    private readonly ILogRepository _logRepository;
    private readonly ILogger<LoggingService> _logger;
    private readonly IOptionsMonitor<AutomaticLoggingOptions> _options;

    public LoggingService(
        ILogRepository logRepository,
        ILogger<LoggingService> logger,
        IOptionsMonitor<AutomaticLoggingOptions> options)
    {
        _logRepository = logRepository;
        _logger = logger;
        _options = options;
    }

    /// <summary>
    /// Registra um log com todos os parâmetros
    /// </summary>
    public async Task LogAsync(
        LogLevelEnum level,
        LogType type,
        LogSource source,
        string message,
        object? dataJson = null,
        string? agentId = null,
        string? siteId = null,
        string? clientId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var options = _options.CurrentValue;

            // Verifica se o nível está habilitado
            if (!options.IsLevelEnabled(level))
            {
                return;
            }

            // Converte string IDs para Guid (nullable)
            var agentIdGuid = Guid.TryParse(agentId, out var aId) ? aId : (Guid?)null;
            var siteIdGuid = Guid.TryParse(siteId, out var sId) ? sId : (Guid?)null;
            var clientIdGuid = Guid.TryParse(clientId, out var cId) ? cId : (Guid?)null;

            // Cria entidade de log
            var logEntry = new LogEntry
            {
                Id = Guid.NewGuid(),
                AgentId = agentIdGuid,
                SiteId = siteIdGuid,
                ClientId = clientIdGuid,
                Type = type,
                Level = level,
                Source = source,
                Message = SanitizeMessage(message),
                DataJson = SanitizeData(dataJson),
                CreatedAt = DateTime.UtcNow
            };

            // Persiste no banco de dados
            await _logRepository.CreateAsync(logEntry);

            // Log local também (ILogger do AspNetCore)
            _logger.Log(
                ConvertLogLevel(level),
                "[{Source}] [{Type}] {Message}",
                source,
                type,
                LogSanitizer.Sanitize(message));
        }
        catch (Exception ex)
        {
            // Fallback: loga apenas via ILogger se o banco falhar
            // Não lança exceção para não deixar a aplicação quebrar
            _logger.LogError(ex, "Erro ao registrar log automático: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Registra um log de nível Info
    /// </summary>
    public Task LogInfoAsync(
        LogType type,
        LogSource source,
        string message,
        object? dataJson = null,
        string? agentId = null,
        string? siteId = null,
        string? clientId = null,
        CancellationToken cancellationToken = default)
    {
        return LogAsync(LogLevelEnum.Info, type, source, message, dataJson, agentId, siteId, clientId, cancellationToken);
    }

    /// <summary>
    /// Registra um log de nível Debug
    /// </summary>
    public Task LogDebugAsync(
        LogType type,
        LogSource source,
        string message,
        object? dataJson = null,
        string? agentId = null,
        string? siteId = null,
        string? clientId = null,
        CancellationToken cancellationToken = default)
    {
        return LogAsync(LogLevelEnum.Debug, type, source, message, dataJson, agentId, siteId, clientId, cancellationToken);
    }

    /// <summary>
    /// Registra um log de nível Warning
    /// </summary>
    public Task LogWarningAsync(
        LogType type,
        LogSource source,
        string message,
        object? dataJson = null,
        string? agentId = null,
        string? siteId = null,
        string? clientId = null,
        CancellationToken cancellationToken = default)
    {
        return LogAsync(LogLevelEnum.Warn, type, source, message, dataJson, agentId, siteId, clientId, cancellationToken);
    }

    /// <summary>
    /// Registra um log de nível Error
    /// </summary>
    public Task LogErrorAsync(
        LogType type,
        LogSource source,
        string message,
        object? dataJson = null,
        string? agentId = null,
        string? siteId = null,
        string? clientId = null,
        CancellationToken cancellationToken = default)
    {
        return LogAsync(LogLevelEnum.Error, type, source, message, dataJson, agentId, siteId, clientId, cancellationToken);
    }

    /// <summary>
    /// Registra um log de nível Fatal
    /// </summary>
    public Task LogFatalAsync(
        LogType type,
        LogSource source,
        string message,
        object? dataJson = null,
        string? agentId = null,
        string? siteId = null,
        string? clientId = null,
        CancellationToken cancellationToken = default)
    {
        return LogAsync(LogLevelEnum.Fatal, type, source, message, dataJson, agentId, siteId, clientId, cancellationToken);
    }

    /// <summary>
    /// Registra uma exceção como log Error
    /// </summary>
    public async Task LogExceptionAsync(
        Exception exception,
        LogType type,
        LogSource source,
        string message,
        object? dataJson = null,
        string? agentId = null,
        string? siteId = null,
        string? clientId = null,
        CancellationToken cancellationToken = default)
    {
        var exceptionData = new
        {
            ExceptionType = exception.GetType().Name,
            ExceptionMessage = exception.Message,
            StackTrace = exception.StackTrace,
            InnerException = exception.InnerException?.Message,
            Data = dataJson
        };

        await LogAsync(
            LogLevelEnum.Error,
            type,
            source,
            message,
            exceptionData,
            agentId,
            siteId,
            clientId,
            cancellationToken);
    }

    /// <summary>
    /// Sanitiza a mensagem removendo dados sensíveis
    /// </summary>
    private static string SanitizeMessage(string message)
    {
        if (string.IsNullOrEmpty(message)) return message;

        var sanitized = message;

        // Remove padrões de senha/token
        sanitized = System.Text.RegularExpressions.Regex.Replace(
            sanitized,
            @"(password|passwd|pwd|token|auth|secret|key|api_key|apikey|authorization|bearer)[\s]*[:=][\s]*[^\s&]+",
            "$1=[REDACTED]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Remove padrões OpenAI específicos (sk-proj-, sk-)
        sanitized = System.Text.RegularExpressions.Regex.Replace(
            sanitized,
            @"sk-proj-[a-zA-Z0-9_-]+",
            "sk-proj-[REDACTED]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        sanitized = System.Text.RegularExpressions.Regex.Replace(
            sanitized,
            @"sk-[a-zA-Z0-9]{48,}",
            "sk-[REDACTED]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return sanitized;
    }

    /// <summary>
    /// Sanitiza dados estruturados removendo campos sensíveis
    /// </summary>
    private static string? SanitizeData(object? data)
    {
        if (data == null) return null;

        try
        {
            var json = JsonSerializer.Serialize(data);
            if (string.IsNullOrEmpty(json)) return null;

            // Remove valores de campos sensíveis
            var sensitiveFields = new[] 
            { 
                "password", "passwd", "pwd", "token", "auth", "secret", 
                "key", "apiKey", "api_key", "authorization", "bearer"
            };

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
           
            // Tenta fazer parse e redação simples de campos sensíveis
            using JsonDocument doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            if (root.ValueKind == JsonValueKind.Object)
            {
                return RedactSensitiveFieldsInJson(json, sensitiveFields);
            }

            return json;
        }
        catch
        {
            // Se não conseguir serializar, retorna a representação em string
            return data.ToString();
        }
    }

    /// <summary>
    /// Remove recursivamente campos sensíveis de um JSON string
    /// </summary>
    private static string RedactSensitiveFieldsInJson(string json, string[] sensitiveFields)
    {
        var result = json;
        var sensitivePattern = $@"({string.Join("|", sensitiveFields)})\s*[:\=]\s*[^,}}\]]*";
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            sensitivePattern,
            "${1}: [REDACTED]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return result;
    }

    /// <summary>
    /// Converte LogLevel (enum do Core) para Microsoft.Extensions.Logging.LogLevel
    /// </summary>
    private static Microsoft.Extensions.Logging.LogLevel ConvertLogLevel(LogLevelEnum level)
    {
        return level switch
        {
            LogLevelEnum.Trace => Microsoft.Extensions.Logging.LogLevel.Trace,
            LogLevelEnum.Debug => Microsoft.Extensions.Logging.LogLevel.Debug,
            LogLevelEnum.Info => Microsoft.Extensions.Logging.LogLevel.Information,
            LogLevelEnum.Warn => Microsoft.Extensions.Logging.LogLevel.Warning,
            LogLevelEnum.Error => Microsoft.Extensions.Logging.LogLevel.Error,
            LogLevelEnum.Fatal => Microsoft.Extensions.Logging.LogLevel.Critical,
            _ => Microsoft.Extensions.Logging.LogLevel.Information
        };
    }
}
