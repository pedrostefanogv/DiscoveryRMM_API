using Discovery.Core.Cqrs.Support.Templates;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Cqrs.Support;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Tests;

/// <summary>
/// Chave padronizada do template: formato (slug), conversão de nomes humanos e
/// unicidade por escopo — com NULLs contando como iguais (globais únicos).
/// </summary>
public class TicketTemplateKeyTests
{
    [Test]
    public void Slugify_ShouldDropAccentsAndPunctuation()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TicketTemplateKey.Slugify("Criação de Login"), Is.EqualTo("criacao_de_login"));
            Assert.That(TicketTemplateKey.Slugify("  VPN — Acesso!  "), Is.EqualTo("vpn_acesso"));
            Assert.That(TicketTemplateKey.Slugify("!!!"), Is.Empty);
        });
    }

    [Test]
    public void Resolve_ShouldAcceptSlugAndConvertHumanText()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TicketTemplateKey.Resolve("criacao_de_login", out var e1), Is.EqualTo("criacao_de_login"));
            Assert.That(e1, Is.Null);

            // compatibilidade: cliente antigo mandando texto humano
            Assert.That(TicketTemplateKey.Resolve("Criação de Login", out var e2), Is.EqualTo("criacao_de_login"));
            Assert.That(e2, Is.Null);

            Assert.That(TicketTemplateKey.Resolve("A", out var e3), Is.Empty);
            Assert.That(e3, Is.Not.Null, "chave curta demais deve ser recusada");
            Assert.That(TicketTemplateKey.Resolve("!!!", out var e4), Is.Empty);
            Assert.That(e4, Is.Not.Null);
        });
    }

    [Test]
    public async Task Create_ShouldStoreSlugKey()
    {
        await using var db = CreateDb();
        var result = await new CreateTicketTemplateCommandHandler(db)
            .Handle(Create("Criação de Login"), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Value!.Name, Is.EqualTo("criacao_de_login"));
            Assert.That(result.Value!.Title, Is.EqualTo("Título"));
        });
    }

    [Test]
    public async Task Create_ShouldRejectDuplicateKeyInSameScope()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var departmentId = Guid.NewGuid();
        var handler = new CreateTicketTemplateCommandHandler(db);

        _ = await handler.Handle(Create("criacao_de_login", clientId: clientId, departmentId: departmentId), CancellationToken.None);
        var conflict = await handler.Handle(Create("criacao_de_login", clientId: clientId, departmentId: departmentId), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(conflict.IsSuccess, Is.False);
            Assert.That(conflict.Errors[0].Code, Is.EqualTo("Conflict"));
            Assert.That(conflict.Errors[0].Message, Does.Contain("criacao_de_login"));
        });
    }

    [Test]
    public async Task Create_ShouldRejectDuplicateGlobalKey()
    {
        await using var db = CreateDb();
        var handler = new CreateTicketTemplateCommandHandler(db);

        _ = await handler.Handle(Create("suporte_padrao"), CancellationToken.None);
        var conflict = await handler.Handle(Create("Suporte Padrão"), CancellationToken.None);

        Assert.That(conflict.IsSuccess, Is.False, "globais também são únicos entre si");
    }

    [Test]
    public async Task Create_ShouldAllowSameKeyInDifferentScope()
    {
        await using var db = CreateDb();
        var handler = new CreateTicketTemplateCommandHandler(db);

        var first = await handler.Handle(Create("acesso_vpn", clientId: Guid.NewGuid(), departmentId: Guid.NewGuid()), CancellationToken.None);
        var second = await handler.Handle(Create("acesso_vpn", clientId: Guid.NewGuid(), departmentId: Guid.NewGuid()), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(first.IsSuccess, Is.True);
            Assert.That(second.IsSuccess, Is.True, "escopos diferentes podem repetir a chave");
        });
    }

    [Test]
    public async Task Create_ShouldRejectKeyHeldByDeletedTemplate()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var departmentId = Guid.NewGuid();

        db.TicketTemplates.Add(new TicketTemplate
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            DepartmentId = departmentId,
            Name = "acesso_vpn",
            Title = "Acesso VPN",
            CustomFieldDefaultsJson = "{}",
            QuestionsJson = "[]",
            IsActive = true,
            DeletedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var conflict = await new CreateTicketTemplateCommandHandler(db)
            .Handle(Create("acesso_vpn", clientId: clientId, departmentId: departmentId), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(conflict.IsSuccess, Is.False);
            Assert.That(conflict.Errors[0].Message, Does.Contain("lixeira"));
        });
    }

    [Test]
    public async Task Update_ShouldAllowKeepingOwnKey()
    {
        await using var db = CreateDb();
        var create = await new CreateTicketTemplateCommandHandler(db)
            .Handle(Create("acesso_vpn"), CancellationToken.None);
        var id = create.Value!.Id;

        var update = await new UpdateTicketTemplateCommandHandler(db).Handle(
            new UpdateTicketTemplateCommand(id, null, null, "acesso_vpn", "Acesso VPN", "d", null, null, "{}", "[]", true),
            CancellationToken.None);

        Assert.That(update.IsSuccess, Is.True, "atualizar sem trocar a chave não pode dar conflito consigo mesmo");
    }

    [Test]
    public async Task Update_ShouldRejectDeletedTemplate()
    {
        await using var db = CreateDb();
        db.TicketTemplates.Add(new TicketTemplate
        {
            Id = Guid.NewGuid(),
            Name = "acesso_vpn",
            Title = "Acesso VPN",
            CustomFieldDefaultsJson = "{}",
            QuestionsJson = "[]",
            IsActive = true,
            DeletedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var id = db.TicketTemplates.Single().Id;

        var update = await new UpdateTicketTemplateCommandHandler(db).Handle(
            new UpdateTicketTemplateCommand(id, null, null, "acesso_vpn", "Acesso VPN", "d", null, null, "{}", "[]", true),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(update.IsSuccess, Is.False);
            Assert.That(update.Errors[0].Code, Is.EqualTo("Conflict"));
            Assert.That(update.Errors[0].Message, Does.Contain("lixeira"));
        });
    }

    [Test]
    public async Task Create_ShouldRequireTitle()
    {
        await using var db = CreateDb();
        var result = await new CreateTicketTemplateCommandHandler(db)
            .Handle(Create("chave_valida", title: "  "), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
        });
    }

    private static CreateTicketTemplateCommand Create(
        string name,
        string title = "Título",
        Guid? clientId = null,
        Guid? departmentId = null)
        => new(clientId, departmentId, name, title, "d", null, null, "{}", "[]", true, "tester");

    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"template-key-tests-{Guid.NewGuid():N}")
            .Options;
        return new KeyTestDbContext(options);
    }

    private sealed class KeyTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type> { typeof(TicketTemplate) };
            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null &&
                                        type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowed.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<TicketTemplate>(entity => entity.HasKey(item => item.Id));
        }
    }
}
