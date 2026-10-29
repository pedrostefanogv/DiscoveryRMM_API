using Discovery.Core.Entities;

namespace Discovery.Core.Cqrs.Patches;

/// <summary>Entrada do catálogo curado de apps de terceiros (WU-21/D24).</summary>
public sealed record AppCatalogEntryDto(
    Guid Id,
    string Source,
    string PackageId,
    string Name,
    string? Publisher,
    string Version,
    string InstallerUrl,
    string Sha256,
    string? InstallerType,
    string? SilentArgs,
    string? ProductCode,
    string Status,
    DateTime? ReleaseDate,
    string? Notes,
    string? CuratedBy,
    DateTime CuratedAt,
    string? RevokedBy,
    DateTime? RevokedAt,
    DateTime UpdatedAt)
{
    public static AppCatalogEntryDto FromEntity(AppCatalogEntry entry) => new(
        entry.Id,
        entry.Source,
        entry.PackageId,
        entry.Name,
        entry.Publisher,
        entry.Version,
        entry.InstallerUrl,
        entry.Sha256,
        entry.InstallerType,
        entry.SilentArgs,
        entry.ProductCode,
        entry.Status,
        entry.ReleaseDate,
        entry.Notes,
        entry.CuratedBy,
        entry.CuratedAt,
        entry.RevokedBy,
        entry.RevokedAt,
        entry.UpdatedAt);
}

public sealed record UpsertAppCatalogEntryCommand(
    string Source,
    string PackageId,
    string Name,
    string Version,
    string InstallerUrl,
    string Sha256,
    string? Publisher = null,
    string? InstallerType = null,
    string? SilentArgs = null,
    string? ProductCode = null,
    DateTime? ReleaseDate = null,
    string? Notes = null,
    string? CuratedBy = null) : ICommand<Result<AppCatalogEntryDto>>;

public sealed record RevokeAppCatalogEntryCommand(
    Guid Id,
    string? RevokedBy = null,
    string? Reason = null) : ICommand<Result<AppCatalogEntryDto>>;

public sealed record ListAppCatalogEntriesQuery(
    string? Source = null,
    string? Status = null,
    string? Search = null,
    int Limit = 100) : IQuery<Result<IReadOnlyList<AppCatalogEntryDto>>>;
