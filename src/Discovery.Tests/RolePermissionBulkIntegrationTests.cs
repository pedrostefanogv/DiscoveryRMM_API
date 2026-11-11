using System.Collections.Concurrent;
using System.Reflection;
using Discovery.Api;
using Discovery.Api.Controllers;
using Discovery.Api.DependencyInjection;
using Discovery.Api.Filters;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Roles.Commands;
using Discovery.Core.DTOs.Roles;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Identity;
using Discovery.Infrastructure.Cqrs.Roles;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Discovery.Tests;

/// <summary>
/// VERIFICAÇÃO INDEPENDENTE E ADVERSARIAL do endpoint em lote de permissões de role
/// (POST/DELETE <c>/api/v1/roles/{id}/permissions/batch</c>).
///
/// <para>
/// Este arquivo NÃO é do autor da feature: ele foi escrito para tentar PROVAR que o
/// endpoint está errado ou incompleto. O valor dele está em falhar quando a garantia
/// não existe — não em confirmar o que o autor já afirmou.
/// </para>
///
/// <para>
/// Alvo das tentativas de refutação: duplicatas no corpo, idempotência real (com
/// leitura do banco, não só do contador devolvido), merge (e não replace), atomicidade
/// quando um id não existe no catálogo, proteção do papel de sistema no caminho em lote,
/// invalidação de cache exatamente uma vez por requisição, e registro/autenticação da rota.
/// </para>
///
/// <para>
/// O fixture SQLite reduzido segue o padrão de <c>SystemRoleProtectionTests</c> (contexto
/// derivado que ignora tudo de <c>Discovery.Core.Entities</c> exceto Role, Permission e
/// RolePermission). O caminho de gravação exercitado é o REAL: os handlers em lote são
/// construídos com o <see cref="IRoleService"/> e o <see cref="IRoleRepository"/> de
/// produção, e enviam os comandos UNITÁRIOS pelo mediator — é justamente esse reuso que
/// o teste do papel de sistema precisa provar.
/// </para>
/// </summary>
[TestFixture]
public class RolePermissionBulkIntegrationTests
{
    // ── Fixture ──────────────────────────────────────────────────────────────

    private sealed class Harness : IDisposable
    {
        public required DiscoveryDbContext Db { get; init; }
        public required SqliteConnection Connection { get; init; }
        public required RoleRepository Repo { get; init; }
        public required RoleService RoleService { get; init; }
        public required CountingPermissionService Permissions { get; init; }
        public required IMediator Mediator { get; init; }

        public static Harness Create()
        {
            // SQLite (e não InMemory): o repositório usa ExecuteDeleteAsync, que o provider
            // InMemory não implementa. É o caminho relacional real que queremos cobrir.
            var connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();

            var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
                .UseSqlite(connection)
                .Options;

            var db = new RolePermissionTestDbContext(options);
            db.Database.EnsureCreated();

            var repo = new RoleRepository(db);
            var roleService = new RoleService(repo);
            var permissions = new CountingPermissionService();

            // Mediator real do MediatR, mas registrando SOMENTE os handlers unitários:
            // é o caminho efetivo de gravação declarado pelo autor. Assim o teste falha
            // se o lote deixar de reusar o unitário e passar a gravar direto.
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMediatR(cfg =>
            {
                cfg.RegisterServicesFromAssembly(typeof(AddRolePermissionCommandHandler).Assembly);
            });

            // Os handlers unitários resolvidos pelo container precisam receber as MESMAS
            // instâncias do harness (o mesmo repositório sobre o mesmo SQLite e o mesmo
            // contador de cache). Sem isso o mediator de teste não ativa o handler unitário
            // e o teste mediria o container, não o comportamento do lote.
            services.AddSingleton<IRoleService>(roleService);
            services.AddSingleton<IRoleRepository>(repo);
            services.AddSingleton<IPermissionService>(permissions);

            var provider = services.BuildServiceProvider();
            var mediator = provider.GetRequiredService<IMediator>();

            return new Harness
            {
                Db = db,
                Connection = connection,
                Repo = repo,
                RoleService = roleService,
                Permissions = permissions,
                Mediator = mediator
            };
        }

        public void Dispose()
        {
            Db.Dispose();
            Connection.Dispose();
        }
    }

    private static Harness NewHarness() => Harness.Create();

    private static async Task<Role> SeedRoleAsync(DiscoveryDbContext db, bool isSystem = false)
    {
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = isSystem ? "Admin" : "Role " + Guid.NewGuid().ToString("N")[..8],
            Type = isSystem ? RoleType.System : RoleType.Custom,
            IsSystem = isSystem
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return role;
    }

    /// <summary>Semeia N permissões no catálogo e devolve os ids na ordem criada.</summary>
    private static async Task<List<Guid>> SeedCatalogAsync(DiscoveryDbContext db, int count)
    {
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            // (ResourceType, ActionType) distintos: o catálogo real não tem pares repetidos.
            var permission = new Permission
            {
                Id = Guid.NewGuid(),
                ResourceType = ResourceType.Users,
                ActionType = (ActionType)(i % 5),
                Description = $"seed-{i}"
            };
            db.Permissions.Add(permission);
            ids.Add(permission.Id);
        }

