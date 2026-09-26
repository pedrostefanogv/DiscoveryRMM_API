using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.CustomFieldTemplates;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Tests;

/// <summary>
/// Modelos de campos personalizados (catálogo pré-configurado) e preparação do
/// chamado por template: validação, serialização de opções e snapshot/erros.
/// </summary>
public class CustomFieldTemplateTests
{
    private static UpsertCustomFieldTemplateInput Input(
        CustomFieldDataType dataType = CustomFieldDataType.Text,
        string name = "cpf",
        string label = "CPF",
        IReadOnlyList<string>? options = null,
        string? regex = null,
        int? minLength = null,
        int? maxLength = null,
        decimal? minValue = null,
        decimal? maxValue = null)
        => new(null, null, name, label, null, dataType, options, regex, null, minLength, maxLength, minValue, maxValue, false, true, 0);

    [Test]
    public void Validate_ShouldAcceptValidTemplate()
    {
        var error = CustomFieldTemplateMapping.Validate(Input(regex: "^\\d{11}$", minLength: 0, maxLength: 20));
        Assert.That(error, Is.Null);
    }

    [Test]
    public void Validate_ShouldRejectMissingNameAndOptions()
    {
        Assert.That(CustomFieldTemplateMapping.Validate(Input(name: "")), Is.Not.Null);
        Assert.That(CustomFieldTemplateMapping.Validate(Input(label: " ")), Is.Not.Null);
        Assert.That(
            CustomFieldTemplateMapping.Validate(Input(dataType: CustomFieldDataType.Dropdown, options: null)),
            Is.Not.Null);
    }

    [Test]
    public void Validate_ShouldRejectInvalidRegexAndRanges()
    {
        Assert.That(CustomFieldTemplateMapping.Validate(Input(regex: "([")), Is.Not.Null);
        Assert.That(CustomFieldTemplateMapping.Validate(Input(minLength: 10, maxLength: 2)), Is.Not.Null);
        Assert.That(CustomFieldTemplateMapping.Validate(Input(minValue: 10, maxValue: 1)), Is.Not.Null);
    }

    [Test]
    public void Options_ShouldRoundTripAndTrim()
    {
        var json = CustomFieldTemplateMapping.SerializeOptions(new[] { " A ", "B", " " });
        Assert.That(json, Is.Not.Null);
        Assert.That(CustomFieldTemplateMapping.ParseOptions(json), Is.EqualTo(new[] { "A", "B" }));
        Assert.That(CustomFieldTemplateMapping.SerializeOptions(Array.Empty<string>()), Is.Null);
    }

