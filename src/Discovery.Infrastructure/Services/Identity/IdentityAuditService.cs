using System.Text.Json;
using Discovery.Core.Entities.Security;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces.Security;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services.Identity;

/// <summary>
/// Implementação de <see cref="IIdentityAuditService"/>.
///
/// Duas decisões deliberadas:
///
/// 1. <b>Nunca propaga exceção.</b> Derrubar uma criação de usuário porque a linha
///    de auditoria falhou trocaria um problema de observabilidade por um de
///    disponibilidade. A falha vira log de erro (não silêncio).
///
/// 2. <b>Grava em transação própria (SaveChanges imediato).</b> A mutação auditada
///    já foi persistida pelo handler; se a auditoria fosse diferida para o mesmo
///    SaveChanges e a transação revertesse, o registro sumiria junto — mas o
///    estado também, então não haveria o que auditar. O caso que importa é a
///    mutação que PERSISTE e precisa deixar rastro.
///
/// Os estados before/after são serializados em JSON compacto e truncados, para não
/// inflar a tabela nem falhar por tamanho de coluna.
/// </summary>
public sealed class IdentityAuditService(
    DiscoveryDbContext db,
    ILogger<IdentityAuditService> logger) : IIdentityAuditService
{
    /// <summary>Prefixo de todos os eventos de identidade, usado no filtro de leitura.</summary>
    public const string EventPrefix = "identity.";

    private const int MaxStateLength = 3900;
    private const int MaxDetailLength = 900;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public async Task RecordAsync(
        string eventType,
        Guid actorUserId,
        string? actorLogin,
        string targetType,
        Guid? targetId = null,
        Guid? targetUserId = null,
        object? before = null,
        object? after = null,
        bool success = true,
        string? failureReason = null)
    {
        try
        {
            var log = new AuthAuditLog
            {
                Id = IdGenerator.NewId(),
                // UserId é o SUJEITO: em mutação de identidade, quem foi afetado.
                // Mantém GetByUserAsync existente funcionando para o histórico da pessoa.
                UserId = targetUserId ?? (targetType == "User" ? targetId : null),
                EventType = eventType,
                Success = success,
                FailureReason = Truncate(failureReason, MaxDetailLength),
                ActorUserId = actorUserId == Guid.Empty ? null : actorUserId,
                ActorLogin = Truncate(actorLogin, 200),
                TargetType = Truncate(targetType, 50),
                TargetId = targetId,
                BeforeState = Serialize(before),
                AfterState = Serialize(after),
                OccurredAt = DateTime.UtcNow
            };

            db.AuthAuditLogs.Add(log);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Auditoria não pode derrubar a operação, mas também não pode ser silenciosa.
            logger.LogError(ex,
                "Falha ao registrar auditoria de identidade: Event={EventType}, Actor={Actor}, Target={TargetType}/{TargetId}",
                eventType, actorUserId, targetType, targetId);
        }
    }

    public async Task<IEnumerable<AuthAuditLog>> GetByActorAsync(Guid actorUserId, int limit = 100)
        => await db.AuthAuditLogs
            .AsNoTracking()
            .Where(l => l.ActorUserId == actorUserId)
            .OrderByDescending(l => l.OccurredAt)
            .Take(Clamp(limit))
            .ToListAsync();

    public async Task<IEnumerable<AuthAuditLog>> GetByTargetAsync(string targetType, Guid targetId, int limit = 100)
        => await db.AuthAuditLogs
            .AsNoTracking()
            .Where(l => l.TargetType == targetType && l.TargetId == targetId)
            .OrderByDescending(l => l.OccurredAt)
            .Take(Clamp(limit))
            .ToListAsync();

    public async Task<IEnumerable<AuthAuditLog>> GetRecentIdentityAsync(string? eventTypePrefix = null, int limit = 200)
    {
        var prefix = eventTypePrefix ?? EventPrefix;
        return await db.AuthAuditLogs
            .AsNoTracking()
            .Where(l => l.EventType.StartsWith(prefix))
            .OrderByDescending(l => l.OccurredAt)
            .Take(Clamp(limit))
            .ToListAsync();
    }

    public async Task<IEnumerable<AuthAuditLog>> GetByUserAsyncForAuditView(Guid userId, int limit = 100)
        => await db.AuthAuditLogs
            .AsNoTracking()
            .Where(l => l.UserId == userId)
            .OrderByDescending(l => l.OccurredAt)
            .Take(Clamp(limit))
            .ToListAsync();

    private static int Clamp(int limit) => Math.Clamp(limit, 1, 500);

    private static string? Serialize(object? state)
    {
        if (state is null)
            return null;

        try
        {
            return Truncate(JsonSerializer.Serialize(state, JsonOptions), MaxStateLength);
        }
        catch
        {
            // Estado não serializável não pode impedir o registro do evento em si.
            return null;
        }
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return value.Length <= max ? value : value[..max];
    }
}
