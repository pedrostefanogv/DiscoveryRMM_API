using Discovery.Core.Configuration;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using LogLevelEnum = Discovery.Core.Enums.LogLevel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text.Json;

namespace Discovery.Api.Middleware;

/// <summary>
/// Middleware de tratamento global de exceções
/// Registra automaticamente exceções não tratadas como logs
/// </summary>
public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        ILoggingService loggingService,
        IOptionsMonitor<AutomaticLoggingOptions> options)
    {
        try
        {
            await _next(context);

            // Também captura erros HTTP que não lançam exceção
            if (context.Response.StatusCode >= 400)
            {
                await LogHttpError(context, loggingService, options);
            }
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex, loggingService, options);
        }
    }

    /// <summary>
    /// Trata uma exceção não capturada
    /// </summary>
    private static async Task HandleExceptionAsync(
        HttpContext context,
        Exception exception,
        ILoggingService loggingService,
        IOptionsMonitor<AutomaticLoggingOptions> options)
    {
        var loggingOptions = options.CurrentValue;

        if (!loggingOptions.Enabled || !loggingOptions.LogExceptions)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new { error = "Internal server error" });
            return;
        }

        // Extrai contexto da requisição
        ExtractContextFromRequest(context, out var agentId, out var siteId, out var clientId);

        // Determina o nível de log baseado no tipo de exceção
        var logLevel = DetermineLogLevel(exception);
        var statusCode = DetermineStatusCode(exception);
        var message = $"Exceção não tratada: {exception.GetType().Name}";

        // Registra a exceção
        try
        {
            // Extrai headers e informações da requisição
            var userAgent = context.Request.Headers["User-Agent"].FirstOrDefault() ?? "Unknown";
            var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
            var contentType = context.Request.Headers["Content-Type"].FirstOrDefault() ?? "N/A";

            var exceptionData = new
            {
                ExceptionType = exception.GetType().Name,
                Message = exception.Message,
                StackTrace = exception.StackTrace?.Split(Environment.NewLine).Take(10),
                InnerException = exception.InnerException?.Message,
                RequestInfo = new
                {
                    Method = context.Request.Method,
                    Path = context.Request.Path.Value,
                    QueryString = context.Request.QueryString.ToString(),
                    ClientIp = clientIp,
                    UserAgent = userAgent,
                    ContentType = contentType,
                    TraceId = context.TraceIdentifier
                },
                ResponseInfo = new
                {
                    StatusCode = statusCode,
                    Timestamp = DateTime.UtcNow
                }
            };

            await loggingService.LogExceptionAsync(
                exception,
                LogType.System,
                LogSource.Api,
                message,
                exceptionData,
                agentId,
                siteId,
                clientId);
        }
        catch (Exception logEx)
        {
            // Se falhar o logging, apenas registra no ILogger
            Console.WriteLine($"Erro ao registrar exceção: {logEx.Message}");
        }

        // Define a resposta HTTP
        context.Response.ContentType = "application/json";
        context.Response.StatusCode = statusCode;

        var response = new
        {
            error = statusCode switch
            {
                StatusCodes.Status400BadRequest => "Requisição inválida",
                StatusCodes.Status401Unauthorized => "Não autorizado",
                StatusCodes.Status403Forbidden => "Acesso proibido",
                StatusCodes.Status404NotFound => "Recurso não encontrado",
                StatusCodes.Status409Conflict => "Conflito com recurso existente",
                StatusCodes.Status503ServiceUnavailable => "Serviço indisponível",
                _ => "Erro interno do servidor"
            },
            timestamp = DateTime.UtcNow,
            traceId = context.TraceIdentifier
        };

        await context.Response.WriteAsJsonAsync(response);
    }

    /// <summary>
    /// Registra erros HTTP que não lançam exceção (4xx, 5xx de métodos)
    /// </summary>
    private static async Task LogHttpError(
        HttpContext context,
        ILoggingService loggingService,
        IOptionsMonitor<AutomaticLoggingOptions> options)
    {
        var loggingOptions = options.CurrentValue;

        if (!loggingOptions.Enabled || !loggingOptions.LogBusinessEvents)
        {
            return;
        }

        // Extrai contexto
        ExtractContextFromRequest(context, out var agentId, out var siteId, out var clientId);

        var logLevel = context.Response.StatusCode switch
        {
            >= 400 and < 500 => LogLevelEnum.Warn,
            >= 500 => LogLevelEnum.Error,
            _ => LogLevelEnum.Info
        };

        var message = $"Erro HTTP {context.Response.StatusCode}: {context.Request.Path}";
        
        // Extrai headers importantes
        var userAgent = context.Request.Headers["User-Agent"].FirstOrDefault() ?? "Unknown";
        var contentType = context.Request.Headers["Content-Type"].FirstOrDefault() ?? "N/A";
        var accept = context.Request.Headers["Accept"].FirstOrDefault() ?? "N/A";
        
        // Obtém IP do cliente
        var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
        
        var data = new
        {
            StatusCode = context.Response.StatusCode,
            Method = context.Request.Method,
            Path = context.Request.Path.Value,
            QueryString = context.Request.QueryString.ToString(),
            ClientIp = clientIp,
            UserAgent = userAgent,
            ContentType = contentType,
            Accept = accept,
            Timestamp = DateTime.UtcNow,
            TraceId = context.TraceIdentifier,
            RequestHeaders = new
            {
                Authorization = context.Request.Headers.ContainsKey("Authorization") ? "[PRESENT]" : "N/A",
                ContentType = contentType,
                UserAgent = userAgent
            }
        };

        try
        {
            await loggingService.LogAsync(
                logLevel,
                LogType.System,
                LogSource.Api,
                message,
                data,
                agentId,
                siteId,
                clientId);
        }
        catch
        {
            // Log error silently
        }
    }

    /// <summary>
    /// Extrai AgentId, SiteId, ClientId do contexto da requisição
    /// Definidos pelo middleware de autenticação
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

        // Tenta extrair do context.Items (setado por AgentAuthMiddleware)
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

        // Fallback: tenta extrair de query string se não estiver no context
        if (string.IsNullOrEmpty(agentId))
        {
            agentId = context.Request.Query["agentId"].FirstOrDefault();
        }

        if (string.IsNullOrEmpty(siteId))
        {
            siteId = context.Request.Query["siteId"].FirstOrDefault();
        }

        if (string.IsNullOrEmpty(clientId))
        {
            clientId = context.Request.Query["clientId"].FirstOrDefault();
        }
    }

    /// <summary>
    /// Determina o nível de log baseado no tipo de exceção
    /// </summary>
    private static LogLevelEnum DetermineLogLevel(Exception exception)
    {
        return exception switch
        {
            ArgumentException or ArgumentNullException or InvalidOperationException => LogLevelEnum.Warn,
            TimeoutException or OperationCanceledException => LogLevelEnum.Warn,
            _ => LogLevelEnum.Error
        };
    }

    /// <summary>
    /// Determina o código HTTP baseado no tipo de exceção
    /// </summary>
    private static int DetermineStatusCode(Exception exception)
    {
        return exception switch
        {
            ArgumentException or ArgumentNullException or InvalidOperationException 
                => StatusCodes.Status400BadRequest,
            UnauthorizedAccessException
                => StatusCodes.Status401Unauthorized,
            TimeoutException 
                => StatusCodes.Status503ServiceUnavailable,
            OperationCanceledException 
                => StatusCodes.Status408RequestTimeout,
            _ => StatusCodes.Status500InternalServerError
        };
    }
}
