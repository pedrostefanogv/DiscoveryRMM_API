using Discovery.Core.Cqrs.Support.Templates;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Cqrs.Support;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Tests;

/// <summary>
/// Proteção da exclusão do template de chamado: template já usado exige
/// confirmação explícita (force), evitando perda acidental do histórico.
/// </summary>
public class TicketTemplateDeleteTests
{
    [Test]
    public async Task Delete_ShouldRefuseWhenTemplateInUse()
    {
        await using var db = CreateDb();
        var template = NewTemplate("Criação de usuário");
        db.TicketTemplates.Add(template);
        db.Tickets.Add(NewTicket("Abrir acesso", template.Id));
        await db.SaveChangesAsync();

        var handler = new DeleteTicketTemplateCommandHandler(db);
        var result = await handler.Handle(new DeleteTicketTemplateCommand(template.Id), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsFailure, Is.True);
            Assert.That(result.Errors[0].Code, Is.EqualTo("Conflict"));
            Assert.That(result.Errors[0].Message, Does.Contain("1 chamado"));
            Assert.That(db.TicketTemplates.Count(), Is.EqualTo(1), "template não deve ser removido sem confirmação");
        });
    }

    [Test]
    public async Task Delete_WithForce_ShouldRemoveTemplate()
    {
        await using var db = CreateDb();
        var template = NewTemplate("Acesso VPN");
        db.TicketTemplates.Add(template);
        db.Tickets.Add(NewTicket("VPN caiu", template.Id));
        await db.SaveChangesAsync();

        var handler = new DeleteTicketTemplateCommandHandler(db);
        var result = await handler.Handle(
            new DeleteTicketTemplateCommand(template.Id, Force: true), CancellationToken.None);

        var remaining = await db.TicketTemplates.CountAsync();
        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(remaining, Is.Zero);
        });
    }

    [Test]
    public async Task Delete_ShouldRemoveUnusedTemplateWithoutForce()
    {
        await using var db = CreateDb();
        var template = NewTemplate("Sem uso");
        db.TicketTemplates.Add(template);
        await db.SaveChangesAsync();

        var handler = new DeleteTicketTemplateCommandHandler(db);
        var result = await handler.Handle(new DeleteTicketTemplateCommand(template.Id), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
    }

    [Test]
    public async Task Delete_ShouldReturnNotFoundWhenMissing()
    {
        await using var db = CreateDb();
        var handler = new DeleteTicketTemplateCommandHandler(db);
        var result = await handler.Handle(
            new DeleteTicketTemplateCommand(Guid.NewGuid()), CancellationToken.None);

        Assert.That(result.Errors[0].Code, Is.EqualTo("NotFound"));
    }

    private static TicketTemplate NewTemplate(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Title = "Título",
        Description = "Descrição",
        CustomFieldDefaultsJson = "{}",
        QuestionsJson = "[]",
        IsActive = true,
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
            .UseInMemoryDatabase($"template-delete-tests-{Guid.NewGuid():N}")
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
