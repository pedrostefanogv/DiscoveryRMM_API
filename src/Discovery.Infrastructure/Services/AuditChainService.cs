using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Hash chain à prova de edição para as tabelas de auditoria (M241).
/// Chains são POR TABELA (escopo): "configuration_audits" e "automation_script_audits".
/// entry_hash = SHA256(prev_head ?? scope || payload) em hex minúsculas.
/// A head do escopo é lida da última entrada com entry_hash não nulo.
/// </summary>
public class AuditChainService(DiscoveryDbContext db, ILogger<AuditChainService> logger) : IAuditChainService
{
    public const string ScopeConfigurationAudits = "configuration_audits";
    public const string ScopeAutomationScriptAudits = "automation_script_audits";

    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>SHA256 de um texto, hex minúsculas.</summary>
    public static string HashText(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Hash encadeado: prev_head nulo/vazio usa o nome do escopo (gênese da chain).
    /// </summary>
    public static string ComputeEntryHash(string? prevHead, string scope, string payloadHashBase)
        => HashText($"{(string.IsNullOrEmpty(prevHead) ? scope : prevHead)}|{payloadHashBase}");

    /// <summary>Hash canônico do payload de uma ConfigurationAudit. Trunca ChangedAt para microssegundos (precisão do Postgres timestamptz).</summary>
    public static string ComputeConfigurationPayloadHash(ConfigurationAudit a)
        => HashText(JsonSerializer.Serialize(new
        {
            a.EntityType,
            a.EntityId,
            a.FieldName,
            a.OldValue,
            a.NewValue,
            a.Reason,
            a.ChangedBy,
            ChangedAt = TruncateToMicroseconds(a.ChangedAt),
            a.IpAddress
        }, PayloadJsonOptions));

    /// <summary>Hash canônico do payload de uma AutomationScriptAudit.</summary>
    public static string ComputeScriptPayloadHash(AutomationScriptAudit a)
        => HashText(JsonSerializer.Serialize(new
        {
            a.ScriptId,
            a.ChangeType,
            a.Reason,
            a.OldValueJson,
            a.NewValueJson,
            a.ChangedBy,
            a.IpAddress,
            a.CorrelationId,
            ChangedAt = TruncateToMicroseconds(a.ChangedAt)
        }, PayloadJsonOptions));

    /// <summary>Postgres timestamptz tem precisão de microssegundos; o reprocesso da chain precisa bater com o hash gravado.</summary>
    public static DateTime TruncateToMicroseconds(DateTime value)
    {
        var ticks = value.Ticks;
        var truncated = ticks - (ticks % 10);
        return new DateTime(truncated, DateTimeKind.Utc);
    }

    public async Task<(string? PrevHash, string? EntryHash)> AppendAsync(string scope, string payloadHashBase, CancellationToken ct = default)
    {
        try
        {
            var prev = await GetScopeHeadAsync(scope, ct);
            var entry = ComputeEntryHash(prev, scope, payloadHashBase);
            return (prev, entry);
        }
        catch (Exception ex)
        {
            // Fail-open para disponibilidade: grava com entry_hash nulo em vez de
            // bloquear a operação de negócio por causa da chain.
            logger.LogWarning(ex, "AuditChain: falha ao calcular hash do escopo {Scope} — entrada será gravada com entry_hash nulo", scope);
            return (null, null);
        }
    }

    public async Task<string?> GetScopeHeadAsync(string scope, CancellationToken ct = default)
    {
        // Query otimizada: índice existente em changed_at (desc) por tabela.
        return scope switch
        {
            ScopeConfigurationAudits => await db.ConfigurationAudits.AsNoTracking()
                .Where(a => a.EntryHash != null)
                .OrderByDescending(a => a.ChangedAt).ThenByDescending(a => a.Id)
                .Select(a => a.EntryHash).FirstOrDefaultAsync(ct),
            ScopeAutomationScriptAudits => await db.AutomationScriptAudits.AsNoTracking()
                .Where(a => a.EntryHash != null)
                .OrderByDescending(a => a.ChangedAt).ThenByDescending(a => a.Id)
                .Select(a => a.EntryHash).FirstOrDefaultAsync(ct),
            _ => null
        };
    }
}

/// <summary>Linha materializada para verificação/exportação da chain.</summary>
public sealed record AuditChainRow(Guid Id, DateTime ChangedAt, string? PrevHash, string? EntryHash, string PayloadHash);

/// <summary>
/// Verificador puro da chain: recomputa os hashes encadeados em ordem e
/// reporta a primeira quebra. Testável sem banco.
/// </summary>
public static class AuditChainVerifier
{
    public static Core.Cqrs.ConfigurationAudit.Queries.AuditChainVerifyDto Verify(
        string scope, IReadOnlyList<AuditChainRow> rowsAscending)
    {
        string? expectedPrev = null;
        var checkedCount = 0;

        foreach (var row in rowsAscending)
        {
            // Entradas fora da chain (legado/fail-open) não fazem parte da prova.
            if (string.IsNullOrEmpty(row.EntryHash))
                continue;

            checkedCount++;
            var expected = AuditChainService.ComputeEntryHash(expectedPrev, scope, row.PayloadHash);
            var prevMatches = expectedPrev is null || string.Equals(row.PrevHash, expectedPrev, StringComparison.Ordinal);
            if (!string.Equals(row.EntryHash, expected, StringComparison.Ordinal) || !prevMatches)
            {
                return new Core.Cqrs.ConfigurationAudit.Queries.AuditChainVerifyDto(
                    Ok: false,
                    checkedCount,
                    row.Id,
                    row.ChangedAt.ToString("O"));
            }

            expectedPrev = row.EntryHash;
        }

        return new Core.Cqrs.ConfigurationAudit.Queries.AuditChainVerifyDto(
            Ok: true, checkedCount, FirstBrokenId: null, BrokenAt: null);
    }
}
