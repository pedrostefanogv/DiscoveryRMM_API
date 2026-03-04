namespace Meduza.Core.Interfaces;

using Meduza.Core.Enums;
using LogLevelEnum = Meduza.Core.Enums.LogLevel;

/// <summary>
/// Serviço centralizado de logging automático
/// Abstrai a persistência em banco de dados com fallback para ILogger
/// </summary>
public interface ILoggingService
{
    /// <summary>
    /// Registra um log com todos os parâmetros
    /// </summary>
    Task LogAsync(
        LogLevelEnum level,
        LogType type,
        LogSource source,
        string message,
        object? dataJson = null,
        string? agentId = null,
        string? siteId = null,
        string? clientId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Registra um log de nível Info
    /// </summary>
    Task LogInfoAsync(
        LogType type,
        LogSource source,
        string message,
        object? dataJson = null,
        string? agentId = null,
        string? siteId = null,
        string? clientId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Registra um log de nível Debug
    /// </summary>
    Task LogDebugAsync(
        LogType type,
        LogSource source,
        string message,
        object? dataJson = null,
        string? agentId = null,
        string? siteId = null,
        string? clientId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Registra um log de nível Warning
    /// </summary>
    Task LogWarningAsync(
        LogType type,
        LogSource source,
        string message,
        object? dataJson = null,
        string? agentId = null,
        string? siteId = null,
        string? clientId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Registra um log de nível Error
    /// </summary>
    Task LogErrorAsync(
        LogType type,
        LogSource source,
        string message,
        object? dataJson = null,
        string? agentId = null,
        string? siteId = null,
        string? clientId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Registra um log de nível Fatal
    /// </summary>
    Task LogFatalAsync(
        LogType type,
        LogSource source,
        string message,
        object? dataJson = null,
        string? agentId = null,
        string? siteId = null,
        string? clientId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Registra uma exceção como log Error
    /// </summary>
    Task LogExceptionAsync(
        Exception exception,
        LogType type,
        LogSource source,
        string message,
        object? dataJson = null,
        string? agentId = null,
        string? siteId = null,
        string? clientId = null,
        CancellationToken cancellationToken = default);
}
