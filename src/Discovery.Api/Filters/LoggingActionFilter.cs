using Discovery.Core.Configuration;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using LogLevelEnum = Discovery.Core.Enums.LogLevel;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace Discovery.Api.Filters;

/// <summary>
/// Action Filter que registra automaticamente eventos de Controllers
/// Captura tanto requisições com sucesso quanto erros de negócio
/// </summary>
public class LoggingActionFilter : IAsyncActionFilter
{
    private readonly ILoggingService _loggingService;
    private readonly IOptionsMonitor<AutomaticLoggingOptions> _options;
    private readonly ILogger<LoggingActionFilter> _logger;

    public LoggingActionFilter(
        ILoggingService loggingService,
        IOptionsMonitor<AutomaticLoggingOptions> options,
        ILogger<LoggingActionFilter> logger)
    {
        _loggingService = loggingService;
        _options = options;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next)
    {
        var options = _options.CurrentValue;

        if (!options.Enabled || !options.LogBusinessEvents)
        {
            await next();
            return;
        }

        // Verifica se a rota deve ser excluída
        var path = context.HttpContext.Request.Path.Value ?? string.Empty;
        if (ShouldExcludeRoute(path, options))
        {
            await next();
            return;
        }

        // Extrai contexto
        ExtractContextFromRequest(context.HttpContext, out var agentId, out var siteId, out var clientId);

        // Loga antes da execução
        var startTime = DateTime.UtcNow;
        await LogRequestStart(context, agentId, siteId, clientId);

        // Executa a action
        var executedContext = await next();

        // Loga após a execução
        await LogRequestEnd(executedContext, agentId, siteId, clientId, startTime);
    }