        await db.SaveChangesAsync();
        return ids;
    }

    /// <summary>
    /// Lê os vínculos RolePermission DIRETO do banco (sem AsNoTracking/cache de contexto),
    /// porque o ponto do teste é justamente provar o estado persistido.
    /// </summary>
    private static async Task<HashSet<Guid>> LinkedFromDbAsync(DiscoveryDbContext db, Guid roleId)
    {
        var linked = await db.RolePermissions
            .AsNoTracking()
            .Where(rp => rp.RoleId == roleId)
            .Select(rp => rp.PermissionId)
            .ToListAsync();
        return linked.ToHashSet();
    }

    private static AddRolePermissionsCommandHandler BulkAdd(Harness h)
        => new(h.Mediator, h.Permissions, h.RoleService);

    private static RemoveRolePermissionsCommandHandler BulkRemove(Harness h)
        => new(h.Mediator, h.Permissions, h.RoleService);

    // ── 1. Duplicatas no corpo ───────────────────────────────────────────────

    /// <summary>
    /// O mesmo id enviado 3x não pode dobrar a contagem nem estourar erro.
    /// Um bug clássico aqui seria contar "added" por item da lista (3) em vez de por
    /// concessão efetiva (1), deixando o toast do console mentir.
    /// </summary>
    [Test]
    public async Task Duplicatas_no_corpo_nao_dobram_a_contagem_nem_erram()
    {
        using var h = NewHarness();
        var role = await SeedRoleAsync(h.Db);
        var catalog = await SeedCatalogAsync(h.Db, 3);
        var alvo = catalog[0];

        var resultado = await BulkAdd(h).Handle(
            new AddRolePermissionsCommand(role.Id, [alvo, alvo, alvo]), CancellationToken.None);

        Assert.That(resultado.IsSuccess, Is.True,
            "reenviar o mesmo id não é erro: deduplicar é requisito do contrato");
        Assert.That(resultado.Value!.Added, Is.EqualTo(1),
            "3 cópias do MESMO id = 1 concessão efetiva, não 3");
        Assert.That(resultado.Value!.Skipped, Is.EqualTo(0),
            "as cópias duplicadas são deduplicadas, não contadas como 'já concedidas'");

        var linked = await LinkedFromDbAsync(h.Db, role.Id);
        Assert.That(linked, Has.Count.EqualTo(1), "o banco deve ter exatamente 1 vínculo");
        Assert.That(linked, Does.Contain(alvo));
    }

    [Test]
    public async Task Duplicatas_em_lote_misto_contam_uma_vez_cada_id()
    {
        using var h = NewHarness();
        var role = await SeedRoleAsync(h.Db);
        var catalog = await SeedCatalogAsync(h.Db, 3);

        // 2 ids distintos, o primeiro repetido 3x => added deve ser 2.
        var resultado = await BulkAdd(h).Handle(
            new AddRolePermissionsCommand(role.Id, [catalog[0], catalog[1], catalog[0], catalog[0]]),
            CancellationToken.None);

        Assert.That(resultado.Value!.Added, Is.EqualTo(2));
        Assert.That(resultado.Value!.Skipped, Is.EqualTo(0));
        Assert.That(await LinkedFromDbAsync(h.Db, role.Id), Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Duplicatas_na_remocao_nao_dobram_removed()
    {
        using var h = NewHarness();
        var role = await SeedRoleAsync(h.Db);
        var catalog = await SeedCatalogAsync(h.Db, 2);
        await BulkAdd(h).Handle(new AddRolePermissionsCommand(role.Id, catalog), CancellationToken.None);

        var resultado = await BulkRemove(h).Handle(
            new RemoveRolePermissionsCommand(role.Id, [catalog[0], catalog[0]]), CancellationToken.None);

        Assert.That(resultado.IsSuccess, Is.True);
        Assert.That(resultado.Value!.Removed, Is.EqualTo(1), "2 cópias do mesmo id = 1 revogação");
        Assert.That(await LinkedFromDbAsync(h.Db, role.Id), Has.Count.EqualTo(1),
            "só a permissão pedida sai; a outra permanece");
    }

    // ── 2. Idempotência de ponta a ponta (contra o BANCO) ────────────────────

    [Test]
    public async Task Idempotencia_real_adicionar_3_e_reenviar_as_mesmas_3()
    {
        using var h = NewHarness();
        var role = await SeedRoleAsync(h.Db);
        var catalog = await SeedCatalogAsync(h.Db, 3);

        var primeira = await BulkAdd(h).Handle(
            new AddRolePermissionsCommand(role.Id, catalog), CancellationToken.None);
        Assert.That(primeira.IsSuccess, Is.True);
        Assert.That(primeira.Value!.Added, Is.EqualTo(3), "primeira passada concede as 3");
        Assert.That(primeira.Value!.Skipped, Is.EqualTo(0));

        var segunda = await BulkAdd(h).Handle(
            new AddRolePermissionsCommand(role.Id, catalog), CancellationToken.None);
        Assert.That(segunda.IsSuccess, Is.True, "reenviar o mesmo lote não pode ser erro");
        Assert.That(segunda.Value!.Added, Is.EqualTo(0), "nada novo a conceder");
        Assert.That(segunda.Value!.Skipped, Is.EqualTo(3), "as 3 já estavam concedidas");

        var linked = await LinkedFromDbAsync(h.Db, role.Id);
        Assert.That(linked, Has.Count.EqualTo(3), "o banco continua com exatamente 3 vínculos");
        Assert.That(linked.SetEquals(catalog), Is.True);
    }

    [Test]
    public async Task Idempotencia_real_remover_e_remover_de_novo()
    {
        using var h = NewHarness();
        var role = await SeedRoleAsync(h.Db);
        var catalog = await SeedCatalogAsync(h.Db, 3);
        await BulkAdd(h).Handle(new AddRolePermissionsCommand(role.Id, catalog), CancellationToken.None);

        var primeira = await BulkRemove(h).Handle(
            new RemoveRolePermissionsCommand(role.Id, catalog), CancellationToken.None);
        Assert.That(primeira.Value!.Removed, Is.EqualTo(3));
        Assert.That(primeira.Value!.Skipped, Is.EqualTo(0));
        Assert.That(await LinkedFromDbAsync(h.Db, role.Id), Is.Empty);

        var segunda = await BulkRemove(h).Handle(
            new RemoveRolePermissionsCommand(role.Id, catalog), CancellationToken.None);
        Assert.That(segunda.IsSuccess, Is.True, "remover o que já não existe não é erro");
        Assert.That(segunda.Value!.Removed, Is.EqualTo(0));
        Assert.That(segunda.Value!.Skipped, Is.EqualTo(3), "as 3 não estavam vinculadas");
        Assert.That(await LinkedFromDbAsync(h.Db, role.Id), Is.Empty);
    }

    /// <summary>Lote parcialmente aplicado: metade nova, metade já concedida.</summary>
    [Test]
    public async Task Lote_misto_classifica_uns_em_added_e_outros_em_skipped()
    {
        using var h = NewHarness();
        var role = await SeedRoleAsync(h.Db);
        var catalog = await SeedCatalogAsync(h.Db, 5);

        await BulkAdd(h).Handle(
            new AddRolePermissionsCommand(role.Id, catalog.Take(2).ToList()), CancellationToken.None);

        var resultado = await BulkAdd(h).Handle(
            new AddRolePermissionsCommand(role.Id, catalog.Take(4).ToList()), CancellationToken.None);

        Assert.That(resultado.Value!.Added, Is.EqualTo(2), "os ids 3 e 4 são novos");
        Assert.That(resultado.Value!.Skipped, Is.EqualTo(2), "os ids 1 e 2 já estavam");
        Assert.That(await LinkedFromDbAsync(h.Db, role.Id), Has.Count.EqualTo(4));
    }

    // ── 3. Merge, não replace ────────────────────────────────────────────────

    /// <summary>
    /// A garantia central do contrato: o lote processa SOMENTE o que foi pedido.
    /// Um bug aqui apagaria silenciosamente permissões que o console nem tocou.
    /// </summary>
    [Test]
    public async Task Merge_nao_replace_role_com_2_antigas_recebe_lote_de_3_e_fica_com_5()
    {
        using var h = NewHarness();
        var role = await SeedRoleAsync(h.Db);
        var catalog = await SeedCatalogAsync(h.Db, 5);
        var antigas = catalog.Take(2).ToList();
        var novas = catalog.Skip(2).Take(3).ToList();

        await BulkAdd(h).Handle(new AddRolePermissionsCommand(role.Id, antigas), CancellationToken.None);

        var resultado = await BulkAdd(h).Handle(
            new AddRolePermissionsCommand(role.Id, novas), CancellationToken.None);
        Assert.That(resultado.Value!.Added, Is.EqualTo(3));

        var linked = await LinkedFromDbAsync(h.Db, role.Id);
        Assert.That(linked, Has.Count.EqualTo(5), "2 antigas + 3 novas = 5 (merge)");
        foreach (var antiga in antigas)
            Assert.That(linked, Does.Contain(antiga), "permissão não citada no lote NÃO pode sumir");
    }

    [Test]
    public async Task Remocao_em_lote_nao_afeta_permissoes_nao_citadas()
    {
        using var h = NewHarness();
        var role = await SeedRoleAsync(h.Db);
        var catalog = await SeedCatalogAsync(h.Db, 5);
        await BulkAdd(h).Handle(new AddRolePermissionsCommand(role.Id, catalog), CancellationToken.None);

        await BulkRemove(h).Handle(
            new RemoveRolePermissionsCommand(role.Id, catalog.Take(2).ToList()), CancellationToken.None);

        var linked = await LinkedFromDbAsync(h.Db, role.Id);
        Assert.That(linked, Has.Count.EqualTo(3));
        foreach (var restante in catalog.Skip(2))
            Assert.That(linked, Does.Contain(restante));
    }

    // ── 4. Papel de sistema (prova do REUSO, não de regra duplicada) ──────────

    /// <summary>
    /// Ponto extra pedido pelo lead: o autor declarou que o lote reusa os handlers
    /// UNITÁRIOS via mediator, e que é POR ISSO que a proteção de papel de sistema
    /// continua valendo. Aqui provamos as duas metades:
    /// (a) o lote recusa o papel de sistema; (b) o banco não muda.
    ///
    /// <para>
    /// Se alguém "otimizar" o lote para gravar direto no repositório, (a) ainda passa
    /// (a checagem IsSystem está no LoadContextAsync) mas o teste
    /// <see cref="Lote_reusa_o_handler_unitario_e_nao_duplica_a_regra_de_sistema"/>
    /// falha, expondo a divergência de dono da invariante.
    /// </para>
    /// </summary>
    [Test]
    public async Task Papel_de_sistema_recusa_lote_add_e_nao_grava_nada()
    {
        using var h = NewHarness();
        var role = await SeedRoleAsync(h.Db, isSystem: true);
        var catalog = await SeedCatalogAsync(h.Db, 3);
        var antes = await LinkedFromDbAsync(h.Db, role.Id);

        var resultado = await BulkAdd(h).Handle(
            new AddRolePermissionsCommand(role.Id, catalog), CancellationToken.None);

        Assert.That(resultado.IsFailure, Is.True, "papel de sistema não pode ganhar permissões");
        Assert.That(resultado.Errors[0].Code, Is.EqualTo("Validation"));
        Assert.That(resultado.Errors[0].Field, Is.EqualTo("RoleId"));
        Assert.That(resultado.Errors[0].Message,
            Is.EqualTo("Permissões de papéis de sistema não podem ser alteradas."),
            "a mensagem precisa ser idêntica à do handler unitário");

        Assert.That(await LinkedFromDbAsync(h.Db, role.Id), Is.EquivalentTo(antes),
            "atomicidade: o conjunto de RolePermissions não pode mudar");
    }

    [Test]
    public async Task Papel_de_sistema_recusa_lote_remove_e_nao_grava_nada()
    {
        using var h = NewHarness();
        var role = await SeedRoleAsync(h.Db, isSystem: true);
        var catalog = await SeedCatalogAsync(h.Db, 3);

        // Semeia vínculos DIRETO no banco: um papel de sistema real já tem permissões
        // fixadas por migração. O lote não pode conseguir removê-las.
        foreach (var permissionId in catalog)
            h.Db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permissionId });
        await h.Db.SaveChangesAsync();
        var antes = await LinkedFromDbAsync(h.Db, role.Id);
        Assert.That(antes, Has.Count.EqualTo(3), "pré-condição: o papel de sistema tem 3 vínculos");

        var resultado = await BulkRemove(h).Handle(
            new RemoveRolePermissionsCommand(role.Id, catalog), CancellationToken.None);

        Assert.That(resultado.IsFailure, Is.True, "papel de sistema não pode perder permissões");
        Assert.That(resultado.Errors[0].Code, Is.EqualTo("Validation"));
        Assert.That(resultado.Errors[0].Message,
            Is.EqualTo("Permissões de papéis de sistema não podem ser alteradas."));

        Assert.That(await LinkedFromDbAsync(h.Db, role.Id), Is.EquivalentTo(antes),
            "atomicidade: nenhum vínculo do papel de sistema pode ter sido removido");
    }

    /// <summary>
    /// Prova que a invariante do papel de sistema segue com UM dono só: o unitário.
    /// O caminho em lote envia <see cref="AddRolePermissionCommand"/>/<see cref="RemoveRolePermissionCommand"/>
    /// pelo mediator, então o mesmo handler unitário (e a defesa em profundidade do
    /// repositório) decide. Aqui o mediator é espiado para provar o reuso.
    /// </summary>
    [Test]
    public async Task Lote_reusa_o_handler_unitario_e_nao_duplica_a_regra_de_sistema()
    {
        using var h = NewHarness();
        var role = await SeedRoleAsync(h.Db);
        var catalog = await SeedCatalogAsync(h.Db, 3);

        var espiado = new EspiandoMediator(h.Mediator);

        var resultado = await new AddRolePermissionsCommandHandler(espiado, h.Permissions, h.RoleService)
            .Handle(new AddRolePermissionsCommand(role.Id, catalog), CancellationToken.None);

        Assert.That(resultado.Value!.Added, Is.EqualTo(3));
        Assert.That(espiado.ComandosUnitarios, Has.Count.EqualTo(3),
            "a gravação precisa passar pelo comando UNITÁRIO, um por permissão");
        Assert.That(espiado.ComandosUnitarios.All(c => c is AddRolePermissionCommand), Is.True,
            "o caminho efetivo é AddRolePermissionCommand (não gravação direta no repositório)");
    }

    /// <summary>
    /// Contraponto: num papel NÃO-sistema, a defesa em profundidade do repositório
    /// continua valendo para o caminho em lote (o vínculo realmente é gravado).
    /// </summary>
    [Test]
    public async Task Papel_custom_grava_de_verdade_pelo_caminho_em_lote()
    {
        using var h = NewHarness();
        var role = await SeedRoleAsync(h.Db);
        var catalog = await SeedCatalogAsync(h.Db, 2);

        var resultado = await BulkAdd(h).Handle(
            new AddRolePermissionsCommand(role.Id, catalog), CancellationToken.None);

        Assert.That(resultado.IsSuccess, Is.True);
        Assert.That(await LinkedFromDbAsync(h.Db, role.Id), Is.EquivalentTo(catalog));
    }

    // ── 5. Id inexistente no catálogo (atomicidade da validação prévia) ───────

    [Test]
    public async Task PermissionId_inexistente_falha_com_Validation_e_nao_grava_nada()
    {
        using var h = NewHarness();
        var role = await SeedRoleAsync(h.Db);
        var catalog = await SeedCatalogAsync(h.Db, 3);
        var fantasma = Guid.NewGuid();

        // O lote começa com 2 ids VÁLIDOS e termina com um inválido: se a validação não
        // fosse prévia, as 2 primeiras seriam gravadas e a role ficaria em estado parcial.
        var resultado = await BulkAdd(h).Handle(
            new AddRolePermissionsCommand(role.Id, [catalog[0], catalog[1], fantasma]),
            CancellationToken.None);

        Assert.That(resultado.IsFailure, Is.True);
        Assert.That(resultado.Errors[0].Code, Is.EqualTo("Validation"));
        Assert.That(resultado.Errors[0].Field, Is.EqualTo("PermissionIds"));

        Assert.That(await LinkedFromDbAsync(h.Db, role.Id), Is.Empty,
            "atomicidade: id inválido NÃO pode deixar metade do lote gravada");
    }

    [Test]
    public async Task PermissionId_inexistente_na_remocao_nao_remove_as_validas()
    {
        using var h = NewHarness();
        var role = await SeedRoleAsync(h.Db);
        var catalog = await SeedCatalogAsync(h.Db, 3);
        await BulkAdd(h).Handle(new AddRolePermissionsCommand(role.Id, catalog), CancellationToken.None);

        var resultado = await BulkRemove(h).Handle(
            new RemoveRolePermissionsCommand(role.Id, [catalog[0], Guid.NewGuid()]),
            CancellationToken.None);

        Assert.That(resultado.IsFailure, Is.True);
        Assert.That(resultado.Errors[0].Code, Is.EqualTo("Validation"));
        Assert.That(await LinkedFromDbAsync(h.Db, role.Id), Is.EquivalentTo(catalog),
            "atomicidade: nada pode ter sido removido");
    }

    /// <summary>Guid.Empty não é uma permissão do catálogo e deve ser recusado.</summary>
    [Test]
    public async Task Guid_vazio_no_corpo_e_recusado_como_inexistente()
    {
        using var h = NewHarness();
        var role = await SeedRoleAsync(h.Db);

        var resultado = await BulkAdd(h).Handle(
            new AddRolePermissionsCommand(role.Id, [Guid.Empty]), CancellationToken.None);

        Assert.That(resultado.IsFailure, Is.True);
        Assert.That(resultado.Errors[0].Code, Is.EqualTo("Validation"));
        Assert.That(await LinkedFromDbAsync(h.Db, role.Id), Is.Empty);
    }

    // ── 6. Role inexistente ──────────────────────────────────────────────────

    [Test]
    public async Task Role_inexistente_no_lote_add_e_NotFound_sem_criar_vinculo()
    {
        using var h = NewHarness();
        var catalog = await SeedCatalogAsync(h.Db, 2);
        var inexistente = Guid.NewGuid();

        var resultado = await BulkAdd(h).Handle(
            new AddRolePermissionsCommand(inexistente, catalog), CancellationToken.None);

        Assert.That(resultado.IsFailure, Is.True);
        Assert.That(resultado.Errors[0].Code, Is.EqualTo("NotFound"));
        Assert.That(await h.Db.RolePermissions.AsNoTracking().CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task Role_inexistente_no_lote_remove_e_NotFound()
    {
        using var h = NewHarness();
        var catalog = await SeedCatalogAsync(h.Db, 2);

        var resultado = await BulkRemove(h).Handle(
            new RemoveRolePermissionsCommand(Guid.NewGuid(), catalog), CancellationToken.None);

        Assert.That(resultado.IsFailure, Is.True);
        Assert.That(resultado.Errors[0].Code, Is.EqualTo("NotFound"));
    }

    /// <summary>
    /// Precedência de erro: role inexistente deve vencer sobre id inexistente.
    /// Sem isso o console mostra "permissão inválida" quando o problema é a role.
    /// </summary>
    [Test]
    public async Task Role_inexistente_tem_precedencia_sobre_permissao_inexistente()
    {
        using var h = NewHarness();

        var resultado = await BulkAdd(h).Handle(
            new AddRolePermissionsCommand(Guid.NewGuid(), [Guid.NewGuid()]), CancellationToken.None);

        Assert.That(resultado.Errors[0].Code, Is.EqualTo("NotFound"),
            "resolver a role primeiro é o que torna a mensagem útil");
    }

    // ── 7. Lista vazia ───────────────────────────────────────────────────────

    [Test]
    public async Task Lista_vazia_no_add_e_Success_com_zeros()
    {
        using var h = NewHarness();
        var role = await SeedRoleAsync(h.Db);

        var resultado = await BulkAdd(h).Handle(
            new AddRolePermissionsCommand(role.Id, []), CancellationToken.None);

        Assert.That(resultado.IsSuccess, Is.True);
        Assert.That(resultado.Value!.Added, Is.EqualTo(0));
        Assert.That(resultado.Value!.Skipped, Is.EqualTo(0));
        Assert.That(await LinkedFromDbAsync(h.Db, role.Id), Is.Empty);
    }

    [Test]
    public async Task Lista_vazia_no_remove_e_Success_com_zeros()
    {
        using var h = NewHarness();
        var role = await SeedRoleAsync(h.Db);
        var catalog = await SeedCatalogAsync(h.Db, 2);
        await BulkAdd(h).Handle(new AddRolePermissionsCommand(role.Id, catalog), CancellationToken.None);

        var resultado = await BulkRemove(h).Handle(
            new RemoveRolePermissionsCommand(role.Id, []), CancellationToken.None);

        Assert.That(resultado.IsSuccess, Is.True);
        Assert.That(resultado.Value!.Removed, Is.EqualTo(0));
        Assert.That(resultado.Value!.Skipped, Is.EqualTo(0));
        Assert.That(await LinkedFromDbAsync(h.Db, role.Id), Is.EquivalentTo(catalog),
            "lista vazia não pode remover nada");
    }

    /// <summary>
    /// Lista vazia para uma role INEXISTENTE: o handler curto-circuita antes de ler a
    /// role. Documenta o comportamento observado como uma limitação conhecida do contrato
    /// (o console nunca chama o lote com lista vazia), em vez de afirmar que é correto.
    /// </summary>
    [Test]
    public async Task Lista_vazia_curto_circuita_antes_de_validar_a_role()
    {
        using var h = NewHarness();

        var resultado = await BulkAdd(h).Handle(
            new AddRolePermissionsCommand(Guid.NewGuid(), []), CancellationToken.None);

        Assert.That(resultado.IsSuccess, Is.True,
            "comportamento observado: lista vazia retorna Success sem consultar a role");
        Assert.That(resultado.Value!.Added, Is.EqualTo(0));
    }

    // ── 8. Invalidação de cache ──────────────────────────────────────────────

    /// <summary>
    /// Contexto da medição: o handler em lote reusa o handler UNITÁRIO via mediator
    /// (RoleHandlers.cs:398/:453) e o unitário invalidaria o cache por conta própria
    /// (RoleHandlers.cs:250/:284). Como o host entrega o MESMO IPermissionService scoped aos
    /// dois (auto-registro TryAddScoped em ServiceCollectionExtensions.cs:82), sem proteção o
    /// lote de N efeitos reais produziria 1 + N invalidações do cache GLOBAL.
    ///
    /// <para>
    /// O lote agora roda o loop dentro de <c>RolePermissionCacheSuppression.Suppress()</c>
    /// (AsyncLocal + IDisposable), então o resultado correto é EXATAMENTE 1 — e o unitário
    /// avulso continua invalidando 1. Estes testes medem os três números separadamente: um
    /// "1" aqui só é confiável se o unitário avulso também for medido (senão a supressão
    /// pode ter virado vazamento silencioso que quebra o endpoint unitário).
    /// </para>
    /// </summary>
    private static async Task<Harness> HarnessComCacheCompartilhadoAsync()
        => Harness.Create();

    /// <summary>
    /// Contrato: lote de 3 com efeito real => exatamente 1 invalidação ao final
    /// (não 1 por item, e não 1 + 3 dos unitários reusados).
    /// </summary>
    [Test]
    public async Task Cache_invalidado_uma_vez_quando_o_lote_de_add_tem_efeito_real()
    {
        using var h = NewHarness();
        var role = await SeedRoleAsync(h.Db);
        var catalog = await SeedCatalogAsync(h.Db, 3);

        var resultado = await BulkAdd(h).Handle(
            new AddRolePermissionsCommand(role.Id, catalog), CancellationToken.None);

        Assert.That(resultado.Value!.Added, Is.EqualTo(3));
        Assert.That(await LinkedFromDbAsync(h.Db, role.Id), Has.Count.EqualTo(3),
            "o estado do banco continua correto: os 3 vínculos foram gravados");

        TestContext.Out.WriteLine(
            "[cache] lote add de 3 => InvalidateAllCacheAsync chamado " + h.Permissions.InvalidateAllCalls + "x");

        Assert.That(h.Permissions.InvalidateAllCalls, Is.EqualTo(1),
            "lote de 3 => exatamente 1 invalidação ao final (contrato: uma vez, não por item)");
    }

    [Test]
    public async Task Cache_invalidado_uma_vez_quando_o_lote_de_remove_tem_efeito_real()
    {
        using var h = NewHarness();
        var role = await SeedRoleAsync(h.Db);
        var catalog = await SeedCatalogAsync(h.Db, 3);
        await BulkAdd(h).Handle(new AddRolePermissionsCommand(role.Id, catalog), CancellationToken.None);
        h.Permissions.Reset();

        await BulkRemove(h).Handle(
            new RemoveRolePermissionsCommand(role.Id, catalog), CancellationToken.None);

        Assert.That(await LinkedFromDbAsync(h.Db, role.Id), Is.Empty,
            "o estado do banco continua correto: os 3 vínculos foram removidos");

        TestContext.Out.WriteLine(
            "[cache] lote remove de 3 => InvalidateAllCacheAsync chamado " + h.Permissions.InvalidateAllCalls + "x");

        Assert.That(h.Permissions.InvalidateAllCalls, Is.EqualTo(1),
            "lote de 3 => exatamente 1 invalidação ao final");
    }

    /// <summary>
    /// GUARDA CONTRA VAZAMENTO: o endpoint UNITÁRIO avulso precisa continuar invalidando 1.
    /// Se a supressão vazasse para fora do lote, o unitário pararia de invalidar e o console
    /// passaria a mostrar permissão velha após um "Atribuir" individual — regressão silenciosa
    /// que só aparece em produção. Este teste é o que pega isso.
    /// </summary>
    [Test]
    public async Task Unitario_avulso_continua_invalidando_uma_vez()
    {
        using var h = NewHarness();
        var role = await SeedRoleAsync(h.Db);
        var catalog = await SeedCatalogAsync(h.Db, 2);

        var handler = new AddRolePermissionCommandHandler(h.RoleService, h.Permissions);
        var resultado = await handler.Handle(
            new AddRolePermissionCommand(role.Id, catalog[0]), CancellationToken.None);

        Assert.That(resultado.IsSuccess, Is.True);
        Assert.That(h.Permissions.InvalidateAllCalls, Is.EqualTo(1),
            "o unitário avulso NÃO é suprimido: continua invalidando 1x (senão a supressão vazou)");

        var remove = new RemoveRolePermissionCommandHandler(h.RoleService, h.Permissions);
        await remove.Handle(new RemoveRolePermissionCommand(role.Id, catalog[0]), CancellationToken.None);

        Assert.That(h.Permissions.InvalidateAllCalls, Is.EqualTo(2),
            "o unitário de remoção também volta a invalidar normalmente");
    }

    /// <summary>
    /// ADVERSARIAL: a supressão é liberada quando o lote falha no meio?
    /// O lote agregado NÃO lança (continua até o fim), então forçamos o caminho de exceção
    /// com um mediator que estoura no segundo item. O using precisa restaurar o flag mesmo
    /// assim — se vazar, a requisição unitária SEGUINTE deixaria de invalidar o cache.
    /// </summary>
    [Test]
    public async Task Supressao_e_liberada_quando_o_lote_falha_no_meio()
    {
        using var h = NewHarness();
        var role = await SeedRoleAsync(h.Db);
        var catalog = await SeedCatalogAsync(h.Db, 3);

        var explosivo = new MediadorQueEstouraNoSegundoItem(h.Mediator);
        var handlerDoLote = new AddRolePermissionsCommandHandler(explosivo, h.Permissions, h.RoleService);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await handlerDoLote.Handle(new AddRolePermissionsCommand(role.Id, catalog), CancellationToken.None));

        Assert.That(RolePermissionCacheSuppression.IsSuppressed, Is.False,
            "após a exceção o flag precisa estar liberado — um vazamento aqui silencia o cache global");

        h.Permissions.Reset();
        var unitario = new AddRolePermissionCommandHandler(h.RoleService, h.Permissions);
        var seguinte = await unitario.Handle(
            new AddRolePermissionCommand(role.Id, catalog[2]), CancellationToken.None);

        Assert.That(seguinte.IsSuccess, Is.True,
            "o unitário seguinte funciona normalmente (não herdou supressão)");
        Assert.That(h.Permissions.InvalidateAllCalls, Is.EqualTo(1),
            "o unitário SEGUINTE invalidou 1x: prova que a supressão não vazou do lote que falhou");
    }

    /// <summary>
    /// ADVERSARIAL: mesma verificação para o handler de REMOÇÃO em lote, que tem o seu
    /// próprio using (RoleHandlers.cs:447).
    /// </summary>
    [Test]
    public async Task Supressao_e_liberada_quando_o_lote_de_remocao_falha()
    {
        using var h = NewHarness();
        var role = await SeedRoleAsync(h.Db);
        var catalog = await SeedCatalogAsync(h.Db, 3);
        await BulkAdd(h).Handle(new AddRolePermissionsCommand(role.Id, catalog), CancellationToken.None);

        var explosivo = new MediadorQueEstouraNoSegundoItem(h.Mediator);
        var handlerDoLote = new RemoveRolePermissionsCommandHandler(explosivo, h.Permissions, h.RoleService);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await handlerDoLote.Handle(new RemoveRolePermissionsCommand(role.Id, catalog), CancellationToken.None));

        Assert.That(RolePermissionCacheSuppression.IsSuppressed, Is.False,
            "o handler de remoção também precisa liberar a supressão no caminho de exceção");

        h.Permissions.Reset();
        var unitario = new RemoveRolePermissionCommandHandler(h.RoleService, h.Permissions);
        await unitario.Handle(new RemoveRolePermissionCommand(role.Id, catalog[2]), CancellationToken.None);

        Assert.That(h.Permissions.InvalidateAllCalls, Is.EqualTo(1),
            "o unitário seguinte volta a invalidar normalmente após a falha do lote");
    }

    /// <summary>
    /// ADVERSARIAL (concorrência): a supressão é um AsyncLocal — ela NÃO pode vazar para o
    /// fluxo de outra requisição. Se vazasse, um lote de um operador silenciaria a
    /// invalidação de cache de um comando unitário de OUTRO operador rodando ao mesmo tempo,
    /// deixando o console dele com permissão obsoleta. Aqui 50 lotes concorrentes rodam
    /// juntos e cada um confirma que a supressão vale dentro do próprio fluxo e some fora.
    /// </summary>
    [Test]
    public async Task Supressao_nao_vaza_entre_requisicoes_concorrentes()
    {
        using var h = NewHarness();
        var role = await SeedRoleAsync(h.Db);
        var catalog = await SeedCatalogAsync(h.Db, 4);

        var erros = 0;

        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(async () =>
        {
            using (RolePermissionCacheSuppression.Suppress())
            {
                await Task.Yield();
                // Dentro do fluxo do lote a supressão DEVE valer (é o que silencia os unitários).
                if (!RolePermissionCacheSuppression.IsSuppressed) Interlocked.Increment(ref erros);
            }

            // Fora do lote, no mesmo fluxo, ela DEVE ter sido liberada.
            if (RolePermissionCacheSuppression.IsSuppressed) Interlocked.Increment(ref erros);
        })));

        Assert.That(erros, Is.EqualTo(0),
            "a supressão precisa ser visível só dentro do fluxo do lote e liberada ao sair");

        // E um comando unitário depois de toda a concorrência volta a invalidar normalmente.
        h.Permissions.Reset();
        var unitario = new AddRolePermissionCommandHandler(h.RoleService, h.Permissions);
        await unitario.Handle(new AddRolePermissionCommand(role.Id, catalog[0]), CancellationToken.None);

        Assert.That(h.Permissions.InvalidateAllCalls, Is.EqualTo(1),
            "após a concorrência, o unitário avulso continua invalidando 1x (nada vazou)");
    }

    /// <summary>
    /// Isola as duas contribuições: o contador do LOTE e o contador dos UNITÁRIOS reusados.
    /// Prova que a dos unitários é exatamente 0 (supressão funcionando) e que a gravação
    /// NÃO foi afetada — a supressão silencia a invalidação, não a escrita.
    /// </summary>
    [Test]
    public async Task Unitarios_reusados_no_lote_tem_a_invalidacao_suprimida()
    {
        using var h = NewHarness();
        var role = await SeedRoleAsync(h.Db);
        var catalog = await SeedCatalogAsync(h.Db, 4);

        var contadorDosUnitarios = new CountingPermissionService();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IPermissionService>(contadorDosUnitarios);
        services.AddSingleton<IRoleService>(h.RoleService);
        services.AddSingleton<IRoleRepository>(h.Repo);
        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(AddRolePermissionCommandHandler).Assembly));
        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var servicoDoLote = new CountingPermissionService();
        var handler = new AddRolePermissionsCommandHandler(mediator, servicoDoLote, h.RoleService);
        var resultado = await handler.Handle(
            new AddRolePermissionsCommand(role.Id, catalog), CancellationToken.None);

        Assert.That(resultado.Value!.Added, Is.EqualTo(4));
        Assert.That(await LinkedFromDbAsync(h.Db, role.Id), Has.Count.EqualTo(4),
            "o banco tem os 4 vínculos: a supressão não impediu a GRAVAÇÃO, só a invalidação");

        TestContext.Out.WriteLine(
            "[cache] lote de 4 => lote=" + servicoDoLote.InvalidateAllCalls +
            "x unitarios=" + contadorDosUnitarios.InvalidateAllCalls + "x");

        Assert.That(servicoDoLote.InvalidateAllCalls, Is.EqualTo(1),
            "o handler em lote invalida exatamente uma vez, seja qual for o N");
        Assert.That(contadorDosUnitarios.InvalidateAllCalls, Is.EqualTo(0),
            "os unitários reusados NÃO invalidam: é exatamente isso que a supressão garante");
    }
    // (teste antigo substituído por Unitarios_reusados_no_lote_tem_a_invalidacao_suprimida)
    // ── 9. Rota registrada, verbo e autorização declarada ────────────────────

    /// <summary>
    /// Enumera a tabela de rotas MVC REAL (via IActionDescriptorCollectionProvider) e
    /// prova que os dois endpoints em lote existem, com o verbo certo. Isto é o que
    /// "a rota está registrada" significa de fato; ler o atributo no código não prova
    /// que o ASP.NET a mapeou.
    /// </summary>
    [Test]
    public void Rotas_batch_estao_registradas_com_POST_e_DELETE()
    {
        var descritores = BuildActionDescriptors();

        var rotaBatch = descritores
            .Where(d => d.AttributeRouteInfo?.Template == "api/v{version:apiVersion}/roles/{id:guid}/permissions/batch")
            .ToList();

        Assert.That(rotaBatch, Has.Count.EqualTo(2),
            "esperados exatamente dois endpoints na rota /roles/{id}/permissions/batch");

        var verbos = rotaBatch.SelectMany(VerbosDe).ToHashSet();
        Assert.That(verbos, Does.Contain("POST"), "o lote de concessão é POST");
        Assert.That(verbos, Does.Contain("DELETE"),
            "o lote de revogação é DELETE — e o ASP.NET precisa tê-lo mapeado " +
            "(histórico de bug: DELETE com corpo não mapeado)");

        Assert.That(VerbosDe(rotaBatch[0]).Count, Is.EqualTo(1),
            "cada action deve responder a exatamente um verbo");
        Assert.That(VerbosDe(rotaBatch[1]).Count, Is.EqualTo(1));
    }

    /// <summary>
    /// A rota em lote não pode "roubar" a rota unitária de permissões: a unitária
    /// /roles/{id}/permissions continua existindo separadamente.
    /// </summary>
    [Test]
    public void Rota_unitaria_de_permissoes_continua_intacta()
    {
        var descritores = BuildActionDescriptors();

        var unitariaPost = descritores.Where(d =>
            d.AttributeRouteInfo?.Template == "api/v{version:apiVersion}/roles/{id:guid}/permissions"
            && VerbosDe(d).Contains("POST")).ToList();
        Assert.That(unitariaPost, Has.Count.EqualTo(1),
            "POST unitário de permissão precisa continuar registrado (o console o usa)");

        var unitariaDelete = descritores.Where(d =>
            d.AttributeRouteInfo?.Template == "api/v{version:apiVersion}/roles/{id:guid}/permissions/{permissionId:guid}"
            && VerbosDe(d).Contains("DELETE")).ToList();
        Assert.That(unitariaDelete, Has.Count.EqualTo(1),
            "DELETE unitário de permissão precisa continuar registrado");
    }

    /// <summary>
    /// As DUAS actions em lote exigem Users.Edit (a mesma permissão do caminho unitário).
    /// Exigir menos (ex.: Users.View) abriria a escrita para quem só lê.
    /// </summary>
    [TestCase(nameof(RolesController.AddRolePermissionsBatch))]
    [TestCase(nameof(RolesController.RemoveRolePermissionsBatch))]
    public void Actions_em_lote_exigem_Users_Edit(string nomeDaAction)
    {
        var metodo = typeof(RolesController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(m => m.Name == nomeDaAction);
        Assert.That(metodo, Is.Not.Null, $"action {nomeDaAction} não encontrada");

        var atributos = metodo!
            .GetCustomAttributes(typeof(RequirePermissionAttribute), inherit: false)
            .Cast<RequirePermissionAttribute>()
            .ToList();

        Assert.That(atributos, Has.Count.EqualTo(1),
            "o endpoint precisa de exatamente um RequirePermission (empilhar dois vira AND e " +
            "tira o acesso de papéis legítimos)");

        var (recurso, acao) = RecursoAcao(atributos[0]);
        Assert.That(recurso, Is.EqualTo(ResourceType.Users));
        Assert.That(acao, Is.EqualTo(ActionType.Edit));
    }

    /// <summary>
    /// O corpo do DELETE é DTO tipado ([FromBody]), não parâmetro de rota/query: é isso
    /// que permite enviar a matriz de permissões sem estourar a URL.
    /// </summary>
    [Test]
    public void Corpo_do_lote_vem_do_body_tipado()
    {
        foreach (var nome in new[] { nameof(RolesController.AddRolePermissionsBatch),
                                     nameof(RolesController.RemoveRolePermissionsBatch) })
        {
            var metodo = typeof(RolesController).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Single(m => m.Name == nome);
            var corpo = metodo.GetParameters()
                .SingleOrDefault(p => p.GetCustomAttribute<FromBodyAttribute>() is not null);

            Assert.That(corpo, Is.Not.Null, $"{nome} precisa de um parâmetro [FromBody]");
            Assert.That(corpo!.ParameterType, Is.EqualTo(typeof(BulkRolePermissionsDto)),
                $"{nome} deve aceitar BulkRolePermissionsDto");
            Assert.That(corpo.ParameterType.GetProperty("PermissionIds"), Is.Not.Null,
                "o contrato é { permissionIds: [...] }");
        }
    }

    /// <summary>
    /// Exercita o <see cref="RequirePermissionFilter"/> REAL (não uma reimplementação):
    /// sem UserId no HttpContext => 401; com UserId mas sem a permissão => 403.
    /// </summary>
    [Test]
    public async Task Filtro_real_devolve_401_sem_usuario_autenticado()
    {
        var permissao = new CountingPermissionService { Permite = true };
        var filtro = NovoFiltro(permissao);
        var (contexto, nextChamado) = ContextoDeFiltro(userId: null);

        await filtro.OnActionExecutionAsync(contexto, () => nextChamado());

        Assert.That(contexto.Result, Is.InstanceOf<UnauthorizedObjectResult>(),
            "sem UserId o filtro deve responder 401");
        Assert.That(((UnauthorizedObjectResult)contexto.Result!).StatusCode,
            Is.EqualTo(StatusCodes.Status401Unauthorized));
    }

    [Test]
    public async Task Filtro_real_devolve_403_sem_a_permissao_Users_Edit()
    {
        var permissao = new CountingPermissionService { Permite = false };
        var filtro = NovoFiltro(permissao);
        var (contexto, nextChamado) = ContextoDeFiltro(Guid.NewGuid());

        await filtro.OnActionExecutionAsync(contexto, () => nextChamado());

        Assert.That(contexto.Result, Is.InstanceOf<ObjectResult>());
        Assert.That(((ObjectResult)contexto.Result!).StatusCode,
            Is.EqualTo(StatusCodes.Status403Forbidden),
            "com UserId mas sem Users.Edit o filtro deve responder 403");

        // E o filtro precisa ter perguntado exatamente pela permissão do endpoint.
        Assert.That(permissao.UltimoRecurso, Is.EqualTo(ResourceType.Users));
        Assert.That(permissao.UltimaAcao, Is.EqualTo(ActionType.Edit));
    }

    [Test]
    public async Task Filtro_real_libera_quando_a_permissao_existe()
    {
        var permissao = new CountingPermissionService { Permite = true };
        var filtro = NovoFiltro(permissao);
        var (contexto, nextChamado) = ContextoDeFiltro(Guid.NewGuid());

        await filtro.OnActionExecutionAsync(contexto, () => nextChamado());

        Assert.That(contexto.Result, Is.Null, "com a permissão, o filtro deve deixar passar");
    }

    /// <summary>
    /// O atributo, quando instanciado pelo ASP.NET, precisa construir exatamente esse
    /// filtro com Users/Edit — é a ponte entre "declarado" e "executado".
    /// </summary>
    [Test]
    public void Atributo_constroi_o_filtro_real_com_Users_Edit()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IPermissionService>(new CountingPermissionService());
        services.AddScoped<IScopeContext, ScopeContextStub>();
        using var provider = services.BuildServiceProvider();

        var atributo = new RequirePermissionAttribute(ResourceType.Users, ActionType.Edit);
        var filtro = atributo.CreateInstance(provider);

        Assert.That(filtro, Is.Not.Null);
        Assert.That(filtro, Is.InstanceOf<RequirePermissionFilter>());

        var campos = filtro.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
        var recurso = (ResourceType)campos.Single(f => f.Name == "_resource").GetValue(filtro)!;
        var acao = (ActionType)campos.Single(f => f.Name == "_action").GetValue(filtro)!;
        Assert.That(recurso, Is.EqualTo(ResourceType.Users));
        Assert.That(acao, Is.EqualTo(ActionType.Edit));
    }

    // ── 10. Serialização do contrato (o console depende do nome dos campos) ──

    [Test]
    public void Respostas_serializam_com_os_nomes_de_campo_do_contrato()
    {
        var opcoes = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };

        var jsonAdd = System.Text.Json.JsonSerializer.Serialize(
            new BulkAssignPermissionsResultDto(2, 1), opcoes);
        Assert.That(jsonAdd, Does.Contain("\"added\":2"));
        Assert.That(jsonAdd, Does.Contain("\"skipped\":1"));

        var jsonRemove = System.Text.Json.JsonSerializer.Serialize(
            new BulkRemovePermissionsResultDto(3, 0), opcoes);
        Assert.That(jsonRemove, Does.Contain("\"removed\":3"));
        Assert.That(jsonRemove, Does.Contain("\"skipped\":0"));
    }

    [Test]
    public void Dto_de_entrada_aceita_permissionIds_em_camelCase()
    {
        var opcoes = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var dto = System.Text.Json.JsonSerializer.Deserialize<BulkRolePermissionsDto>(
            $"{{\"permissionIds\":[\"{id1}\",\"{id2}\"]}}", opcoes);

        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.PermissionIds, Is.EqualTo(new List<Guid> { id1, id2 }),
            "o console envia { permissionIds: [...] }");
    }

    // ── 11. Resultado HTTP do controller ─────────────────────────────────────

    [Test]
    public async Task Controller_devolve_200_com_os_contadores_do_lote()
    {
        using var h = NewHarness();
        var role = await SeedRoleAsync(h.Db);
        var catalog = await SeedCatalogAsync(h.Db, 3);

        var controller = NovoController(new MediadorDeUmHandler(BulkAdd(h), BulkRemove(h)));

        var acao = await controller.AddRolePermissionsBatch(
            role.Id, new BulkRolePermissionsDto { PermissionIds = catalog });

        Assert.That(acao, Is.InstanceOf<OkObjectResult>());
        var dto = ((OkObjectResult)acao).Value as BulkAssignPermissionsResultDto;
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.Added, Is.EqualTo(3));
        Assert.That(dto.Skipped, Is.EqualTo(0));
    }

    [Test]
    public async Task Controller_traduz_papel_de_sistema_em_400()
    {
        using var h = NewHarness();
        var role = await SeedRoleAsync(h.Db, isSystem: true);
        var catalog = await SeedCatalogAsync(h.Db, 2);

        var controller = NovoController(new MediadorDeUmHandler(BulkAdd(h), BulkRemove(h)));
        var acao = await controller.AddRolePermissionsBatch(
            role.Id, new BulkRolePermissionsDto { PermissionIds = catalog });

        Assert.That(acao, Is.InstanceOf<BadRequestObjectResult>(),
            "Validation vira 400 via ToActionResult");
    }

    [Test]
    public async Task Controller_traduz_role_inexistente_em_404()
    {
        using var h = NewHarness();
        var catalog = await SeedCatalogAsync(h.Db, 2);

        var controller = NovoController(new MediadorDeUmHandler(BulkAdd(h), BulkRemove(h)));
        var acao = await controller.RemoveRolePermissionsBatch(
            Guid.NewGuid(), new BulkRolePermissionsDto { PermissionIds = catalog });

        Assert.That(acao, Is.InstanceOf<NotFoundObjectResult>(),
            "NotFound vira 404 via ToActionResult");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static RolesController NovoController(IMediator mediator)
        => new(mediator)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private static RequirePermissionFilter NovoFiltro(IPermissionService permissao)
        => new(permissao, new ScopeContextStub(), ResourceType.Users, ActionType.Edit);

    private static (ActionExecutingContext Contexto, Func<Task<ActionExecutedContext>> Next)
        ContextoDeFiltro(Guid? userId)
    {
        var http = new DefaultHttpContext();
        if (userId.HasValue) http.Items["UserId"] = userId.Value;

        var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var contexto = new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            new ControllerBaseStub());

        return (contexto, () => Task.FromResult(new ActionExecutedContext(
            actionContext, new List<IFilterMetadata>(), new ControllerBaseStub())));
    }

    /// <summary>
    /// Constrói a coleção de actions do MVC REAL sem subir o host do Program.cs (que
    /// exige Postgres, NATS e Quartz). Só o controller de roles é montado, com o mesmo
    /// versionamento por segmento de URL usado em produção.
    /// </summary>
    private static IReadOnlyList<ActionDescriptor> BuildActionDescriptors()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development"
        });
        builder.Logging.ClearProviders();
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(RolesController).Assembly);
        builder.Services.AddDiscoveryApiVersioning();
        builder.Services.AddSingleton<IPermissionService>(new CountingPermissionService());
        builder.Services.AddScoped<IScopeContext, ScopeContextStub>();

        var app = builder.Build();
        app.MapControllers();

        return app.Services
            .GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items;
    }

    private static IReadOnlyCollection<string> VerbosDe(ActionDescriptor descritor)
        => descritor.ActionConstraints?
            .OfType<Microsoft.AspNetCore.Mvc.ActionConstraints.HttpMethodActionConstraint>()
            .SelectMany(c => c.HttpMethods)
            .ToHashSet() ?? [];

    private static (ResourceType Recurso, ActionType Acao) RecursoAcao(RequirePermissionAttribute atributo)
    {
        var tipo = typeof(RequirePermissionAttribute);
        var recurso = (ResourceType)tipo.GetField("_resource", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(atributo)!;
        var acao = (ActionType)tipo.GetField("_action", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(atributo)!;
        return (recurso, acao);
    }

    // ── Dublês ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Conta as invalidações de cache global. É o instrumento do ponto "uma vez por
    /// requisição" — um stub que só devolve Task.CompletedTask não prova nada.
    /// </summary>
    private sealed class CountingPermissionService : IPermissionService
    {
        private int _invalidateAllCalls;

        public int InvalidateAllCalls => Volatile.Read(ref _invalidateAllCalls);
        public bool Permite { get; set; } = true;
        public ResourceType? UltimoRecurso { get; private set; }
        public ActionType? UltimaAcao { get; private set; }

        public void Reset() => Interlocked.Exchange(ref _invalidateAllCalls, 0);

        public Task<bool> HasPermissionAsync(Guid userId, ResourceType resource, ActionType action,
            ScopeLevel scopeLevel = ScopeLevel.Global, Guid? scopeId = null, Guid? parentScopeId = null)
        {
            UltimoRecurso = resource;
            UltimaAcao = action;
            return Task.FromResult(Permite);
        }

        public Task<UserScopeAccess> GetScopeAccessAsync(Guid userId, ResourceType resource, ActionType action)
            => Task.FromResult(new UserScopeAccess());

        public Task InvalidateUserCacheAsync(Guid userId) => Task.CompletedTask;

        public Task InvalidateAllCacheAsync()
        {
            Interlocked.Increment(ref _invalidateAllCalls);
            return Task.CompletedTask;
        }
    }

    private sealed class ScopeContextStub : IScopeContext
    {
        public Guid? ResolvedClientId { get; set; }
        public Guid? ResolvedSiteId { get; set; }
        public void SetUserId(Guid userId) { }
        public Task<UserScopeAccess> GetAccessAsync(ResourceType resource, ActionType action)
            => Task.FromResult(new UserScopeAccess());
        public Task<bool> HasGlobalAccessAsync(ResourceType resource, ActionType action)
            => Task.FromResult(true);
    }

    private sealed class ControllerBaseStub : ControllerBase;

    /// <summary>
    /// Mediador que registra os comandos unitários enviados pelo handler em lote.
    /// É o instrumento que prova o REUSO (em vez de gravação direta no repositório).
    /// </summary>
    private sealed class EspiandoMediator(IMediator interno) : IMediator
    {
        public List<object> ComandosUnitarios { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            ComandosUnitarios.Add(request);
            return interno.Send(request, cancellationToken);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest
        {
            ComandosUnitarios.Add(request!);
            return interno.Send(request, cancellationToken);
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            ComandosUnitarios.Add(request);
            return interno.Send(request, cancellationToken);
        }

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default)
            => interno.Publish(notification, cancellationToken);

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
            => interno.Publish(notification, cancellationToken);
    }

    /// <summary>
    /// Mediador que repassa ao mediator real, mas estoura no SEGUNDO comando unitário.
    /// Existe para forçar o caminho de EXCEÇÃO dentro do loop do lote: sem isso não dá para
    /// provar que a supressão de cache é liberada em finally (o lote agregado não lança,
    /// ele acumula o erro e devolve Failure).
    /// </summary>
    private sealed class MediadorQueEstouraNoSegundoItem(IMediator interno) : IMediator
    {
        private int _enviados;

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            _enviados++;
            if (_enviados == 2)
                throw new InvalidOperationException("falha injetada: segundo item do lote");
            return interno.Send(request, cancellationToken);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest => throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default)
            => interno.Publish(notification, cancellationToken);

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
            => interno.Publish(notification, cancellationToken);
    }

    /// <summary>
    /// Mediador que serve UM handler registrado localmente — usado nos testes de
    /// controller, que não precisam do container de DI.
    /// </summary>
    private sealed class MediadorDeUmHandler : IMediator
    {
        private readonly Dictionary<Type, object> _handlers = [];

        public MediadorDeUmHandler(params object[] handlers)
        {
            foreach (var h in handlers)
                foreach (var iface in h.GetType().GetInterfaces()
                             .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)))
                    _handlers[iface.GetGenericArguments()[0]] = h;
        }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            // O controller pode enviar mais de um tipo de comando; despachamos pelo tipo do
            // request em vez de assumir um handler único.
            if (!_handlers.TryGetValue(request!.GetType(), out var handler))
                throw new InvalidOperationException(
                    $"Nenhum handler de teste registrado para {request.GetType().Name}");

            return (Task<TResponse>)handler.GetType().GetMethod("Handle")!
                .Invoke(handler, [request, cancellationToken])!;
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest => throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    /// <summary>Contexto reduzido às três entidades de identidade usadas aqui.</summary>
    private sealed class RolePermissionTestDbContext(DbContextOptions<DiscoveryDbContext> options)
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
                entity.HasKey(item => new { item.RoleId, item.PermissionId }));
        }
    }
}
