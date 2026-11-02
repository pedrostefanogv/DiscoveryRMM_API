using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AppStore.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.AppStore;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// Lixeira dos pacotes custom da Loja: soft delete (que APAGA as regras de
/// aprovação de forma obrigatória), restore, purge com limpeza de S3 (com a
/// verificação de existência quando o delete falha) e a recriação de um
/// packageId não herdar aprovação (D5). Nenhum banco real é usado.
/// </summary>
[TestFixture]
public class AppStoreSoftDeletePurgeTests
{
    // ── Soft delete ──────────────────────────────────────────────────────

    [Test]
    public async Task SoftDelete_apaga_as_regras_e_move_o_pacote_para_a_lixeira()
    {
        await using var db = StoreLifecycleTestDbContext.New();
        var (packageId, _) = await SeedPackageWithRulesAsync(db);

        var handler = NewDeleteHandler(db);
        var result = await handler.Handle(new DeleteCustomAppPackageCommand(packageId, packageId), default);

        Assert.That(result.IsSuccess, Is.True);
        var package = await db.AppPackages.SingleAsync(p => p.PackageId == packageId);
        Assert.That(package.IsDeleted, Is.True);
        Assert.That(package.DeletedAt, Is.Not.Null);
        Assert.That(await db.AppApprovalRules.CountAsync(r => r.PackageId == packageId), Is.EqualTo(0));
    }

    [Test]
    public async Task SoftDelete_sem_confirmacao_do_packageId_falha_e_nao_exclui()
    {
        await using var db = StoreLifecycleTestDbContext.New();
        var (packageId, _) = await SeedPackageWithRulesAsync(db);

        var handler = NewDeleteHandler(db);
        var result = await handler.Handle(new DeleteCustomAppPackageCommand(packageId, "outro"), default);

        Assert.That(result.IsFailure, Is.True);
        var package = await db.AppPackages.SingleAsync(p => p.PackageId == packageId);
        Assert.That(package.IsDeleted, Is.False);
    }

    [Test]
    public async Task SoftDelete_falha_quando_o_delete_de_uma_regra_falha_e_nao_exclui_o_pacote()
    {
        await using var db = StoreLifecycleTestDbContext.New();
        var (packageId, ruleIds) = await SeedPackageWithRulesAsync(db);

        var appStore = new FakeAppStoreService { FailDeleteForRuleId = ruleIds[1] };
        var handler = NewDeleteHandler(db, appStore);
        var result = await handler.Handle(new DeleteCustomAppPackageCommand(packageId, packageId), default);

        Assert.That(result.IsFailure, Is.True);
        // O pacote NÃO foi excluído e as regras seguem lá (nenhuma pela metade).
        var package = await db.AppPackages.SingleAsync(p => p.PackageId == packageId);
        Assert.That(package.IsDeleted, Is.False);
        Assert.That(await db.AppApprovalRules.CountAsync(r => r.PackageId == packageId), Is.EqualTo(2));
    }

    [Test]
    public async Task SoftDelete_exige_confirmacao_ignorando_caixa()
    {
        await using var db = StoreLifecycleTestDbContext.New();
        var (packageId, _) = await SeedPackageWithRulesAsync(db);

        var handler = NewDeleteHandler(db);
        var result = await handler.Handle(new DeleteCustomAppPackageCommand(packageId, packageId.ToUpperInvariant()), default);

        Assert.That(result.IsSuccess, Is.True);
    }

    // ── Restore ──────────────────────────────────────────────────────────

    [Test]
    public async Task Restore_remove_as_marcas_e_avisa_sobre_as_aprovacoes()
    {
        await using var db = StoreLifecycleTestDbContext.New();
        var (packageId, _) = await SeedPackageWithRulesAsync(db);

        var packages = new AppPackageRepository(db);
        await packages.SoftDeleteCustomAsync(packageId, "ti.user", "obsoleto");

        var handler = new RestoreCustomAppPackageCommandHandler(packages);
        var result = await handler.Handle(new RestoreCustomAppPackageCommand(packageId), default);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Message, Is.EqualTo("Aprovações anteriores foram excluídas — recadastre as aprovações para este pacote."));

