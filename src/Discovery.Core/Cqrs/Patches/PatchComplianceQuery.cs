using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Patches;

/// <summary>
/// Conformidade de patches (patch compliance) por escopo: global, cliente ou site.
///
/// Regras:
/// • Compliant = agente com scan recente (janela configuravel, default 7 dias)
///   e zero pendencias obrigatorias; agente sem scan e fail-closed (nao-compliant,
///   LastScanUtc nulo);
/// • Score = agentes com zero patches pendentes falhos / total de agentes x 100,
///   arredondado a 1 casa;
/// • Agregacao: com SiteId uma row por agente; sem, uma row por site
///   (totals sempre sobre o escopo inteiro da consulta).
/// </summary>
public sealed record GetPatchComplianceQuery(
    Guid? ClientId,
    Guid? SiteId,
    int ScanWindowDays = 7,
    bool HasGlobalAccess = true,
    IReadOnlyList<Guid>? AllowedClientIds = null
) : IQuery<Result<PatchComplianceDto>>;

public sealed record PatchComplianceDto(
    DateTime GeneratedAtUtc,
    PatchComplianceScopeDto Scope,
    PatchComplianceTotalsDto Totals,
    IReadOnlyList<PatchComplianceRowDto> Rows);

public sealed record PatchComplianceScopeDto(Guid? ClientId, string? ClientName, Guid? SiteId);

public sealed record PatchComplianceTotalsDto(
    int Agents,
    int Compliant,
    double Score,
    int PendingPatches,
    int FailedPatches);

public sealed record PatchComplianceRowDto(
    Guid ClientId,
    string ClientName,
    Guid? SiteId,
    string? SiteName,
    int Agents,
    int Compliant,
    double Score,
    int PendingPatches,
    int FailedPatches,
    DateTime? LastScanUtc);
