using Discovery.Core.Cqrs.AppStore.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Cqrs.AppStore;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Cadeia de heranca das regras de aprovacao da Loja.
///
/// A listagem antiga (GetRulesByScopeAsync) devolvia APENAS as regras criadas no
/// escopo filtrado: com um cliente selecionado, as regras Globais herdadas nao
/// apareciam, embora fossem aplicadas pelo agent. Estes testes cobrem o contrato
/// do endpoint /app-store/approvals/resolved:
///  - uma linha por pacote, com Sources mostrando cada nivel da cadeia;
///  - Inherited=true quando a regra que vence nao e do escopo consultado;
///  - Overridden=true quando o nivel mais especifico substitui o generico;
///  - AutoUpdate herda do nivel anterior quando a regra mais especifica nao
///    define o campo (mesma semantica de BuildEffectiveRuleState, que e o que o
///    agent aplica de verdade).
/// </summary>
[TestFixture]
public class AppStoreApprovalResolvedRulesTests
{
    private const AppInstallationType Tipo = AppInstallationType.Custom;

    private static AppApprovalRule Regra(
        AppApprovalScopeType scope,
        Guid? clientId,
        string packageId,
        AppApprovalActionType action,
        bool? autoUpdate = null,
        string? reason = null)
    {
        return new AppApprovalRule
        {
            ScopeType = scope,
            ClientId = scope == AppApprovalScopeType.Client ? clientId : null,
            InstallationType = Tipo,
            PackageId = packageId,
            Action = action,
            AutoUpdateEnabled = autoUpdate,
            Reason = reason,
        };
    }

    [Test]
    public async Task Cliente_mostra_regra_global_herdada_e_regra_propria()
    {
        await using var db = ApprovalRuleTestDbContext.New();
        var approvalRepo = new AppApprovalRuleRepository(db);
        var packageRepo = new AppPackageRepository(db);
        var clientId = Guid.NewGuid();

        db.AppPackages.Add(new AppPackage
        {
            Id = Guid.NewGuid(),
            InstallationType = Tipo,
            PackageId = "bleachbit",
            Name = "BleachBit",
            InstallCommand = "\"{{installer}}\" /S",
        });
        await db.SaveChangesAsync();

        await approvalRepo.CreateAsync(Regra(
            AppApprovalScopeType.Global, null, "bleachbit", AppApprovalActionType.Allow, false));
        await approvalRepo.CreateAsync(Regra(
            AppApprovalScopeType.Client, clientId, "firefox", AppApprovalActionType.Allow, true));

        var service = new AppStoreService(approvalRepo, null!, null!, null!, null!, packageRepo, null!);
        var page = await service.GetResolvedRulesAsync(AppApprovalScopeType.Client, clientId, Tipo);

        Assert.That(page.Count, Is.EqualTo(2));

        var herdada = page.Items.Single(item => item.PackageId == "bleachbit");
        Assert.That(herdada.Inherited, Is.True);
        Assert.That(herdada.EffectiveScopeType, Is.EqualTo(AppApprovalScopeType.Global));
        Assert.That(herdada.EffectiveScopeId, Is.Null);
        Assert.That(herdada.Overridden, Is.False);
        Assert.That(herdada.Sources, Has.Count.EqualTo(1));
        Assert.That(herdada.Sources[0].IsEffective, Is.True);
        Assert.That(herdada.PackageName, Is.EqualTo("BleachBit"));

        var propria = page.Items.Single(item => item.PackageId == "firefox");
        Assert.That(propria.Inherited, Is.False);
        Assert.That(propria.EffectiveScopeType, Is.EqualTo(AppApprovalScopeType.Client));
        Assert.That(propria.EffectiveScopeId, Is.EqualTo(clientId));
        Assert.That(propria.AutoUpdateEnabled, Is.True);
    }

    [Test]
    public async Task Cliente_sobrescreve_permissao_e_auto_update_da_global()
    {
        await using var db = ApprovalRuleTestDbContext.New();
        var approvalRepo = new AppApprovalRuleRepository(db);
        var clientId = Guid.NewGuid();

        await approvalRepo.CreateAsync(Regra(
            AppApprovalScopeType.Global, null, "google.chrome", AppApprovalActionType.Allow, false, "Padrao da TI"));
        await approvalRepo.CreateAsync(Regra(
            AppApprovalScopeType.Client, clientId, "google.chrome", AppApprovalActionType.Deny, true, "Cliente bloqueou"));

        var service = new AppStoreService(approvalRepo, null!, null!, null!, null!, new AppPackageRepository(db), null!);
        var page = await service.GetResolvedRulesAsync(AppApprovalScopeType.Client, clientId, Tipo);

        Assert.That(page.Count, Is.EqualTo(1));
        var item = page.Items[0];
        Assert.That(item.Action, Is.EqualTo(AppApprovalActionType.Deny));
        Assert.That(item.AutoUpdateEnabled, Is.True);
        Assert.That(item.Inherited, Is.False);
        Assert.That(item.Overridden, Is.True);
        Assert.That(item.Sources, Has.Count.EqualTo(2));
        Assert.That(item.Sources[0].ScopeType, Is.EqualTo(AppApprovalScopeType.Global));
        Assert.That(item.Sources[0].IsOverridden, Is.True);
        Assert.That(item.Sources[1].ScopeType, Is.EqualTo(AppApprovalScopeType.Client));
        Assert.That(item.Sources[1].IsEffective, Is.True);
    }

