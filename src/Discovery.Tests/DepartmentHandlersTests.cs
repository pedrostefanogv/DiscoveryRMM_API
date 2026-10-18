using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Departments.Commands;
using Discovery.Core.Cqrs.Departments.Queries;
using Discovery.Core.Departments;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Departments;

namespace Discovery.Tests;

/// <summary>
/// Handlers de departamento: D5 (limpar herança), D6 (bloquear delete com
/// filhos), D11 (activeOnly) e endpoint /effective com origem por campo.
/// </summary>
public class DepartmentHandlersTests
{
    private static Department NewDepartment(
        Guid? clientId = null, Guid? parentId = null, bool active = true)
        => new()
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            Name = "Depto",
            IsActive = active,
            InheritFromGlobalId = parentId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    [Test]
    public async Task Update_com_Set_null_desfaz_a_heranca()
    {
        var parent = NewDepartment();
        var dept = NewDepartment(clientId: Guid.NewGuid(), parentId: parent.Id);
        var service = new FakeDepartmentService(dept, parent);
        var handler = new UpdateDepartmentCommandHandler(service);

        var result = await handler.Handle(
            new UpdateDepartmentCommand(
                dept.Id, null, null, OptionalGuid.Set(null), null, null),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(service.Updated!.InheritFromGlobalId, Is.Null);
    }

    [Test]
    public async Task Update_sem_o_campo_preserva_a_heranca()
    {
        var parent = NewDepartment();
        var dept = NewDepartment(clientId: Guid.NewGuid(), parentId: parent.Id);
        var service = new FakeDepartmentService(dept, parent);
        var handler = new UpdateDepartmentCommandHandler(service);

        var result = await handler.Handle(
            new UpdateDepartmentCommand(
                dept.Id, "Novo nome", null, OptionalGuid.Unset, null, null),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(service.Updated!.InheritFromGlobalId, Is.EqualTo(parent.Id));
    }

    [Test]
    public async Task Update_do_vinculo_registra_auditoria_com_antes_e_depois()
    {
        // B4 — auditoria: alteração do vínculo de herança grava antes/depois.
        var parent = NewDepartment();
        var dept = NewDepartment(clientId: Guid.NewGuid());
        var service = new FakeDepartmentService(dept, parent);
        var audit = new FakeConfigurationAudit();
        var handler = new UpdateDepartmentCommandHandler(service, audit);

        var result = await handler.Handle(
            new UpdateDepartmentCommand(
                dept.Id, null, null, OptionalGuid.Set(parent.Id), null, null),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(audit.Changes, Has.Count.EqualTo(1));
        Assert.That(audit.Changes[0].EntityType, Is.EqualTo("Department"));
        Assert.That(audit.Changes[0].Field, Is.EqualTo("InheritFromGlobalId"));
        Assert.That(audit.Changes[0].OldValue, Is.Null);
        Assert.That(audit.Changes[0].NewValue, Is.EqualTo(parent.Id.ToString()));
    }

    [Test]
    public async Task Delete_bloqueado_quando_ha_filho_ativo()
    {
        var dept = NewDepartment();
        var service = new FakeDepartmentService(dept) { HasActiveChildren = true };
        var handler = new DeleteDepartmentCommandHandler(service);

        var result = await handler.Handle(new DeleteDepartmentCommand(dept.Id), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
        Assert.That(service.Deleted, Is.False);
    }

    [Test]
    public async Task Delete_permitido_sem_filho_ativo()
    {
        var dept = NewDepartment();
        var service = new FakeDepartmentService(dept);
        var handler = new DeleteDepartmentCommandHandler(service);

        var result = await handler.Handle(new DeleteDepartmentCommand(dept.Id), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(service.Deleted, Is.True);
    }

    [Test]
    public async Task List_respeita_activeOnly()
    {
        var active = NewDepartment();
        var inactive = NewDepartment(active: false);
        var service = new FakeDepartmentService(active, inactive);
        var handler = new ListDepartmentsQueryHandler(service);

        var all = await handler.Handle(
            new ListDepartmentsQuery(null, IncludeGlobal: false), CancellationToken.None);
        var onlyActive = await handler.Handle(
            new ListDepartmentsQuery(null, IncludeGlobal: false, ActiveOnly: true), CancellationToken.None);

        Assert.That(all.Value!, Has.Count.EqualTo(2));
        Assert.That(onlyActive.Value!, Has.Count.EqualTo(1));
        Assert.That(onlyActive.Value![0].Id, Is.EqualTo(active.Id));
    }

    [Test]
    public async Task List_sem_clientId_devolve_so_globais_por_padrao()
    {
        // Comportamento histórico preservado: sem clientId e sem allClients a
        // listagem devolve apenas os globais (D11).
        var global = NewDepartment();
        var client = NewDepartment(clientId: Guid.NewGuid());
        var service = new FakeDepartmentService(global, client);
        var handler = new ListDepartmentsQueryHandler(service);

        var result = await handler.Handle(new ListDepartmentsQuery(null), CancellationToken.None);

        Assert.That(result.Value!.Select(d => d.Id), Is.EqualTo(new[] { global.Id }));
    }

    [Test]
    public async Task List_com_allClients_devolve_globais_e_clientes()
    {
        // Filtro de escopo "todos" do console: a API não tem clientId, então a
        // união precisa ser pedida explicitamente (D11 / B3).
        var global = NewDepartment();
        var clientA = NewDepartment(clientId: Guid.NewGuid());
        var clientB = NewDepartment(clientId: Guid.NewGuid());
        var service = new FakeDepartmentService(global, clientA, clientB);
        var handler = new ListDepartmentsQueryHandler(service);

        var result = await handler.Handle(
            new ListDepartmentsQuery(null, AllClients: true), CancellationToken.None);

        Assert.That(result.Value!.Select(d => d.Id),
            Is.EquivalentTo(new[] { global.Id, clientA.Id, clientB.Id }));
    }

    [Test]
    public async Task Effective_retorna_origem_por_campo()
    {
        var parent = NewDepartment();
        parent.AiAssignmentModeOverride = 1;
        parent.AiAssignmentMinConfidenceOverride = 0.90;
        parent.AiAssignmentInstructionsOverride = "Herdado";

        var dept = NewDepartment(clientId: Guid.NewGuid(), parentId: parent.Id);
        dept.AiAssignmentMinConfidenceOverride = 0.40; // próprio vence
        dept.AiAssignmentMaxCandidatesOverride = null;  // herda do global

        var service = new FakeDepartmentService(dept, parent);
        var handler = new GetDepartmentEffectiveConfigQueryHandler(service);

        var result = await handler.Handle(
            new GetDepartmentEffectiveConfigQuery(dept.Id), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        var dto = result.Value!;

        Assert.That(dto.AiAssignmentMode.Value, Is.EqualTo(1));
        Assert.That(dto.AiAssignmentMode.Origin, Is.EqualTo("inherited-global"));
        Assert.That(dto.AiAssignmentMode.SourceDepartmentId, Is.EqualTo(parent.Id));

        Assert.That(dto.AiAssignmentMinConfidence.Value, Is.EqualTo(0.40));
        Assert.That(dto.AiAssignmentMinConfidence.Origin, Is.EqualTo("own"));

        Assert.That(dto.AiAssignmentInstructions.Value, Is.EqualTo("Herdado"));
        Assert.That(dto.AiAssignmentInstructions.Origin, Is.EqualTo("inherited-global"));

        // Sem override no próprio: usa o valor-base do global pai.
        Assert.That(dto.AiAssignmentMaxOutputTokens.Value, Is.EqualTo(1200));
        Assert.That(dto.AiAssignmentMaxOutputTokens.Origin, Is.EqualTo("inherited-global"));
    }

    [Test]
    public async Task Effective_ignora_pai_inativo()
    {
        var parent = NewDepartment(active: false);
        parent.AiAssignmentModeOverride = 1;
        var dept = NewDepartment(clientId: Guid.NewGuid(), parentId: parent.Id);
        dept.AiAssignmentMode = 0;

        var service = new FakeDepartmentService(dept, parent);
        var handler = new GetDepartmentEffectiveConfigQueryHandler(service);

        var result = await handler.Handle(
            new GetDepartmentEffectiveConfigQuery(dept.Id), CancellationToken.None);

        Assert.That(result.Value!.AiAssignmentMode.Value, Is.EqualTo(0));
        Assert.That(result.Value!.AiAssignmentMode.Origin, Is.EqualTo("default"));
    }

    [Test]
    public async Task Effective_inclui_config_origins_team_e_warnings()
    {
        var parent = NewDepartment();
        parent.Name = "Global Suporte";
        parent.AiAssignmentModeOverride = 1;
        parent.AiSkillLearningModeOverride = 2;

        var dept = NewDepartment(clientId: Guid.NewGuid(), parentId: parent.Id);
        dept.Name = "Depto Cliente";
        dept.AiAssignmentMinConfidenceOverride = 0.40;

        var ownUser = Guid.NewGuid();
        var inheritedUser = Guid.NewGuid();
        var team = new FakeTeamResolver(
        [
            new DepartmentTeamMember(ownUser, "Ana", null, 1, null, 1m, true, DateTime.UtcNow, "own"),
            new DepartmentTeamMember(inheritedUser, "Bia", null, 1, null, 1m, true,
                DateTime.UtcNow, $"inherited:{parent.Id}")
        ]);

        var handler = new GetDepartmentEffectiveConfigQueryHandler(
            new FakeDepartmentService(dept, parent), team);

        var result = await handler.Handle(
            new GetDepartmentEffectiveConfigQuery(dept.Id), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        var dto = result.Value!;

        // Shape novo: 17 valores + 17 origens em camelCase (B3 adiciona a
        // estratégia de auto-atribuição ao conjunto herdável).
        Assert.That(dto.Config, Has.Count.EqualTo(17));
        Assert.That(dto.Origins, Has.Count.EqualTo(17));
        Assert.That(dto.Config["aiAssignmentMode"], Is.EqualTo(1));
        Assert.That(dto.Config["assignmentStrategy"], Is.EqualTo(0));
        Assert.That(dto.Origins["assignmentStrategy"], Is.EqualTo("inherited-global"));
        Assert.That(dto.Config["aiSkillLearningMode"], Is.EqualTo(2));
        Assert.That(dto.Config["aiWeightMax"], Is.EqualTo(0.50m));
        Assert.That(dto.Origins["aiAssignmentMode"], Is.EqualTo("inherited-global"));
        Assert.That(dto.Origins["aiAssignmentMinConfidence"], Is.EqualTo("own"));
        // Com pai global utilizável, campo sem override no filho é herdado.
        Assert.That(dto.Origins["aiWeightMax"], Is.EqualTo("inherited-global"));

        // Campos flat continuam (compatibilidade) e cobrem os 17.
        Assert.That(dto.AssignmentStrategy.Value, Is.EqualTo(0));
        Assert.That(dto.AssignmentStrategy.Origin, Is.EqualTo("inherited-global"));
        Assert.That(dto.AiAssignmentMode.Value, Is.EqualTo(1));
        Assert.That(dto.AiAssignmentMode.Origin, Is.EqualTo("inherited-global"));
        Assert.That(dto.AiSkillLearningMode.Value, Is.EqualTo(2));
        Assert.That(dto.AiSkillLearningMode.Origin, Is.EqualTo("inherited-global"));
        Assert.That(dto.AiWeightMaxDeltaPerCycle.Value, Is.EqualTo(0.10m));
        Assert.That(dto.AiWeightMax.Value, Is.EqualTo(0.50m));

        Assert.That(dto.InheritFromGlobalName, Is.EqualTo(parent.Name));
        Assert.That(dto.ResolvedAt.Kind, Is.EqualTo(DateTimeKind.Utc));
        Assert.That(dto.Warnings, Is.Empty);

        Assert.That(dto.Team, Has.Count.EqualTo(2));
        Assert.That(dto.Team[0].UserId, Is.EqualTo(ownUser));
        Assert.That(dto.Team[0].UserName, Is.EqualTo("Ana"));
        Assert.That(dto.Team[0].Inherited, Is.False);
        Assert.That(dto.Team[0].Origin, Is.EqualTo("own"));
        Assert.That(dto.Team[0].SourceDepartmentId, Is.EqualTo(dept.Id));
        Assert.That(dto.Team[0].SourceDepartmentName, Is.EqualTo(dept.Name));

        Assert.That(dto.Team[1].UserId, Is.EqualTo(inheritedUser));
        Assert.That(dto.Team[1].Inherited, Is.True);
        Assert.That(dto.Team[1].Origin, Is.EqualTo("inherited-global"));
        Assert.That(dto.Team[1].SourceDepartmentId, Is.EqualTo(parent.Id));
        Assert.That(dto.Team[1].SourceDepartmentName, Is.EqualTo(parent.Name));
    }

    [Test]
    public async Task Effective_emite_warning_de_pai_inativo()
    {
        var parent = NewDepartment(active: false);
        var dept = NewDepartment(clientId: Guid.NewGuid(), parentId: parent.Id);

        var handler = new GetDepartmentEffectiveConfigQueryHandler(
            new FakeDepartmentService(dept, parent));

        var result = await handler.Handle(
            new GetDepartmentEffectiveConfigQuery(dept.Id), CancellationToken.None);

        Assert.That(result.Value!.Warnings,
            Does.Contain(DepartmentInheritanceValidator.InactiveParentWarning));
        Assert.That(result.Value!.GlobalParentId, Is.Null);
        Assert.That(result.Value!.InheritFromGlobalName, Is.Null);
    }

    [Test]
    public async Task Effective_emite_warning_de_ciclo_legado()
    {
        // Dados legados: o "global" aponta de volta para o departamento de cliente.
        var dept = NewDepartment(clientId: Guid.NewGuid());
        var parent = NewDepartment();
        dept.InheritFromGlobalId = parent.Id;
        parent.InheritFromGlobalId = dept.Id;

        var handler = new GetDepartmentEffectiveConfigQueryHandler(
            new FakeDepartmentService(dept, parent));

        var result = await handler.Handle(
            new GetDepartmentEffectiveConfigQuery(dept.Id), CancellationToken.None);

        var dto = result.Value!;
        Assert.That(dto.Warnings, Does.Contain(DepartmentInheritanceValidator.CycleWarning));
        Assert.That(dto.GlobalParentId, Is.Null);
        Assert.That(dto.Origins["aiAssignmentMode"], Is.EqualTo("default"));
    }

    [Test]
    public async Task Update_aplica_os_16_nomes_base_como_override()
    {
        var dept = NewDepartment();
        var service = new FakeDepartmentService(dept);
        var handler = new UpdateDepartmentCommandHandler(service);

        var result = await handler.Handle(new UpdateDepartmentCommand(
            Id: dept.Id, Name: null, Description: null,
            InheritFromGlobalId: OptionalGuid.Unset, SortOrder: null, IsActive: null,
            AiAssignmentMode: OptionalValue<int>.Set(1),
            AiAssignmentMinConfidence: OptionalValue<double>.Set(0.75),
            AiAssignmentFallbackStrategy: OptionalValue<int>.Set(2),
            AiAssignmentMaxCandidates: OptionalValue<int>.Set(9),
            AiAssignmentWeightsJson: OptionalText.Set("{\"skill\":0.4}"),
            AiAssignmentInstructions: OptionalText.Set("Priorize rede"),
            AiAssignmentUseAffinity: OptionalValue<bool>.Set(false),
            AiAssignmentMaxOutputTokens: OptionalValue<int>.Set(2000),
            AiSkillLearningMode: OptionalValue<int>.Set(2),
            AiSkillMinEvidence: OptionalValue<int>.Set(5),
            AiSkillMaxTags: OptionalValue<int>.Set(20),
            AiWeightLearningMode: OptionalValue<int>.Set(2),
            AiWeightMaxDeltaPerCycle: OptionalValue<decimal>.Set(0.20m),
            AiWeightCycleDays: OptionalValue<int>.Set(14),
            AiWeightMin: OptionalValue<decimal>.Set(0.10m),
            AiWeightMax: OptionalValue<decimal>.Set(0.60m)),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True, string.Join("; ", result.Errors.Select(e => e.Message)));
        var updated = service.Updated!;
        Assert.That(updated.AiAssignmentModeOverride, Is.EqualTo(1));
        Assert.That(updated.AiAssignmentMinConfidenceOverride, Is.EqualTo(0.75));
        Assert.That(updated.AiAssignmentFallbackStrategyOverride, Is.EqualTo(2));
        Assert.That(updated.AiAssignmentMaxCandidatesOverride, Is.EqualTo(9));
        Assert.That(updated.AiAssignmentWeightsJsonOverride, Is.EqualTo("{\"skill\":0.4}"));
        Assert.That(updated.AiAssignmentInstructionsOverride, Is.EqualTo("Priorize rede"));
        Assert.That(updated.AiAssignmentUseAffinityOverride, Is.False);
        Assert.That(updated.AiAssignmentMaxOutputTokensOverride, Is.EqualTo(2000));
        Assert.That(updated.AiSkillLearningModeOverride, Is.EqualTo(2));
        Assert.That(updated.AiSkillMinEvidenceOverride, Is.EqualTo(5));
        Assert.That(updated.AiSkillMaxTagsOverride, Is.EqualTo(20));
        Assert.That(updated.AiWeightLearningModeOverride, Is.EqualTo(2));
        Assert.That(updated.AiWeightMaxDeltaPerCycleOverride, Is.EqualTo(0.20m));
        Assert.That(updated.AiWeightCycleDaysOverride, Is.EqualTo(14));
        Assert.That(updated.AiWeightMinOverride, Is.EqualTo(0.10m));
        Assert.That(updated.AiWeightMaxOverride, Is.EqualTo(0.60m));
    }

    [Test]
    public async Task Update_com_null_explicito_nos_16_campos_desfaz_o_override()
    {
        var dept = NewDepartment();
        dept.AiAssignmentModeOverride = 1;
        dept.AiAssignmentMinConfidenceOverride = 0.75;
        dept.AiAssignmentFallbackStrategyOverride = 2;
        dept.AiAssignmentMaxCandidatesOverride = 9;
        dept.AiAssignmentWeightsJsonOverride = "{}";
        dept.AiAssignmentInstructionsOverride = "x";
        dept.AiAssignmentUseAffinityOverride = false;
        dept.AiAssignmentMaxOutputTokensOverride = 2000;
        dept.AiSkillLearningModeOverride = 2;
        dept.AiSkillMinEvidenceOverride = 5;
        dept.AiSkillMaxTagsOverride = 20;
        dept.AiWeightLearningModeOverride = 2;
        dept.AiWeightMaxDeltaPerCycleOverride = 0.20m;
        dept.AiWeightCycleDaysOverride = 14;
        dept.AiWeightMinOverride = 0.10m;
        dept.AiWeightMaxOverride = 0.60m;

        var service = new FakeDepartmentService(dept);
        var handler = new UpdateDepartmentCommandHandler(service);

        var result = await handler.Handle(new UpdateDepartmentCommand(
            Id: dept.Id, Name: null, Description: null,
            InheritFromGlobalId: OptionalGuid.Unset, SortOrder: null, IsActive: null,
            AiAssignmentMode: OptionalValue<int>.Set(null),
            AiAssignmentMinConfidence: OptionalValue<double>.Set(null),
            AiAssignmentFallbackStrategy: OptionalValue<int>.Set(null),
            AiAssignmentMaxCandidates: OptionalValue<int>.Set(null),
            AiAssignmentWeightsJson: OptionalText.Set(null),
            AiAssignmentInstructions: OptionalText.Set(null),
            AiAssignmentUseAffinity: OptionalValue<bool>.Set(null),
            AiAssignmentMaxOutputTokens: OptionalValue<int>.Set(null),
            AiSkillLearningMode: OptionalValue<int>.Set(null),
            AiSkillMinEvidence: OptionalValue<int>.Set(null),
            AiSkillMaxTags: OptionalValue<int>.Set(null),
            AiWeightLearningMode: OptionalValue<int>.Set(null),
            AiWeightMaxDeltaPerCycle: OptionalValue<decimal>.Set(null),
            AiWeightCycleDays: OptionalValue<int>.Set(null),
            AiWeightMin: OptionalValue<decimal>.Set(null),
            AiWeightMax: OptionalValue<decimal>.Set(null)),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        var updated = service.Updated!;
        Assert.That(updated.AiAssignmentModeOverride, Is.Null);
        Assert.That(updated.AiAssignmentMinConfidenceOverride, Is.Null);
        Assert.That(updated.AiAssignmentFallbackStrategyOverride, Is.Null);
        Assert.That(updated.AiAssignmentMaxCandidatesOverride, Is.Null);
        Assert.That(updated.AiAssignmentWeightsJsonOverride, Is.Null);
        Assert.That(updated.AiAssignmentInstructionsOverride, Is.Null);
        Assert.That(updated.AiAssignmentUseAffinityOverride, Is.Null);
        Assert.That(updated.AiAssignmentMaxOutputTokensOverride, Is.Null);
        Assert.That(updated.AiSkillLearningModeOverride, Is.Null);
        Assert.That(updated.AiSkillMinEvidenceOverride, Is.Null);
        Assert.That(updated.AiSkillMaxTagsOverride, Is.Null);
        Assert.That(updated.AiWeightLearningModeOverride, Is.Null);
        Assert.That(updated.AiWeightMaxDeltaPerCycleOverride, Is.Null);
        Assert.That(updated.AiWeightCycleDaysOverride, Is.Null);
        Assert.That(updated.AiWeightMinOverride, Is.Null);
        Assert.That(updated.AiWeightMaxOverride, Is.Null);
    }

    [Test]
    public async Task Update_campo_ausente_preserva_o_override()
    {
        var dept = NewDepartment();
        dept.AiSkillLearningModeOverride = 2;
        dept.AiWeightMaxOverride = 0.60m;

        var service = new FakeDepartmentService(dept);
        var handler = new UpdateDepartmentCommandHandler(service);

        var result = await handler.Handle(
            new UpdateDepartmentCommand(
                dept.Id, "Só o nome", null, OptionalGuid.Unset, null, null),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(service.Updated!.AiSkillLearningModeOverride, Is.EqualTo(2));
        Assert.That(service.Updated!.AiWeightMaxOverride, Is.EqualTo(0.60m));
    }

    [Test]
    public async Task Update_alias_override_escreve_a_mesma_coluna()
    {
        var dept = NewDepartment();
        var service = new FakeDepartmentService(dept);
        var handler = new UpdateDepartmentCommandHandler(service);

        var applied = await handler.Handle(new UpdateDepartmentCommand(
            Id: dept.Id, Name: null, Description: null,
            InheritFromGlobalId: OptionalGuid.Unset, SortOrder: null, IsActive: null,
            AiAssignmentModeOverride: OptionalValue<int>.Set(1),
            AiSkillLearningModeOverride: OptionalValue<int>.Set(2)),
            CancellationToken.None);

        Assert.That(applied.IsSuccess, Is.True);
        Assert.That(service.Updated!.AiAssignmentModeOverride, Is.EqualTo(1));
        Assert.That(service.Updated!.AiSkillLearningModeOverride, Is.EqualTo(2));

        var cleared = await handler.Handle(new UpdateDepartmentCommand(
            Id: dept.Id, Name: null, Description: null,
            InheritFromGlobalId: OptionalGuid.Unset, SortOrder: null, IsActive: null,
            AiAssignmentModeOverride: OptionalValue<int>.Set(null),
            AiSkillLearningModeOverride: OptionalValue<int>.Set(null)),
            CancellationToken.None);

        Assert.That(cleared.IsSuccess, Is.True);
        Assert.That(service.Updated!.AiAssignmentModeOverride, Is.Null);
        Assert.That(service.Updated!.AiSkillLearningModeOverride, Is.Null);
    }

    [Test]
    public void Update_command_json_distingue_null_explicito_de_ausente()
    {
        var id = Guid.NewGuid();
        var json = $$"""
        {
          "id": "{{id}}",
          "name": null,
          "description": null,
          "inheritFromGlobalId": null,
          "sortOrder": null,
          "isActive": null,
          "aiSkillLearningMode": null,
          "aiWeightMax": 0.4
        }
        """;

        var cmd = JsonSerializer.Deserialize<UpdateDepartmentCommand>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.That(cmd.AiSkillLearningMode.IsSet, Is.True);
        Assert.That(cmd.AiSkillLearningMode.Value, Is.Null);
        Assert.That(cmd.AiWeightMax.IsSet, Is.True);
        Assert.That(cmd.AiWeightMax.Value, Is.EqualTo(0.4m));
        Assert.That(cmd.AiWeightMin.IsSet, Is.False, "campo ausente mantém o valor atual");
        Assert.That(cmd.AiAssignmentMode.IsSet, Is.False);
    }

    [Test]
    public async Task Create_rejeita_pai_de_outro_cliente()
    {
        var nonGlobalParent = NewDepartment(clientId: Guid.NewGuid());
        var service = new FakeDepartmentService(nonGlobalParent);
        var handler = new CreateDepartmentCommandHandler(service);

        var result = await handler.Handle(
            new CreateDepartmentCommand(
                "Depto", null, Guid.NewGuid(), nonGlobalParent.Id, 0),
            CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
        Assert.That(service.Created, Is.Null);
    }

    [Test]
    public async Task Create_registra_o_ator_do_request_na_auditoria()
    {
        // B1 — o usuário do request chega pelo comando e é gravado como ator.
        var service = new FakeDepartmentService();
        var audit = new FakeConfigurationAudit();
        var handler = new CreateDepartmentCommandHandler(service, audit);

        var result = await handler.Handle(
            new CreateDepartmentCommand("Depto", null, Guid.NewGuid(), null, 0)
                with { ChangedBy = "maria.silva" },
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(audit.Changes, Has.Count.EqualTo(1));
        Assert.That(audit.Changes[0].ChangedBy, Is.EqualTo("maria.silva"));
    }

    [Test]
    public async Task Update_registra_ator_e_transicao_de_IsActive_com_antes_e_depois()
    {
        // B1 — desligamento auditado com antes/depois e ator do request.
        var dept = NewDepartment();
        var service = new FakeDepartmentService(dept);
        var audit = new FakeConfigurationAudit();
        var handler = new UpdateDepartmentCommandHandler(service, audit);

        var result = await handler.Handle(
            new UpdateDepartmentCommand(
                dept.Id, null, null, OptionalGuid.Unset, null, IsActive: false)
                with { ChangedBy = "joao.pereira" },
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        var change = audit.Changes.Single(c => c.Field == "IsActive");
        Assert.That(change.OldValue, Is.EqualTo("true"));
        Assert.That(change.NewValue, Is.EqualTo("false"));
        Assert.That(change.Reason, Is.EqualTo("department_deactivate"));
        Assert.That(change.ChangedBy, Is.EqualTo("joao.pereira"));
    }

    [Test]
    public async Task Update_registra_a_reativacao_de_IsActive()
    {
        // B1 — a transição inversa (inativo -> ativo) também é auditada.
        var dept = NewDepartment(active: false);
        var service = new FakeDepartmentService(dept);
        var audit = new FakeConfigurationAudit();
        var handler = new UpdateDepartmentCommandHandler(service, audit);

        var result = await handler.Handle(
            new UpdateDepartmentCommand(
                dept.Id, null, null, OptionalGuid.Unset, null, IsActive: true)
                with { ChangedBy = "ana.souza" },
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        var change = audit.Changes.Single(c => c.Field == "IsActive");
        Assert.That(change.OldValue, Is.EqualTo("false"));
        Assert.That(change.NewValue, Is.EqualTo("true"));
        Assert.That(change.Reason, Is.EqualTo("department_activate"));
        Assert.That(change.ChangedBy, Is.EqualTo("ana.souza"));
    }

    [Test]
    public async Task Delete_registra_o_ator_do_request_na_auditoria()
    {
        var dept = NewDepartment();
        var service = new FakeDepartmentService(dept);
        var audit = new FakeConfigurationAudit();
        var handler = new DeleteDepartmentCommandHandler(service, audit);

        var result = await handler.Handle(
            new DeleteDepartmentCommand(dept.Id, "carla.mendes"), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(audit.Changes, Is.Not.Empty);
        Assert.That(audit.Changes.Select(c => c.ChangedBy), Is.All.EqualTo("carla.mendes"));
    }

    [Test]
    public async Task Update_assignment_strategy_grava_override_e_null_volta_a_herdar()
    {
        // B3 — mesmo patch semântico dos demais 16 campos.
        var dept = NewDepartment();
        var service = new FakeDepartmentService(dept);
        var handler = new UpdateDepartmentCommandHandler(service);

        var applied = await handler.Handle(new UpdateDepartmentCommand(
            Id: dept.Id, Name: null, Description: null,
            InheritFromGlobalId: OptionalGuid.Unset, SortOrder: null, IsActive: null,
            AssignmentStrategy: OptionalValue<int>.Set(2)),
            CancellationToken.None);

        Assert.That(applied.IsSuccess, Is.True);
        Assert.That(service.Updated!.AssignmentStrategy, Is.EqualTo(2));
        Assert.That(service.Updated!.AssignmentStrategyOverride, Is.EqualTo(2));

        var cleared = await handler.Handle(new UpdateDepartmentCommand(
            Id: dept.Id, Name: null, Description: null,
            InheritFromGlobalId: OptionalGuid.Unset, SortOrder: null, IsActive: null,
            AssignmentStrategy: OptionalValue<int>.Set(null)),
            CancellationToken.None);

        Assert.That(cleared.IsSuccess, Is.True);
        Assert.That(service.Updated!.AssignmentStrategyOverride, Is.Null,
            "null explícito volta a herdar do global pai");
        Assert.That(service.Updated!.AssignmentStrategy, Is.EqualTo(2),
            "a coluna base/legada é preservada");
    }

    [Test]
    public async Task Update_alias_assignment_strategy_override_volta_a_herdar()
    {
        // O console envia SOMENTE assignmentStrategyOverride (null = herdar) ao
        // desmarcar "Herdar"; o alias precisa limpar o override e preservar a base.
        var parent = NewDepartment();
        parent.AssignmentStrategy = 1;
        parent.AssignmentStrategyOverride = 1;
        var dept = NewDepartment(clientId: Guid.NewGuid(), parentId: parent.Id);
        dept.AssignmentStrategy = 2;
        dept.AssignmentStrategyOverride = 2;

        var service = new FakeDepartmentService(dept, parent);
        var handler = new UpdateDepartmentCommandHandler(service);

        var result = await handler.Handle(new UpdateDepartmentCommand(
            Id: dept.Id, Name: null, Description: null,
            InheritFromGlobalId: OptionalGuid.Unset, SortOrder: null, IsActive: null,
            AssignmentStrategyOverride: OptionalValue<int>.Set(null)),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(service.Updated!.AssignmentStrategyOverride, Is.Null);
        Assert.That(service.Updated!.AssignmentStrategy, Is.EqualTo(2),
            "o alias mexe só no override; a coluna base/legada é preservada");

        var effective = DepartmentEffectiveSettingsResolver.Resolve(service.Updated!, parent);
        Assert.That(effective.AssignmentStrategy.Value, Is.EqualTo(1),
            "sem override, a estratégia volta a vir do global pai");
        Assert.That(effective.AssignmentStrategy.Origin,
            Is.EqualTo(DepartmentSettingOrigin.InheritedGlobal));
    }

    [Test]
    public void Update_command_json_reconhece_assignment_strategy_override()
    {
        // Regressão do contrato do console: o campo precisa existir no comando,
        // senão o System.Text.Json ignora o null e o PUT vira no-op.
        var id = Guid.NewGuid();
        var json = $$"""
        {
          "id": "{{id}}",
          "assignmentStrategyOverride": null
        }
        """;

        var cmd = JsonSerializer.Deserialize<UpdateDepartmentCommand>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.That(cmd.AssignmentStrategyOverride.IsSet, Is.True);
        Assert.That(cmd.AssignmentStrategyOverride.Value, Is.Null);
        Assert.That(cmd.AssignmentStrategy.IsSet, Is.False);
    }

    [Test]
    public async Task Update_rejeita_assignment_strategy_fora_da_faixa()
    {
        var dept = NewDepartment();
        var service = new FakeDepartmentService(dept);
        var handler = new UpdateDepartmentCommandHandler(service);

        var result = await handler.Handle(new UpdateDepartmentCommand(
            Id: dept.Id, Name: null, Description: null,
            InheritFromGlobalId: OptionalGuid.Unset, SortOrder: null, IsActive: null,
            AssignmentStrategy: OptionalValue<int>.Set(99)),
            CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
        Assert.That(result.Errors[0].Message, Does.Contain("Estratégia de auto-atribuição"));
        Assert.That(service.Updated, Is.Null);
    }

    [Test]
    public async Task Create_rejeita_assignment_strategy_fora_da_faixa()
    {
        var service = new FakeDepartmentService();
        var handler = new CreateDepartmentCommandHandler(service);

        var result = await handler.Handle(
            new CreateDepartmentCommand(
                "Depto", null, Guid.NewGuid(), null, 0, AssignmentStrategy: 99),
            CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
        Assert.That(service.Created, Is.Null);
    }

    [Test]
    public async Task Create_departamento_global_descarta_heranca()
    {
        // Global nunca herda: o vínculo informado não deve ser gravado.
        var parent = NewDepartment();
        var service = new FakeDepartmentService(parent);
        var handler = new CreateDepartmentCommandHandler(service);

        var result = await handler.Handle(
            new CreateDepartmentCommand(
                "Global", null, null, parent.Id, 0, AssignmentStrategy: 1),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(service.Created!.ClientId, Is.Null);
        Assert.That(service.Created!.InheritFromGlobalId, Is.Null);
    }

    private sealed class FakeConfigurationAudit : IConfigurationAuditService
    {
        public List<(string EntityType, Guid EntityId, string Field, string? OldValue, string? NewValue,
            string? Reason, string? ChangedBy)> Changes { get; } = [];

        public Task LogChangeAsync(string entityType, Guid entityId, string fieldName,
            string? oldValue, string? newValue, string? reason = null, string? changedBy = null, string? ipAddress = null)
        {
            Changes.Add((entityType, entityId, fieldName, oldValue, newValue, reason, changedBy));
            return Task.CompletedTask;
        }

        public Task<IEnumerable<ConfigurationAudit>> GetEntityHistoryAsync(string entityType, Guid entityId, int limit = 100)
            => Task.FromResult<IEnumerable<ConfigurationAudit>>([]);

        public Task<IEnumerable<ConfigurationAudit>> GetRecentChangesAsync(int days = 90, int limit = 1000)
            => Task.FromResult<IEnumerable<ConfigurationAudit>>([]);

        public Task<IEnumerable<ConfigurationAudit>> GetChangesByUserAsync(string username, int limit = 100)
            => Task.FromResult<IEnumerable<ConfigurationAudit>>([]);

        public Task<IEnumerable<ConfigurationAudit>> GetFieldHistoryAsync(string entityType, Guid entityId, string fieldName)
            => Task.FromResult<IEnumerable<ConfigurationAudit>>([]);

        public Task<IEnumerable<ConfigurationAudit>> GetAuditReportAsync(DateTime startDate, DateTime endDate)
            => Task.FromResult<IEnumerable<ConfigurationAudit>>([]);
    }

    private sealed class FakeTeamResolver(params DepartmentTeamMember[] members)
        : IDepartmentTeamResolver
    {
        public Task<IReadOnlyList<DepartmentTeamMember>> ResolveMembersAsync(
            Guid departmentId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DepartmentTeamMember>>(members);
    }

    private sealed class FakeDepartmentService(params Department[] departments)
        : IDepartmentService
    {
        private readonly List<Department> _departments = [.. departments];

        public bool HasActiveChildren { get; init; }
        public bool Deleted { get; private set; }
        public Department? Created { get; private set; }
        public Department? Updated { get; private set; }

        public Task<IReadOnlyList<Department>> GetByClientAsync(
            Guid clientId, bool includeGlobal = true, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Department>>(_departments
                .Where(d => d.ClientId == clientId || (includeGlobal && d.ClientId == null))
                .ToList());

        public Task<IReadOnlyList<Department>> GetGlobalAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Department>>(
                _departments.Where(d => d.ClientId == null).ToList());

        public Task<IReadOnlyList<Department>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Department>>(_departments.ToList());

        public Task<Department?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(_departments.FirstOrDefault(d => d.Id == id));

        public Task<Department> CreateAsync(Department department, CancellationToken ct = default)
        {
            Created = department;
            _departments.Add(department);
            return Task.FromResult(department);
        }

        public Task<Department> UpdateAsync(Department department, CancellationToken ct = default)
        {
            Updated = department;
            return Task.FromResult(department);
        }

        public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
        {
            Deleted = true;
            return Task.FromResult(true);
        }

        public Task<bool> HasActiveChildrenAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(HasActiveChildren);
    }
}
