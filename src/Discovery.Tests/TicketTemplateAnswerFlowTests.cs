using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// Fluxo real da abertura pelo agent com resposta de template: o agent envia
/// ListBox como ARRAY (["Sistema2"]) e a API precisa transformar em resposta
/// gravada. Cobre também o questions_json com dataType em texto, que o parser
/// do site aceita — se a API recusar, as perguntas (e as respostas) somem sem
/// erro visível.
/// </summary>
public class TicketTemplateAnswerFlowTests
{
    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"template-answer-flow-{Guid.NewGuid():N}")
            .Options;
        return new FlowTestDbContext(options);
    }

    private static Guid SeedDepartment(DiscoveryDbContext db)
    {
        var department = new Department
        {
            Id = Guid.NewGuid(),
            Name = "TI",
            IsActive = true,
            ClientId = null,
        };
        db.Departments.Add(department);
        return department.Id;
    }

    private static TicketTemplate SeedTemplate(DiscoveryDbContext db, string questionsJson)
    {
        var template = new TicketTemplate
        {
            Id = Guid.NewGuid(),
            Name = "criacao-login",
            Title = "Criacao de login",
            Description = "Modelo",
            IsActive = true,
            DepartmentId = null,
            QuestionsJson = questionsJson,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.TicketTemplates.Add(template);
        return template;
    }

    private static TicketSubmissionRequest Request(Guid departmentId, Guid templateId, JsonElement answer)
        => new(
            ClientId: Guid.NewGuid(),
            DepartmentId: departmentId,
            TemplateId: templateId,
            Title: "Criacao de login",
            Description: "Modelo",
            Category: null,
            Priority: null,
            CustomFieldValues: null,
            TemplateAnswers: new Dictionary<string, JsonElement> { ["sistema"] = answer });

    [Test]
    public async Task PrepareAsync_ShouldCollectAgentListBoxAnswer()
    {
        await using var db = CreateDb();
        var departmentId = SeedDepartment(db);
        var questionsJson = TicketTemplateQuestions.Serialize(
        [
            new TicketTemplateQuestion(
                "sistema", "Sistema", CustomFieldDataType.ListBox, true,
                ["Sistema1", "Sistema2", "Sistema3"],
                null, null, null, null, null, null, null),
        ]);
        var template = SeedTemplate(db, questionsJson);
        await db.SaveChangesAsync();

        var service = new TicketSubmissionService(db, new FakeDepartmentCustomFieldService(), NullLogger<TicketSubmissionService>.Instance);
        var result = await service.PrepareAsync(
            Request(departmentId, template.Id, JsonSerializer.SerializeToElement(new[] { "Sistema2" })));

        Assert.That(result.IsValid, Is.True, string.Join(" | ", result.Errors.Select(e => e.ErrorMessage)));
        Assert.That(result.Answers, Is.Not.Null);
        Assert.That(result.Answers!.Select(a => a.QuestionKey), Is.EqualTo(new[] { "sistema" }));
        Assert.That(result.Answers![0].ValueText, Is.EqualTo("Sistema2"));
    }

    [Test]
    public void Parse_ShouldAcceptDataTypeAsText()
    {
        // Formato que o parser do site aceita (normalizeDataType).
        var json = """[{"key":"sistema","label":"Sistema","dataType":"ListBox","isRequired":true,"options":["Sistema1","Sistema2"]}]""";

        var questions = TicketTemplateQuestions.Parse(json);

        Assert.That(questions, Has.Count.EqualTo(1), "dataType em texto não pode zerar as perguntas do template.");
        Assert.That(questions[0].DataType, Is.EqualTo(CustomFieldDataType.ListBox));
        Assert.That(questions[0].Options, Is.EquivalentTo(new[] { "Sistema1", "Sistema2" }));
    }

    [Test]
    public async Task PrepareAsync_ShouldNotSilentlyDropAnswersWhenQuestionsAreUnreadable()
    {
        await using var db = CreateDb();
        var departmentId = SeedDepartment(db);
        // questions_json com dataType em texto (aceito pelo site): hoje a API
        // devolve lista vazia e descarta a resposta sem avisar ninguém.
        var template = SeedTemplate(db,
            """[{"key":"sistema","label":"Sistema","dataType":"ListBox","isRequired":true,"options":["Sistema1","Sistema2"]}]""");
        await db.SaveChangesAsync();

        var service = new TicketSubmissionService(db, new FakeDepartmentCustomFieldService(), NullLogger<TicketSubmissionService>.Instance);
        var result = await service.PrepareAsync(
            Request(departmentId, template.Id, JsonSerializer.SerializeToElement(new[] { "Sistema2" })));

        Assert.That(result.Answers, Is.Not.Null);
        Assert.That(result.Answers!.Select(a => a.QuestionKey), Is.EqualTo(new[] { "sistema" }));
    }

    private sealed class FlowTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(Department), typeof(CustomFieldDefinition), typeof(TicketTemplate),
            };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(t => t.IsClass && t.Namespace is not null
                                     && t.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(t => !allowed.Contains(t)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Department>(e => e.HasKey(d => d.Id));
            modelBuilder.Entity<CustomFieldDefinition>(e => e.HasKey(d => d.Id));
            modelBuilder.Entity<TicketTemplate>(e => e.HasKey(t => t.Id));
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
