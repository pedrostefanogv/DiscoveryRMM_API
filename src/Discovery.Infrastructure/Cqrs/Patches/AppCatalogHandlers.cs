using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Patches;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Patches;

/// <summary>
/// Curadoria do catálogo de apps de terceiros (WU-21/D24). Toda escrita passa
/// pelo repositório, que valida origem, SHA-256 e URL https.
/// </summary>
public sealed class UpsertAppCatalogEntryCommandHandler(
    IAppCatalogRepository catalogRepo
) : IRequestHandler<UpsertAppCatalogEntryCommand, Result<AppCatalogEntryDto>>
{
    public async Task<Result<AppCatalogEntryDto>> Handle(UpsertAppCatalogEntryCommand cmd, CancellationToken ct)
    {
        try
        {
            var saved = await catalogRepo.UpsertAsync(new AppCatalogEntry
            {
                Source = cmd.Source,
                PackageId = cmd.PackageId,
                Name = cmd.Name,
                Publisher = cmd.Publisher,
                Version = cmd.Version,
                InstallerUrl = cmd.InstallerUrl,
                Sha256 = cmd.Sha256,
                InstallerType = cmd.InstallerType,
                SilentArgs = cmd.SilentArgs,
                ProductCode = cmd.ProductCode,
                ReleaseDate = cmd.ReleaseDate,
                Notes = cmd.Notes,
                CuratedBy = cmd.CuratedBy,
                Status = "active"
            }, ct);

            return Result<AppCatalogEntryDto>.Success(AppCatalogEntryDto.FromEntity(saved));
        }
        catch (ArgumentException ex)
        {
            // Validação de integridade (hash/URL/origem): erro do pedido, não 500.
            return Result<AppCatalogEntryDto>.Failure(Error.Validation("catalog", ex.Message));
        }
    }
}

/// <summary>
/// Ingestão de manifesto do winget-pkgs (WU-21/D24): parseia, valida
/// integridade e grava como <c>pending</c>. Não ativa sozinho — curadoria é
/// humana por decisão de produto (evita promover às cegas o que a comunidade
/// publicou).
/// </summary>
public sealed class IngestWingetManifestCommandHandler(
    IAppCatalogRepository catalogRepo
) : IRequestHandler<IngestWingetManifestCommand, Result<AppCatalogEntryDto>>
{
    public async Task<Result<AppCatalogEntryDto>> Handle(IngestWingetManifestCommand cmd, CancellationToken ct)
    {
        var (manifest, error) = Discovery.Core.Catalog.WingetManifestParser.Parse(cmd.ManifestYaml);
        if (manifest is null)
            return Result<AppCatalogEntryDto>.Failure(Error.Validation("manifest", error ?? "manifesto invalido"));

        try
        {
            var saved = await catalogRepo.UpsertAsync(new AppCatalogEntry
            {
                Source = "winget",
                PackageId = manifest.PackageIdentifier,
                Name = string.IsNullOrWhiteSpace(manifest.PackageName)
                    ? manifest.PackageIdentifier
                    : manifest.PackageName!,
                Publisher = manifest.Publisher,
                Version = manifest.PackageVersion,
                InstallerUrl = manifest.Installer.InstallerUrl,
                Sha256 = manifest.Installer.Sha256,
                InstallerType = manifest.Installer.InstallerType,
                SilentArgs = manifest.Installer.SilentArgs,
                ProductCode = manifest.Installer.ProductCode,
                ReleaseDate = manifest.ReleaseDate,
                CuratedBy = cmd.CuratedBy,
                Status = "pending"
            }, ct);

            return Result<AppCatalogEntryDto>.Success(AppCatalogEntryDto.FromEntity(saved));
        }
        catch (ArgumentException ex)
        {
            return Result<AppCatalogEntryDto>.Failure(Error.Validation("manifest", ex.Message));
        }
    }
}

public sealed class ActivateAppCatalogEntryCommandHandler(
    IAppCatalogRepository catalogRepo
) : IRequestHandler<ActivateAppCatalogEntryCommand, Result<AppCatalogEntryDto>>
{
    public async Task<Result<AppCatalogEntryDto>> Handle(ActivateAppCatalogEntryCommand cmd, CancellationToken ct)
    {
        var activated = await catalogRepo.ActivateAsync(cmd.Id, cmd.CuratedBy, ct);
        return activated is null
            ? Result<AppCatalogEntryDto>.Failure(Error.NotFound("Entrada de catálogo não encontrada."))
            : Result<AppCatalogEntryDto>.Success(AppCatalogEntryDto.FromEntity(activated));
    }
}

public sealed class RevokeAppCatalogEntryCommandHandler(
    IAppCatalogRepository catalogRepo
) : IRequestHandler<RevokeAppCatalogEntryCommand, Result<AppCatalogEntryDto>>
{
    public async Task<Result<AppCatalogEntryDto>> Handle(RevokeAppCatalogEntryCommand cmd, CancellationToken ct)
    {
        var revoked = await catalogRepo.RevokeAsync(cmd.Id, cmd.RevokedBy, ct);
        if (!revoked)
            return Result<AppCatalogEntryDto>.Failure(Error.NotFound("Entrada de catálogo não encontrada."));

        var entry = await catalogRepo.GetByIdAsync(cmd.Id, ct);
        return entry is null
            ? Result<AppCatalogEntryDto>.Failure(Error.NotFound("Entrada de catálogo não encontrada."))
            : Result<AppCatalogEntryDto>.Success(AppCatalogEntryDto.FromEntity(entry));
    }
}

public sealed class ListAppCatalogEntriesQueryHandler(
    IAppCatalogRepository catalogRepo
) : IRequestHandler<ListAppCatalogEntriesQuery, Result<IReadOnlyList<AppCatalogEntryDto>>>
{
    public async Task<Result<IReadOnlyList<AppCatalogEntryDto>>> Handle(
        ListAppCatalogEntriesQuery query,
        CancellationToken ct)
    {
        var entries = await catalogRepo.ListAsync(query.Source, query.Status, query.Search, query.Limit, ct);
        IReadOnlyList<AppCatalogEntryDto> dtos = entries.Select(AppCatalogEntryDto.FromEntity).ToList();
        return Result<IReadOnlyList<AppCatalogEntryDto>>.Success(dtos);
    }
}
