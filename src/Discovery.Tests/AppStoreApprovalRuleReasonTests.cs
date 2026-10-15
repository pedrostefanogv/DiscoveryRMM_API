using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace Discovery.Tests;

/// <summary>
/// Motivo e nome do pacote nas regras de aprovação da Loja de Softwares.
///
/// O motivo era gravado SÓ na auditoria (app_approval_audits) e a listagem de
/// regras não o mostrava; o nome do pacote nunca era resolvido, então a aba
/// Aprovações exibia apenas o packageId. Este teste cobre:
///  - a coluna app_approval_rules.reason (migração M226) indo e voltando;
///  - AppApprovalRuleRepository.UpdateAsync copiando o motivo de uma instância
///    DESANEXADA (com a linha removida, o valor antigo permaneceria e o teste
///    falharia — a versão anterior mutava a instância rastreada e passava por
///    acidente, sem exercitar o repositório);
///  - o comportamento do service: motivo vazio na atualização PRESERVA o que já
///    estava gravado, e a auditoria recebe o motivo normalizado;
///  - o mapeamento EF da coluna (nome + limite de 500) que a M226 cria.
/// </summary>
[TestFixture]
public class AppStoreApprovalRuleReasonTests
{
    [Test]
    public async Task Update_copia_o_motivo_de_uma_instancia_desanexada()
    {
        await using var db = ApprovalRuleTestDbContext.New();
        var repo = new AppApprovalRuleRepository(db);

        var created = await repo.CreateAsync(new AppApprovalRule
        {
            ScopeType = AppApprovalScopeType.Global,
            InstallationType = AppInstallationType.Custom,
            PackageId = "foxit_editor",
            Action = AppApprovalActionType.Allow,
            Reason = "Aprovado pela TI",
        });

        // Instância nova, não rastreada: é o que o UpdateAsync recebe em produção.
        await repo.UpdateAsync(new AppApprovalRule
        {
            Id = created.Id,
            Action = AppApprovalActionType.Deny,
            Reason = "Renovado pela TI",
        });

        var reloaded = await repo.GetByIdAsync(created.Id);
        Assert.That(reloaded, Is.Not.Null);
        Assert.That(reloaded!.Reason, Is.EqualTo("Renovado pela TI"));
        Assert.That(reloaded.Action, Is.EqualTo(AppApprovalActionType.Deny));
    }

    [Test]
    public async Task Service_preserva_o_motivo_quando_a_atualizacao_nao_informa_um_novo()
    {
        await using var db = ApprovalRuleTestDbContext.New();
        var approvalRepo = new AppApprovalRuleRepository(db);
        var packageRepo = new AppPackageRepository(db);
        var audit = new RecordingAuditService();

        db.AppPackages.Add(new AppPackage
        {
            Id = Guid.NewGuid(),
            InstallationType = AppInstallationType.Custom,
            PackageId = "foxit_editor",
            Name = "Foxit Editor",
            InstallCommand = "\"{{installer}}\" /S",
        });
        await db.SaveChangesAsync();

        var service = new AppStoreService(approvalRepo, audit, null!, null!, null!, packageRepo, null!);

        var created = await service.UpsertRuleAsync(
            AppApprovalScopeType.Global, null, AppInstallationType.Custom, "foxit_editor",
            AppApprovalActionType.Allow, false, "  Aprovado pela TI  ", "ti.user", "10.0.0.9");

        // Motivo chega trimado na regra e na auditoria (mesmo valor nos dois).
        Assert.That(created.Reason, Is.EqualTo("Aprovado pela TI"));
        Assert.That(created.PackageName, Is.EqualTo("Foxit Editor"));
        Assert.That(audit.Entries[0].Reason, Is.EqualTo("Aprovado pela TI"));
        Assert.That(audit.Entries[0].ChangedBy, Is.EqualTo("ti.user"));
        Assert.That(audit.Entries[0].IpAddress, Is.EqualTo("10.0.0.9"));

        // Segunda gravação sem motivo: mantém o anterior e nega a regra.
        var updated = await service.UpsertRuleAsync(
            AppApprovalScopeType.Global, null, AppInstallationType.Custom, "foxit_editor",
            AppApprovalActionType.Deny, false, null, "ti.user", "10.0.0.9");

        Assert.That(updated.Reason, Is.EqualTo("Aprovado pela TI"));
        Assert.That(updated.Action, Is.EqualTo(AppApprovalActionType.Deny));
        Assert.That(audit.Entries[1].Reason, Is.Null);
    }

