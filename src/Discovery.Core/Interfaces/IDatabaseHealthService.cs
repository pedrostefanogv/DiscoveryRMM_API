using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Discovery.Core.Interfaces;

/// <summary>Estatísticas físicas de uma tabela (base para bloat e vacuum).</summary>
public sealed record DatabaseTableStat(
    string Table,
    long TotalBytes,
    long LiveTuples,
    long DeadTuples,
    double DeadPercent,
    DateTime? LastVacuumUtc,
    DateTime? LastAutovacuumUtc,
    DateTime? LastAnalyzeUtc,
    long XidAge);

/// <summary>
/// Uma constatação do diagnóstico físico. <c>Count</c> é o número de objetos
/// atingidos e <c>Samples</c> traz amostras legíveis.
/// </summary>
public sealed record DatabaseHealthIssue(
    string Key,
    string Title,
    string Severity,
    int Count,
    IReadOnlyList<string> Samples);

/// <summary>Diagnóstico de saúde física do banco (somente leitura).</summary>
public sealed record DatabaseHealthReport(
    DateTime GeneratedAtUtc,
    long DatabaseSizeBytes,
    long TotalTableSizeBytes,
    IReadOnlyList<DatabaseTableStat> Tables,
    IReadOnlyList<DatabaseHealthIssue> Issues);

/// <summary>
/// Chaves estáveis das constatações de saúde física, usadas pela tela de
/// manutenção (mesmo racional das chaves de sanitização).
/// </summary>
public static class DatabaseHealthIssueKeys
{
    public const string InvalidIndexes = "invalid-indexes";
    public const string BloatedTables = "bloated-tables";
    public const string UnusedIndexes = "unused-indexes";
    public const string ForeignKeysWithoutIndex = "foreign-keys-without-index";
    public const string ForeignKeysWithoutLeadingIndex = "foreign-keys-without-leading-index";
    public const string StaleStatistics = "stale-statistics";
    public const string XidAge = "xid-age";
    public const string LongRunningTransactions = "long-running-transactions";
    public const string NeverVacuumed = "never-vacuumed";
}

/// <summary>
/// Diagnóstico de saúde do PostgreSQL: índices inválidos/não usados, bloat,
/// estatísticas velhas, FKs sem índice, idade de XID (wraparound) e transações
/// longas que bloqueiam o vacuum. Somente leitura — não altera nada.
/// </summary>
public interface IDatabaseHealthService
{
    Task<DatabaseHealthReport> GetReportAsync(CancellationToken ct = default);
}
