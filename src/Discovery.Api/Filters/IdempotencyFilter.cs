using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Discovery.Core.Entities;
using Discovery.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Discovery.Api.Filters;

/// <summary>
/// Deduplica operações mutáveis via header <c>Idempotency-Key</c>.
/// Persiste (scope, key) → resposta concluída; reenvios devolvem a mesma
/// resposta. Enquanto uma requisição idêntica está em processamento, responde
/// 409 para o cliente aguardar.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class IdempotencyFilterAttribute : Attribute, IFilterFactory
{
    public bool IsReusable => false;
    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) => new IdempotencyFilter();
}

public sealed class IdempotencyFilter : IAsyncActionFilter
{
    private const int Processing = -1;
    private static readonly TimeSpan CompletedTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan ProcessingTtl = TimeSpan.FromMinutes(2);
    private const int MaxKeyLength = 200;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        var key = ResolveKey(http.Request);
        var db = http.RequestServices.GetService<DiscoveryDbContext>();
        var logger = http.RequestServices.GetService<ILogger<IdempotencyFilter>>();

        if (string.IsNullOrWhiteSpace(key) || db is null)
        {
            await next();
            return;
        }

        var scope = ResolveScope(http);
        var requestHash = ComputeRequestHash(context);
        var now = DateTime.UtcNow;

        var existing = await db.IdempotencyRecords
            .FirstOrDefaultAsync(r => r.Scope == scope && r.Key == key, http.RequestAborted);

        if (existing is not null && existing.ExpiresAt > now)
        {
            // Em processamento: 409 independentemente do payload (semântica de lock).
            if (!existing.IsCompleted)
            {
                context.Result = new ObjectResult(new { error = "Requisição idêntica em processamento. Tente novamente em instantes." })
                {
                    StatusCode = StatusCodes.Status409Conflict,
                };
                return;
            }

            if (existing.RequestHash.Length > 0 && existing.RequestHash != requestHash)
            {
                context.Result = new ObjectResult(new { error = "Idempotency-Key já utilizada com um payload diferente." })
                {
                    StatusCode = StatusCodes.Status422UnprocessableEntity,
                };
                return;
            }

            context.Result = new ContentResult
            {
                StatusCode = existing.StatusCode,
                ContentType = existing.ContentType ?? "application/json; charset=utf-8",
                Content = existing.ResponseBody,
            };
            return;
        }

        // Reserva expirada (ex.: execução anterior caiu) → remove e segue.
        if (existing is not null)
        {
            db.IdempotencyRecords.Remove(existing);
            await db.SaveChangesAsync(http.RequestAborted);
        }

        var reservation = new IdempotencyRecord
        {
            Id = Guid.NewGuid(),
            Scope = scope,
            Key = key,
            Endpoint = $"{http.Request.Method} {http.Request.Path}",
            RequestHash = requestHash,
            StatusCode = Processing,
            CreatedAt = now,
            ExpiresAt = now.Add(ProcessingTtl),
        };
        db.IdempotencyRecords.Add(reservation);

        try
        {
            await db.SaveChangesAsync(http.RequestAborted);
        }
        catch (DbUpdateException)
        {
            // Corrida: outra requisição reservou a mesma chave.
            db.Entry(reservation).State = EntityState.Detached;
            context.Result = new ObjectResult(new { error = "Requisição idêntica em processamento. Tente novamente em instantes." })
            {
                StatusCode = StatusCodes.Status409Conflict,
            };
            return;
        }

        var executed = await next();

        // Falha não é idempotente: libera a chave e deixa a exceção propagar.
        // Sem isso, um 500 seria gravado como 200 "vazio" e o retry receberia sucesso falso.
        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            db.IdempotencyRecords.Remove(reservation);
            try { await db.SaveChangesAsync(http.RequestAborted); } catch (DbUpdateException) { }
            return;
        }

        var status = ResolveStatusCode(executed);

        if (status is >= 200 and < 300)
        {
            var (body, contentType) = SerializeResult(executed.Result);
            reservation.StatusCode = status;
            reservation.ResponseBody = body;
            reservation.ContentType = contentType;
            reservation.ExpiresAt = DateTime.UtcNow.Add(CompletedTtl);
        }
        else
        {
            // Erro não é idempotente: libera a chave para o cliente poder tentar de novo.
            db.IdempotencyRecords.Remove(reservation);
        }

        try
        {
            // CancellationToken.None: o resultado precisa ser persistido mesmo que
            // o cliente tenha desconectado, para deduplicar um reenvio posterior.
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateException ex)
        {
            // Sem o resultado persistido a chave expira em 2 min e um retry tardio
            // reexecuta a operação; registra para diagnóstico.
            logger?.LogWarning(ex, "Falha ao persistir o resultado de idempotência para a chave {Key}.", key);
        }
    }

    private static string? ResolveKey(HttpRequest request)
    {
        if (request.Headers.TryGetValue("Idempotency-Key", out var value) && !StringValues.IsNullOrEmpty(value))
            return Normalize(value.ToString());
        if (request.Headers.TryGetValue("X-Idempotency-Key", out var legacy) && !StringValues.IsNullOrEmpty(legacy))
            return Normalize(legacy.ToString());
        return null;
    }

    private static string Normalize(string raw)
    {
        var key = raw.Trim();
        if (key.Length <= MaxKeyLength) return key;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeRequestHash(ActionExecutingContext context)
    {
        var payload = new StringBuilder();
        payload.Append(context.HttpContext.Request.Method).Append(' ').Append(context.HttpContext.Request.Path);

        foreach (var argument in context.ActionArguments.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            payload.Append('|').Append(argument.Key).Append('=');
            try
            {
                payload.Append(JsonSerializer.Serialize(argument.Value, JsonOptions));
            }
            catch
            {
                payload.Append(argument.Value?.GetType().Name);
            }
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ResolveScope(HttpContext http)
    {
        if (http.Items["AgentId"] is Guid agentId) return $"agent:{agentId}";
        if (http.Items["UserId"] is Guid userId) return $"user:{userId}";
        return "anonymous";
    }

    private static int ResolveStatusCode(ActionExecutedContext executed) => executed.Result switch
    {
        ObjectResult objectResult => objectResult.StatusCode ?? StatusCodes.Status200OK,
        StatusCodeResult statusCodeResult => statusCodeResult.StatusCode,
        _ => StatusCodes.Status200OK,
    };

    private static (string? Body, string? ContentType) SerializeResult(IActionResult? result) => result switch
    {
        ObjectResult objectResult => (JsonSerializer.Serialize(objectResult.Value, JsonOptions), "application/json; charset=utf-8"),
        JsonResult jsonResult => (JsonSerializer.Serialize(jsonResult.Value, JsonOptions), "application/json; charset=utf-8"),
        ContentResult contentResult => (contentResult.Content, contentResult.ContentType ?? "text/plain; charset=utf-8"),
        _ => (null, null),
    };
}