        var package = await db.AppPackages.SingleAsync(p => p.PackageId == packageId);
        Assert.Multiple(() =>
        {
            Assert.That(package.IsDeleted, Is.False);
            Assert.That(package.DeletedAt, Is.Null);
            Assert.That(package.DeleteReason, Is.Null);
        });
    }

    // ── Purge ────────────────────────────────────────────────────────────

    [Test]
    public async Task Purge_feliz_apaga_assets_e_a_linha()
    {
        await using var db = StoreLifecycleTestDbContext.New();
        var (packageId, _) = await SeedPackageWithRulesAsync(db);

        var packages = new AppPackageRepository(db);
        await packages.SoftDeleteCustomAsync(packageId, "ti.user", null);

        var assets = new FakeAssets();
        var handler = NewPurgeHandler(db, assets);
        var result = await handler.Handle(new PurgeCustomAppPackageCommand(packageId, packageId), default);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.PurgeFailed, Is.False);
        Assert.That(await db.AppPackages.CountAsync(p => p.PackageId == packageId), Is.EqualTo(0));
        Assert.That(assets.DeletedKeys, Is.Not.Empty);
    }

    [Test]
    public async Task Purge_com_falha_no_storage_e_objeto_existente_retém_a_linha()
    {
        await using var db = StoreLifecycleTestDbContext.New();
        var (packageId, _) = await SeedPackageWithRulesAsync(db);

        var packages = new AppPackageRepository(db);
        await packages.SoftDeleteCustomAsync(packageId, "ti.user", null);

        // O delete no storage falha E o objeto continua lá (Exists = true).
        var assets = new FakeAssets { ThrowOnDelete = true, ExistsByKey = { ["app-store/custom/installers/x"] = true } };
        var handler = NewPurgeHandler(db, assets);
        var result = await handler.Handle(new PurgeCustomAppPackageCommand(packageId, packageId), default);

        Assert.That(result.IsSuccess, Is.True); // resultado estruturado, não exceção
        Assert.That(result.Value!.PurgeFailed, Is.True);
        Assert.That(result.Value.ObjectKey, Is.Not.Null);

        var package = await db.AppPackages.SingleAsync(p => p.PackageId == packageId);
        Assert.That(package.IsDeleted, Is.True, "a linha fica retida na lixeira");
        Assert.That(package.PurgeFailedAt, Is.Not.Null);
        Assert.That(package.PurgeError, Is.Not.Empty);
    }

    [Test]
    public async Task Purge_com_falha_no_storage_e_verificação_falhando_também_retém_a_linha()
    {
        await using var db = StoreLifecycleTestDbContext.New();
        var (packageId, _) = await SeedPackageWithRulesAsync(db);

        var packages = new AppPackageRepository(db);
        await packages.SoftDeleteCustomAsync(packageId, "ti.user", null);

        // Delete falha e a VERIFICAÇÃO falha (null = não sei).
        var assets = new FakeAssets { ThrowOnDelete = true, ThrowOnExists = true };
        var handler = NewPurgeHandler(db, assets);
        var result = await handler.Handle(new PurgeCustomAppPackageCommand(packageId, packageId), default);

        Assert.That(result.Value!.PurgeFailed, Is.True);
        Assert.That((await db.AppPackages.SingleAsync(p => p.PackageId == packageId)).IsDeleted, Is.True);
    }

    [Test]
    public async Task Purge_com_falha_no_storage_e_objeto_inexistente_prossegue()
    {
        await using var db = StoreLifecycleTestDbContext.New();
        var (packageId, _) = await SeedPackageWithRulesAsync(db);

        var packages = new AppPackageRepository(db);
        await packages.SoftDeleteCustomAsync(packageId, "ti.user", null);

        // Delete falha, mas o objeto NÃO existe mais: o purge do banco prossegue.
        var assets = new FakeAssets { ThrowOnDelete = true, ExistsByKey = { ["app-store/custom/installers/x"] = false } };
        var handler = NewPurgeHandler(db, assets);
        var result = await handler.Handle(new PurgeCustomAppPackageCommand(packageId, packageId), default);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.PurgeFailed, Is.False);
        Assert.That(await db.AppPackages.CountAsync(p => p.PackageId == packageId), Is.EqualTo(0));
    }

    [Test]
    public async Task Purge_forcado_apaga_a_linha_sem_tocar_no_storage()
    {
        await using var db = StoreLifecycleTestDbContext.New();
        var (packageId, _) = await SeedPackageWithRulesAsync(db);

        var packages = new AppPackageRepository(db);
        await packages.SoftDeleteCustomAsync(packageId, "ti.user", null);

        var assets = new FakeAssets { ThrowOnDelete = true, ExistsByKey = { ["app-store/custom/installers/x"] = true } };
        var handler = NewPurgeHandler(db, assets);
        var result = await handler.Handle(new PurgeCustomAppPackageCommand(packageId, packageId, Force: true), default);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.PurgeFailed, Is.False);
        Assert.That(await db.AppPackages.CountAsync(p => p.PackageId == packageId), Is.EqualTo(0));
    }

    [Test]
    public async Task Purge_só_aceita_pacote_que_está_na_lixeira()
    {
        await using var db = StoreLifecycleTestDbContext.New();
        var (packageId, _) = await SeedPackageWithRulesAsync(db);

        var handler = NewPurgeHandler(db, new FakeAssets());
        var result = await handler.Handle(new PurgeCustomAppPackageCommand(packageId, packageId), default);

        Assert.That(result.IsFailure, Is.True);
    }

    // ── D5: recriação não herda aprovação ────────────────────────────────

    [Test]
    public async Task Upsert_de_criacao_descarta_regras_orfas_do_packageId()
    {
        await using var db = StoreLifecycleTestDbContext.New();
        var approvalRepo = new AppApprovalRuleRepository(db);
        var audit = new RecordingAuditService();

        await approvalRepo.CreateAsync(new AppApprovalRule
        {
            ScopeType = AppApprovalScopeType.Global,
            InstallationType = AppInstallationType.Custom,
            PackageId = "foxitzip",
            Action = AppApprovalActionType.Allow,
        });

        var service = NewService(db, approvalRepo, audit);
        var dto = await service.UpsertCustomCatalogPackageAsync(new UpsertCustomAppCatalogPackageRequest
        {
            PackageId = "foxitzip",
            Name = "Foxit ZIP",
            InstallCommand = "install.cmd",
        });

        Assert.That(dto.Id, Is.Not.Empty);
        Assert.That(await db.AppApprovalRules.CountAsync(r => r.PackageId == "foxitzip"), Is.EqualTo(0));
        Assert.That(audit.Entries, Has.Count.EqualTo(1));
        Assert.That(audit.Entries[0].Reason, Does.Contain("recriação").IgnoreCase);
    }

    [Test]
    public async Task Upsert_de_pacote_na_lixeira_também_descarta_as_regras()
    {
        await using var db = StoreLifecycleTestDbContext.New();
        var approvalRepo = new AppApprovalRuleRepository(db);
        var packages = new AppPackageRepository(db);
        var audit = new RecordingAuditService();

        var (packageId, _) = await SeedPackageWithRulesAsync(db);
        await packages.SoftDeleteCustomAsync(packageId, "ti.user", null);

        var service = NewService(db, approvalRepo, audit);
        await service.UpsertCustomCatalogPackageAsync(new UpsertCustomAppCatalogPackageRequest
        {
            PackageId = packageId,
            Name = "Recriado",
            InstallCommand = "install.cmd",
        });

        Assert.That(await db.AppApprovalRules.CountAsync(r => r.PackageId == packageId), Is.EqualTo(0));
        var package = await db.AppPackages.SingleAsync(p => p.PackageId == packageId);
        Assert.That(package.IsDeleted, Is.False, "o upsert recria/retira da lixeira");
    }

    // ── Regras para pacote excluído ──────────────────────────────────────

    [Test]
    public async Task UpsertRule_recusa_pacote_custom_na_lixeira()
    {
        await using var db = StoreLifecycleTestDbContext.New();
        var (packageId, _) = await SeedPackageWithRulesAsync(db);

        var packages = new AppPackageRepository(db);
        await packages.SoftDeleteCustomAsync(packageId, "ti.user", null);

        var service = NewService(db, new AppApprovalRuleRepository(db), new RecordingAuditService());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpsertRuleAsync(
            AppApprovalScopeType.Global, null, AppInstallationType.Custom, packageId,
            AppApprovalActionType.Allow, false, null, "ti.user", null));

        Assert.That(ex!.Message, Does.Contain(packageId));
    }

    // ── Política efetiva / catálogo ignoram excluídos ────────────────────

    [Test]
    public async Task Leituras_de_catalogo_ignoram_pacotes_na_lixeira()
    {
        await using var db = StoreLifecycleTestDbContext.New();
        var (packageId, _) = await SeedPackageWithRulesAsync(db);

        var packages = new AppPackageRepository(db);
        await packages.SoftDeleteCustomAsync(packageId, "ti.user", null);

        Assert.That(await packages.GetAllByInstallationTypeAsync(AppInstallationType.Custom), Has.Count.EqualTo(0));

        var (items, _) = await packages.SearchPageAsync(AppInstallationType.Custom, null, null, null, 10);
        Assert.That(items, Has.Count.EqualTo(0));

        Assert.That(await packages.GetByInstallationTypeAndPackageIdAsync(AppInstallationType.Custom, packageId), Is.Null);
        Assert.That(await packages.GetByInstallationTypeAndPackageIdAsync(AppInstallationType.Custom, packageId, includeDeleted: true), Is.Not.Null);

        // Referência de asset CONTINUA contando a lixeira (não apagar arquivo em restore).
        var package = await packages.GetByInstallationTypeAndPackageIdAsync(AppInstallationType.Custom, packageId, includeDeleted: true);
        Assert.That(await packages.IsAssetObjectKeyReferencedAsync(package!.FileObjectKey!), Is.True);
    }

    [Test]
    public async Task ListCustomTrash_só_devolve_excluídos_com_paginacao()
    {
        await using var db = StoreLifecycleTestDbContext.New();
        var packages = new AppPackageRepository(db);

        await SeedPackageAsync(db, "aaa_lixeira");
        await SeedPackageAsync(db, "bbb_ativo");
        await packages.SoftDeleteCustomAsync("aaa_lixeira", "ti.user", "teste");

        var (items, hasMore) = await packages.ListCustomTrashAsync("lixeira", null, 10);
        Assert.That(items.Select(p => p.PackageId), Is.EquivalentTo(["aaa_lixeira"]));
        Assert.That(hasMore, Is.False);

        // Busca por pacote ativo não traz nada: ele não está na lixeira.
        var (none, _) = await packages.ListCustomTrashAsync("bbb_ativo", null, 10);
        Assert.That(none, Is.Empty);
    }

    // ── Órfãs: enriquecimento + limpeza ──────────────────────────────────

    [Test]
    public async Task Listagem_de_regras_marca_orfa_quando_o_pacote_esta_na_lixeira()
    {
        await using var db = StoreLifecycleTestDbContext.New();
        var (packageId, _) = await SeedPackageWithRulesAsync(db);

        var packages = new AppPackageRepository(db);
        await packages.SoftDeleteCustomAsync(packageId, "ti.user", null);

        var service = NewService(db, new AppApprovalRuleRepository(db), new RecordingAuditService());
        var rules = await service.GetRulesByScopeAsync(AppApprovalScopeType.Global, null, AppInstallationType.Custom);

        // Duas regras (Allow + Deny) apontam para o MESMO packageId.
        Assert.That(
            rules.Where(r => r.PackageId == packageId).Select(r => r.HasPackage),
            Is.All.False,
            "pacote na lixeira = regra órfã");
    }

    [Test]
    public async Task Cleanup_de_orfas_dry_run_nao_remove_e_apply_remove_com_auditoria()
    {
        await using var db = StoreLifecycleTestDbContext.New();
        var (packageId, _) = await SeedPackageWithRulesAsync(db);

        var packages = new AppPackageRepository(db);
        await packages.SoftDeleteCustomAsync(packageId, "ti.user", null);

        var service = NewService(db, new AppApprovalRuleRepository(db), new RecordingAuditService());

        var dryRun = await service.CleanupOrphanApprovalRulesAsync(dryRun: true, changedBy: "ti.user");
        // Duas regras do mesmo packageId aparecem como duas entradas órfãs.
        Assert.That(dryRun.Orphans.Select(o => o.PackageId).Distinct(), Is.EquivalentTo([packageId]));
        Assert.That(dryRun.Orphans, Has.Count.EqualTo(2));
        Assert.That(dryRun.Removed, Is.EqualTo(0));
        Assert.That(await db.AppApprovalRules.CountAsync(r => r.PackageId == packageId), Is.EqualTo(2));

        var apply = await service.CleanupOrphanApprovalRulesAsync(dryRun: false, changedBy: "ti.user");
        Assert.That(apply.Removed, Is.EqualTo(2));
        Assert.That(await db.AppApprovalRules.CountAsync(r => r.PackageId == packageId), Is.EqualTo(0));
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static DeleteCustomAppPackageCommandHandler NewDeleteHandler(DiscoveryDbContext db, FakeAppStoreService? appStore = null)
    {
        var approvalRepo = new AppApprovalRuleRepository(db);
        return new(
            new AppPackageRepository(db),
            approvalRepo,
            new AppApprovalAuditRepository(db),
            appStore ?? new FakeAppStoreService(approvalRepo));
    }

    private static PurgeCustomAppPackageCommandHandler NewPurgeHandler(DiscoveryDbContext db, FakeAssets assets)
    {
        var approvalRepo = new AppApprovalRuleRepository(db);
        return new(
            new AppPackageRepository(db),
            approvalRepo,
            new AppApprovalAuditRepository(db),
            new FakeAppStoreService(approvalRepo),
            assets,
            NullLogger<PurgeCustomAppPackageCommandHandler>.Instance);
    }

    private static AppStoreService NewService(
        DiscoveryDbContext db, IAppApprovalRuleRepository approvalRepo, IAppApprovalAuditService audit)
        => new(approvalRepo, audit, null!, null!, null!, new AppPackageRepository(db), new FakeAssets());

    private static async Task<(string PackageId, List<Guid> RuleIds)> SeedPackageWithRulesAsync(DiscoveryDbContext db)
    {
        const string packageId = "foxitzip";
        await SeedPackageAsync(db, packageId);

        var approvalRepo = new AppApprovalRuleRepository(db);
        var ids = new List<Guid>();
        foreach (var action in new[] { AppApprovalActionType.Allow, AppApprovalActionType.Deny })
        {
            var rule = await approvalRepo.CreateAsync(new AppApprovalRule
            {
                ScopeType = AppApprovalScopeType.Global,
                InstallationType = AppInstallationType.Custom,
                PackageId = packageId,
                Action = action,
            });
            ids.Add(rule.Id);
        }

        return (packageId, ids);
    }

    private static async Task SeedPackageAsync(DiscoveryDbContext db, string packageId)
    {
        db.AppPackages.Add(new AppPackage
        {
            Id = Guid.NewGuid(),
            InstallationType = AppInstallationType.Custom,
            PackageId = packageId,
            Name = packageId + " name",
            InstallCommand = "install.cmd",
            FileObjectKey = "app-store/custom/installers/x",
            FileName = "setup.exe",
        });
        await db.SaveChangesAsync();
    }
}

