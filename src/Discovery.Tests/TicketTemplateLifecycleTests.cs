using Discovery.Core.Cqrs.Support.Templates;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Cqrs.Support;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Tests;

/// <summary>
/// Ciclo de vida do template de chamado: soft delete (lixeira), restauração e
/// purge com proteção de uso. O histórico dos chamados (tickets.template_name)
/// independe da linha do template.
/// </summary>
public class TicketTemplateLifecycleTests
{
    [Test]
    public async Task Delete_ShouldSoftDeleteAndKeepRow()
    {
        await using var db = CreateDb();
        var template = NewTemplate("Suporte padrão");
        db.TicketTemplates.Add(template);
        await db.SaveChangesAsync();

        var handler = new DeleteTicketTemplateCommandHandler(db);
        var result = await handler.Handle(new DeleteTicketTemplateCommand(template.Id, "admin"), CancellationToken.None);

        var stored = await db.TicketTemplates.AsNoTracking().SingleAsync(t => t.Id == template.Id);
        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(stored.DeletedAt, Is.Not.Null);
            Assert.That(stored.DeletedBy, Is.EqualTo("admin"));
        });
    }

    [Test]
    public async Task Delete_ShouldBeIdempotent()
    {
        await using var db = CreateDb();
        var template = NewTemplate("Suporte");
        db.TicketTemplates.Add(template);
        await db.SaveChangesAsync();

        var handler = new DeleteTicketTemplateCommandHandler(db);
        _ = await handler.Handle(new DeleteTicketTemplateCommand(template.Id), CancellationToken.None);
        var first = await db.TicketTemplates.AsNoTracking().SingleAsync(t => t.Id == template.Id);

        var result = await handler.Handle(new DeleteTicketTemplateCommand(template.Id, "outro"), CancellationToken.None);
        var second = await db.TicketTemplates.AsNoTracking().SingleAsync(t => t.Id == template.Id);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            // Segunda exclusão não sobrescreve quem excluiu primeiro.
            Assert.That(second.DeletedBy, Is.EqualTo(first.DeletedBy));
        });
    }

    [Test]
    public async Task Restore_ShouldClearDeletedAt()
    {
        await using var db = CreateDb();
        var template = NewTemplate("VPN");
        template.DeletedAt = DateTime.UtcNow;
        template.DeletedBy = "admin";
        db.TicketTemplates.Add(template);
        await db.SaveChangesAsync();

        var result = await new RestoreTicketTemplateCommandHandler(db)
            .Handle(new RestoreTicketTemplateCommand(template.Id), CancellationToken.None);
        var stored = await db.TicketTemplates.AsNoTracking().SingleAsync(t => t.Id == template.Id);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(stored.DeletedAt, Is.Null);
            Assert.That(stored.DeletedBy, Is.Null);
        });
    }

    [Test]
    public async Task Restore_ShouldKeepInactiveTemplateInactive()
    {
        await using var db = CreateDb();
        var template = NewTemplate("Inativo");
        template.IsActive = false;
        template.DeletedAt = DateTime.UtcNow;
        db.TicketTemplates.Add(template);
        await db.SaveChangesAsync();

        _ = await new RestoreTicketTemplateCommandHandler(db)
            .Handle(new RestoreTicketTemplateCommand(template.Id), CancellationToken.None);
        var stored = await db.TicketTemplates.AsNoTracking().SingleAsync(t => t.Id == template.Id);

        Assert.Multiple(() =>
        {
            Assert.That(stored.DeletedAt, Is.Null);
            Assert.That(stored.IsActive, Is.False, "restaurar não deve ativar automaticamente");
        });
    }

    [Test]
    public async Task Restore_MissingTemplate_ShouldReturnNotFound()
    {
        await using var db = CreateDb();
        var result = await new RestoreTicketTemplateCommandHandler(db)
            .Handle(new RestoreTicketTemplateCommand(Guid.NewGuid()), CancellationToken.None);

        Assert.That(result.Errors[0].Code, Is.EqualTo("NotFound"));
    }

    [Test]
    public async Task Purge_ShouldRefuseWhenTemplateInUseWithoutForce()
    {
        await using var db = CreateDb();
        var template = NewTemplate("Criação de usuário");
        db.TicketTemplates.Add(template);
        db.Tickets.Add(NewTicket("Abrir acesso", template.Id));
        await db.SaveChangesAsync();

        var result = await new PurgeTicketTemplateCommandHandler(db)
            .Handle(new PurgeTicketTemplateCommand(template.Id), CancellationToken.None);

        var count = await db.TicketTemplates.CountAsync();
        Assert.Multiple(() =>
        {
            Assert.That(result.IsFailure, Is.True);
            Assert.That(result.Errors[0].Code, Is.EqualTo("Conflict"));
            Assert.That(result.Errors[0].Message, Does.Contain("1 chamado"));
            Assert.That(count, Is.EqualTo(1), "purge sem força não deve remover a linha");
        });
    }

    [Test]
    public async Task Purge_WithForce_ShouldRemoveRow()
    {
        await using var db = CreateDb();
        var template = NewTemplate("Acesso VPN");
        db.TicketTemplates.Add(template);
        db.Tickets.Add(NewTicket("VPN caiu", template.Id));
        await db.SaveChangesAsync();

        var result = await new PurgeTicketTemplateCommandHandler(db)
            .Handle(new PurgeTicketTemplateCommand(template.Id, Force: true), CancellationToken.None);
        var remaining = await db.TicketTemplates.CountAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(remaining, Is.Zero);
        });
    }

    [Test]
    public async Task Purge_UnusedTemplate_ShouldRemoveWithoutForce()
    {
        await using var db = CreateDb();
        var template = NewTemplate("Sem uso");
        db.TicketTemplates.Add(template);
        await db.SaveChangesAsync();

        var result = await new PurgeTicketTemplateCommandHandler(db)
            .Handle(new PurgeTicketTemplateCommand(template.Id), CancellationToken.None);
        var remaining = await db.TicketTemplates.CountAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(remaining, Is.Zero);
        });
    }

    [Test]
    public async Task Purge_MissingTemplate_ShouldReturnNotFound()
    {
        await using var db = CreateDb();
        var result = await new PurgeTicketTemplateCommandHandler(db)
            .Handle(new PurgeTicketTemplateCommand(Guid.NewGuid()), CancellationToken.None);

        Assert.That(result.Errors[0].Code, Is.EqualTo("NotFound"));
    }

    [Test]
    public async Task List_ShouldHideDeletedAndInactiveByDefault()
    {
        await using var db = CreateDb();
        db.TicketTemplates.AddRange(
            NewTemplate("Ativo global"),
            NewTemplate("Inativo global", isActive: false),
            NewTemplate("Excluído global", deletedAt: DateTime.UtcNow));
        await db.SaveChangesAsync();

        var result = await new ListTicketTemplatesQueryHandler(db)
            .Handle(new ListTicketTemplatesQuery(), CancellationToken.None);

        Assert.That(result.Value!.Select(t => t.Name), Is.EqualTo(new[] { "Ativo global" }));
    }

    [Test]
    public async Task List_IncludeDeletedAndInactive_ShouldReturnEverything()
    {
        await using var db = CreateDb();
        db.TicketTemplates.AddRange(
            NewTemplate("Ativo"),
            NewTemplate("Inativo", isActive: false),
            NewTemplate("Excluído", deletedAt: DateTime.UtcNow));
        await db.SaveChangesAsync();

        var result = await new ListTicketTemplatesQueryHandler(db)
            .Handle(new ListTicketTemplatesQuery(IncludeInactive: true, IncludeDeleted: true), CancellationToken.None);

        Assert.That(result.Value!.Select(t => t.Name), Is.EquivalentTo(new[] { "Ativo", "Excluído", "Inativo" }));
    }

    [Test]
    public async Task List_AllClients_ShouldIncludeClientTemplatesWithoutClientId()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        db.TicketTemplates.AddRange(
            NewTemplate("Global"),
            NewTemplate("Do cliente Acme", clientId: clientId));
        await db.SaveChangesAsync();

        // Bug antigo: sem clientId a listagem só trazia globais e templates por
        // cliente ficavam invisíveis na página de administração.
        var result = await new ListTicketTemplatesQueryHandler(db)
            .Handle(new ListTicketTemplatesQuery(AllClients: true), CancellationToken.None);

        Assert.That(result.Value!.Select(t => t.Name), Is.EquivalentTo(new[] { "Do cliente Acme", "Global" }));
    }

    [Test]
    public async Task List_WithoutAllClients_ShouldReturnOnlyGlobalTemplates()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        db.TicketTemplates.AddRange(
            NewTemplate("Global"),
            NewTemplate("Do cliente Acme", clientId: clientId));
        await db.SaveChangesAsync();

        var result = await new ListTicketTemplatesQueryHandler(db)
            .Handle(new ListTicketTemplatesQuery(), CancellationToken.None);

        Assert.That(result.Value!.Select(t => t.Name), Is.EqualTo(new[] { "Global" }));
    }

    [Test]
    public async Task MapTemplate_ShouldExposeDeletedFields()
    {
        var deletedAt = DateTime.UtcNow.AddDays(-1);
        var dto = CreateTicketTemplateCommandHandler.MapTemplate(new TicketTemplate
        {
            Id = Guid.NewGuid(),
            Name = "X",
            Title = "T",
            CustomFieldDefaultsJson = "{}",
            QuestionsJson = "[]",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            DeletedAt = deletedAt,
            DeletedBy = "admin",
        });

        Assert.Multiple(() =>
        {
            Assert.That(dto.DeletedAt, Is.EqualTo(deletedAt));
            Assert.That(dto.DeletedBy, Is.EqualTo("admin"));
        });
    }

    private static TicketTemplate NewTemplate(
        string name,
        bool isActive = true,
        Guid? clientId = null,
        DateTime? deletedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Title = "Título",
        Description = "Descrição",
        ClientId = clientId,
        CustomFieldDefaultsJson = "{}",
        QuestionsJson = "[]",
        IsActive = isActive,
        DeletedAt = deletedAt,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static Ticket NewTicket(string title, Guid templateId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = Guid.NewGuid(),
        Title = title,
        Description = "d",
        Priority = TicketPriority.Medium,
        WorkflowStateId = Guid.NewGuid(),
        TemplateId = templateId,
        TemplateName = "template",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"template-lifecycle-tests-{Guid.NewGuid():N}")
            .Options;
        return new DeleteTestDbContext(options);
    }

    private sealed class DeleteTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type> { typeof(Ticket), typeof(TicketTemplate) };
            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null &&
                                        type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowed.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Ticket>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<TicketTemplate>(entity => entity.HasKey(item => item.Id));
        }
    }
}
