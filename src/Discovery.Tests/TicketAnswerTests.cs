using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Tests;

/// <summary>
/// Respostas estruturadas do questionário: persistência e filtros da listagem
/// (por template e por resposta).
/// </summary>
public class TicketAnswerTests
{
    [Test]
    public async Task SaveTemplateAnswersAsync_ShouldPersistRows()
    {
        await using var db = CreateDb();
        var service = new TicketSubmissionService(db, new FakeDepartmentCustomFieldService());
        var ticketId = Guid.NewGuid();
        var templateId = Guid.NewGuid();

        await service.SaveTemplateAnswersAsync(ticketId, templateId, new[]
        {
            new TicketAnswerDraft("nome", "Nome", "Ana", "\"Ana\""),
            new TicketAnswerDraft("aceite", "Aceite", "Sim", "true"),
        });

        var rows = await db.TicketAnswers.Where(a => a.TicketId == ticketId).ToListAsync();
        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(rows.All(r => r.TemplateId == templateId), Is.True);
            Assert.That(rows.Single(r => r.QuestionKey == "nome").ValueText, Is.EqualTo("Ana"));
            Assert.That(rows.Single(r => r.QuestionKey == "aceite").ValueText, Is.EqualTo("Sim"));
        });
    }

    [Test]
    public async Task GetAnswersAsync_ShouldReturnAnswersOfTheTicket()
    {
        await using var db = CreateDb();
        var ticketId = Guid.NewGuid();
        db.TicketAnswers.Add(new TicketAnswer
        {
            Id = Guid.NewGuid(), TicketId = ticketId, TemplateId = null,
            QuestionKey = "email", QuestionLabel = "E-mail",
            ValueText = "ana@empresa.com", ValueJson = "\"ana@empresa.com\"",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var service = new TicketQueryService(db);
        var answers = await service.GetAnswersAsync(ticketId);

        Assert.Multiple(() =>
        {
            Assert.That(answers, Has.Count.EqualTo(1));
            Assert.That(answers[0].QuestionLabel, Is.EqualTo("E-mail"));
            Assert.That(answers[0].ValueText, Is.EqualTo("ana@empresa.com"));
        });
    }

    [Test]
    public async Task GetAnswersAsync_ShouldPreserveTemplateOrder()
    {
        await using var db = CreateDb();
        var service = new TicketSubmissionService(db, new FakeDepartmentCustomFieldService());
        var ticketId = Guid.NewGuid();

        // Ordem do template (não alfabética): todas as respostas compartilham o
        // mesmo created_at, então a ordem depende de sort_order.
        await service.SaveTemplateAnswersAsync(ticketId, null, new[]
        {
            new TicketAnswerDraft("zebra", "Zebra", "1", "\"1\""),
            new TicketAnswerDraft("alfa", "Alfa", "2", "\"2\""),
        });

        var query = new TicketQueryService(db);
        var answers = await query.GetAnswersAsync(ticketId);

        Assert.That(answers.Select(a => a.QuestionKey), Is.EqualTo(new[] { "zebra", "alfa" }));
    }

    [Test]
    public async Task SaveTemplateAnswersAsync_ShouldReplacePreviousAnswers()
    {
        await using var db = CreateDb();
        var service = new TicketSubmissionService(db, new FakeDepartmentCustomFieldService());
        var ticketId = Guid.NewGuid();

        await service.SaveTemplateAnswersAsync(ticketId, null, new[]
        {
            new TicketAnswerDraft("nome", "Nome", "Ana", "\"Ana\""),
        });
        // Replay: substitui sem violar o índice único (ticket_id, question_key).
        await service.SaveTemplateAnswersAsync(ticketId, null, new[]
        {
            new TicketAnswerDraft("nome", "Nome", "Bruno", "\"Bruno\""),
            new TicketAnswerDraft("email", "E-mail", "b@e.com", "\"b@e.com\""),
        });

        var rows = await db.TicketAnswers.Where(a => a.TicketId == ticketId).ToListAsync();
        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(rows.Single(r => r.QuestionKey == "nome").ValueText, Is.EqualTo("Bruno"));
            Assert.That(rows.Single(r => r.QuestionKey == "email").SortOrder, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task SaveTemplateAnswersAsync_ShouldDoNothingWhenEmpty()
    {
        await using var db = CreateDb();
        var service = new TicketSubmissionService(db, new FakeDepartmentCustomFieldService());
        await service.SaveTemplateAnswersAsync(Guid.NewGuid(), null, Array.Empty<TicketAnswerDraft>());
        Assert.That(await db.TicketAnswers.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task ListTicketsAsync_ShouldFilterByTemplateAndAnswer()
    {
        await using var db = CreateDb();
        var templateA = Guid.NewGuid();
        var templateB = Guid.NewGuid();

        var openWithTemplate = NewTicket("Com template A", templateA);
        var openNoTemplate = NewTicket("Sem template", null);
        var otherTemplate = NewTicket("Com template B", templateB);
        db.Tickets.AddRange(openWithTemplate, openNoTemplate, otherTemplate);

        db.TicketAnswers.Add(new TicketAnswer
        {
            Id = Guid.NewGuid(), TicketId = openWithTemplate.Id, TemplateId = templateA,
            QuestionKey = "sistema", QuestionLabel = "Sistema",
            ValueText = "Sistemas internos", ValueJson = JsonSerializer.SerializeToElement("Sistemas internos").GetRawText(),
            CreatedAt = DateTime.UtcNow,
        });
        db.TicketAnswers.Add(new TicketAnswer
        {
            Id = Guid.NewGuid(), TicketId = otherTemplate.Id, TemplateId = templateB,
            QuestionKey = "sistema", QuestionLabel = "Sistema",
            ValueText = "Softwares de terceiros", ValueJson = JsonSerializer.SerializeToElement("Softwares de terceiros").GetRawText(),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var service = new TicketQueryService(db);

        var byTemplate = await service.ListTicketsAsync(new TicketFilterQuery(TemplateId: templateA));
        Assert.That(byTemplate.Items.Select(i => i.Id), Is.EquivalentTo(new[] { openWithTemplate.Id }));

        var byAnswer = await service.ListTicketsAsync(new TicketFilterQuery(
            AnswerKey: "sistema", AnswerValue: "Softwares de terceiros"));
        Assert.That(byAnswer.Items.Select(i => i.Id), Is.EquivalentTo(new[] { otherTemplate.Id }));

        var anyAnswer = await service.ListTicketsAsync(new TicketFilterQuery(AnswerKey: "sistema"));
        Assert.That(anyAnswer.Items.Select(i => i.Id),
            Is.EquivalentTo(new[] { openWithTemplate.Id, otherTemplate.Id }));

        var noMatch = await service.ListTicketsAsync(new TicketFilterQuery(
            AnswerKey: "sistema", AnswerValue: "Inexistente"));
        Assert.That(noMatch.Items, Is.Empty);
    }

    private static Ticket NewTicket(string title, Guid? templateId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = Guid.NewGuid(),
        Title = title,
        Description = "d",
        Priority = TicketPriority.Medium,
        WorkflowStateId = Guid.NewGuid(),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        TemplateId = templateId,
    };

    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"ticket-answer-tests-{Guid.NewGuid():N}")
            .Options;
        return new AnswerTestDbContext(options);
    }

    private sealed class AnswerTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowedTypes = new HashSet<Type> { typeof(Ticket), typeof(TicketAnswer) };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null &&
                                        type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowedTypes.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Ticket>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<TicketAnswer>(entity => entity.HasKey(item => item.Id));
        }
    }

    private sealed class FakeDepartmentCustomFieldService : IDepartmentCustomFieldService
    {
        public Task<IReadOnlyList<CustomFieldDefinition>> GetDefinitionsByDepartmentAsync(Guid departmentId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CustomFieldDefinition>>(Array.Empty<CustomFieldDefinition>());
        public Task<CustomFieldDefinition> CreateDepartmentFieldAsync(Guid departmentId, CreateDepartmentCustomFieldInput input, string? updatedBy, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<CustomFieldDefinition?> UpdateDepartmentFieldAsync(Guid fieldId, Guid departmentId, UpdateDepartmentCustomFieldInput input, string? updatedBy, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<bool> DeleteDepartmentFieldAsync(Guid fieldId, Guid departmentId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<DepartmentFieldSchemaItemDto>> GetPublicSchemaForDepartmentAsync(Guid departmentId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DepartmentFieldSchemaItemDto>>(Array.Empty<DepartmentFieldSchemaItemDto>());
        public Task<IReadOnlyList<DepartmentFieldSchemaItemDto>> GetFullSchemaForDepartmentAsync(Guid departmentId, Guid? ticketId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DepartmentFieldSchemaItemDto>>(Array.Empty<DepartmentFieldSchemaItemDto>());
        public Task<IReadOnlyList<DepartmentFieldValidationError>> ValidateTicketFieldsAsync(Guid departmentId, IReadOnlyDictionary<Guid, string> fieldValues, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DepartmentFieldValidationError>>(Array.Empty<DepartmentFieldValidationError>());
        public Task SaveTicketFieldValuesAsync(Guid ticketId, Guid departmentId, IReadOnlyDictionary<Guid, string> fieldValues, string? updatedBy, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
