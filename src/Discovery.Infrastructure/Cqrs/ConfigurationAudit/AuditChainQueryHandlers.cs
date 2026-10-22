using System.Text;
using System.Text.Json;
using MediatR;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.ConfigurationAudit.Queries;
using Discovery.Core.Entities;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Cqrs.ConfigurationAudit;

/// <summary>
/// Verificação e exportação da hash chain de configuration_audits (M241).
/// A verificação recomputa entry_hash = SHA256(prev|payload) em ordem crescente.
/// </summary>
public sealed class VerifyAuditChainQueryHandler(DiscoveryDbContext db)
    : IRequestHandler<VerifyAuditChainQuery, Result<AuditChainVerifyDto>>
{
    public async Task<Result<AuditChainVerifyDto>> Handle(VerifyAuditChainQuery q, CancellationToken ct)
    {
        var rows = await LoadRowsAsync(db, q.From, q.To, ct);
        var result = AuditChainVerifier.Verify(AuditChainService.ScopeConfigurationAudits, rows);
        return Result<AuditChainVerifyDto>.Success(result);
    }

    internal static async Task<List<AuditChainRow>> LoadRowsAsync(DiscoveryDbContext dbContext, DateTime? from, DateTime? to, CancellationToken ct)
    {
        var query = dbContext.ConfigurationAudits.AsNoTracking()
            .OrderBy(a => a.ChangedAt).ThenBy(a => a.Id)
            .AsQueryable();

        if (from.HasValue)
            query = query.Where(a => a.ChangedAt >= from.Value);

        if (to.HasValue)
            query = query.Where(a => a.ChangedAt <= to.Value);

        var entries = await query.ToListAsync(ct);

        return entries.Select(a => new AuditChainRow(
            a.Id, a.ChangedAt, a.PrevHash, a.EntryHash,
            AuditChainService.ComputeConfigurationPayloadHash(a))).ToList();
    }
}

public sealed class ExportAuditChainQueryHandler(DiscoveryDbContext db)
    : IRequestHandler<ExportAuditChainQuery, Result<AuditChainExportDto>>
{
    public async Task<Result<AuditChainExportDto>> Handle(ExportAuditChainQuery q, CancellationToken ct)
    {
        var rows = await VerifyAuditChainQueryHandler.LoadRowsAsync(db, q.From, q.To, ct);
        var format = string.Equals(q.Format, "csv", StringComparison.OrdinalIgnoreCase) ? "csv" : "json";
        var content = format == "csv"
            ? ToCsv(rows)
            : JsonSerializer.Serialize(rows.Select(r => new
            {
                r.Id,
                r.ChangedAt,
                r.PrevHash,
                r.EntryHash,
                r.PayloadHash
            }), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });

        return Result<AuditChainExportDto>.Success(new AuditChainExportDto(format, content));
    }

    internal static string ToCsv(IReadOnlyList<AuditChainRow> rows)
    {
        var sb = new StringBuilder("id,changed_at,prev_hash,entry_hash,payload_hash");
        foreach (var r in rows)
        {
            sb.AppendLine();
            sb.Append(Csv(r.Id.ToString())).Append(',')
                .Append(Csv(r.ChangedAt.ToString("O"))).Append(',')
                .Append(Csv(r.PrevHash)).Append(',')
                .Append(Csv(r.EntryHash)).Append(',')
                .Append(Csv(r.PayloadHash));
        }

        return sb.ToString();
    }

    private static string Csv(string? value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";
}