    [Test]
    public async Task Auto_update_da_global_e_herdado_quando_cliente_nao_define_o_campo()
    {
        await using var db = ApprovalRuleTestDbContext.New();
        var approvalRepo = new AppApprovalRuleRepository(db);
        var clientId = Guid.NewGuid();

        await approvalRepo.CreateAsync(Regra(
            AppApprovalScopeType.Global, null, "foxit.foxitreader", AppApprovalActionType.Allow, true));
        // Regra de cliente sem AutoUpdateEnabled: a acao sobrescreve, o campo herda.
        await approvalRepo.CreateAsync(Regra(
            AppApprovalScopeType.Client, clientId, "foxit.foxitreader", AppApprovalActionType.Allow, null));

        var service = new AppStoreService(approvalRepo, null!, null!, null!, null!, new AppPackageRepository(db), null!);
        var page = await service.GetResolvedRulesAsync(AppApprovalScopeType.Client, clientId, Tipo);

        var item = page.Items.Single();
        Assert.That(item.Action, Is.EqualTo(AppApprovalActionType.Allow));
        Assert.That(item.AutoUpdateEnabled, Is.True);
        Assert.That(item.Sources, Has.Count.EqualTo(2));
        Assert.That(item.Sources[1].AutoUpdateEnabled, Is.Null);
    }

    [Test]
    public async Task Busca_filtra_por_package_id_no_servidor()
    {
        await using var db = ApprovalRuleTestDbContext.New();
        var approvalRepo = new AppApprovalRuleRepository(db);
        var clientId = Guid.NewGuid();

        await approvalRepo.CreateAsync(Regra(
            AppApprovalScopeType.Client, clientId, "mozilla.firefox", AppApprovalActionType.Allow, true));
        await approvalRepo.CreateAsync(Regra(
            AppApprovalScopeType.Client, clientId, "google.chrome", AppApprovalActionType.Allow, false));

        var service = new AppStoreService(approvalRepo, null!, null!, null!, null!, new AppPackageRepository(db), null!);
        var page = await service.GetResolvedRulesAsync(AppApprovalScopeType.Client, clientId, Tipo, search: "firefox");

        Assert.That(page.Search, Is.EqualTo("firefox"));
        Assert.That(page.Count, Is.EqualTo(1));
        Assert.That(page.Items[0].PackageId, Is.EqualTo("mozilla.firefox"));
    }

    [Test]
    public async Task Upsert_rejeita_pacote_custom_inexistente()
    {
        // Antes só Winget/Chocolatey validavam o catálogo: digitar um fragmento
        // no campo de busca criava regra para um pacote que não existe.
        var db = ApprovalRuleTestDbContext.New();
        var service = new AppStoreService(
            new AppApprovalRuleRepository(db), null!, null!, null!, null!, new AppPackageRepository(db), null!);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpsertRuleAsync(
            AppApprovalScopeType.Global, null, AppInstallationType.Custom, "pacote_inexistente",
            AppApprovalActionType.Allow, false, null, "ti.user", null));

        Assert.That(ex!.Message, Does.Contain("nao encontrado"));
    }

    [Test]
    public async Task Handler_recusa_escopo_sem_id_antes_de_consultar()
    {
        // O service lançava InvalidOperationException (500) quando o escopo
        // Cliente/Site/Agente vinha sem id — a aba não consulta nesse estado,
        // mas a API precisa responder 400 em vez de estourar.
        var handler = new GetAppStoreResolvedApprovalsQueryHandler(null!);

        var result = await handler.Handle(
            new GetAppStoreResolvedApprovalsQuery(AppApprovalScopeType.Client, null, (int)Tipo),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
        Assert.That(result.Errors[0].Field, Is.EqualTo("scopeId"));
    }

    [Test]
    public async Task Handler_recusa_escopo_desconhecido()
    {
        var handler = new GetAppStoreResolvedApprovalsQueryHandler(null!);

        var result = await handler.Handle(
            new GetAppStoreResolvedApprovalsQuery((AppApprovalScopeType)99, Guid.NewGuid(), (int)Tipo),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0].Field, Is.EqualTo("scopeType"));
    }

    [Test]
    public async Task Escopo_global_nao_herda_regras_de_cliente()
    {
        await using var db = ApprovalRuleTestDbContext.New();
        var approvalRepo = new AppApprovalRuleRepository(db);
        var clientId = Guid.NewGuid();

        await approvalRepo.CreateAsync(Regra(
            AppApprovalScopeType.Global, null, "global.app", AppApprovalActionType.Allow, false));
        await approvalRepo.CreateAsync(Regra(
            AppApprovalScopeType.Client, clientId, "cliente.app", AppApprovalActionType.Allow, true));

        var service = new AppStoreService(approvalRepo, null!, null!, null!, null!, new AppPackageRepository(db), null!);
        var page = await service.GetResolvedRulesAsync(AppApprovalScopeType.Global, null, Tipo);

        Assert.That(page.Count, Is.EqualTo(1));
        Assert.That(page.Items[0].PackageId, Is.EqualTo("global.app"));
        Assert.That(page.Items[0].Inherited, Is.False);
        Assert.That(page.Items[0].Overridden, Is.False);
    }
}
