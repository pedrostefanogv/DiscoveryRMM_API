using System.Text.RegularExpressions;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

/// <summary>
/// Catálogo de apps de terceiros (WU-21/D24). As validações ficam aqui porque
/// qualquer caminho de escrita (API hoje, importador de manifestos amanhã)
/// precisa das mesmas garantias: origem conhecida, hash SHA-256 válido e URL
/// https.
/// </summary>
public sealed class AppCatalogRepository(DiscoveryDbContext db) : IAppCatalogRepository
{
    private static readonly HashSet<string> Sources = new(StringComparer.OrdinalIgnoreCase)
    {
        "winget", "msi"
    };

    private static readonly HashSet<string> Statuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "active", "revoked"
    };

    private static readonly Regex Sha256Pattern = new("^[a-fA-F0-9]{64}$", RegexOptions.Compiled);

    public Task<AppCatalogEntry?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.AppCatalogEntries.FirstOrDefaultAsync(entry => entry.Id == id, ct);

    public async Task<AppCatalogEntry> UpsertAsync(AppCatalogEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var source = NormalizeSource(entry.Source);
        var packageId = Require(entry.PackageId, "packageId", 200);
        var version = Require(entry.Version, "version", 100);
        var name = Require(entry.Name, "name", 300);
        var url = Require(entry.InstallerUrl, "installerUrl", 2000);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl) ||
            !string.Equals(parsedUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("installerUrl precisa ser uma URL https absoluta.", nameof(entry));
        }

        var sha = Require(entry.Sha256, "sha256", 64);
        if (!Sha256Pattern.IsMatch(sha))
            throw new ArgumentException("sha256 precisa ter 64 caracteres hexadecimais.", nameof(entry));

        var status = string.IsNullOrWhiteSpace(entry.Status) ? "active" : entry.Status.Trim().ToLowerInvariant();
        if (!Statuses.Contains(status))
            throw new ArgumentException("status precisa ser active ou revoked.", nameof(entry));

        var now = DateTime.UtcNow;
        var existing = await db.AppCatalogEntries.FirstOrDefaultAsync(
            item => item.Source == source && item.PackageId == packageId && item.Version == version, ct);

        if (existing is null)
        {
            entry.Id = entry.Id == Guid.Empty ? Guid.NewGuid() : entry.Id;
            entry.Source = source;
            entry.PackageId = packageId;
            entry.Version = version;
            entry.Name = name;
            entry.InstallerUrl = url;
            entry.Sha256 = sha.ToLowerInvariant();
            entry.Status = status;
            entry.CuratedAt = entry.CuratedAt == default ? now : entry.CuratedAt;
            entry.CreatedAt = now;
            entry.UpdatedAt = now;
            db.AppCatalogEntries.Add(entry);
            await db.SaveChangesAsync(ct);
            return entry;
        }

        existing.Name = name;
        existing.Publisher = entry.Publisher ?? existing.Publisher;
        existing.InstallerUrl = url;
        existing.Sha256 = sha.ToLowerInvariant();
        existing.InstallerType = entry.InstallerType ?? existing.InstallerType;
        existing.SilentArgs = entry.SilentArgs ?? existing.SilentArgs;
        existing.ProductCode = entry.ProductCode ?? existing.ProductCode;
        existing.Status = status;
        existing.ReleaseDate = entry.ReleaseDate ?? existing.ReleaseDate;
        existing.Notes = entry.Notes ?? existing.Notes;
        existing.CuratedBy = entry.CuratedBy ?? existing.CuratedBy;
        existing.UpdatedAt = now;

        // Re-curadoria reativa a versão: limpa a revogação anterior.
        if (status == "active")
        {
            existing.RevokedAt = null;
            existing.RevokedBy = null;
        }

        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<bool> RevokeAsync(Guid id, string? revokedBy, CancellationToken ct = default)
    {
        var entry = await db.AppCatalogEntries.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entry is null)
            return false;

        entry.Status = "revoked";
        entry.RevokedBy = string.IsNullOrWhiteSpace(revokedBy) ? null : revokedBy.Trim();
        entry.RevokedAt = DateTime.UtcNow;
        entry.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<AppCatalogEntry>> ListAsync(
        string? source,
        string? status,
        string? search,
        int limit,
        CancellationToken ct = default)
    {
        var take = Math.Clamp(limit <= 0 ? 100 : limit, 1, 500);
        var query = db.AppCatalogEntries.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(source))
        {
            var normalized = source.Trim().ToLowerInvariant();
            query = query.Where(entry => entry.Source == normalized);
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalized = status.Trim().ToLowerInvariant();
            query = query.Where(entry => entry.Status == normalized);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            // ToLower/Contains: traduzível em Postgres E no provider InMemory dos
            // testes (EF.Functions.ILike não é suportado por InMemory).
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(entry =>
                entry.PackageId.ToLower().Contains(term) ||
                entry.Name.ToLower().Contains(term));
        }

        return await query
            .OrderBy(entry => entry.Name)
            .ThenBy(entry => entry.Version)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlySet<string>> GetActiveKeysAsync(CancellationToken ct = default)
    {
        var active = await db.AppCatalogEntries
            .AsNoTracking()
            .Where(entry => entry.Status == "active")
            .Select(entry => new { entry.Source, entry.PackageId })
            .ToListAsync(ct);

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in active)
            keys.Add(ActiveKey(entry.Source, entry.PackageId));
        return keys;
    }

    public async Task<IReadOnlyList<AppCatalogEntry>> GetActiveEntriesForAsync(
        string source,
        IEnumerable<string> packageIds,
        CancellationToken ct = default)
    {
        var normalizedSource = (source ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedSource.Length == 0)
            return [];

        var ids = packageIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim().ToLowerInvariant())
            .Distinct()
            .Take(200)
            .ToList();
        if (ids.Count == 0)
            return [];

        return await db.AppCatalogEntries
            .AsNoTracking()
            .Where(entry => entry.Status == "active" &&
                            entry.Source == normalizedSource &&
                            ids.Contains(entry.PackageId.ToLower()))
            .OrderByDescending(entry => entry.CuratedAt)
            .ToListAsync(ct);
    }

    /// <summary>Chave de match inventário × catálogo.</summary>
    public static string ActiveKey(string source, string packageId) =>
        (source ?? string.Empty).Trim().ToLowerInvariant() + ":" + (packageId ?? string.Empty).Trim().ToLowerInvariant();

    private static string NormalizeSource(string? source)
    {
        var normalized = (source ?? string.Empty).Trim().ToLowerInvariant();
        if (!Sources.Contains(normalized))
            throw new ArgumentException("source precisa ser winget ou msi.", nameof(source));
        return normalized;
    }

    private static string Require(string? value, string field, int maxLength)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException(field + " é obrigatório.", field);
        if (trimmed.Length > maxLength)
            throw new ArgumentException(field + " excede " + maxLength + " caracteres.", field);
        return trimmed;
    }
}