    [Test]
    public async Task Service_rejeita_motivo_maior_que_a_coluna()
    {
        await using var db = ApprovalRuleTestDbContext.New();
        var service = new AppStoreService(
            new AppApprovalRuleRepository(db), new RecordingAuditService(),
            null!, null!, null!, new AppPackageRepository(db), null!);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpsertRuleAsync(
            AppApprovalScopeType.Global, null, AppInstallationType.Custom, "foxit_editor",
            AppApprovalActionType.Allow, false, new string('x', 501), "ti.user", null));

        // O middleware converte InvalidOperationException em 400 com a mensagem.
        Assert.That(ex!.Message, Does.Contain("500"));
    }

    [Test]
    public async Task Listagem_nao_quebra_quando_o_pacote_nao_existe_mais()
    {
        await using var db = ApprovalRuleTestDbContext.New();
        var approvalRepo = new AppApprovalRuleRepository(db);
        var packageRepo = new AppPackageRepository(db);

        await approvalRepo.CreateAsync(new AppApprovalRule
        {
            ScopeType = AppApprovalScopeType.Global,
            InstallationType = AppInstallationType.Custom,
            PackageId = "pacote_removido",
            Action = AppApprovalActionType.Allow,
        });

        var service = new AppStoreService(approvalRepo, null!, null!, null!, null!, packageRepo, null!);
        var rules = await service.GetRulesByScopeAsync(
            AppApprovalScopeType.Global, null, AppInstallationType.Custom);

        Assert.That(rules, Has.Count.EqualTo(1));
        Assert.That(rules[0].PackageName, Is.Null);
        Assert.That(rules[0].Reason, Is.Null);
    }

    [Test]
    public async Task Nome_do_pacote_custom_resolve_sem_diferenciar_maiusculas()
    {
        await using var db = ApprovalRuleTestDbContext.New();
        var approvalRepo = new AppApprovalRuleRepository(db);
        var packageRepo = new AppPackageRepository(db);

        // Pacote gravado com maiúsculas (cadastro via API/import) e regra com o
        // id em minúsculas, como o site envia.
        db.AppPackages.Add(new AppPackage
        {
            Id = Guid.NewGuid(),
            InstallationType = AppInstallationType.Custom,
            PackageId = "Foxit_Editor",
            Name = "Foxit Editor",
            InstallCommand = "\"{{installer}}\" /S",
        });
        await db.SaveChangesAsync();

        await approvalRepo.CreateAsync(new AppApprovalRule
        {
            ScopeType = AppApprovalScopeType.Global,
            InstallationType = AppInstallationType.Custom,
            PackageId = "foxit_editor",
            Action = AppApprovalActionType.Allow,
        });

        var service = new AppStoreService(approvalRepo, null!, null!, null!, null!, packageRepo, null!);
        var rules = await service.GetRulesByScopeAsync(
            AppApprovalScopeType.Global, null, AppInstallationType.Custom);

        Assert.That(rules, Has.Count.EqualTo(1));
        Assert.That(rules[0].PackageName, Is.EqualTo("Foxit Editor"));
    }

    [Test]
    public void Filtros_em_lote_traduzem_no_Postgres()
    {
        // O provider InMemory não valida tradução de LINQ: `ToLower()` dentro de
        // `Contains` poderia estourar só em produção. ToQueryString() monta o SQL
        // sem conectar, exercitando a MESMA expressão usada pelo repositório.
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=discovery_test;Username=postgres;Password=postgres",
                npgsqlOptions => npgsqlOptions.UseVector())
            .Options;

        using var db = new DiscoveryDbContext(options);

        var customSql = AppPackageRepository
            .FilterCustomByPackageIds(db.AppPackages.AsNoTracking(), ["foxit_editor"])
            .ToQueryString();
        Assert.That(customSql, Does.Contain("lower("));
        Assert.That(customSql, Does.Contain("package_id"));

        // Winget/Chocolatey: igualdade simples, preservando o índice único.
        var wingetSql = AppPackageRepository
            .FilterByPackageIds(db.AppPackages.AsNoTracking(), AppInstallationType.Winget, ["microsoft.vscode"])
            .ToQueryString();
        Assert.That(wingetSql, Does.Not.Contain("lower("));
        Assert.That(wingetSql, Does.Contain("package_id"));
    }

    [Test]
    public async Task Delete_de_regra_inexistente_devolve_false()
    {
        await using var db = ApprovalRuleTestDbContext.New();
        var service = new AppStoreService(
            new AppApprovalRuleRepository(db), new RecordingAuditService(),
            null!, null!, null!, new AppPackageRepository(db), null!);

        // O handler converte false em NotFound (a API responde 404); antes a
        // exclusão de uma regra inexistente devolvia sucesso silencioso.
        var removed = await service.DeleteRuleAsync(Guid.NewGuid(), "motivo", "ti.user", "10.0.0.9");

        Assert.That(removed, Is.False);
    }

    /// <summary>
    /// Garante que o mapeamento EF continua batendo com a migração M226 (nome e
    /// tamanho da coluna). A M226 é a única fonte do varchar(500); um
    /// HasMaxLength divergente aqui só apareceria em runtime, no INSERT.
    /// </summary>
    [Test]
    public void Mapeamento_EF_da_coluna_reason_bate_com_a_migracao()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=discovery_test;Username=postgres;Password=postgres",
                npgsqlOptions => npgsqlOptions.UseVector())
            .Options;

        using var db = new DiscoveryDbContext(options);
        var property = db.Model
            .FindEntityType(typeof(AppApprovalRule))?
            .FindProperty(nameof(AppApprovalRule.Reason));

        Assert.That(property, Is.Not.Null);
        Assert.That(property!.GetColumnName(), Is.EqualTo("reason"));
        Assert.That(property.GetMaxLength(), Is.EqualTo(500));
    }
}