    [Test]
    public async Task PrepareAsync_ShouldReturnTemplateNameAndStructuredAnswers()
    {
        await using var db = CreateDb();
        var templateId = Guid.NewGuid();
        db.TicketTemplates.Add(new TicketTemplate
        {
            Id = templateId,
            Name = "Criação de usuário",
            Title = "Novo usuário",
            Description = "Abertura de acesso",
            QuestionsJson = TicketTemplateQuestions.Serialize(new[]
            {
                new TicketTemplateQuestion("nome", "Nome", CustomFieldDataType.Text, true,
                    Array.Empty<string>(), null, null, 2, null, null, null, null),
                new TicketTemplateQuestion("email", "E-mail", CustomFieldDataType.Text, false,
                    Array.Empty<string>(), null, null, null, null, null, null, null),
            }),
            CustomFieldDefaultsJson = "{}",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var service = new TicketSubmissionService(db, new FakeDepartmentCustomFieldService());
        var result = await service.PrepareAsync(new TicketSubmissionRequest(
            Guid.NewGuid(), DepartmentId: null, TemplateId: templateId,
            Title: null, Description: null, Category: null, Priority: null,
            CustomFieldValues: null,
            TemplateAnswers: new Dictionary<string, JsonElement>
            {
                ["nome"] = JsonSerializer.SerializeToElement("Ana"),
                ["email"] = JsonSerializer.SerializeToElement("ana@empresa.com"),
            }));

        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.True);
            Assert.That(result.TemplateId, Is.EqualTo(templateId));
            Assert.That(result.TemplateName, Is.EqualTo("Criação de usuário"));
            Assert.That(result.Answers, Is.Not.Null);
            Assert.That(result.Answers!.Select(a => a.QuestionKey), Is.EquivalentTo(new[] { "nome", "email" }));
            Assert.That(result.Answers!.Single(a => a.QuestionKey == "nome").QuestionLabel, Is.EqualTo("Nome"));
            Assert.That(result.Answers!.Single(a => a.QuestionKey == "nome").ValueText, Is.EqualTo("Ana"));
            Assert.That(result.SnapshotMarkdown, Does.Contain("Criação de usuário"));
            Assert.That(result.SnapshotMarkdown, Does.Contain("Questionário do modelo"));
        });
    }

    [Test]
    public async Task PrepareAsync_ShouldEnforceRequiredTemplateQuestions()
    {
        await using var db = CreateDb();
        var templateId = Guid.NewGuid();
        db.TicketTemplates.Add(new TicketTemplate
        {
            Id = templateId,
            Name = "Modelo com obrigatória",
            Title = "T",
            Description = "D",
            QuestionsJson = TicketTemplateQuestions.Serialize(new[]
            {
                new TicketTemplateQuestion("nome", "Nome", CustomFieldDataType.Text, true,
                    Array.Empty<string>(), null, null, null, null, null, null, null),
            }),
            CustomFieldDefaultsJson = "{}",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var service = new TicketSubmissionService(db, new FakeDepartmentCustomFieldService());
        var result = await service.PrepareAsync(new TicketSubmissionRequest(
            Guid.NewGuid(), DepartmentId: null, TemplateId: templateId,
            Title: null, Description: null, Category: null, Priority: null,
            CustomFieldValues: null));

        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Errors.Select(e => e.FieldName), Contains.Item("nome"));
            Assert.That(result.SnapshotMarkdown, Is.Null);
        });
    }

    [Test]
    public async Task PrepareAsync_ShouldRequireDepartmentWhenFieldsProvided()
    {
        await using var db = CreateDb();
        var service = new TicketSubmissionService(db, new FakeDepartmentCustomFieldService());

        var result = await service.PrepareAsync(new TicketSubmissionRequest(
            Guid.NewGuid(), DepartmentId: null, TemplateId: null,
            "T", "D", null, "Medium",
            new Dictionary<Guid, JsonElement>
            {
                [Guid.NewGuid()] = JsonSerializer.SerializeToElement("valor"),
            }));

        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.SnapshotMarkdown, Is.Null);
            Assert.That(result.Errors, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task PrepareAsync_ShouldPassThroughWithoutTemplateOrFields()
    {
        await using var db = CreateDb();
        var service = new TicketSubmissionService(db, new FakeDepartmentCustomFieldService());

        var result = await service.PrepareAsync(new TicketSubmissionRequest(
            Guid.NewGuid(), DepartmentId: null, TemplateId: null,
            "Título", "Descrição", null, "High", null));

        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Title, Is.EqualTo("Título"));
            Assert.That(result.Priority, Is.EqualTo("High"));
            Assert.That(result.SnapshotMarkdown, Is.Null);
            Assert.That(result.CustomFieldValues, Is.Empty);
        });
    }

    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"custom-field-template-tests-{Guid.NewGuid():N}")
            .Options;
        return new TemplateTestDbContext(options);
    }

    private sealed class TemplateTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowedTypes = new HashSet<Type>
            {
                typeof(TicketTemplate),
                typeof(CustomFieldDefinition),
                typeof(CustomFieldValue),
            };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null &&
                                        type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowedTypes.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<TicketTemplate>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<CustomFieldDefinition>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<CustomFieldValue>(entity => entity.HasKey(item => item.Id));
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
