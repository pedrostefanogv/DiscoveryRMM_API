using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Sanitização do object storage: varre o bucket, mede o que está publicado e
/// remove os assets da LOJA que ficaram sem vínculo com nenhum pacote.
///
/// Segurança: nada fora de <see cref="CustomAssetsPrefix"/> é removido. Anexos de
/// chamados, relatórios e imagens de KB vivem em <c>clients/</c> e continuam sob a
/// responsabilidade dos seus próprios serviços — aqui eles só entram na contagem
/// do inventário.
/// </summary>
public sealed class ObjectStorageSanitizationService(
    IObjectStorageProviderFactory storageFactory,
    IAppPackageRepository packages,
    ILogger<ObjectStorageSanitizationService> logger) : IObjectStorageSanitizationService
{
    /// <summary>Teto de objetos varridos por execução (proteção de memória/tempo).</summary>
    public const int MaxScanObjects = 200_000;

    /// <summary>Idade mínima padrão (horas) para um objeto sem vínculo ser órfão.</summary>
    public const int DefaultMinAgeHours = 24;

    /// <summary>Prefixo dos assets da loja — o ÚNICO que esta limpeza pode apagar.</summary>
    public const string CustomAssetsPrefix = "app-store/custom/";

    private const int SampleLimit = 25;

    private int _running;

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public async Task<ObjectStorageSanitizationReport> RunAsync(
        bool dryRun,
        IReadOnlyList<string>? checks,
        int? minAgeHours,
        CancellationToken cancellationToken = default)
    {
        // Uma execução por vez: duas varreduras simultâneas dobrariam a carga no
        // bucket e poderiam apagar/contar o mesmo objeto duas vezes.
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new ObjectStorageSanitizationBusyException();

        try
        {
            return await RunCoreAsync(dryRun, checks, minAgeHours, cancellationToken);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task<ObjectStorageSanitizationReport> RunCoreAsync(
        bool dryRun,
        IReadOnlyList<string>? checks,
        int? minAgeHours,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        var minAge = Math.Clamp(minAgeHours ?? DefaultMinAgeHours, 0, 24 * 365);

        var storage = await storageFactory.CreateObjectStorageServiceAsync(cancellationToken);
        var listing = await storage.ListObjectsAsync(null, MaxScanObjects, cancellationToken);

        var inventory = BuildInventory(listing.Items);
        var totalBytes = listing.Items.Sum(item => item.SizeBytes);

        var wanted = checks is { Count: > 0 }
            ? new HashSet<string>(checks, StringComparer.OrdinalIgnoreCase)
            : null;

        var results = new List<ObjectStorageSanitizationCheckResult>();

        if (wanted is null || wanted.Contains(ObjectStorageSanitizationChecks.BucketInventory))
            results.Add(BuildInventoryCheck(listing, inventory));

        if (wanted is null || wanted.Contains(ObjectStorageSanitizationChecks.OrphanCustomAssets))
        {
            results.Add(await SweepOrphanCustomAssetsAsync(
                storage, listing.Items, minAge, dryRun, cancellationToken));
        }

        return new ObjectStorageSanitizationReport(
            StartedAtUtc: startedAt,
            FinishedAtUtc: DateTime.UtcNow,
            DryRun: dryRun,
            MinAgeHours: minAge,
            TotalObjects: listing.Items.Count,
            TotalBytes: totalBytes,
            Truncated: listing.Truncated,
            Inventory: inventory,
            TotalExamined: results.Sum(r => r.Examined),
            TotalFound: results.Sum(r => r.Found),
            TotalFixed: results.Sum(r => r.Fixed),
            TotalFoundBytes: results.Sum(r => r.FoundBytes),
            TotalFixedBytes: results.Sum(r => r.FixedBytes),
            Checks: results);
    }

    /// <summary>Inventário do bucket agrupado pelo prefixo raiz (app-store/, clients/…).</summary>
    private static IReadOnlyList<ObjectStorageInventoryEntry> BuildInventory(
        IReadOnlyList<StorageObjectInfo> items)
        => items
            .GroupBy(item => RootPrefixOf(item.ObjectKey), StringComparer.Ordinal)
            .Select(group => new ObjectStorageInventoryEntry(
                group.Key, group.Count(), group.Sum(item => item.SizeBytes)))
            .OrderByDescending(entry => entry.Bytes)
            .ToArray();

    private static string RootPrefixOf(string objectKey)
    {
        var slash = objectKey.IndexOf('/');
        return slash > 0 ? objectKey[..(slash + 1)] : "(raiz)";
    }

    private static ObjectStorageSanitizationCheckResult BuildInventoryCheck(
        ObjectStorageListing listing,
        IReadOnlyList<ObjectStorageInventoryEntry> inventory)
    {
        var samples = inventory
            .Take(SampleLimit)
            .Select(entry => $"{entry.Prefix} — {entry.Objects} objeto(s), {FormatBytes(entry.Bytes)}")
            .ToList();

        if (listing.Truncated)
        {
            samples.Add(
                $"Varredura truncada em {listing.Items.Count.ToString("N0")} objetos: há mais no bucket (os totais abaixo são parciais).");
        }

        return new ObjectStorageSanitizationCheckResult(
            Key: ObjectStorageSanitizationChecks.BucketInventory,
            Title: "Inventário do bucket",
            Examined: listing.Items.Count,
            Found: 0,
            Fixed: 0,
            Skipped: 0,
            HasMore: listing.Truncated,
            Error: null,
            FoundBytes: 0,
            FixedBytes: 0,
            Samples: samples);
    }

    private async Task<ObjectStorageSanitizationCheckResult> SweepOrphanCustomAssetsAsync(
        IObjectStorageService storage,
        IReadOnlyList<StorageObjectInfo> items,
        int minAgeHours,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        const string title = "Assets da loja sem vínculo";

        var candidates = items
            .Where(item => item.ObjectKey.StartsWith(CustomAssetsPrefix, StringComparison.Ordinal))
            .ToArray();

        var referenced = await packages.GetReferencedAssetKeysAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;

        var orphans = new List<StorageObjectInfo>();
        var skipped = 0;

        foreach (var item in candidates)
        {
            if (referenced.Contains(item.ObjectKey))
                continue;

            // Sem data de modificação o serviço NÃO assume que é lixo: providers
            // que não devolvem a data ficam de fora e são contados como pulados.
            if (item.LastModifiedUtc is not { } modifiedAt
                || now - modifiedAt < TimeSpan.FromHours(minAgeHours))
            {
                skipped++;
                continue;
            }

            orphans.Add(item);
        }

        var foundBytes = orphans.Sum(orphan => orphan.SizeBytes);
        var samples = orphans
            .OrderByDescending(orphan => orphan.SizeBytes)
            .Take(SampleLimit)
            .Select(orphan => $"{orphan.ObjectKey} ({FormatBytes(orphan.SizeBytes)})")
            .ToList();

        if (dryRun || orphans.Count == 0)
        {
            return new ObjectStorageSanitizationCheckResult(
                Key: ObjectStorageSanitizationChecks.OrphanCustomAssets,
                Title: title,
                Examined: candidates.Length,
                Found: orphans.Count,
                Fixed: 0,
                Skipped: skipped,
                HasMore: false,
                Error: null,
                FoundBytes: foundBytes,
                FixedBytes: 0,
                Samples: samples);
        }

        var deleted = 0;
        long deletedBytes = 0;
        var failures = new List<string>();

        foreach (var orphan in orphans)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await storage.DeleteAsync(orphan.ObjectKey, cancellationToken);
                deleted++;
                deletedBytes += orphan.SizeBytes;
            }
            catch (Exception ex)
            {
                // Best-effort: um objeto que o storage recusou remover não pode
                // abortar a limpeza dos outros (o operador vê a falha no relatório).
                logger.LogWarning(ex, "Falha ao remover asset orfao {ObjectKey} do object storage.", orphan.ObjectKey);
                failures.Add($"{orphan.ObjectKey}: {ex.Message}");
            }
        }

        return new ObjectStorageSanitizationCheckResult(
            Key: ObjectStorageSanitizationChecks.OrphanCustomAssets,
            Title: title,
            Examined: candidates.Length,
            Found: orphans.Count,
            Fixed: deleted,
            Skipped: skipped + failures.Count,
            HasMore: false,
            Error: failures.Count > 0 ? string.Join(" | ", failures.Take(5)) : null,
            FoundBytes: foundBytes,
            FixedBytes: deletedBytes,
            Samples: samples);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value.ToString("0.##")} {units[unit]}";
    }
}
