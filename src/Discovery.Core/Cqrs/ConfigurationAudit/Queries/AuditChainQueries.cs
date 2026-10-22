using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.ConfigurationAudit.Queries;

/// <summary>Recomputa a hash chain das entradas no período (escopo: configuration_audits).</summary>
public sealed record VerifyAuditChainQuery(DateTime? From = null, DateTime? To = null)
    : IQuery<Result<AuditChainVerifyDto>>;

/// <summary>Exporta as entradas encadeadas no período (JSON default; format=csv).</summary>
public sealed record ExportAuditChainQuery(DateTime? From = null, DateTime? To = null, string Format = "json")
    : IQuery<Result<AuditChainExportDto>>;

public sealed record AuditChainVerifyDto(
    bool Ok,
    int EntriesChecked,
    Guid? FirstBrokenId,
    string? BrokenAt);

/// <summary>Formato "json" ou "csv"; Content é o documento completo pronto para resposta.</summary>
public sealed record AuditChainExportDto(
    string Format,
    string Content);
