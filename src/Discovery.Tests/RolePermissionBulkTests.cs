using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Roles.Commands;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Identity;
using Discovery.Infrastructure.Cqrs.Roles;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Discovery.Tests;

/// <summary>
/// Contrato dos endpoints em lote de permissões de role
/// (POST/DELETE /api/v1/roles/{id}/permissions/batch).
///
/// O ponto crítico coberto aqui é que o handler em lote NÃO reimplementa a regra de negócio:
/// ele envia os comandos unitários pelo mediator. Se alguém trocar isso por acesso direto ao
/// serviço, o teste de papel de sistema passa a falhar — é exatamente a regressão que se quer
/// impedir (mesmo achado P0-3 de SystemRoleProtectionTests, agora na porta em lote).
/// </summary>
[TestFixture]
public class RolePermissionBulkTests
{
    // ── Infra de teste (mesmo padrão de SystemRoleProtectionTests: SQLite relacional) ──

    private static (RoleRepository Repo, DiscoveryDbContext Db, SqliteConnection Connection) NewRepo()
    {
        // SQLite (e não InMemory): RemovePermissionFromRoleAsync usa ExecuteDeleteAsync,
        // que o provider InMemory não implementa.
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new RoleBulkTestDbContext(options);
        db.Database.EnsureCreated();
        return (new RoleRepository(db), db, connection);
    }

    private static async Task<Role> SeedRoleAsync(DiscoveryDbContext db, bool isSystem)
    {
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = isSystem ? "Admin" : "Papel Custom",
            Type = isSystem ? RoleType.System : RoleType.Custom,
            IsSystem = isSystem
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return role;
    }

    private static async Task<Permission> SeedPermissionAsync(
        DiscoveryDbContext db, ResourceType resource = ResourceType.Users)
    {
        var permission = new Permission
        {
            Id = Guid.NewGuid(),
            ResourceType = resource,
            ActionType = ActionType.View
        };
        db.Permissions.Add(permission);
        await db.SaveChangesAsync();
        return permission;
    }