/// <summary>Auditoria em memória: registra o que o service mandou gravar.</summary>
internal sealed class RecordingAuditService : IAppApprovalAuditService
{
    public List<(AppApprovalAuditChangeType ChangeType, string? Reason, string? ChangedBy, string? IpAddress)> Entries { get; } = [];

    public Task LogAsync(
        AppApprovalAuditChangeType changeType,
        AppApprovalRule? currentRule,
        AppApprovalActionType? oldAction,
        bool? oldAutoUpdateEnabled,
        string? reason,
        string? changedBy,
        string? ipAddress)
    {
        Entries.Add((changeType, reason, changedBy, ipAddress));
        return Task.CompletedTask;
    }

    public Task<AppApprovalAuditPageDto> GetHistoryAsync(
        AppInstallationType installationType,
        string? packageId,
        AppApprovalScopeType? scopeType,
        Guid? scopeId,
        string? changedBy,
        DateTime? changedFrom,
        DateTime? changedTo,
        AppApprovalAuditChangeType? changeType,
        int limit = 100,
        Guid? cursor = null)
        => throw new NotSupportedException();
}

/// <summary>
/// Contexto InMemory restrito a AppApprovalRule + AppPackage (mesmo padrão de
/// AppPackageTestDbContext: o modelo completo não sobe no provider InMemory).
/// </summary>
internal sealed class ApprovalRuleTestDbContext(DbContextOptions<DiscoveryDbContext> options)
    : DiscoveryDbContext(options)
{
    private static readonly Type[] Mapped = [typeof(AppApprovalRule), typeof(AppPackage)];

    public static DiscoveryDbContext New()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"app-approval-rule-{Guid.NewGuid():N}")
            .Options;
        return new ApprovalRuleTestDbContext(options);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        foreach (var entityType in typeof(AppApprovalRule).Assembly.GetTypes()
                     .Where(type => type.IsClass && type.Namespace is not null
                         && type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                     .Where(type => !Mapped.Contains(type)))
        {
            modelBuilder.Ignore(entityType);
        }

        modelBuilder.Entity<AppApprovalRule>(entity => entity.HasKey(r => r.Id));
        modelBuilder.Entity<AppPackage>(entity => entity.HasKey(p => p.Id));
    }
}
