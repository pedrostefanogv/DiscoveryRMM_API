using System.Text.Json;
using Discovery.Core.Cqrs.Agents.Crud.Queries;
using Discovery.Core.Cqrs.CustomFields.Commands;
using Discovery.Core.Cqrs.CustomFields.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Agents.QueryHandlers;
using Discovery.Infrastructure.Cqrs.CustomFields;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

public class CustomFieldServiceTests
{
    [Test]
    public async Task GetSchemaAsync_ShouldReturnMaskedValuesBindingsAndInactiveDefinitions()
    {
        await using var fixture = await CreateFixtureAsync();
        var taskId = Guid.NewGuid();
        var secretDefinition = new CustomFieldDefinition
        {
            Id = Guid.NewGuid(),
            Name = "agent_secret",
            Label = "Agent Secret",
            ScopeType = CustomFieldScopeType.Agent,
            DataType = CustomFieldDataType.Text,
            IsActive = true,
            IsSecret = true,
            AllowRuntimeRead = true,
            AllowAgentWrite = true,
            RuntimeAccessMode = CustomFieldRuntimeAccessMode.RestrictedTaskScript,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var inactiveDefinition = new CustomFieldDefinition
        {
            Id = Guid.NewGuid(),
            Name = "deployment_ring",
            Label = "Deployment Ring",
            ScopeType = CustomFieldScopeType.Agent,
            DataType = CustomFieldDataType.Dropdown,
            IsActive = false,
            OptionsJson = JsonSerializer.Serialize(new[] { "critical", "standard" }),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        fixture.Db.CustomFieldDefinitions.AddRange(secretDefinition, inactiveDefinition);
        fixture.Db.CustomFieldValues.Add(new CustomFieldValue
        {
            Id = Guid.NewGuid(),
            DefinitionId = secretDefinition.Id,
            ScopeType = CustomFieldScopeType.Agent,
            EntityId = fixture.Agent.Id,
            EntityKey = fixture.Agent.Id.ToString("D"),
            ValueJson = JsonSerializer.Serialize("super-secret"),
            UpdatedBy = "tester",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        fixture.Db.CustomFieldExecutionAccesses.Add(new CustomFieldExecutionAccess
        {
            Id = Guid.NewGuid(),
            DefinitionId = secretDefinition.Id,
            TaskId = taskId,
            CanRead = true,
            CanWrite = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await fixture.Db.SaveChangesAsync();

        var schema = await fixture.Service.GetSchemaAsync(
            CustomFieldScopeType.Agent,
            fixture.Agent.Id,
            includeInactive: true,
            includeSecrets: false,
            cancellationToken: CancellationToken.None);

        Assert.That(schema, Has.Count.EqualTo(2));

        var secretItem = schema.Single(item => item.DefinitionId == secretDefinition.Id);
        var inactiveItem = schema.Single(item => item.DefinitionId == inactiveDefinition.Id);
        using var secretJson = JsonDocument.Parse(secretItem.ValueJson);

        Assert.Multiple(() =>
        {
            Assert.That(secretItem.EntityId, Is.EqualTo(fixture.Agent.Id));
            Assert.That(secretJson.RootElement.GetString(), Is.EqualTo("***"));
            Assert.That(secretItem.AccessBindings, Has.Count.EqualTo(1));
            Assert.That(secretItem.AccessBindings[0].TaskId, Is.EqualTo(taskId));
            Assert.That(secretItem.AccessBindings[0].CanWrite, Is.True);
            Assert.That(secretItem.ValueUpdatedAt, Is.Not.Null);
            Assert.That(inactiveItem.IsActive, Is.False);
            Assert.That(inactiveItem.Options, Is.EquivalentTo(new[] { "critical", "standard" }));
        });
    }

    [Test]
    public async Task GetRuntimeValuesForAgentAsync_ShouldHonorExecutionBindings()
    {
        await using var fixture = await CreateFixtureAsync();
        var allowedTaskId = Guid.NewGuid();
        var blockedTaskId = Guid.NewGuid();
        var serverDefinition = new CustomFieldDefinition
        {
            Id = Guid.NewGuid(),
            Name = "global_banner",
            Label = "Global Banner",
            ScopeType = CustomFieldScopeType.Server,
            DataType = CustomFieldDataType.Text,
            IsActive = true,
            AllowRuntimeRead = true,
            RuntimeAccessMode = CustomFieldRuntimeAccessMode.Public,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var allowedSiteDefinition = new CustomFieldDefinition
        {
            Id = Guid.NewGuid(),
            Name = "site_token",
            Label = "Site Token",
            ScopeType = CustomFieldScopeType.Site,
            DataType = CustomFieldDataType.Text,
            IsActive = true,
            AllowRuntimeRead = true,
            RuntimeAccessMode = CustomFieldRuntimeAccessMode.RestrictedTaskScript,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var blockedAgentDefinition = new CustomFieldDefinition
        {
            Id = Guid.NewGuid(),
            Name = "agent_serial",
            Label = "Agent Serial",
            ScopeType = CustomFieldScopeType.Agent,
            DataType = CustomFieldDataType.Text,
            IsActive = true,
            AllowRuntimeRead = true,
            RuntimeAccessMode = CustomFieldRuntimeAccessMode.RestrictedTaskScript,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        fixture.Db.CustomFieldDefinitions.AddRange(serverDefinition, allowedSiteDefinition, blockedAgentDefinition);
        fixture.Db.CustomFieldExecutionAccesses.AddRange(
            new CustomFieldExecutionAccess
            {
                Id = Guid.NewGuid(),
                DefinitionId = allowedSiteDefinition.Id,
                TaskId = allowedTaskId,
                CanRead = true,
                CanWrite = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new CustomFieldExecutionAccess
            {
                Id = Guid.NewGuid(),
                DefinitionId = blockedAgentDefinition.Id,
                TaskId = blockedTaskId,
                CanRead = true,
                CanWrite = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        fixture.Db.CustomFieldValues.AddRange(
            new CustomFieldValue
            {
                Id = Guid.NewGuid(),
                DefinitionId = serverDefinition.Id,
                ScopeType = CustomFieldScopeType.Server,
                EntityKey = "server",
                ValueJson = JsonSerializer.Serialize("maintenance-window"),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new CustomFieldValue
            {
                Id = Guid.NewGuid(),
                DefinitionId = allowedSiteDefinition.Id,
                ScopeType = CustomFieldScopeType.Site,
                EntityId = fixture.Site.Id,
                EntityKey = fixture.Site.Id.ToString("D"),
                ValueJson = JsonSerializer.Serialize("site-123"),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new CustomFieldValue
            {
                Id = Guid.NewGuid(),
                DefinitionId = blockedAgentDefinition.Id,
                ScopeType = CustomFieldScopeType.Agent,
                EntityId = fixture.Agent.Id,
                EntityKey = fixture.Agent.Id.ToString("D"),
                ValueJson = JsonSerializer.Serialize("agent-serial"),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        await fixture.Db.SaveChangesAsync();

        var values = await fixture.Service.GetRuntimeValuesForAgentAsync(
            fixture.Agent.Id,
            allowedTaskId,
            scriptId: null,
            cancellationToken: CancellationToken.None);

        Assert.That(values.Select(item => item.Name), Is.EquivalentTo(new[] { "global_banner", "site_token" }));

        var siteValue = values.Single(item => item.Name == "site_token");
        using var siteJson = JsonDocument.Parse(siteValue.ValueJson);
        Assert.That(siteJson.RootElement.GetString(), Is.EqualTo("site-123"));
    }

    [Test]
    public async Task UpsertAgentCollectedValueAsync_ShouldPersistValueWhenWriteBindingMatches()
    {
        await using var fixture = await CreateFixtureAsync();
        var taskId = Guid.NewGuid();
        var definition = new CustomFieldDefinition
        {
            Id = Guid.NewGuid(),
            Name = "agent_runtime_flag",
            Label = "Agent Runtime Flag",
            ScopeType = CustomFieldScopeType.Agent,
            DataType = CustomFieldDataType.Text,
            IsActive = true,
            AllowAgentWrite = true,
            RuntimeAccessMode = CustomFieldRuntimeAccessMode.RestrictedTaskScript,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        fixture.Db.CustomFieldDefinitions.Add(definition);
        fixture.Db.CustomFieldExecutionAccesses.Add(new CustomFieldExecutionAccess
        {
            Id = Guid.NewGuid(),
            DefinitionId = definition.Id,
            TaskId = taskId,
            CanRead = true,
            CanWrite = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Service.UpsertAgentCollectedValueAsync(
            fixture.Agent.Id,
            new AgentCustomFieldCollectedValueInput(
                definition.Id,
                null,
                JsonSerializer.Serialize("enabled"),
                taskId,
                null,
                "runtime-agent"),
            CancellationToken.None);

        var stored = await fixture.Db.CustomFieldValues.SingleAsync(item => item.DefinitionId == definition.Id);
        using var resultJson = JsonDocument.Parse(result.ValueJson);

        Assert.Multiple(() =>
        {
            Assert.That(stored.EntityId, Is.EqualTo(fixture.Agent.Id));
            Assert.That(stored.UpdatedBy, Is.EqualTo("runtime-agent"));
            Assert.That(stored.ValueJson, Is.EqualTo(JsonSerializer.Serialize("enabled")));
            Assert.That(resultJson.RootElement.GetString(), Is.EqualTo("enabled"));
        });
    }

    [Test]
    public async Task UpsertAgentCollectedValueAsync_ShouldRejectWriteWithoutMatchingBinding()
    {
        await using var fixture = await CreateFixtureAsync();
        var allowedTaskId = Guid.NewGuid();
        var currentTaskId = Guid.NewGuid();
        var definition = new CustomFieldDefinition
        {
            Id = Guid.NewGuid(),
            Name = "script_result",
            Label = "Script Result",
            ScopeType = CustomFieldScopeType.Agent,
            DataType = CustomFieldDataType.Text,
            IsActive = true,
            AllowAgentWrite = true,
            RuntimeAccessMode = CustomFieldRuntimeAccessMode.RestrictedTaskScript,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        fixture.Db.CustomFieldDefinitions.Add(definition);
        fixture.Db.CustomFieldExecutionAccesses.Add(new CustomFieldExecutionAccess
        {
            Id = Guid.NewGuid(),
            DefinitionId = definition.Id,
            TaskId = allowedTaskId,
            CanRead = true,
            CanWrite = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await fixture.Db.SaveChangesAsync();

        Assert.That(async () => await fixture.Service.UpsertAgentCollectedValueAsync(
                fixture.Agent.Id,
                new AgentCustomFieldCollectedValueInput(
                    null,
                    definition.Name,
                    JsonSerializer.Serialize("blocked"),
                    currentTaskId,
                    null,
                    null),
                CancellationToken.None),
            Throws.TypeOf<InvalidOperationException>()
                .With.Message.EqualTo("O agente não pode escrever este custom field no contexto de execução atual."));
    }

    [Test]
    public async Task UpsertValue_ShouldAllowTicketWritingItsOwnDepartmentField()
    {
        await using var fixture = await CreateFixtureAsync();
        var db = fixture.Db;
        var now = DateTime.UtcNow;

        var department = new Department
        {
            Id = Guid.NewGuid(),
            ClientId = fixture.Client.Id,
            Name = "TI",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        var otherDepartment = new Department
        {
            Id = Guid.NewGuid(),
            ClientId = fixture.Client.Id,
            Name = "Compras",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        var ticket = new Ticket
        {
            Id = Guid.NewGuid(),
            ClientId = fixture.Client.Id,
            DepartmentId = department.Id,
            Title = "Chamado",
            Description = "d",
            WorkflowStateId = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now
        };
        var definition = new CustomFieldDefinition
        {
            Id = Guid.NewGuid(),
            Name = "ramal",
            Label = "Ramal",
            ScopeType = CustomFieldScopeType.Department,
            DepartmentId = department.Id,
            DataType = CustomFieldDataType.Text,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        var otherDefinition = new CustomFieldDefinition
        {
            Id = Guid.NewGuid(),
            Name = "centro_custo",
            Label = "Centro de custo",
            ScopeType = CustomFieldScopeType.Department,
            DepartmentId = otherDepartment.Id,
            DataType = CustomFieldDataType.Text,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.AddRange(department, otherDepartment, ticket, definition, otherDefinition);
        await db.SaveChangesAsync();

        // Campo do departamento DO chamado: aceito (gravado com escopo Ticket).
        var saved = await fixture.Service.UpsertValueAsync(new UpsertCustomFieldValueInput(
            definition.Id, CustomFieldScopeType.Ticket, ticket.Id, "\"1024\"", null));

        var stored = await db.CustomFieldValues.AsNoTracking()
            .SingleAsync(value => value.DefinitionId == definition.Id);
        Assert.Multiple(() =>
        {
            Assert.That(saved.ValueJson, Is.EqualTo("\"1024\""));
            Assert.That(stored.ScopeType, Is.EqualTo(CustomFieldScopeType.Ticket));
            Assert.That(stored.ValueJson, Is.EqualTo("\"1024\""));
        });

        // Campo de OUTRO departamento: segue recusado.
        Assert.That(
            async () => await fixture.Service.UpsertValueAsync(new UpsertCustomFieldValueInput(
                otherDefinition.Id, CustomFieldScopeType.Ticket, ticket.Id, "\"x\"", null)),
            Throws.TypeOf<InvalidOperationException>()
                .With.Message.EqualTo("O escopo do valor não corresponde ao escopo da definição."));
    }

    [Test]
    public async Task UpsertValue_ShouldRejectClearingRequiredDepartmentField()
    {
        await using var fixture = await CreateFixtureAsync();
        var db = fixture.Db;
        var now = DateTime.UtcNow;

        var department = new Department { Id = Guid.NewGuid(), ClientId = fixture.Client.Id, Name = "TI", IsActive = true, CreatedAt = now, UpdatedAt = now };
        var ticket = new Ticket { Id = Guid.NewGuid(), ClientId = fixture.Client.Id, DepartmentId = department.Id, Title = "Chamado", Description = "d", WorkflowStateId = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now };
        var requiredDefinition = new CustomFieldDefinition
        {
            Id = Guid.NewGuid(), Name = "ramal", Label = "Ramal",
            ScopeType = CustomFieldScopeType.Department, DepartmentId = department.Id,
            DataType = CustomFieldDataType.Text, IsRequired = true, IsActive = true,
            CreatedAt = now, UpdatedAt = now
        };
        db.AddRange(department, ticket, requiredDefinition);
        await db.SaveChangesAsync();

        Assert.That(
            async () => await fixture.Service.UpsertValueAsync(new UpsertCustomFieldValueInput(
                requiredDefinition.Id, CustomFieldScopeType.Ticket, ticket.Id, "null", null)),
            Throws.TypeOf<InvalidOperationException>()
                .With.Message.EqualTo("O valor do custom field é obrigatório."));
    }

    [Test]
    public async Task GetValuesAsync_ShouldIncludeTicketDepartmentFields()
    {
        await using var fixture = await CreateFixtureAsync();
        var db = fixture.Db;
        var now = DateTime.UtcNow;

        var department = new Department
        {
            Id = Guid.NewGuid(),
            ClientId = fixture.Client.Id,
            Name = "TI",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        var ticket = new Ticket
        {
            Id = Guid.NewGuid(),
            ClientId = fixture.Client.Id,
            DepartmentId = department.Id,
            Title = "Chamado",
            Description = "d",
            WorkflowStateId = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now
        };
        var departmentDefinition = new CustomFieldDefinition
        {
            Id = Guid.NewGuid(),
            Name = "ramal",
            Label = "Ramal",
            ScopeType = CustomFieldScopeType.Department,
            DepartmentId = department.Id,
            DataType = CustomFieldDataType.Text,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        var ticketDefinition = new CustomFieldDefinition
        {
            Id = Guid.NewGuid(),
            Name = "observacao",
            Label = "Observação",
            ScopeType = CustomFieldScopeType.Ticket,
            DataType = CustomFieldDataType.Text,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.AddRange(department, ticket, departmentDefinition, ticketDefinition);
        db.CustomFieldValues.Add(new CustomFieldValue
        {
            Id = Guid.NewGuid(),
            DefinitionId = departmentDefinition.Id,
            ScopeType = CustomFieldScopeType.Ticket,
            EntityId = ticket.Id,
            EntityKey = ticket.Id.ToString("D"),
            ValueJson = "\"1024\"",
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();

        var values = await fixture.Service.GetValuesAsync(CustomFieldScopeType.Ticket, ticket.Id);

        var departmentValue = values.SingleOrDefault(item => item.DefinitionId == departmentDefinition.Id);
        Assert.Multiple(() =>
        {
            Assert.That(departmentValue, Is.Not.Null, "campo do departamento deve aparecer no chamado");
            Assert.That(departmentValue!.ScopeType, Is.EqualTo(CustomFieldScopeType.Department));
            Assert.That(departmentValue.ValueJson, Is.EqualTo("\"1024\""));
            Assert.That(values.Select(item => item.DefinitionId), Contains.Item(ticketDefinition.Id));
        });
    }


    // ── Capacidades de automação (allowRuntimeRead / allowAgentWrite / runtimeAccessMode / vínculos) ──

    [Test]
    public async Task CreateCustomFieldCommand_ShouldPersistAutomationCapabilitiesAndBindings()
    {
        await using var fixture = await CreateFixtureAsync();
        var taskId = Guid.NewGuid();
        var handler = new CreateCustomFieldCommandHandler(fixture.Service, fixture.Db);

        var result = await handler.Handle(
            new CreateCustomFieldCommand(
                "anydesk",
                "ID do anydesk",
                "ID usado no acesso remoto",
                CustomFieldScopeType.Agent,
                CustomFieldDataType.Text,
                IsRequired: false,
                IsSecret: false,
                OptionsJson: null,
                ValidationRegex: null,
                DepartmentId: null,
                UpdatedBy: "tester",
                AllowRuntimeRead: true,
                AllowAgentWrite: true,
                RuntimeAccessMode: CustomFieldRuntimeAccessMode.RestrictedTaskScript,
                AccessBindings: [new CustomFieldAccessBindingCommand(taskId, null, CanRead: true, CanWrite: true)]),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True, string.Join("; ", result.Errors.Select(e => e.Message)));
        var dto = result.Value!;

        var storedAccess = await fixture.Db.CustomFieldExecutionAccesses.AsNoTracking().ToListAsync();
        var storedDefinition = await fixture.Db.CustomFieldDefinitions.AsNoTracking().SingleAsync(d => d.Id == dto.Id);

        Assert.Multiple(() =>
        {
            Assert.That(dto.AllowRuntimeRead, Is.True);
            Assert.That(dto.AllowAgentWrite, Is.True);
            Assert.That(dto.RuntimeAccessMode, Is.EqualTo(CustomFieldRuntimeAccessMode.RestrictedTaskScript));
            Assert.That(dto.AccessBindings, Has.Count.EqualTo(1));
            Assert.That(dto.AccessBindings![0].TaskId, Is.EqualTo(taskId));
            Assert.That(dto.AccessBindings![0].CanWrite, Is.True);
            Assert.That(storedDefinition.AllowRuntimeRead, Is.True);
            Assert.That(storedDefinition.AllowAgentWrite, Is.True);
            Assert.That(storedDefinition.RuntimeAccessMode, Is.EqualTo(CustomFieldRuntimeAccessMode.RestrictedTaskScript));
            Assert.That(storedAccess, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task UpdateCustomFieldCommand_ShouldPreserveAutomationWhenFlagsAreOmitted()
    {
        await using var fixture = await CreateFixtureAsync();
        var taskId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var definition = new CustomFieldDefinition
        {
            Id = Guid.NewGuid(),
            Name = "anydesk",
            Label = "ID antigo",
            ScopeType = CustomFieldScopeType.Agent,
            DataType = CustomFieldDataType.Dropdown,
            IsActive = true,
            OptionsJson = JsonSerializer.Serialize(new[] { "a", "b" }),
            MinLength = 3,
            MaxLength = 20,
            AllowRuntimeRead = true,
            AllowAgentWrite = true,
            RuntimeAccessMode = CustomFieldRuntimeAccessMode.RestrictedTaskScript,
            CreatedAt = now,
            UpdatedAt = now
        };
        fixture.Db.CustomFieldDefinitions.Add(definition);
        fixture.Db.CustomFieldExecutionAccesses.Add(new CustomFieldExecutionAccess
        {
            Id = Guid.NewGuid(),
            DefinitionId = definition.Id,
            TaskId = taskId,
            CanRead = true,
            CanWrite = true,
            CreatedAt = now,
            UpdatedAt = now
        });
        await fixture.Db.SaveChangesAsync();

        var handler = new UpdateCustomFieldCommandHandler(fixture.Service, fixture.Db);
        var result = await handler.Handle(
            new UpdateCustomFieldCommand(
                definition.Id,
                Name: null,
                Label: "ID do anydesk",
                Description: null,
                IsRequired: null,
                IsSecret: null,
                OptionsJson: null,
                ValidationRegex: null,
                DepartmentId: null,
                IsActive: null,
                UpdatedBy: "tester"),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True, string.Join("; ", result.Errors.Select(e => e.Message)));
        var dto = result.Value!;

        var stored = await fixture.Db.CustomFieldDefinitions.AsNoTracking().SingleAsync(d => d.Id == definition.Id);
        var storedAccess = await fixture.Db.CustomFieldExecutionAccesses.AsNoTracking().ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(stored.Label, Is.EqualTo("ID do anydesk"), "o título deve ser atualizado");
            Assert.That(stored.AllowRuntimeRead, Is.True, "editar o título não pode zerar allowRuntimeRead");
            Assert.That(stored.AllowAgentWrite, Is.True, "editar o título não pode zerar allowAgentWrite");
            Assert.That(stored.RuntimeAccessMode, Is.EqualTo(CustomFieldRuntimeAccessMode.RestrictedTaskScript));
            Assert.That(storedAccess, Has.Count.EqualTo(1), "os vínculos não podem ser apagados");
            Assert.That(stored.MinLength, Is.EqualTo(3), "minLength não pode ser zerado");
            Assert.That(stored.MaxLength, Is.EqualTo(20), "maxLength não pode ser zerado");
            Assert.That(stored.OptionsJson, Is.Not.Null, "as opções não podem ser apagadas");
            Assert.That(dto.AccessBindings, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task UpdateCustomFieldCommand_ShouldApplyAndClearExplicitBindings()
    {
        await using var fixture = await CreateFixtureAsync();
        var now = DateTime.UtcNow;
        var definition = new CustomFieldDefinition
        {
            Id = Guid.NewGuid(),
            Name = "ativo_agente",
            Label = "AtivoAgenteTexto",
            ScopeType = CustomFieldScopeType.Agent,
            DataType = CustomFieldDataType.Boolean,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        fixture.Db.CustomFieldDefinitions.Add(definition);
        await fixture.Db.SaveChangesAsync();

        var handler = new UpdateCustomFieldCommandHandler(fixture.Service, fixture.Db);
        var scriptId = Guid.NewGuid();

        var applied = await handler.Handle(
            new UpdateCustomFieldCommand(
                definition.Id, null, null, null, null, null, null, null, null, null, "tester",
                AllowRuntimeRead: true,
                AllowAgentWrite: false,
                RuntimeAccessMode: CustomFieldRuntimeAccessMode.RestrictedTaskScript,
                AccessBindings: [new CustomFieldAccessBindingCommand(null, scriptId, CanRead: true, CanWrite: false)]),
            CancellationToken.None);

        Assert.That(applied.IsSuccess, Is.True, string.Join("; ", applied.Errors.Select(e => e.Message)));
        Assert.That(applied.Value!.AccessBindings, Has.Count.EqualTo(1));
        Assert.That(applied.Value!.AccessBindings![0].ScriptId, Is.EqualTo(scriptId));

        var cleared = await handler.Handle(
            new UpdateCustomFieldCommand(
                definition.Id, null, null, null, null, null, null, null, null, null, "tester",
                RuntimeAccessMode: CustomFieldRuntimeAccessMode.Public,
                AccessBindings: []),
            CancellationToken.None);

        Assert.That(cleared.IsSuccess, Is.True, string.Join("; ", cleared.Errors.Select(e => e.Message)));
        var storedAccess = await fixture.Db.CustomFieldExecutionAccesses.AsNoTracking().ToListAsync();
        var stored = await fixture.Db.CustomFieldDefinitions.AsNoTracking().SingleAsync(d => d.Id == definition.Id);

        Assert.Multiple(() =>
        {
            Assert.That(storedAccess, Is.Empty, "lista vazia deve remover os vínculos");
            Assert.That(stored.RuntimeAccessMode, Is.EqualTo(CustomFieldRuntimeAccessMode.Public));
            Assert.That(stored.AllowRuntimeRead, Is.True);
            Assert.That(cleared.Value!.AccessBindings, Is.Empty);
        });
    }

    [Test]
    public async Task ListCustomFieldsQuery_ShouldFilterByExecutionBinding()
    {
        await using var fixture = await CreateFixtureAsync();
        var now = DateTime.UtcNow;
        var taskId = Guid.NewGuid();
        var scriptId = Guid.NewGuid();
        var boundToTask = new CustomFieldDefinition
        {
            Id = Guid.NewGuid(),
            Name = "anydesk",
            Label = "ID do anydesk",
            ScopeType = CustomFieldScopeType.Agent,
            DataType = CustomFieldDataType.Text,
            IsActive = true,
            AllowRuntimeRead = true,
            RuntimeAccessMode = CustomFieldRuntimeAccessMode.RestrictedTaskScript,
            CreatedAt = now,
            UpdatedAt = now
        };
        var boundToScript = new CustomFieldDefinition
        {
            Id = Guid.NewGuid(),
            Name = "ativo_agente",
            Label = "AtivoAgenteTexto",
            ScopeType = CustomFieldScopeType.Agent,
            DataType = CustomFieldDataType.Boolean,
            IsActive = true,
            AllowRuntimeRead = true,
            RuntimeAccessMode = CustomFieldRuntimeAccessMode.RestrictedTaskScript,
            CreatedAt = now,
            UpdatedAt = now
        };
        var unbound = new CustomFieldDefinition
        {
            Id = Guid.NewGuid(),
            Name = "sem_vinculo",
            Label = "Sem vínculo",
            ScopeType = CustomFieldScopeType.Agent,
            DataType = CustomFieldDataType.Text,
            IsActive = true,
            AllowRuntimeRead = true,
            RuntimeAccessMode = CustomFieldRuntimeAccessMode.Public,
            CreatedAt = now,
            UpdatedAt = now
        };
        fixture.Db.CustomFieldDefinitions.AddRange(boundToTask, boundToScript, unbound);
        fixture.Db.CustomFieldExecutionAccesses.AddRange(
            new CustomFieldExecutionAccess
            {
                Id = Guid.NewGuid(), DefinitionId = boundToTask.Id, TaskId = taskId,
                CanRead = true, CanWrite = true, CreatedAt = now, UpdatedAt = now
            },
            new CustomFieldExecutionAccess
            {
                Id = Guid.NewGuid(), DefinitionId = boundToScript.Id, ScriptId = scriptId,
                CanRead = true, CanWrite = false, CreatedAt = now, UpdatedAt = now
            });
        await fixture.Db.SaveChangesAsync();

        var handler = new ListCustomFieldsQueryHandler(fixture.Service, fixture.Db);

        var byTask = await handler.Handle(
            new ListCustomFieldsQuery(CustomFieldScopeType.Agent, false, taskId, null), CancellationToken.None);
        var byScript = await handler.Handle(
            new ListCustomFieldsQuery(CustomFieldScopeType.Agent, false, null, scriptId), CancellationToken.None);
        var all = await handler.Handle(
            new ListCustomFieldsQuery(CustomFieldScopeType.Agent, false, null, null), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(byTask.Value!.Select(d => d.Name), Is.EqualTo(new[] { "anydesk" }));
            Assert.That(byTask.Value![0].AccessBindings, Has.Count.EqualTo(1));
            Assert.That(byScript.Value!.Select(d => d.Name), Is.EqualTo(new[] { "ativo_agente" }));
            Assert.That(byScript.Value![0].AccessBindings![0].CanWrite, Is.False);
            Assert.That(all.Value!.Select(d => d.Name), Does.Contain("sem_vinculo"));
            Assert.That(all.Value!.Single(d => d.Name == "anydesk").AccessBindings, Has.Count.EqualTo(1));
        });
    }


    // ── Visibilidade no portal (allow_web_read / allow_web_edit / is_pinned) ──

    [Test]
    public async Task CreateCustomFieldCommand_ShouldRejectWebEditWithoutWebRead()
    {
        await using var fixture = await CreateFixtureAsync();
        var handler = new CreateCustomFieldCommandHandler(fixture.Service, fixture.Db);

        var result = await handler.Handle(
            new CreateCustomFieldCommand(
                "invisivel", "Invisível", null,
                CustomFieldScopeType.Agent, CustomFieldDataType.Text,
                IsRequired: false, IsSecret: false, OptionsJson: null, ValidationRegex: null,
                DepartmentId: null, UpdatedBy: "tester",
                AllowWebRead: false, AllowWebEdit: true),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(
                result.Errors.Select(e => e.Message),
                Has.Some.Contains("allowWebEdit"),
                "a mensagem deve explicar que editar exige ver");
        });
    }

    [Test]
    public async Task GetAgentCustomFieldSchema_ShouldHideNonWebReadFieldsAndMaskSecrets()
    {
        await using var fixture = await CreateFixtureAsync();
        var now = DateTime.UtcNow;
        var visible = new CustomFieldDefinition
        {
            Id = Guid.NewGuid(),
            Name = "anydesk",
            Label = "ID do anydesk",
            ScopeType = CustomFieldScopeType.Agent,
            DataType = CustomFieldDataType.Text,
            IsActive = true,
            AllowWebRead = true,
            AllowWebEdit = true,
            IsPinned = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        var hidden = new CustomFieldDefinition
        {
            Id = Guid.NewGuid(),
            Name = "token_interno",
            Label = "Token interno",
            ScopeType = CustomFieldScopeType.Agent,
            DataType = CustomFieldDataType.Text,
            IsActive = true,
            AllowRuntimeRead = true,
            AllowWebRead = false,
            AllowWebEdit = false,
            CreatedAt = now,
            UpdatedAt = now
        };
        var secret = new CustomFieldDefinition
        {
            Id = Guid.NewGuid(),
            Name = "api_key",
            Label = "API Key",
            ScopeType = CustomFieldScopeType.Agent,
            DataType = CustomFieldDataType.Text,
            IsActive = true,
            IsSecret = true,
            AllowWebRead = true,
            AllowWebEdit = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        fixture.Db.CustomFieldDefinitions.AddRange(visible, hidden, secret);
        fixture.Db.CustomFieldValues.AddRange(
            new CustomFieldValue
            {
                Id = Guid.NewGuid(), DefinitionId = visible.Id, ScopeType = CustomFieldScopeType.Agent,
                EntityId = fixture.Agent.Id, EntityKey = fixture.Agent.Id.ToString("D"),
                ValueJson = JsonSerializer.Serialize("123 456 789"), UpdatedBy = "agent",
                CreatedAt = now, UpdatedAt = now
            },
            new CustomFieldValue
            {
                Id = Guid.NewGuid(), DefinitionId = secret.Id, ScopeType = CustomFieldScopeType.Agent,
                EntityId = fixture.Agent.Id, EntityKey = fixture.Agent.Id.ToString("D"),
                ValueJson = JsonSerializer.Serialize("super-secreto"), UpdatedBy = "tester",
                CreatedAt = now, UpdatedAt = now
            });
        await fixture.Db.SaveChangesAsync();

        var handler = new GetAgentCustomFieldSchemaQueryHandler(new TestAgentRepository(fixture.Db), fixture.Service);
        var result = await handler.Handle(new GetAgentCustomFieldSchemaQuery(fixture.Agent.Id), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True, string.Join("; ", result.Errors.Select(e => e.Message)));
        var items = result.Value!;

        Assert.Multiple(() =>
        {
            Assert.That(items.Select(item => item.Name), Is.EquivalentTo(new[] { "anydesk", "api_key" }),
                "campo sem webRead não pode aparecer no card");
            var anydesk = items.Single(item => item.Name == "anydesk");
            Assert.That(anydesk.IsPinned, Is.True);
            Assert.That(anydesk.AllowWebEdit, Is.True);
            Assert.That(anydesk.ValueUpdatedBy, Is.EqualTo("agent"));
            Assert.That(JsonDocument.Parse(anydesk.ValueJson).RootElement.GetString(), Is.EqualTo("123 456 789"));

            var apiKey = items.Single(item => item.Name == "api_key");
            Assert.That(apiKey.IsSecret, Is.True);
            Assert.That(JsonDocument.Parse(apiKey.ValueJson).RootElement.GetString(), Is.EqualTo("***"),
                "segredo deve voltar mascarado por padrão");
        });
    }


    [Test]
    public async Task CreateCustomFieldCommand_ShouldPersistAndReturnOptionsFromList()
    {
        await using var fixture = await CreateFixtureAsync();
        var handler = new CreateCustomFieldCommandHandler(fixture.Service, fixture.Db);

        var created = await handler.Handle(
            new CreateCustomFieldCommand(
                "ambiente",
                "Ambiente",
                null,
                CustomFieldScopeType.Agent,
                CustomFieldDataType.Dropdown,
                IsRequired: false,
                IsSecret: false,
                OptionsJson: null,
                ValidationRegex: null,
                DepartmentId: null,
                UpdatedBy: "tester",
                Options: ["prod", "homolog"]),
            CancellationToken.None);

        Assert.That(created.IsSuccess, Is.True, string.Join("; ", created.Errors.Select(e => e.Message)));
        Assert.That(created.Value!.Options, Is.EquivalentTo(new[] { "prod", "homolog" }),
            "a lista de opções enviada pela tela precisa ser persistida");

        // Editar sem enviar Options preserva as opções atuais (patch).
        var updateHandler = new UpdateCustomFieldCommandHandler(fixture.Service, fixture.Db);
        var updated = await updateHandler.Handle(
            new UpdateCustomFieldCommand(
                created.Value!.Id, null, "Ambiente 2", null, null, null, null, null, null, null, "tester"),
            CancellationToken.None);

        Assert.That(updated.IsSuccess, Is.True, string.Join("; ", updated.Errors.Select(e => e.Message)));
        Assert.That(updated.Value!.Options, Is.EquivalentTo(new[] { "prod", "homolog" }));
        Assert.That(updated.Value!.Label, Is.EqualTo("Ambiente 2"));
    }


    // ── Valor padrão ─────────────────────────────────────────────────────

    [Test]
    public async Task DefaultValue_ShouldFeedPortalAndRuntimeAndBeClearable()
    {
        await using var fixture = await CreateFixtureAsync();
        var now = DateTime.UtcNow;
        var createHandler = new CreateCustomFieldCommandHandler(fixture.Service, fixture.Db);

        var created = await createHandler.Handle(
            new CreateCustomFieldCommand(
                "ambiente", "Ambiente", null,
                CustomFieldScopeType.Agent, CustomFieldDataType.Text,
                IsRequired: false, IsSecret: false, OptionsJson: null, ValidationRegex: null,
                DepartmentId: null, UpdatedBy: "tester",
                AllowRuntimeRead: true,
                RuntimeAccessMode: CustomFieldRuntimeAccessMode.Public,
                DefaultValueJson: JsonSerializer.Serialize("prod")),
            CancellationToken.None);

        Assert.That(created.IsSuccess, Is.True, string.Join("; ", created.Errors.Select(e => e.Message)));
        Assert.That(created.Value!.DefaultValueJson, Is.EqualTo(JsonSerializer.Serialize("prod")));

        // Sem valor gravado, portal e runtime entregam o padrão.
        var values = await fixture.Service.GetValuesAsync(CustomFieldScopeType.Agent, fixture.Agent.Id);
        var runtime = await fixture.Service.GetRuntimeValuesForAgentAsync(fixture.Agent.Id, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(
                JsonDocument.Parse(values.Single(v => v.DefinitionId == created.Value!.Id).ValueJson).RootElement.GetString(),
                Is.EqualTo("prod"));
            Assert.That(
                JsonDocument.Parse(runtime.Single(f => f.Name == "ambiente").ValueJson).RootElement.GetString(),
                Is.EqualTo("prod"));
        });

        // Segredo com padrão não expõe o padrão em listagem sem permissão de ver.
        var secret = new CustomFieldDefinition
        {
            Id = Guid.NewGuid(), Name = "token_padrao", Label = "Token padrão",
            ScopeType = CustomFieldScopeType.Agent, DataType = CustomFieldDataType.Text,
            IsActive = true, IsSecret = true, AllowRuntimeRead = true,
            DefaultValueJson = JsonSerializer.Serialize("nao-expor"),
            CreatedAt = now, UpdatedAt = now
        };
        fixture.Db.CustomFieldDefinitions.Add(secret);
        await fixture.Db.SaveChangesAsync();

        var masked = await fixture.Service.GetValuesAsync(CustomFieldScopeType.Agent, fixture.Agent.Id, includeSecrets: false);
        Assert.That(
            JsonDocument.Parse(masked.Single(v => v.DefinitionId == secret.Id).ValueJson).RootElement.ValueKind,
            Is.EqualTo(JsonValueKind.Null), "segredo sem valor não pode devolver o padrão em claro");

        // String vazia limpa o padrão (patch).
        var updateHandler = new UpdateCustomFieldCommandHandler(fixture.Service, fixture.Db);
        var cleared = await updateHandler.Handle(
            new UpdateCustomFieldCommand(
                created.Value!.Id, null, null, null, null, null, null, null, null, null, "tester",
                DefaultValueJson: ""),
            CancellationToken.None);

        Assert.That(cleared.IsSuccess, Is.True, string.Join("; ", cleared.Errors.Select(e => e.Message)));
        Assert.That(cleared.Value!.DefaultValueJson, Is.Null);
    }

    private static async Task<CustomFieldFixture> CreateFixtureAsync()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"custom-field-tests-{Guid.NewGuid():N}")
            .Options;

        var db = new TestDiscoveryDbContext(options);

        var now = DateTime.UtcNow;
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Name = "Client 01",
            CreatedAt = now,
            UpdatedAt = now
        };
        var site = new Site
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Name = "Site 01",
            CreatedAt = now,
            UpdatedAt = now
        };
        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            SiteId = site.Id,
            Hostname = "agent-01",
            Status = AgentStatus.Online,
            AgentVersion = "1.0.0",
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Clients.Add(client);
        db.Sites.Add(site);
        db.Agents.Add(agent);
        await db.SaveChangesAsync();

        var service = new CustomFieldService(
            db,
            new TestAgentRepository(db),
            new TestSiteRepository(db),
            new TestLogRepository(),
            NullLogger<CustomFieldService>.Instance);

        return new CustomFieldFixture(db, client, site, agent, service);
    }

    private sealed class CustomFieldFixture : IAsyncDisposable
    {
        public CustomFieldFixture(DiscoveryDbContext db, Client client, Site site, Agent agent, CustomFieldService service)
        {
            Db = db;
            Client = client;
            Site = site;
            Agent = agent;
            Service = service;
        }

        public DiscoveryDbContext Db { get; }
        public Client Client { get; }
        public Site Site { get; }
        public Agent Agent { get; }
        public CustomFieldService Service { get; }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
        }
    }

    private sealed class TestDiscoveryDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowedTypes = new HashSet<Type>
            {
                typeof(Client),
                typeof(Site),
                typeof(Agent),
                typeof(CustomFieldDefinition),
                typeof(CustomFieldValue),
                typeof(CustomFieldExecutionAccess),
                // GetValuesAsync(Ticket) consulta o departamento do chamado para
                // incluir os campos do departamento.
                typeof(Ticket),
                typeof(Department)
            };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null && type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowedTypes.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Client>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Name).IsRequired();
            });

            modelBuilder.Entity<Site>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Name).IsRequired();
            });

            modelBuilder.Entity<Agent>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Hostname).IsRequired();
            });

            modelBuilder.Entity<CustomFieldDefinition>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Name).IsRequired();
                entity.Property(item => item.Label).IsRequired();
            });

            modelBuilder.Entity<CustomFieldValue>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.Property(item => item.EntityKey).IsRequired();
                entity.Property(item => item.ValueJson).IsRequired();
            });

            modelBuilder.Entity<CustomFieldExecutionAccess>(entity =>
            {
                entity.HasKey(item => item.Id);
            });

            modelBuilder.Entity<Ticket>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<Department>(entity => entity.HasKey(item => item.Id));
        }
    }

    private sealed class TestAgentRepository(DiscoveryDbContext db) : IAgentRepository
    {
        public Task<Agent?> GetByIdAsync(Guid id)
            => db.Agents.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id);

        public async Task<IReadOnlyList<Agent>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
        {
            var idList = ids.ToList();
            return await db.Agents.AsNoTracking()
                .Where(item => idList.Contains(item.Id))
                .ToListAsync(ct);
        }

        public async Task<IEnumerable<Agent>> GetAllAsync()
            => await db.Agents.AsNoTracking().ToListAsync();

        public async Task<IEnumerable<Agent>> GetBySiteIdAsync(Guid siteId)
            => await db.Agents.AsNoTracking().Where(item => item.SiteId == siteId).ToListAsync();

        public async Task<IEnumerable<Agent>> GetByClientIdAsync(Guid clientId)
        {
            return await (
                from agent in db.Agents.AsNoTracking()
                join site in db.Sites.AsNoTracking() on agent.SiteId equals site.Id
                where site.ClientId == clientId
                select agent).ToListAsync();
        }

        public Task<Agent> CreateAsync(Agent agent) => throw new NotSupportedException();
        public Task UpdateAsync(Agent agent) => throw new NotSupportedException();
        public Task UpdateStatusAsync(Guid id, AgentStatus status, string? ipAddress) => throw new NotSupportedException();
        public Task ApproveZeroTouchAsync(Guid agentId) => throw new NotSupportedException();
        public Task SetPolicySyncAsync(Guid id, string fingerprint, DateTime syncedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetMaintenanceAsync(Guid id, bool enabled, string? reason, Guid changedByUserId) => throw new NotSupportedException();
        public Task TransferSiteAsync(Guid agentId, Guid newSiteId) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id) => throw new NotSupportedException();
        public Task<IReadOnlyList<Agent>> GetOnlineAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Agent>> FindByFingerprintAsync(string fingerprintHash, Guid clientId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class TestSiteRepository(DiscoveryDbContext db) : ISiteRepository
    {
        public Task<Site?> GetByIdAsync(Guid id)
            => db.Sites.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id);

        public async Task<IEnumerable<Site>> GetByClientIdAsync(Guid clientId, bool includeInactive = false)
        {
            var query = db.Sites.AsNoTracking().Where(item => item.ClientId == clientId);
            if (!includeInactive)
                query = query.Where(item => item.IsActive);

            return await query.ToListAsync();
        }

        public async Task<IEnumerable<Site>> GetByClientIdsAsync(IEnumerable<Guid> clientIds, bool includeInactive = false)
        {
            var ids = clientIds.ToArray();
            var query = db.Sites.AsNoTracking().Where(item => ids.Contains(item.ClientId));
            if (!includeInactive)
                query = query.Where(item => item.IsActive);

            return await query.ToListAsync();
        }

        public Task<IEnumerable<Site>> GetAllAsync(bool includeInactive = false)
        {
            var query = db.Sites.AsNoTracking();
            if (!includeInactive)
                query = query.Where(item => item.IsActive);

            return Task.FromResult<IEnumerable<Site>>(query.ToList());
        }

        public Task<IEnumerable<Site>> GetByIdsAsync(IEnumerable<Guid> siteIds, bool includeInactive = false)
        {
            var ids = siteIds.ToArray();
            var query = db.Sites.AsNoTracking().Where(item => ids.Contains(item.Id));
            if (!includeInactive)
                query = query.Where(item => item.IsActive);

            return Task.FromResult<IEnumerable<Site>>(query.ToList());
        }

        public Task<Site> CreateAsync(Site site) => throw new NotSupportedException();
        public Task UpdateAsync(Site site) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id) => throw new NotSupportedException();
    }

    private sealed class TestAgentAutoLabelingService : IAgentAutoLabelingService
    {
        public Task EvaluateAgentAsync(Guid agentId, string reason, string? actor = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EvaluateAgentsAsync(IReadOnlyCollection<Guid> agentIds, string reason, string? actor = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> HasEnabledRulesAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task ReprocessAllAgentsAsync(string reason, int batchSize = 200, string? actor = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReprocessAllAgentsAsync(string reason, int batchSize, IProgress<AgentLabelReprocessProgress>? progress, string? actor = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReprocessChangedAgentsAsync(string reason, int batchSize = 200, IProgress<AgentLabelReprocessProgress>? progress = null, string? actor = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AgentLabelRuleDryRunResponse> DryRunAsync(AgentLabelRuleDryRunRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentLabelRuleDryRunResponse>> DryRunBatchAsync(AgentLabelRuleDryRunBatchRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentLabelRuleImpactResponse> EvaluateImpactAsync(AgentLabelRuleImpactRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TestLogRepository : ILogRepository
    {
        public Task<LogEntry> CreateAsync(LogEntry entry)
            => Task.FromResult(entry);

        public Task<IEnumerable<LogEntry>> QueryAsync(LogQuery query)
            => Task.FromResult<IEnumerable<LogEntry>>([]);

        public Task<IReadOnlyList<LogEntry>> QueryPageAsync(LogQuery query)
            => Task.FromResult<IReadOnlyList<LogEntry>>([]);

        public Task<LogSummaryRawDto> GetSummaryAsync(LogQuery query)
            => Task.FromResult(new LogSummaryRawDto(0, [], [], [], [], [], []));

        public Task<int> PurgeAsync(DateTime cutoff)
            => Task.FromResult(0);
    }
}