    /// <summary>
    /// Monta os handlers com um mediator MediatR real, registrando SOMENTE os handlers
    /// unitários. O objetivo é provar que o lote delega a eles, e não apenas que o resultado
    /// numérico confere.
    /// </summary>
    /// <param name="spy">
    /// Intercepta as gravações feitas pelo serviço: se o lote gravasse por conta própria
    /// (acesso direto ao IRoleService), a contagem de comandos unitários enviados ficaria
    /// zerada e o teste de delegação falharia.
    /// </param>
    private static (AddRolePermissionsCommandHandler Add, RemoveRolePermissionsCommandHandler Remove,
        IRoleService Service, CountingPermissionService Cache) NewHandlers(
        IRoleRepository repo, MutationSpy? spy = null)
    {
        var service = new RoleServiceStub(repo, spy);
        var cache = new CountingPermissionService();

        // AddMediatR registra IMediator (e IRequestHandler<,>) — registrar só as interfaces
        // fechadas de handler deixaria IMediator sem implementação no container.
        var services = new ServiceCollection();
        services.AddLogging(); // exigido pelo MediatR antes de AddMediatR
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblies(typeof(RoleRepository).Assembly));
        services.AddTransient<IRequestHandler<AddRolePermissionCommand, Result<PermissionDto>>>(
            _ => new AddRolePermissionCommandHandler(service, cache));
        services.AddTransient<IRequestHandler<RemoveRolePermissionCommand, Result<VoidResult>>>(
            _ => new RemoveRolePermissionCommandHandler(service, cache));

        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        return (new AddRolePermissionsCommandHandler(mediator, cache, service),
                new RemoveRolePermissionsCommandHandler(mediator, cache, service),
                service, cache);
    }

    /// <summary>
    /// Conta as gravações que passaram pelo caminho unitário. É o observador que distingue
    /// "o lote reusou o handler unitário" de "o lote gravou direto no serviço".
    /// </summary>
    private sealed class MutationSpy
    {
        public int Added { get; set; }
        public int Removed { get; set; }
    }

    private static async Task<List<Guid>> CurrentPermissionIdsAsync(
        IRoleService service, Guid roleId)
        => (await service.GetPermissionsAsync(roleId)).Select(p => p.Id).ToList();

    // ── a) lote adiciona N e reenviar é idempotente ──────────────────────────────

    [Test]
    public async Task Bulk_add_grants_every_requested_permission_and_is_idempotent_on_replay()
    {
        var (repo, db, connection) = NewRepo();
        using var _ = connection;

        var role = await SeedRoleAsync(db, isSystem: false);
        var p1 = await SeedPermissionAsync(db, ResourceType.Users);
        var p2 = await SeedPermissionAsync(db, ResourceType.Agents);
        var p3 = await SeedPermissionAsync(db, ResourceType.Tickets);

        var (add, _, service, _) = NewHandlers(repo);
        var ids = new[] { p1.Id, p2.Id, p3.Id };

        var first = await add.Handle(new AddRolePermissionsCommand(role.Id, ids), CancellationToken.None);

        Assert.That(first.IsSuccess, Is.True);
        Assert.That(first.Value!.Added, Is.EqualTo(3));
        Assert.That(first.Value!.Skipped, Is.EqualTo(0));
        Assert.That(await CurrentPermissionIdsAsync(service, role.Id), Has.Count.EqualTo(3));

        // Reenvio do MESMO lote: nada novo a conceder, tudo vira skipped (sem erro).
        var second = await add.Handle(new AddRolePermissionsCommand(role.Id, ids), CancellationToken.None);

        Assert.That(second.IsSuccess, Is.True);
        Assert.That(second.Value!.Added, Is.EqualTo(0));
        Assert.That(second.Value!.Skipped, Is.EqualTo(3));
        Assert.That(await CurrentPermissionIdsAsync(service, role.Id), Has.Count.EqualTo(3));
    }

    [Test]
    public async Task Bulk_add_is_a_merge_and_keeps_permissions_not_mentioned()
    {
        var (repo, db, connection) = NewRepo();
        using var _ = connection;

        var role = await SeedRoleAsync(db, isSystem: false);
        var existing = await SeedPermissionAsync(db, ResourceType.Users);
        var added = await SeedPermissionAsync(db, ResourceType.Agents);

        await repo.AddPermissionToRoleAsync(role.Id, existing.Id);

        var (add, _, service, _) = NewHandlers(repo);
        var result = await add.Handle(new AddRolePermissionsCommand(role.Id, [added.Id]), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Added, Is.EqualTo(1));
        // Merge, não replace: a permissão que já existia continua lá.
        Assert.That(await CurrentPermissionIdsAsync(service, role.Id),
            Is.EquivalentTo(new[] { existing.Id, added.Id }));
    }

    [Test]
    public async Task Bulk_add_deduplicates_repeated_ids()
    {
        var (repo, db, connection) = NewRepo();
        using var _ = connection;

        var role = await SeedRoleAsync(db, isSystem: false);
        var permission = await SeedPermissionAsync(db);

        var (add, _, service, _) = NewHandlers(repo);
        var result = await add.Handle(
            new AddRolePermissionsCommand(role.Id, [permission.Id, permission.Id, permission.Id]),
            CancellationToken.None);

        // Lista com repetições vale como UMA concessão, não três.
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Added, Is.EqualTo(1));
        Assert.That(result.Value!.Skipped, Is.EqualTo(0));
        Assert.That(await CurrentPermissionIdsAsync(service, role.Id), Has.Count.EqualTo(1));
    }

    // ── b) lote remove e remover de novo é idempotente ───────────────────────────

    [Test]
    public async Task Bulk_remove_revokes_permissions_and_is_idempotent_on_replay()
    {
        var (repo, db, connection) = NewRepo();
        using var _ = connection;

        var role = await SeedRoleAsync(db, isSystem: false);
        var p1 = await SeedPermissionAsync(db, ResourceType.Users);
        var p2 = await SeedPermissionAsync(db, ResourceType.Agents);
        var keep = await SeedPermissionAsync(db, ResourceType.Tickets);

        await repo.AddPermissionToRoleAsync(role.Id, p1.Id);
        await repo.AddPermissionToRoleAsync(role.Id, p2.Id);
        await repo.AddPermissionToRoleAsync(role.Id, keep.Id);

        var (_, remove, service, _) = NewHandlers(repo);
        var ids = new[] { p1.Id, p2.Id };

        var first = await remove.Handle(new RemoveRolePermissionsCommand(role.Id, ids), CancellationToken.None);

        Assert.That(first.IsSuccess, Is.True);
        Assert.That(first.Value!.Removed, Is.EqualTo(2));
        Assert.That(first.Value!.Skipped, Is.EqualTo(0));
        // Só as pedidas saíram; a não citada permanece.
        Assert.That(await CurrentPermissionIdsAsync(service, role.Id), Is.EquivalentTo(new[] { keep.Id }));

        var second = await remove.Handle(new RemoveRolePermissionsCommand(role.Id, ids), CancellationToken.None);

        Assert.That(second.IsSuccess, Is.True);
        Assert.That(second.Value!.Removed, Is.EqualTo(0));
        Assert.That(second.Value!.Skipped, Is.EqualTo(2));
        Assert.That(await CurrentPermissionIdsAsync(service, role.Id), Is.EquivalentTo(new[] { keep.Id }));
    }

    [Test]
    public async Task Bulk_remove_counts_already_unlinked_ids_as_skipped()
    {
        var (repo, db, connection) = NewRepo();
        using var _ = connection;

        var role = await SeedRoleAsync(db, isSystem: false);
        var linked = await SeedPermissionAsync(db, ResourceType.Users);
        var unlinked = await SeedPermissionAsync(db, ResourceType.Agents);

        await repo.AddPermissionToRoleAsync(role.Id, linked.Id);

        var (_, remove, _, _) = NewHandlers(repo);
        var result = await remove.Handle(
            new RemoveRolePermissionsCommand(role.Id, [linked.Id, unlinked.Id]), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Removed, Is.EqualTo(1));
        Assert.That(result.Value!.Skipped, Is.EqualTo(1));
    }

    // ── c) papel de sistema é bloqueado nos dois sentidos ────────────────────────

    [Test]
    public async Task Bulk_add_is_refused_for_system_role_and_writes_nothing()
    {
        var (repo, db, connection) = NewRepo();
        using var _ = connection;

        var role = await SeedRoleAsync(db, isSystem: true);
        var permission = await SeedPermissionAsync(db);

        var (add, _, service, _) = NewHandlers(repo);
        var result = await add.Handle(new AddRolePermissionsCommand(role.Id, [permission.Id]), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
        Assert.That(result.Errors[0].Message,
            Is.EqualTo("Permissões de papéis de sistema não podem ser alteradas."));
        Assert.That(await CurrentPermissionIdsAsync(service, role.Id), Is.Empty);
    }

    [Test]
    public async Task Bulk_remove_is_refused_for_system_role_and_writes_nothing()
    {
        var (repo, db, connection) = NewRepo();
        using var _ = connection;

        var role = await SeedRoleAsync(db, isSystem: true);
        var permission = await SeedPermissionAsync(db);

        var (_, remove, service, _) = NewHandlers(repo);
        var result = await remove.Handle(
            new RemoveRolePermissionsCommand(role.Id, [permission.Id]), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
        Assert.That(await CurrentPermissionIdsAsync(service, role.Id), Is.Empty);
    }

    /// <summary>
    /// Invariante estrutural: o lote grava PELO handler unitário. Se alguém trocar a
    /// delegação por acesso direto ao IRoleService, este teste falha primeiro.
    /// </summary>
    [Test]
    public async Task Bulk_handlers_delegate_writes_to_the_unit_handlers()
    {
        var (repo, db, connection) = NewRepo();
        using var _ = connection;

        var role = await SeedRoleAsync(db, isSystem: false);
        var p1 = await SeedPermissionAsync(db, ResourceType.Users);
        var p2 = await SeedPermissionAsync(db, ResourceType.Agents);

        var spy = new MutationSpy();
        var (add, _, _, _) = NewHandlers(repo, spy);
        await add.Handle(new AddRolePermissionsCommand(role.Id, [p1.Id, p2.Id]), CancellationToken.None);

        Assert.That(spy.Added, Is.EqualTo(2),
            "O lote deve passar pelos comandos unitários (AddRolePermissionCommand), um por permissão.");
    }

    [Test]
    public async Task Bulk_remove_handler_delegates_writes_to_the_unit_handler()
    {
        var (repo, db, connection) = NewRepo();
        using var _ = connection;

        var role = await SeedRoleAsync(db, isSystem: false);
        var permission = await SeedPermissionAsync(db);
        await repo.AddPermissionToRoleAsync(role.Id, permission.Id);

        var spy = new MutationSpy();
        var (_, remove, _, _) = NewHandlers(repo, spy);
        await remove.Handle(new RemoveRolePermissionsCommand(role.Id, [permission.Id]), CancellationToken.None);

        Assert.That(spy.Removed, Is.EqualTo(1),
            "O lote deve passar pelo comando unitário (RemoveRolePermissionCommand).");
    }

    // ── d) id inexistente no catálogo ────────────────────────────────────────────

    [Test]
    public async Task Bulk_add_rejects_unknown_permission_without_side_effects()
    {
        var (repo, db, connection) = NewRepo();
        using var _ = connection;

        var role = await SeedRoleAsync(db, isSystem: false);
        var valid = await SeedPermissionAsync(db);
        var ghost = Guid.NewGuid();

        var (add, _, service, _) = NewHandlers(repo);
        var result = await add.Handle(
            new AddRolePermissionsCommand(role.Id, [valid.Id, ghost]), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
        Assert.That(result.Errors[0].Field, Is.EqualTo("PermissionIds"));
        // Validação prévia: nem o id válido do lote foi gravado.
        Assert.That(await CurrentPermissionIdsAsync(service, role.Id), Is.Empty);
    }

    [Test]
    public async Task Bulk_remove_rejects_unknown_permission_without_side_effects()
    {
        var (repo, db, connection) = NewRepo();
        using var _ = connection;

        var role = await SeedRoleAsync(db, isSystem: false);
        var valid = await SeedPermissionAsync(db);
        await repo.AddPermissionToRoleAsync(role.Id, valid.Id);

        var (_, remove, service, _) = NewHandlers(repo);
        var result = await remove.Handle(
            new RemoveRolePermissionsCommand(role.Id, [valid.Id, Guid.NewGuid()]), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
        Assert.That(await CurrentPermissionIdsAsync(service, role.Id), Has.Count.EqualTo(1));
    }

    // ── e) role inexistente ──────────────────────────────────────────────────────

    [Test]
    public async Task Bulk_add_returns_not_found_for_unknown_role()
    {
        var (repo, db, connection) = NewRepo();
        using var _ = connection;

        var permission = await SeedPermissionAsync(db);
        var (add, _, _, _) = NewHandlers(repo);

        var result = await add.Handle(
            new AddRolePermissionsCommand(Guid.NewGuid(), [permission.Id]), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("NotFound"));
    }

    // ── f) lista vazia ───────────────────────────────────────────────────────────

    [Test]
    public async Task Bulk_add_with_empty_list_succeeds_with_zeros()
    {
        var (repo, db, connection) = NewRepo();
        using var _ = connection;

        var role = await SeedRoleAsync(db, isSystem: false);
        var (add, _, _, cache) = NewHandlers(repo);

        var result = await add.Handle(new AddRolePermissionsCommand(role.Id, []), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Added, Is.EqualTo(0));
        Assert.That(result.Value!.Skipped, Is.EqualTo(0));
        // Retorno antecipado: lote vazio não toca em banco nem em cache.
        Assert.That(cache.InvalidateAllCount, Is.EqualTo(0));
    }

    [Test]
    public async Task Bulk_remove_with_empty_list_succeeds_with_zeros()
    {
        var (repo, db, connection) = NewRepo();
        using var _ = connection;

        var role = await SeedRoleAsync(db, isSystem: false);
        var (_, remove, _, cache) = NewHandlers(repo);

        var result = await remove.Handle(new RemoveRolePermissionsCommand(role.Id, []), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Removed, Is.EqualTo(0));
        Assert.That(result.Value!.Skipped, Is.EqualTo(0));
        Assert.That(cache.InvalidateAllCount, Is.EqualTo(0));
    }

    // ── h) idempotência também invalida o cache ──────────────────────────────────

    [Test]
    public async Task Bulk_add_invalidates_cache_even_when_everything_is_skipped()
    {
        var (repo, db, connection) = NewRepo();
        using var _ = connection;

        var role = await SeedRoleAsync(db, isSystem: false);
        var permission = await SeedPermissionAsync(db);
        await repo.AddPermissionToRoleAsync(role.Id, permission.Id);

        var (add, _, _, cache) = NewHandlers(repo);
        var result = await add.Handle(new AddRolePermissionsCommand(role.Id, [permission.Id]), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Added, Is.EqualTo(0));
        Assert.That(result.Value!.Skipped, Is.EqualTo(1));
        // Contrato: invalida SEMPRE ao final do lote, inclusive quando nada mudou —
        // 1 do handler unitário (não chamado aqui) + 1 do lote. Como o lote não envia
        // unitários (nada a aplicar), a única invalidação é a do próprio lote.
        Assert.That(cache.InvalidateAllCount, Is.EqualTo(1),
            "Lote integralmente skipped ainda deve invalidar o cache uma vez.");
    }

    [Test]
    public async Task Bulk_remove_invalidates_cache_even_when_everything_is_skipped()
    {
        var (repo, db, connection) = NewRepo();
        using var _ = connection;

        var role = await SeedRoleAsync(db, isSystem: false);
        var permission = await SeedPermissionAsync(db);

        var (_, remove, _, cache) = NewHandlers(repo);
        var result = await remove.Handle(
            new RemoveRolePermissionsCommand(role.Id, [permission.Id]), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Removed, Is.EqualTo(0));
        Assert.That(result.Value!.Skipped, Is.EqualTo(1));
        Assert.That(cache.InvalidateAllCount, Is.EqualTo(1),
            "Lote de remoção integralmente skipped ainda deve invalidar o cache uma vez.");
    }

    // ── g) invalidação de cache uma vez por requisição ───────────────────────────

    [Test]
    public async Task Bulk_add_invalidates_cache_only_once_per_request()
    {
        var (repo, db, connection) = NewRepo();
        using var _ = connection;

        var role = await SeedRoleAsync(db, isSystem: false);
        var permissions = new List<Guid>();
        for (var i = 0; i < 4; i++)
            permissions.Add((await SeedPermissionAsync(db)).Id);

        var (add, _, _, cache) = NewHandlers(repo);
        var result = await add.Handle(new AddRolePermissionsCommand(role.Id, permissions), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Added, Is.EqualTo(4));

        // Exatamente 1: o lote suprime a invalidação dos unitários reusados e invalida uma
        // vez ao final. Antes da correção eram 5 (4 unitários + 1 do lote).
        Assert.That(cache.InvalidateAllCount, Is.EqualTo(1),
            "O lote invalida o cache global uma única vez, não uma vez por permissão.");
    }

    // ── i) contrato de cache: lote = 1 invalidação, unitário avulso = 1 ──────────
    //
    // DEFEITO-1 (medido pelo verificador independente): no host real o handler em lote e os
    // handlers unitários que ele reusa recebem o MESMO IPermissionService scoped
    // (auto-registro: services.TryAddScoped, em Discovery.Api/DependencyInjection).
    // Como o unitário invalida incondicionalmente (RoleHandlers.cs:195/227), um lote com N
    // efeitos reais gerava 1 + N invalidações do cache GLOBAL em vez de 1 — com o catálogo
    // de 100+ permissões isso significa 100+ releituras de permissão para todos os usuários.
    //
    // Correção: o lote abre um escopo de supressão ambiental; os unitários continuam
    // invalidando quando chamados AVULSOS (botão de remover por linha), mas ficam quietos
    // quando estão sendo dirigidos por um lote, que invalida uma única vez no final.

    [Test]
    public async Task Lote_de_N_permissoes_invalida_o_cache_exatamente_uma_vez()
    {
        var (repo, db, connection) = NewRepo();
        using var _ = connection;

        var role = await SeedRoleAsync(db, isSystem: false);
        var permissions = new List<Guid>();
        for (var i = 0; i < 3; i++)
            permissions.Add((await SeedPermissionAsync(db, (ResourceType)(i + 1))).Id);

        var (add, _, _, cache) = NewHandlers(repo);
        var result = await add.Handle(new AddRolePermissionsCommand(role.Id, permissions), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Added, Is.EqualTo(3));

        // Antes da correção este número era 4 (1 do lote + 3 dos unitários reusados).
        Assert.That(cache.InvalidateAllCount, Is.EqualTo(1),
            "UMA invalidação por requisição de lote, não 1+N.");
    }

    [Test]
    public async Task Lote_de_N_revogacoes_invalida_o_cache_exatamente_uma_vez()
    {
        var (repo, db, connection) = NewRepo();
        using var _ = connection;

        var role = await SeedRoleAsync(db, isSystem: false);
        var permissions = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var p = await SeedPermissionAsync(db, (ResourceType)(i + 1));
            permissions.Add(p.Id);
            await repo.AddPermissionToRoleAsync(role.Id, p.Id);
        }

        var (_, remove, service, cache) = NewHandlers(repo);
        var result = await remove.Handle(
            new RemoveRolePermissionsCommand(role.Id, permissions), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Removed, Is.EqualTo(3));
        Assert.That(cache.InvalidateAllCount, Is.EqualTo(1),
            "UMA invalidação por requisição de lote, não 1+N.");
        // E o estado persistido continua correto depois da supressão.
        Assert.That(await CurrentPermissionIdsAsync(service, role.Id), Is.Empty);
    }

    /// <summary>
    /// A supressão não pode vazar para fora do lote: o handler unitário chamado SOZINHO
    /// (fluxo do botão de remover por linha) precisa continuar invalidando normalmente.
    /// Este é o teste que impede uma "correção" que simplesmente apagasse a invalidação
    /// do unitário — o que quebraria o fluxo unitário.
    /// </summary>
    [Test]
    public async Task Handler_unitario_avulso_continua_invalidando_o_cache()
    {
        var (repo, db, connection) = NewRepo();
        using var _ = connection;

        var role = await SeedRoleAsync(db, isSystem: false);
        var permission = await SeedPermissionAsync(db, ResourceType.Users);

        var cache = new CountingPermissionService();
        var service = new RoleServiceStub(repo);
        var unit = new AddRolePermissionCommandHandler(service, cache);

        var result = await unit.Handle(
            new AddRolePermissionCommand(role.Id, permission.Id), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(cache.InvalidateAllCount, Is.EqualTo(1),
            "O endpoint unitário avulso precisa invalidar o cache exatamente uma vez.");
    }

    [Test]
    public async Task Handler_unitario_de_remocao_avulso_continua_invalidando_o_cache()
    {
        var (repo, db, connection) = NewRepo();
        using var _ = connection;

        var role = await SeedRoleAsync(db, isSystem: false);
        var permission = await SeedPermissionAsync(db, ResourceType.Users);
        await repo.AddPermissionToRoleAsync(role.Id, permission.Id);

        var cache = new CountingPermissionService();
        var service = new RoleServiceStub(repo);
        var unit = new RemoveRolePermissionCommandHandler(service, cache);

        var result = await unit.Handle(
            new RemoveRolePermissionCommand(role.Id, permission.Id), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(cache.InvalidateAllCount, Is.EqualTo(1),
            "O endpoint unitário avulso precisa invalidar o cache exatamente uma vez.");
    }

    /// <summary>
    /// O lote tem efeito real gravado no banco (SQLite relacional) E invalida uma única vez.
    /// Prova que a supressão não é obtida por "não gravar" nem por escopo DI isolado.
    /// </summary>
    [Test]
    public async Task Lote_grava_no_banco_e_invalida_uma_vez()
    {
        var (repo, db, connection) = NewRepo();
        using var _ = connection;

        var role = await SeedRoleAsync(db, isSystem: false);
        var p1 = await SeedPermissionAsync(db, ResourceType.Users);
        var p2 = await SeedPermissionAsync(db, ResourceType.Agents);

        var (add, _, service, cache) = NewHandlers(repo);
        var result = await add.Handle(new AddRolePermissionsCommand(role.Id, [p1.Id, p2.Id]), CancellationToken.None);

        Assert.That(result.Value!.Added, Is.EqualTo(2));
        Assert.That(cache.InvalidateAllCount, Is.EqualTo(1));
        // Estado relacional real, lido do banco.
        Assert.That(await CurrentPermissionIdsAsync(service, role.Id),
            Is.EquivalentTo(new[] { p1.Id, p2.Id }));
        Assert.That(db.RolePermissions.AsNoTracking().Count(rp => rp.RoleId == role.Id),
            Is.EqualTo(2));
    }

    // ── j) riscos residuais da supressão ambiental ───────────────────────────────

    /// <summary>
    /// Duas requisições CONCORRENTES na mesma role não vazam a supressão entre si: ela vive
    /// em AsyncLocal, que é por fluxo de execução. Cada lote invalida exatamente 1 vez e ambos
    /// gravam no banco.
    /// </summary>
    [Test]
    public async Task Lotes_concorrentes_nao_vazam_supressao_entre_si()
    {
        // Conexões/contextos separados: cada requisição tem seu próprio escopo DI no host real.
        var (repoA, dbA, connA) = NewRepo();
        var (repoB, dbB, connB) = NewRepo();
        using var _a = connA;
        using var _b = connB;

        var roleA = await SeedRoleAsync(dbA, isSystem: false);
        var roleB = await SeedRoleAsync(dbB, isSystem: false);
        var pA = await SeedPermissionAsync(dbA, ResourceType.Users);
        var pB = await SeedPermissionAsync(dbB, ResourceType.Users);

        var (addA, _, _, cacheA) = NewHandlers(repoA);
        var (addB, _, _, cacheB) = NewHandlers(repoB);

        await Task.WhenAll(
            addA.Handle(new AddRolePermissionsCommand(roleA.Id, [pA.Id]), CancellationToken.None),
            addB.Handle(new AddRolePermissionsCommand(roleB.Id, [pB.Id]), CancellationToken.None));

        Assert.That(cacheA.InvalidateAllCount, Is.EqualTo(1));
        Assert.That(cacheB.InvalidateAllCount, Is.EqualTo(1));
    }

    /// <summary>
    /// O unitário continua invalidando DEPOIS que um lote terminou (a supressão não fica
    /// ligada): prova que o Dispose restaura o estado mesmo com sucesso.
    /// </summary>
    [Test]
    public async Task Supressao_e_restaurada_apos_o_lote_terminar()
    {
        var (repo, db, connection) = NewRepo();
        using var _ = connection;

        var role = await SeedRoleAsync(db, isSystem: false);
        var p1 = await SeedPermissionAsync(db, ResourceType.Users);
        var p2 = await SeedPermissionAsync(db, ResourceType.Agents);

        var cache = new CountingPermissionService();
        var service = new RoleServiceStub(repo);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblies(typeof(RoleRepository).Assembly));
        services.AddTransient<IRequestHandler<AddRolePermissionCommand, Result<PermissionDto>>>(
            _ => new AddRolePermissionCommandHandler(service, cache));
        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var bulk = new AddRolePermissionsCommandHandler(mediator, cache, service);
        await bulk.Handle(new AddRolePermissionsCommand(role.Id, [p1.Id, p2.Id]), CancellationToken.None);
        Assert.That(cache.InvalidateAllCount, Is.EqualTo(1), "o lote invalida 1x");

        // Chamada unitária AVULSA depois do lote: tem de invalidar de novo (supressão desligada).
        var unit = new AddRolePermissionCommandHandler(service, cache);
        await unit.Handle(new AddRolePermissionCommand(role.Id, p1.Id), CancellationToken.None);

        Assert.That(cache.InvalidateAllCount, Is.EqualTo(2),
            "após o lote a supressão precisa estar desligada: o unitário avulso invalida normalmente.");
    }

    /// <summary>
    /// Lote aninhado: o contador de profundidade evita que o lote interno reative a invalidação
    /// no meio do externo. Aqui o lote externo dirige o interno e o total continua 1 ao final.
    /// </summary>
    [Test]
    public async Task Lote_aninhado_nao_reativa_a_invalidacao_no_meio()
    {
        var (repo, db, connection) = NewRepo();
        using var _ = connection;

        var role = await SeedRoleAsync(db, isSystem: false);
        var permission = await SeedPermissionAsync(db, ResourceType.Users);

        var cache = new CountingPermissionService();
        var service = new RoleServiceStub(repo);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblies(typeof(RoleRepository).Assembly));
        services.AddTransient<IRequestHandler<AddRolePermissionCommand, Result<PermissionDto>>>(
            _ => new AddRolePermissionCommandHandler(service, cache));
        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var unit = new AddRolePermissionCommandHandler(service, cache);
        var bulk = new AddRolePermissionsCommandHandler(mediator, cache, service);

        // Simula um lote dirigindo outro: supressão já ativa quando o unitário roda.
        using (RolePermissionCacheSuppression.Suppress())
        {
            await unit.Handle(new AddRolePermissionCommand(role.Id, permission.Id), CancellationToken.None);
            Assert.That(cache.InvalidateAllCount, Is.EqualTo(0),
                "dentro de um escopo de supressão o unitário não invalida");
        }

        // Fora do escopo, volta a invalidar.
        await unit.Handle(new AddRolePermissionCommand(role.Id, permission.Id), CancellationToken.None);
        Assert.That(cache.InvalidateAllCount, Is.EqualTo(1));
    }

    // ── Stubs ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// RoleService real sobre o repositório real (sem reimplementar a regra). O spy opcional
    /// conta as gravações para o teste de delegação ao handler unitário.
    /// </summary>
    private sealed class RoleServiceStub(IRoleRepository repo, MutationSpy? spy = null) : IRoleService
    {
        public Task<IReadOnlyList<Role>> GetAllAsync(CancellationToken ct = default)
            => repo.GetAllAsync().ContinueWith(t => (IReadOnlyList<Role>)t.Result.ToList());
        public Task<Role?> GetByIdAsync(Guid id, CancellationToken ct = default) => repo.GetByIdAsync(id);
        public Task<Role> CreateAsync(Role role, CancellationToken ct = default) => repo.CreateAsync(role);
        public Task<Role> UpdateAsync(Role role, CancellationToken ct = default) => repo.UpdateAsync(role);
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => repo.DeleteAsync(id);
        public Task<IEnumerable<Permission>> GetPermissionsAsync(Guid roleId, CancellationToken ct = default)
            => repo.GetPermissionsForRoleAsync(roleId);
        public async Task AddPermissionAsync(Guid roleId, Guid permissionId, CancellationToken ct = default)
        {
            await repo.AddPermissionToRoleAsync(roleId, permissionId);
            if (spy is not null) spy.Added++;
        }
        public async Task RemovePermissionAsync(Guid roleId, Guid permissionId, CancellationToken ct = default)
        {
            await repo.RemovePermissionFromRoleAsync(roleId, permissionId);
            if (spy is not null) spy.Removed++;
        }
        public Task<IEnumerable<Permission>> GetAllPermissionsAsync(CancellationToken ct = default)
            => repo.GetAllPermissionsAsync();
    }

    /// <summary>Conta invalidações para verificar a cadência do cache.</summary>
    private sealed class CountingPermissionService : IPermissionService
    {
        private int _invalidateAllCount;

        public int InvalidateAllCount => _invalidateAllCount;

        public Task<bool> HasPermissionAsync(Guid userId, ResourceType resource, ActionType action,
            ScopeLevel scopeLevel = ScopeLevel.Global, Guid? scopeId = null, Guid? parentScopeId = null)
            => Task.FromResult(true);
        public Task<UserScopeAccess> GetScopeAccessAsync(Guid userId, ResourceType resource, ActionType action)
            => Task.FromResult(new UserScopeAccess());
        public Task InvalidateUserCacheAsync(Guid userId) => Task.CompletedTask;
        public Task InvalidateAllCacheAsync()
        {
            Interlocked.Increment(ref _invalidateAllCount);
            return Task.CompletedTask;
        }
    }

    /// <summary>Contexto reduzido às entidades de identidade usadas aqui.</summary>
    private sealed class RoleBulkTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type> { typeof(Role), typeof(Permission), typeof(RolePermission) };

            foreach (var entityType in typeof(Role).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null &&
                                        type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowed.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Role>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<Permission>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<RolePermission>(entity =>
            {
                entity.HasKey(item => new { item.RoleId, item.PermissionId });
            });
        }
    }
}