    /// <summary>
    /// Verifica se a rota deve ser excluída do logging
    /// </summary>
    private static bool ShouldExcludeRoute(string path, AutomaticLoggingOptions options)
    {
        return options.ExcludePatterns.Any(pattern =>
            path.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Loga a solicitação inicial
    /// </summary>
    private async Task LogRequestStart(
        ActionExecutingContext context,
        string? agentId,
        string? siteId,
        string? clientId)
    {
        var method = context.HttpContext.Request.Method;
        var path = context.HttpContext.Request.Path;
        var controller = context.RouteData.Values["controller"]?.ToString() ?? "Unknown";
        var action = context.RouteData.Values["action"]?.ToString() ?? "Unknown";

        // Apenas loga POST, PUT, PATCH, DELETE como Info; GET como Debug
        if (method != "GET")
        {
            var userAgent = context.HttpContext.Request.Headers["User-Agent"].FirstOrDefault() ?? "Unknown";
            var clientIp = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
            var contentType = context.HttpContext.Request.Headers["Content-Type"].FirstOrDefault() ?? "N/A";

            var message = $"Operação iniciada: {method} {path}";
            var data = new
            {
                Controller = controller,
                Action = action,
                Method = method,
                Path = path.Value,
                QueryString = context.HttpContext.Request.QueryString.ToString(),
                ClientIp = clientIp,
                UserAgent = userAgent,
                ContentType = contentType,
                TraceId = context.HttpContext.TraceIdentifier,
                Timestamp = DateTime.UtcNow
            };

            try
            {
                await _loggingService.LogInfoAsync(
                    DetermineLogType(path.Value ?? string.Empty),
                    LogSource.Api,
                    message,
                    data,
                    agentId,
                    siteId,
                    clientId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao registrar log de início de requisição");
            }
        }
    }

    /// <summary>
    /// Loga o resultado da solicitação
    /// </summary>
    private async Task LogRequestEnd(
        ActionExecutedContext context,
        string? agentId,
        string? siteId,
        string? clientId,
        DateTime startTime)
    {
        var path = context.HttpContext.Request.Path;
        var method = context.HttpContext.Request.Method;
        var statusCode = context.HttpContext.Response.StatusCode;
        var duration = DateTime.UtcNow - startTime;

        // Se houve exceção que não foi tratada antes
        if (context.Exception != null)
        {
            var message = $"Erro em {method} {path}: {context.Exception.Message}";
            var data = new
            {
                Method = method,
                Path = path.Value,
                StatusCode = statusCode,
                DurationMs = duration.TotalMilliseconds,
                ExceptionType = context.Exception.GetType().Name,
                ExceptionMessage = context.Exception.Message
            };

            try
            {
                await _loggingService.LogErrorAsync(
                    DetermineLogType(path.Value ?? string.Empty),
                    LogSource.Api,
                    message,
                    data,
                    agentId,
                    siteId,
                    clientId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao registrar log de erro de requisição");
            }

            return;
        }

        // Log de sucesso ou erro HTTP
        if (statusCode >= 400)
        {
            var logLevel = statusCode >= 500 ? LogLevelEnum.Error : LogLevelEnum.Warn;
            var message = $"{method} {path} retornou {statusCode}";
            var userAgent = context.HttpContext.Request.Headers["User-Agent"].FirstOrDefault() ?? "Unknown";
            var clientIp = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
            
            var data = new
            {
                Method = method,
                Path = path.Value,
                StatusCode = statusCode,
                DurationMs = duration.TotalMilliseconds,
                ClientIp = clientIp,
                UserAgent = userAgent,
                TraceId = context.HttpContext.TraceIdentifier,
                Timestamp = DateTime.UtcNow
            };

            try
            {
                await _loggingService.LogAsync(
                    logLevel,
                    DetermineLogType(path.Value ?? string.Empty),
                    LogSource.Api,
                    message,
                    data,
                    agentId,
                    siteId,
                    clientId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao registrar log de resposta com erro");
            }
        }
        else if (method != "GET")
        {
            // Log de sucesso para operações que modificam dados
            var message = $"Operação concluída: {method} {path} [{statusCode}]";
            var userAgent = context.HttpContext.Request.Headers["User-Agent"].FirstOrDefault() ?? "Unknown";
            var clientIp = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
            
            var data = new
            {
                Method = method,
                Path = path.Value,
                StatusCode = statusCode,
                DurationMs = duration.TotalMilliseconds,
                ClientIp = clientIp,
                UserAgent = userAgent,
                TraceId = context.HttpContext.TraceIdentifier,
                Timestamp = DateTime.UtcNow
            };

            try
            {
                await _loggingService.LogInfoAsync(
                    DetermineLogType(path.Value ?? string.Empty),
                    LogSource.Api,
                    message,
                    data,
                    agentId,
                    siteId,
                    clientId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao registrar log de sucesso de requisição");
            }
        }
    }

    /// <summary>
    /// Determina o LogType baseado na rota
    /// </summary>
    private static LogType DetermineLogType(string path)
    {
        return path switch
        {
            var p when p.Contains("/agents", StringComparison.OrdinalIgnoreCase) => LogType.Agent,
            var p when p.Contains("/clients", StringComparison.OrdinalIgnoreCase) => LogType.Agent,
            var p when p.Contains("/sites", StringComparison.OrdinalIgnoreCase) => LogType.Agent,
            var p when p.Contains("/tickets", StringComparison.OrdinalIgnoreCase) => LogType.Ticket,
            var p when p.Contains("/workflows", StringComparison.OrdinalIgnoreCase) => LogType.Workflow,
            var p when p.Contains("/software-inventory", StringComparison.OrdinalIgnoreCase) => LogType.Inventory,
            var p when p.Contains("/logs", StringComparison.OrdinalIgnoreCase) => LogType.System,
            var p when p.Contains("/auth", StringComparison.OrdinalIgnoreCase) => LogType.Auth,
            _ => LogType.System
        };
    }

    /// <summary>
    /// Extrai AgentId, SiteId, ClientId do contexto da requisição
    /// </summary>
    private static void ExtractContextFromRequest(
        HttpContext context,
        out string? agentId,
        out string? siteId,
        out string? clientId)
    {
        agentId = null;
        siteId = null;
        clientId = null;

        // Tenta extrair do context.Items
        if (context.Items.TryGetValue("AgentId", out var agent))
        {
            agentId = agent?.ToString();
        }

        if (context.Items.TryGetValue("SiteId", out var site))
        {
            siteId = site?.ToString();
        }

        if (context.Items.TryGetValue("ClientId", out var client))
        {
            clientId = client?.ToString();
        }

        // Fallback: tenta extrair de route data ou query string
        if (string.IsNullOrEmpty(agentId))
        {
            agentId = context.Request.Query["agentId"].FirstOrDefault() ??
                      context.Request.Query["id"].FirstOrDefault();
        }
    }
}
