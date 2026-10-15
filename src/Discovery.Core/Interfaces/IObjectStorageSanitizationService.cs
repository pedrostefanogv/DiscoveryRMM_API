namespace Discovery.Core.Interfaces;

/// <summary>
/// Chaves e descritores das verificações de sanitização do object storage. As
/// chaves são o contrato estável entre serviço, endpoint e tela de manutenção.
/// </summary>
public static class ObjectStorageSanitizationChecks
{
    /// <summary>
    /// Assets da loja (<c>app-store/custom/**</c>: instaladores e ícones) que não
    /// estão vinculados a nenhum pacote. Objetos recentes são preservados.
    /// </summary>
    public const string OrphanCustomAssets = "object-storage-orphan-custom-assets";

    /// <summary>Somente relatório: inventário do bucket (objetos e bytes por prefixo).</summary>
    public const string BucketInventory = "object-storage-bucket-inventory";

    public static readonly IReadOnlyList<ObjectStorageSanitizationCheckDescriptor> All =
    [
        new(OrphanCustomAssets, "Assets da loja sem vínculo",
            "Instaladores/ícones em app-store/custom/ que nenhum pacote referencia (troca de arquivo antes de salvar, aba fechada, upload abandonado). Objetos mais novos que a idade mínima são preservados: podem estar a caminho do vínculo."),
        new(BucketInventory, "Inventário do bucket",
            "Somente relatório: total de objetos e bytes no bucket, agrupados pelo prefixo raiz. Anexos de chamados, relatórios e imagens de KB vivem em clients/ e NÃO são removidos por esta limpeza.", ReportOnly: true),
    ];
}

/// <summary>Descrição de uma verificação disponível (consumida pela tela).</summary>
public sealed record ObjectStorageSanitizationCheckDescriptor(
    string Key,
    string Title,
    string Description,
    bool ReportOnly = false);

/// <summary>
/// Resultado de uma verificação. <c>Examined</c> = objetos avaliados;
/// <c>Found</c> = órfãos encontrados; <c>Fixed</c> = removidos do storage
/// (0 em dry-run); <c>FoundBytes</c>/<c>FixedBytes</c> = tamanho que eles ocupam.
/// </summary>
public sealed record ObjectStorageSanitizationCheckResult(
    string Key,
    string Title,
    int Examined,
    int Found,
    int Fixed,
    int Skipped,
    bool HasMore,
    string? Error,
    long FoundBytes,
    long FixedBytes,
    IReadOnlyList<string> Samples);

/// <summary>Uma linha do inventário: objetos e bytes por prefixo raiz.</summary>
public sealed record ObjectStorageInventoryEntry(string Prefix, int Objects, long Bytes);

/// <summary>Relatório agregado de uma execução de sanitização do object storage.</summary>
public sealed record ObjectStorageSanitizationReport(
    DateTime StartedAtUtc,
    DateTime FinishedAtUtc,
    bool DryRun,
    int MinAgeHours,
    long TotalObjects,
    long TotalBytes,
    bool Truncated,
    IReadOnlyList<ObjectStorageInventoryEntry> Inventory,
    int TotalExamined,
    int TotalFound,
    int TotalFixed,
    long TotalFoundBytes,
    long TotalFixedBytes,
    IReadOnlyList<ObjectStorageSanitizationCheckResult> Checks);

/// <summary>
/// Parâmetros de execução. <c>DryRun</c> é nullable de propósito: ausente =
/// pré-visualização, para que o endpoint nunca apague nada com corpo vazio.
/// <c>MinAgeHours</c> é o tempo mínimo desde a última modificação para um objeto
/// sem vínculo ser considerado órfão (padrão de 24 h).
/// </summary>
public sealed record ObjectStorageSanitizationRequest(
    bool? DryRun,
    IReadOnlyList<string>? Checks,
    int? MinAgeHours);

/// <summary>Lançada quando outra execução já está em andamento.</summary>
public sealed class ObjectStorageSanitizationBusyException() :
    InvalidOperationException("Já existe uma sanitização do object storage em andamento. Aguarde a conclusão antes de iniciar outra.");

/// <summary>
/// Sanitização do object storage: mede o que está publicado no bucket e remove os
/// assets da loja que ficaram sem vínculo com nenhum pacote. Idempotente.
/// </summary>
public interface IObjectStorageSanitizationService
{
    /// <summary>Indica se existe uma execução em andamento (guarda contra concorrência).</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Varre o bucket e, quando <paramref name="dryRun"/> é false, remove os órfãos
    /// elegíveis. Nada fora de <c>app-store/custom/</c> é apagado.
    /// </summary>
    Task<ObjectStorageSanitizationReport> RunAsync(
        bool dryRun,
        IReadOnlyList<string>? checks,
        int? minAgeHours,
        CancellationToken cancellationToken = default);
}
