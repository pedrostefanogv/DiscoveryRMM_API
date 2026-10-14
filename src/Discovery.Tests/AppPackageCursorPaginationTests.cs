using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace Discovery.Tests;

/// <summary>
/// Regressão da página 2+ do catálogo (Winget/Chocolatey/Custom).
///
/// O cursor Type C é um keyset (Name, Id) e o predicado era montado por
/// <see cref="CursorPaginationHelper.ApplyNameCursor"/> com dois defeitos somados:
///  1) O parâmetro da lambda de <c>Id</c> nunca era religado ao da lambda de <c>Name</c>,
///     deixando um parâmetro não vinculado no predicado. O EF abortava em VisitParameter
///     com "The LINQ expression 'x' could not be translated" (HTTP 400). A 1ª página
///     funcionava porque não há cursor — o que mascarava o bug.
///  2) As sobrecargas string.Compare/Equals + StringComparison também não são traduzidas;
///     o helper agora usa operadores relacionais (a.name &gt; @name / a.id &gt; @id).
///
/// O teste de tradução usa o provider Npgsql e chama ToQueryString: ele não abre conexão,
/// apenas compila a expressão — então quebra no CI em vez de quebrar só em produção.
/// </summary>
public class AppPackageCursorPaginationTests
{
    // ── Tradução SQL (o defeito que derrubava a página 2) ─────────────────

    [Test]
    public void Cursor_predicate_translates_to_postgres_sql()
    {
        using var db = CreateNpgsqlContext();

        var query = CursorPaginationHelper.ApplyNameCursor(
            db.AppPackages.AsNoTracking().Where(x => x.InstallationType == AppInstallationType.Winget),
            "1Password CLI",
            Guid.Parse("01a13c648ae076ffac295d0f3b0c08d3"),
            x => x.Name,
            x => x.Id);

        var sql = query
            .OrderBy(x => x.Name)
            .ThenBy(x => x.Id)
            .Take(21)
            .ToQueryString();

        Assert.That(sql, Does.Contain("app_packages"));
        // Keyset presente no WHERE (não degradado para avaliação em memória).
        // As constantes do cursor são embutidas na tradução, então aparecem como literais.
        Assert.That(sql, Does.Contain("> '1Password CLI'"), sql);
        Assert.That(sql, Does.Contain("= '1Password CLI'"), sql);
        Assert.That(sql, Does.Contain("> '01a13c64-8ae0-76ff-ac29-5d0f3b0c08d3'"), sql);
        Assert.That(sql, Does.Contain("ORDER BY"), sql);
    }

    [Test]
    public void First_page_without_cursor_still_translates()
    {
        using var db = CreateNpgsqlContext();

        var sql = db.AppPackages.AsNoTracking()
            .Where(x => x.InstallationType == AppInstallationType.Winget)
            .OrderBy(x => x.Name)
            .ThenBy(x => x.Id)
            .Take(21)
            .ToQueryString();

        Assert.That(sql, Does.Contain("app_packages"));
    }

    // ── Comportamento do keyset (sem duplicar/omitir) ─────────────────────

    /// <summary>
    /// Avalia o MESMO predicado/ordenação em memória (LINQ-to-Objects), sem banco:
    /// percorre as páginas com o cursor real e exige cobertura exata do catálogo.
    /// Ids propositalmente em ordem inversa ao PackageId — se o ORDER BY desempatar
    /// por outra coluna, o keyset repete/perde itens.
    /// </summary>
    [Test]
    public void Paging_with_cursor_covers_catalog_once_even_with_duplicate_names()
    {
        var all = new[]
        {
            NewPackage("Foo", "foo-a", "00000000-0000-0000-0000-000000000002"),
            NewPackage("Foo", "foo-b", "00000000-0000-0000-0000-000000000001"),
            NewPackage("Bar", "bar-a", "00000000-0000-0000-0000-000000000003"),
            NewPackage("Zed", "zed-a", "00000000-0000-0000-0000-000000000004"),
            NewPackage("Alpha", "alpha-a", "00000000-0000-0000-0000-000000000005"),
        };

        const int pageSize = 2;
        var seen = new List<Guid>();
        string? cursor = null;

        for (var pageNo = 0; pageNo < 20; pageNo++)
        {
            var query = all.AsQueryable();
            if (CursorPaginationHelper.TryDecodeNameCursor(cursor, out var cursorName, out var cursorId))
            {
                query = CursorPaginationHelper.ApplyNameCursor(query, cursorName, cursorId, x => x.Name, x => x.Id);
            }

            var items = query
                .OrderBy(x => x.Name)
                .ThenBy(x => x.Id)
                .Take(pageSize + 1)
                .ToList();

            var page = items.Take(pageSize).ToList();
            if (page.Count == 0)
                break;

            seen.AddRange(page.Select(p => p.Id));

            if (items.Count <= pageSize)
                break;

            var last = page[^1];
            var next = CursorPaginationHelper.EncodeNameCursor(last.Name, last.Id);
            Assert.That(next, Is.Not.EqualTo(cursor), "o cursor precisa avançar a cada página");
            cursor = next;
        }

        Assert.That(seen, Is.Unique, "nenhum item pode aparecer em duas páginas");
        Assert.That(seen, Is.EquivalentTo(all.Select(p => p.Id)), "todas as páginas somadas devem cobrir o catálogo");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static AppPackage NewPackage(string name, string packageId, string id) => new()
    {
        Id = Guid.Parse(id),
        InstallationType = AppInstallationType.Winget,
        PackageId = packageId,
        Name = name,
        SyncedAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static DiscoveryDbContext CreateNpgsqlContext()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=discovery_translation_only;Username=none;Password=none",
                npgsql => npgsql.UseVector())
            .Options;

        return new DiscoveryDbContext(options);
    }
}