/// <summary>
/// Double de IAppStoreService para os handlers: apenas DeleteRuleAsync tem
/// comportamento (com falha configurável); o resto falha explícito.
/// </summary>
internal sealed class FakeAppStoreService(IAppApprovalRuleRepository? approvalRepo = null) : IAppStoreService
{
    public Guid? FailDeleteForRuleId { get; set; }
    public List<Guid> DeletedRuleIds { get; } = [];

    public Task<bool> DeleteRuleAsync(Guid ruleId, CancellationToken cancellationToken = default)
        => DeleteRuleAsync(ruleId, null, null, null, cancellationToken);

    public async Task<bool> DeleteRuleAsync(Guid ruleId, string? reason, string? changedBy, string? ipAddress, CancellationToken cancellationToken = default)
    {
        if (FailDeleteForRuleId == ruleId)
            return false;

        // Mesma semântica do service real: carrega, audita não (fake), remove.
        var existing = approvalRepo is null ? null : await approvalRepo.GetByIdAsync(ruleId);
        if (existing is null)
            return false;

        await approvalRepo!.DeleteAsync(ruleId);
        DeletedRuleIds.Add(ruleId);
        return true;
    }

    public Task<AppCatalogSearchResultDto> SearchCatalogAsync(AppInstallationType installationType, string? search, string? architecture, int limit, string? cursor, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<AppCatalogPackageDto?> GetCatalogPackageByIdAsync(AppInstallationType installationType, string packageId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<AppCatalogPackageDto> UpsertCustomCatalogPackageAsync(UpsertCustomAppCatalogPackageRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<bool> DeleteCustomPackageFileAsync(string packageId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<bool> DeleteCustomPackageIconAsync(string packageId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<AppApprovalRuleResolvedDto> UpsertRuleAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, string packageId, AppApprovalActionType action, bool? autoUpdateEnabled, string? reason, string? changedBy, string? ipAddress, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<AppApprovalRuleResolvedDto>> GetRulesByScopeAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<AppApprovalResolvedPageDto> GetResolvedRulesAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, string? search = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<AppApprovalAuditPageDto> GetAuditHistoryAsync(AppInstallationType installationType, string? packageId, AppApprovalScopeType? scopeType, Guid? scopeId, string? changedBy, DateTime? changedFrom, DateTime? changedTo, AppApprovalAuditChangeType? changeType, int limit, Guid? cursor, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<EffectiveApprovedAppDto>> GetEffectiveAppsAsync(Guid? clientId, Guid? siteId, Guid? agentId, AppInstallationType installationType, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<bool> IsPackageApprovedAsync(Guid? clientId, Guid? siteId, Guid? agentId, AppInstallationType installationType, string packageId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<EffectiveApprovedAppPageDto> GetEffectiveAppsPageAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, string? search, int limit, string? cursor, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<AppApprovalPackageDiffDto> GetPackageDiffAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, string packageId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<AppEffectivePackageDiffPageDto> GetEffectiveAppDiffsAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, string? search, int limit, string? cursor, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

/// <summary>Double de ICustomPackageAssetService com falha/verificação configuráveis.</summary>
internal sealed class FakeAssets : ICustomPackageAssetService
{
    public bool ThrowOnDelete { get; set; }
    public bool ThrowOnExists { get; set; }
    public List<string> DeletedKeys { get; } = [];
    public Dictionary<string, bool> ExistsByKey { get; } = new(StringComparer.Ordinal);

    public Task<CustomPackageAssetDto> UploadAsync(string kind, string fileName, string contentType, Stream content, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<CustomPackageAssetContent> DownloadAsync(string objectKey, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task WriteToAsync(string objectKey, Stream destination, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        if (ThrowOnDelete)
            throw new InvalidOperationException("storage down");
        DeletedKeys.Add(objectKey);
        return Task.CompletedTask;
    }

    public Task<bool?> ExistsAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        if (ThrowOnExists)
            throw new InvalidOperationException("storage check down");
        return Task.FromResult<bool?>(ExistsByKey.TryGetValue(objectKey, out var exists) ? exists : true);
    }
}

/// <summary>
/// Contexto InMemory com as três entidades do ciclo de vida da loja custom
/// (AppPackage, AppApprovalRule e AppApprovalAudit) — o modelo completo não
/// sobe no provider InMemory (jsonb/vector).
/// </summary>
internal sealed class StoreLifecycleTestDbContext(DbContextOptions<DiscoveryDbContext> options)
    : DiscoveryDbContext(options)
{
    private static readonly Type[] Mapped = [typeof(AppPackage), typeof(AppApprovalRule), typeof(AppApprovalAudit)];

    public static DiscoveryDbContext New()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"store-lifecycle-{Guid.NewGuid():N}")
            .Options;
        return new StoreLifecycleTestDbContext(options);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        foreach (var entityType in typeof(AppPackage).Assembly.GetTypes()
                     .Where(type => type.IsClass && type.Namespace is not null
                         && type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                     .Where(type => !Mapped.Contains(type)))
        {
            modelBuilder.Ignore(entityType);
        }

        modelBuilder.Entity<AppPackage>(entity => entity.HasKey(p => p.Id));
        modelBuilder.Entity<AppApprovalRule>(entity => entity.HasKey(r => r.Id));
        modelBuilder.Entity<AppApprovalAudit>(entity => entity.HasKey(a => a.Id));
    }
}
