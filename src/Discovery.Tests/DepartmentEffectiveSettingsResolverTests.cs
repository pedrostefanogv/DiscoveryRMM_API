using Discovery.Core.Departments;
using Discovery.Core.Entities;

namespace Discovery.Tests;

/// <summary>
/// Configuração efetiva campo a campo: override próprio vence, NULL herda do
/// global pai, sem pai cai no valor base ("default") e o backfill da M229
/// preserva o comportamento anterior (override = valor antigo).
/// </summary>
public class DepartmentEffectiveSettingsResolverTests
{
    private static Department Global(Guid id) => new()
    {
        Id = id,
        ClientId = null,
        Name = "Global",
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static Department ClientDepartment(
        Guid id, Guid? parentId, Guid? clientId = null) => new()
        {
            Id = id,
            ClientId = clientId ?? Guid.NewGuid(),
            Name = "Cliente",
            IsActive = true,
            InheritFromGlobalId = parentId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    [Test]
    public void Override_proprio_vence_o_global()
    {
        var parent = Global(Guid.NewGuid());
        parent.AiAssignmentModeOverride = (int)1;
        parent.AiAssignmentMinConfidenceOverride = 0.90;

        var dept = ClientDepartment(Guid.NewGuid(), parent.Id);
        dept.AiAssignmentModeOverride = 0;
        dept.AiAssignmentMinConfidenceOverride = 0.50;
        dept.AiAssignmentMaxCandidatesOverride = 5;

        var settings = DepartmentEffectiveSettingsResolver.Resolve(dept, parent);

        Assert.That(settings.AiAssignmentMode.Value, Is.EqualTo(0));
        Assert.That(settings.AiAssignmentMode.Origin, Is.EqualTo(DepartmentSettingOrigin.Own));
        Assert.That(settings.AiAssignmentMinConfidence.Value, Is.EqualTo(0.50));
        Assert.That(settings.AiAssignmentMaxCandidates.Value, Is.EqualTo(5));
        Assert.That(settings.AiAssignmentMaxCandidates.Origin, Is.EqualTo(DepartmentSettingOrigin.Own));
    }

    [Test]
    public void Override_null_herda_o_campo_do_global()
    {
        var parent = Global(Guid.NewGuid());
        parent.AiAssignmentModeOverride = (int)1;
        parent.AiAssignmentMinConfidenceOverride = 0.85;
        parent.AiAssignmentFallbackStrategyOverride = 2;
        parent.AiAssignmentInstructionsOverride = "Priorize rede";

        var dept = ClientDepartment(Guid.NewGuid(), parent.Id);

        var settings = DepartmentEffectiveSettingsResolver.Resolve(dept, parent);

        Assert.That(settings.AiAssignmentMode.Value, Is.EqualTo(1));
        Assert.That(settings.AiAssignmentMode.Origin, Is.EqualTo(DepartmentSettingOrigin.InheritedGlobal));
        Assert.That(settings.AiAssignmentMode.SourceDepartmentId, Is.EqualTo(parent.Id));
        Assert.That(settings.AiAssignmentMinConfidence.Value, Is.EqualTo(0.85));
        Assert.That(settings.AiAssignmentFallbackStrategy.Value, Is.EqualTo(2));
        Assert.That(settings.AiAssignmentInstructions.Value, Is.EqualTo("Priorize rede"));
        Assert.That(settings.AiAssignmentInstructions.Origin, Is.EqualTo(DepartmentSettingOrigin.InheritedGlobal));
    }

    [Test]
    public void Backfill_preserva_o_comportamento_anterior()
    {
        // M229 copia o valor base para o override de todas as linhas existentes.
        var dept = ClientDepartment(Guid.NewGuid(), parentId: null);
        dept.AiAssignmentMode = 1;
        dept.AiAssignmentModeOverride = 1;
        dept.AiAssignmentMinConfidence = 0.75;
        dept.AiAssignmentMinConfidenceOverride = 0.75;

        var settings = DepartmentEffectiveSettingsResolver.Resolve(dept, null);

        Assert.That(settings.AiAssignmentMode.Value, Is.EqualTo(1));
        Assert.That(settings.AiAssignmentMode.Origin, Is.EqualTo(DepartmentSettingOrigin.Own));
        Assert.That(settings.AiAssignmentMinConfidence.Value, Is.EqualTo(0.75));
        Assert.That(settings.AiAssignmentMinConfidence.Origin, Is.EqualTo(DepartmentSettingOrigin.Own));
    }

    [Test]
    public void Sem_override_e_sem_pai_usa_o_valor_base_como_default()
    {
        var dept = ClientDepartment(Guid.NewGuid(), parentId: null);
        dept.AiAssignmentMaxOutputTokens = 2048;

        var settings = DepartmentEffectiveSettingsResolver.Resolve(dept, null);

        Assert.That(settings.AiAssignmentMaxOutputTokens.Value, Is.EqualTo(2048));
        Assert.That(settings.AiAssignmentMaxOutputTokens.Origin, Is.EqualTo(DepartmentSettingOrigin.Default));
    }

    [Test]
    public void Departamento_global_nunca_herda()
    {
        var other = Global(Guid.NewGuid());
        other.AiAssignmentModeOverride = 1;

        var global = Global(Guid.NewGuid());
        global.InheritFromGlobalId = other.Id; // dado legado inválido
        global.AiAssignmentMode = 0;

        var settings = DepartmentEffectiveSettingsResolver.Resolve(global, other);

        Assert.That(settings.AiAssignmentMode.Value, Is.EqualTo(0));
        Assert.That(settings.AiAssignmentMode.Origin, Is.EqualTo(DepartmentSettingOrigin.Default));
        Assert.That(settings.AiAssignmentMode.SourceDepartmentId, Is.EqualTo(global.Id));
    }

    [Test]
    public void Override_dos_8_campos_de_aprendizado_vence_e_null_herda()
    {
        var parent = Global(Guid.NewGuid());
        parent.AiSkillLearningMode = 2;                 // base do global (sem override)
        parent.AiSkillMinEvidenceOverride = 5;
        parent.AiSkillMaxTagsOverride = 20;
        parent.AiWeightLearningModeOverride = 0;
        parent.AiWeightMaxDeltaPerCycleOverride = 0.30m;
        parent.AiWeightCycleDaysOverride = 30;
        parent.AiWeightMinOverride = 0.10m;
        parent.AiWeightMaxOverride = 0.80m;

        var dept = ClientDepartment(Guid.NewGuid(), parent.Id);
        dept.AiSkillMinEvidenceOverride = 7; // próprio vence
        dept.AiWeightMaxOverride = null;     // herda do global

        var settings = DepartmentEffectiveSettingsResolver.Resolve(dept, parent);

        Assert.That(settings.AiSkillLearningMode.Value, Is.EqualTo(2));
        Assert.That(settings.AiSkillLearningMode.Origin, Is.EqualTo(DepartmentSettingOrigin.InheritedGlobal));
        Assert.That(settings.AiSkillMinEvidence.Value, Is.EqualTo(7));
        Assert.That(settings.AiSkillMinEvidence.Origin, Is.EqualTo(DepartmentSettingOrigin.Own));
        Assert.That(settings.AiSkillMaxTags.Value, Is.EqualTo(20));
        Assert.That(settings.AiSkillMaxTags.Origin, Is.EqualTo(DepartmentSettingOrigin.InheritedGlobal));

        Assert.That(settings.AiWeightLearningMode.Value, Is.EqualTo(0));
        Assert.That(settings.AiWeightMaxDeltaPerCycle.Value, Is.EqualTo(0.30m));
        Assert.That(settings.AiWeightCycleDays.Value, Is.EqualTo(30));
        Assert.That(settings.AiWeightMin.Value, Is.EqualTo(0.10m));
        Assert.That(settings.AiWeightMax.Value, Is.EqualTo(0.80m));
        Assert.That(settings.AiWeightMax.Origin, Is.EqualTo(DepartmentSettingOrigin.InheritedGlobal));
    }

    [Test]
    public void Backfill_dos_16_campos_preserva_comportamento()
    {
        // M229 copia base -> override em todas as linhas; depois disso o valor
        // do próprio vence até um pai global com valores diferentes.
        var parent = Global(Guid.NewGuid());
        parent.AiAssignmentModeOverride = 1;
        parent.AiSkillLearningModeOverride = 2;

        var dept = ClientDepartment(Guid.NewGuid(), parent.Id);
        dept.AiAssignmentMode = 0; dept.AiAssignmentModeOverride = 0;
        dept.AiAssignmentMinConfidence = 0.70; dept.AiAssignmentMinConfidenceOverride = 0.70;
        dept.AiAssignmentFallbackStrategy = 2; dept.AiAssignmentFallbackStrategyOverride = 2;
        dept.AiAssignmentMaxCandidates = 6; dept.AiAssignmentMaxCandidatesOverride = 6;
        dept.AiAssignmentWeightsJson = "{}"; dept.AiAssignmentWeightsJsonOverride = "{}";
        dept.AiAssignmentInstructions = "próprio"; dept.AiAssignmentInstructionsOverride = "próprio";
        dept.AiAssignmentUseAffinity = false; dept.AiAssignmentUseAffinityOverride = false;
        dept.AiAssignmentMaxOutputTokens = 2048; dept.AiAssignmentMaxOutputTokensOverride = 2048;
        dept.AiSkillLearningMode = 1; dept.AiSkillLearningModeOverride = 1;
        dept.AiSkillMinEvidence = 4; dept.AiSkillMinEvidenceOverride = 4;
        dept.AiSkillMaxTags = 10; dept.AiSkillMaxTagsOverride = 10;
        dept.AiWeightLearningMode = 1; dept.AiWeightLearningModeOverride = 1;
        dept.AiWeightMaxDeltaPerCycle = 0.20m; dept.AiWeightMaxDeltaPerCycleOverride = 0.20m;
        dept.AiWeightCycleDays = 14; dept.AiWeightCycleDaysOverride = 14;
        dept.AiWeightMin = 0.10m; dept.AiWeightMinOverride = 0.10m;
        dept.AiWeightMax = 0.60m; dept.AiWeightMaxOverride = 0.60m;
        dept.AssignmentStrategy = 3; dept.AssignmentStrategyOverride = 3;

        var settings = DepartmentEffectiveSettingsResolver.Resolve(dept, parent);

        Assert.That(settings.AiAssignmentMode.Value, Is.EqualTo(0));
        Assert.That(settings.AiAssignmentMode.Origin, Is.EqualTo(DepartmentSettingOrigin.Own));
        Assert.That(settings.AiAssignmentMinConfidence.Value, Is.EqualTo(0.70));
        Assert.That(settings.AiAssignmentFallbackStrategy.Value, Is.EqualTo(2));
        Assert.That(settings.AiAssignmentMaxCandidates.Value, Is.EqualTo(6));
        Assert.That(settings.AiAssignmentWeightsJson.Value, Is.EqualTo("{}"));
        Assert.That(settings.AiAssignmentInstructions.Value, Is.EqualTo("próprio"));
        Assert.That(settings.AiAssignmentUseAffinity.Value, Is.False);
        Assert.That(settings.AiAssignmentMaxOutputTokens.Value, Is.EqualTo(2048));
        Assert.That(settings.AiSkillLearningMode.Value, Is.EqualTo(1));
        Assert.That(settings.AiSkillMinEvidence.Value, Is.EqualTo(4));
        Assert.That(settings.AiSkillMaxTags.Value, Is.EqualTo(10));
        Assert.That(settings.AiWeightLearningMode.Value, Is.EqualTo(1));
        Assert.That(settings.AiWeightMaxDeltaPerCycle.Value, Is.EqualTo(0.20m));
        Assert.That(settings.AiWeightCycleDays.Value, Is.EqualTo(14));
        Assert.That(settings.AiWeightMin.Value, Is.EqualTo(0.10m));
        Assert.That(settings.AiWeightMax.Value, Is.EqualTo(0.60m));
        Assert.That(settings.AssignmentStrategy.Value, Is.EqualTo(3));

        // Todos os 17 campos têm origem própria depois do backfill.
        var origins = new[]
        {
            settings.AiAssignmentMode.Origin, settings.AiAssignmentMinConfidence.Origin,
            settings.AiAssignmentFallbackStrategy.Origin, settings.AiAssignmentMaxCandidates.Origin,
            settings.AiAssignmentInstructions.Origin, settings.AiAssignmentWeightsJson.Origin,
            settings.AiAssignmentUseAffinity.Origin, settings.AiAssignmentMaxOutputTokens.Origin,
            settings.AiSkillLearningMode.Origin, settings.AiSkillMinEvidence.Origin,
            settings.AiSkillMaxTags.Origin, settings.AiWeightLearningMode.Origin,
            settings.AiWeightMaxDeltaPerCycle.Origin, settings.AiWeightCycleDays.Origin,
            settings.AiWeightMin.Origin, settings.AiWeightMax.Origin,
            settings.AssignmentStrategy.Origin
        };
        Assert.That(origins, Has.Length.EqualTo(17));
        Assert.That(origins, Is.All.EqualTo(DepartmentSettingOrigin.Own));
    }

    [Test]
    public void AssignmentStrategy_override_vence_e_null_herda()
    {
        // B3 — 17º campo: estratégia de auto-atribuição herda como os demais.
        var parent = Global(Guid.NewGuid());
        parent.AssignmentStrategy = 3;
        parent.AssignmentStrategyOverride = 3;

        var own = ClientDepartment(Guid.NewGuid(), parent.Id);
        own.AssignmentStrategy = 1;
        own.AssignmentStrategyOverride = 1;

        var ownSettings = DepartmentEffectiveSettingsResolver.Resolve(own, parent);
        Assert.That(ownSettings.AssignmentStrategy.Value, Is.EqualTo(1));
        Assert.That(ownSettings.AssignmentStrategy.Origin, Is.EqualTo(DepartmentSettingOrigin.Own));
        Assert.That(ownSettings.AssignmentStrategy.SourceDepartmentId, Is.EqualTo(own.Id));

        var inheriting = ClientDepartment(Guid.NewGuid(), parent.Id);
        inheriting.AssignmentStrategy = 1; // base/legado não vence o global
        inheriting.AssignmentStrategyOverride = null;

        var inheritedSettings = DepartmentEffectiveSettingsResolver.Resolve(inheriting, parent);
        Assert.That(inheritedSettings.AssignmentStrategy.Value, Is.EqualTo(3));
        Assert.That(inheritedSettings.AssignmentStrategy.Origin,
            Is.EqualTo(DepartmentSettingOrigin.InheritedGlobal));
        Assert.That(inheritedSettings.AssignmentStrategy.SourceDepartmentId, Is.EqualTo(parent.Id));
    }

    [Test]
    public void AssignmentStrategy_sem_pai_usa_o_valor_base()
    {
        var dept = ClientDepartment(Guid.NewGuid(), parentId: null);
        dept.AssignmentStrategy = 2;

        var settings = DepartmentEffectiveSettingsResolver.Resolve(dept, null);

        Assert.That(settings.AssignmentStrategy.Value, Is.EqualTo(2));
        Assert.That(settings.AssignmentStrategy.Origin, Is.EqualTo(DepartmentSettingOrigin.Default));
    }

    [Test]
    public void Pai_nao_global_ou_inativo_e_ignorado()
    {
        var nonGlobalParent = new Department
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            Name = "Outro cliente",
            IsActive = true,
            AiAssignmentModeOverride = 1
        };
        var dept = ClientDepartment(Guid.NewGuid(), nonGlobalParent.Id);
        dept.AiAssignmentMode = 0;

        var settings = DepartmentEffectiveSettingsResolver.Resolve(dept, nonGlobalParent);

        Assert.That(settings.AiAssignmentMode.Value, Is.EqualTo(0));
        Assert.That(settings.AiAssignmentMode.Origin, Is.EqualTo(DepartmentSettingOrigin.Default));

        var inactiveParent = Global(Guid.NewGuid());
        inactiveParent.IsActive = false;
        inactiveParent.AiAssignmentModeOverride = 1;

        var inherited = DepartmentEffectiveSettingsResolver.Resolve(dept, inactiveParent);
        Assert.That(inherited.AiAssignmentMode.Origin, Is.EqualTo(DepartmentSettingOrigin.Default));
    }
}
