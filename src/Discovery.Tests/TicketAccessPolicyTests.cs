using Discovery.Core.Entities;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces.Auth;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Discovery.Tests;

/// <summary>
/// Testes da política ÚNICA de acesso a chamado (ITicketAccessPolicy).
///
/// Cobrem as seis regras do requisito de departamento/time:
/// R2 só o time trata; R3 qualquer um abre; R4/R5 fecha só o próprio;
/// R6 o time trata todos os chamados do departamento.
///
/// O shadow mode é DESLIGADO nestes testes: aqui queremos a decisão real, não a
/// medição. Um teste específico cobre o comportamento do shadow ligado.
/// </summary>
public class TicketAccessPolicyTests
{
    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase("ticket-access-" + Guid.NewGuid().ToString("N"))
            .Options;
        return new PolicyTestDbContext(options);
    }

    /// <summary>
    /// Contexto mínimo para os testes da política: só as entidades que a decisão
    /// consulta (departamento, vínculo, usuário e estados de workflow). Ignorar o
    /// resto do modelo evita que o provider in-memory exija configurações que não
    /// têm relação com a regra sob teste.
    /// </summary>
    private sealed class PolicyTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(User), typeof(Department), typeof(DepartmentMember), typeof(WorkflowState),
                // Ticket entra por causa do cenario de restauracao (D6), que
                // precisa ler o registro com DeletedAt preenchido.
                typeof(Ticket)
            };

            foreach (var entityType in typeof(Department).Assembly.GetTypes()
                         .Where(t => t.IsClass && t.Namespace is not null
                                     && t.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(t => !allowed.Contains(t)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<User>(e => e.HasKey(u => u.Id));
            modelBuilder.Entity<Department>(e => e.HasKey(d => d.Id));
            modelBuilder.Entity<DepartmentMember>(e => e.HasKey(m => m.Id));
            modelBuilder.Entity<WorkflowState>(e => e.HasKey(s => s.Id));
            modelBuilder.Entity<Ticket>(e => e.HasKey(t => t.Id));
        }
    }

    /// <summary>ScopeContext de teste com o acesso já resolvido (sem Redis/Db).</summary>
    private sealed class FakeScopeContext(UserScopeAccess access) : IScopeContext
    {
        public Guid? UserId { get; private set; }
        public Guid? ResolvedClientId { get; set; }
        public Guid? ResolvedSiteId { get; set; }
        public void SetUserId(Guid userId) => UserId = userId;
        public Task<UserScopeAccess> GetAccessAsync(ResourceType r, ActionType a) => Task.FromResult(access);
        public Task<bool> HasGlobalAccessAsync(ResourceType r, ActionType a) => Task.FromResult(access.HasGlobalAccess);
    }

    private static Ticket NewTicket(Guid clientId, Guid? departmentId, Guid? requesterUserId = null) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        DepartmentId = departmentId,
        RequesterUserId = requesterUserId,
        Title = "Chamado",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static async Task<(DiscoveryDbContext db, TicketAccessPolicy policy, Guid clientId, Guid deptId, Guid teamUserId, Guid outsiderId, Guid finalStateId)>
        BuildScenarioAsync(bool globalAccess = false)
    {
        var db = CreateDb();
        var clientId = Guid.NewGuid();
        var deptId = Guid.NewGuid();
        var teamUserId = Guid.NewGuid();
        var outsiderId = Guid.NewGuid();

        var department = new Department
        {
            Id = deptId, ClientId = clientId, Name = "Suporte", IsActive = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        var teamUser = new User { Id = teamUserId, Login = "team", Email = "t@x.com", FullName = "Time", IsActive = true };
        var outsider = new User { Id = outsiderId, Login = "out", Email = "o@x.com", FullName = "Fora", IsActive = true };
        var member = new DepartmentMember
        {
            Id = Guid.NewGuid(), DepartmentId = deptId, UserId = teamUserId,
            IsActive = true, CreatedAt = DateTime.UtcNow
        };
        var finalState = new WorkflowState
        {
            Id = Guid.NewGuid(), ClientId = clientId, Name = "Fechado",
            IsFinal = true, IsInitial = false
        };
        var openState = new WorkflowState
        {
            Id = Guid.NewGuid(), ClientId = clientId, Name = "Aberto",
            IsFinal = false, IsInitial = true
        };

        db.Departments.Add(department);
        db.Users.AddRange(teamUser, outsider);
        db.DepartmentMembers.Add(member);
        db.WorkflowStates.AddRange(finalState, openState);
        await db.SaveChangesAsync();

        var access = new UserScopeAccess
        {
            HasGlobalAccess = globalAccess,
            AllowedClientIds = globalAccess ? [] : [clientId],
            AllowedSiteIds = []
        };

        var policy = new TicketAccessPolicy(db, new DepartmentTeamResolver(db), new FakeScopeContext(access))
        {
            ShadowMode = false
        };

        return (db, policy, clientId, deptId, teamUserId, outsiderId, finalState.Id);
    }

    // ── R6: membro do time trata QUALQUER chamado do departamento ────────

    [Test]
    public async Task Membro_do_time_trata_chamado_de_outro_membro()
    {
        var (db, policy, clientId, deptId, teamUserId, outsiderId, _) = await BuildScenarioAsync();
        await using var _db = db;

        // Chamado aberto por OUTRA pessoa, mas no departamento do time.
        var ticket = NewTicket(clientId, deptId, requesterUserId: outsiderId);

        var decision = await policy.CanTreatAsync(teamUserId, ticket);

        Assert.That(decision.Allowed, Is.True, decision.Reason);
    }

    // ── R2: quem não é do time NÃO trata ────────────────────────────────

    [Test]
    public async Task Nao_membro_nao_trata_chamado_do_departamento()
    {
        var (db, policy, clientId, deptId, teamUserId, outsiderId, _) = await BuildScenarioAsync();
        await using var _db = db;

        var ticket = NewTicket(clientId, deptId, requesterUserId: outsiderId);

        var decision = await policy.CanTreatAsync(outsiderId, ticket);

        Assert.That(decision.Allowed, Is.False);
        Assert.That(decision.Code, Is.EqualTo(TicketAccessPolicy.CodeNotInTeam));
    }

    // ── R4: quem abriu FECHA o próprio chamado ──────────────────────────

    [Test]
    public async Task Solicitante_nao_membro_fecha_o_proprio_chamado()
    {
        var (db, policy, clientId, deptId, _, outsiderId, finalStateId) = await BuildScenarioAsync();
        await using var _db = db;

        var ticket = NewTicket(clientId, deptId, requesterUserId: outsiderId);

        var decision = await policy.CanCloseAsync(outsiderId, ticket, finalStateId);

        Assert.That(decision.Allowed, Is.True, decision.Reason);
    }

    [Test]
    public async Task Solicitante_nao_membro_nao_fecha_chamado_de_outra_pessoa()
    {
        var (db, policy, clientId, deptId, teamUserId, outsiderId, finalStateId) = await BuildScenarioAsync();
        await using var _db = db;

        // Chamado aberto pelo MEMBRO do time; o de fora tenta fechar.
        var ticket = NewTicket(clientId, deptId, requesterUserId: teamUserId);

        var decision = await policy.CanCloseAsync(outsiderId, ticket, finalStateId);

        Assert.That(decision.Allowed, Is.False);
        Assert.That(decision.Code, Is.EqualTo(TicketAccessPolicy.CodeCannotCloseOthers));
    }

    // ── R4 (limite): o solicitante só pode FECHAR, não mover para outro estado ──

    [Test]
    public async Task Solicitante_nao_membro_nao_move_para_estado_nao_final()
    {
        var (db, policy, clientId, deptId, _, outsiderId, _) = await BuildScenarioAsync();
        await using var _db = db;

        var ticket = NewTicket(clientId, deptId, requesterUserId: outsiderId);
        var openState = await db.WorkflowStates.FirstAsync(s => s.ClientId == clientId && !s.IsFinal);

        var decision = await policy.CanCloseAsync(outsiderId, ticket, openState.Id);

        Assert.That(decision.Allowed, Is.False,
            "A excecao do solicitante deve valer apenas para encerrar (estado final).");
    }

    // ── Membro do time fecha qualquer chamado do departamento ───────────

    [Test]
    public async Task Membro_do_time_fecha_chamado_de_colega()
    {
        var (db, policy, clientId, deptId, teamUserId, outsiderId, finalStateId) = await BuildScenarioAsync();
        await using var _db = db;

        var ticket = NewTicket(clientId, deptId, requesterUserId: outsiderId);

        var decision = await policy.CanCloseAsync(teamUserId, ticket, finalStateId);

        Assert.That(decision.Allowed, Is.True, decision.Reason);
    }

    // ── D1: chamado sem departamento = só acesso global trata ───────────

    [Test]
    public async Task Chamado_sem_departamento_nao_e_tratado_por_usuario_restrito()
    {
        var (db, policy, clientId, deptId, teamUserId, outsiderId, _) = await BuildScenarioAsync();
        await using var _db = db;

        // Mesmo sendo membro de um time, o chamado não tem departamento.
        var ticket = NewTicket(clientId, departmentId: null, requesterUserId: teamUserId);

        var decision = await policy.CanTreatAsync(teamUserId, ticket);

        Assert.That(decision.Allowed, Is.False);
        Assert.That(decision.Code, Is.EqualTo(TicketAccessPolicy.CodeNoDepartment));
    }

    // ── R3: ABRIR continua livre (ver é por cliente/site) ───────────────

    [Test]
    public async Task Nao_membro_ve_o_chamado_que_abriu_no_departamento()
    {
        var (db, policy, clientId, deptId, _, outsiderId, _) = await BuildScenarioAsync();
        await using var _db = db;

        var ticket = NewTicket(clientId, deptId, requesterUserId: outsiderId);

        var decision = await policy.CanViewAsync(outsiderId, ticket);

        Assert.That(decision.Allowed, Is.True,
            "Quem abriu precisa ENXERGAR o proprio chamado, mesmo sem ser do time.");
    }

    // ── Acesso global não regride ───────────────────────────────────────

    [Test]
    public async Task Acesso_global_trata_e_fecha_qualquer_chamado()
    {
        var (db, policy, clientId, deptId, _, _, finalStateId) = await BuildScenarioAsync(globalAccess: true);
        await using var _db = db;

        var outsider = Guid.NewGuid();
        var ticket = NewTicket(clientId, deptId, requesterUserId: Guid.NewGuid());

        Assert.That((await policy.CanTreatAsync(outsider, ticket)).Allowed, Is.True);
        Assert.That((await policy.CanCloseAsync(outsider, ticket, finalStateId)).Allowed, Is.True);
    }

    // ── Escopo de cliente continua sendo pré-requisito ─────────────────

    [Test]
    public async Task Membro_do_time_nao_trata_chamado_de_cliente_fora_do_escopo()
    {
        var (db, policy, clientId, deptId, teamUserId, _, _) = await BuildScenarioAsync();
        await using var _db = db;

        // Chamado de OUTRO cliente (fora do ACL), no mesmo departamento.
        var ticket = NewTicket(Guid.NewGuid(), deptId, requesterUserId: teamUserId);

        var decision = await policy.CanTreatAsync(teamUserId, ticket);

        Assert.That(decision.Allowed, Is.False,
            "O vinculo com o time nao pode furar o escopo de cliente/site.");
    }

    // ── Membros inativos deixam de autorizar ───────────────────────────

    [Test]
    public async Task Membro_inativo_do_time_nao_trata()
    {
        var (db, policy, clientId, deptId, teamUserId, _, _) = await BuildScenarioAsync();
        await using var _db = db;

        var member = await db.DepartmentMembers.FirstAsync(m => m.UserId == teamUserId);
        member.IsActive = false;
        await db.SaveChangesAsync();

        var ticket = NewTicket(clientId, deptId, requesterUserId: Guid.NewGuid());
        var decision = await policy.CanTreatAsync(teamUserId, ticket);

        Assert.That(decision.Allowed, Is.False);
        Assert.That(decision.Code, Is.EqualTo(TicketAccessPolicy.CodeNotInTeam));
    }

    // ── Shadow mode: mede sem bloquear ─────────────────────────────────

    [Test]
    public async Task Shadow_mode_registra_negacao_mas_concede_acesso()
    {
        var (db, policy, clientId, deptId, _, outsiderId, _) = await BuildScenarioAsync();
        await using var _db = db;

        var shadow = new TicketAccessPolicy(
            db, new DepartmentTeamResolver(db), new FakeScopeContext(new UserScopeAccess
            {
                HasGlobalAccess = false,
                AllowedClientIds = [clientId],
                AllowedSiteIds = []
            }))
        {
            ShadowMode = true
        };

        var ticket = NewTicket(clientId, deptId, requesterUserId: Guid.NewGuid());
        var real = await shadow.CanTreatAsync(outsiderId, ticket);
        Assert.That(real.Allowed, Is.False, "A decisao real deve negar.");

        var applied = shadow.ApplyShadow(real, outsiderId, ticket.Id, "Edit");
        Assert.That(applied.Allowed, Is.True, "Em shadow o acesso e concedido para medicao.");
    }

    // ── D6: exclusão reversível ─────────────────────────────────────────
    //
    // A exclusão sempre foi soft (o registro nunca saiu do banco), mas não havia
    // caminho de volta: um clique errado tornava o chamado inalcançável pela API.

    [Test]
    public async Task Restaurar_chamado_da_lixeira_limpa_o_DeletedAt()
    {
        var db = CreateDb();
        await using var _db = db;
        var ticketId = Guid.NewGuid();

        db.Tickets.Add(new Ticket
        {
            Id = ticketId, ClientId = Guid.NewGuid(), Title = "Chamado",
            Description = "x", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            DeletedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        // GetByIdAsync (usado pela leitura normal) NÃO enxerga o excluído...
        Assert.That(await db.Tickets.Where(t => t.DeletedAt == null)
            .AnyAsync(t => t.Id == ticketId), Is.False);

        // ...mas a restauração precisa encontrá-lo, por isso a variante
        // "IncludingDeleted".
        var found = await db.Tickets.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == ticketId);
        Assert.That(found, Is.Not.Null, "o registro continua no banco (soft delete)");
        Assert.That(found!.DeletedAt, Is.Not.Null);
    }

    // ── D2: execução em máquina por DEPARTAMENTO ────────────────────────
    //
    // O caso que motivou a regra: um chamado de COMPRAS ("comprar um monitor")
    // pode estar vinculado a um agent, e nem por isso o time de compras deve rodar
    // script no equipamento. Sem a trava por departamento, o vínculo com o chamado
    // (que autoriza tratar) traria junto o poder de execução.

    private static async Task<(DiscoveryDbContext db, Guid clientId, Guid deptId, Guid memberId)>
        BuildExecuteScenarioAsync(bool departmentCanExecute)
    {
        var (db, _, clientId, deptId, memberId, _, _) = await BuildScenarioAsync();
        var dept = await db.Departments.FirstAsync(d => d.Id == deptId);
        dept.CanExecuteOnMachines = departmentCanExecute;
        await db.SaveChangesAsync();
        return (db, clientId, deptId, memberId);
    }

    /// <summary>
    /// ScopeContext que concede uma ação ESPECÍFICA (ex.: Execute) além de View.
    ///
    /// View é sempre concedido porque a decisão de executar passa internamente por
    /// CanTreat → CanView (o pré-requisito de escopo). Sem isso, um cenário que
    /// testa "tem Execute" falharia antes, com TICKET_OUT_OF_SCOPE, medindo a coisa
    /// errada.
    /// </summary>
    private sealed class ActionScopedContext(Guid clientId, ActionType granted) : IScopeContext
    {
        public Guid? UserId { get; private set; }
        public Guid? ResolvedClientId { get; set; }
        public Guid? ResolvedSiteId { get; set; }
        public void SetUserId(Guid userId) => UserId = userId;

        public Task<UserScopeAccess> GetAccessAsync(ResourceType r, ActionType a) =>
            Task.FromResult(new UserScopeAccess
            {
                HasGlobalAccess = false,
                AllowedClientIds = (a == granted || a == ActionType.View) ? [clientId] : [],
                AllowedSiteIds = []
            });

        public Task<bool> HasGlobalAccessAsync(ResourceType r, ActionType a) =>
            Task.FromResult(false);
    }

    [Test]
    public async Task Departamento_habilitado_permite_execucao_para_membro_do_time()
    {
        var (db, clientId, _, memberId) = await BuildExecuteScenarioAsync(departmentCanExecute: true);
        await using var _db = db;

        var policy = new TicketAccessPolicy(
            db, new DepartmentTeamResolver(db),
            new ActionScopedContext(clientId, ActionType.Execute))
        { ShadowMode = false };

        var deptId = await db.Departments.Select(d => d.Id).FirstAsync();
        var ticket = NewTicket(clientId, deptId, requesterUserId: Guid.NewGuid());
        var decision = await policy.CanExecuteOnMachineAsync(memberId, ticket);

        Assert.That(decision.Allowed, Is.True, decision.Reason);
    }

    [Test]
    public async Task Departamento_de_compras_NAO_executa_mesmo_com_agent_vinculado()
    {
        // O cenário exato do requisito: chamado do time, vinculado a uma máquina,
        // mas o departamento não executa automação.
        var (db, clientId, deptId, memberId) = await BuildExecuteScenarioAsync(departmentCanExecute: false);
        await using var _db = db;

        var policy = new TicketAccessPolicy(
            db, new DepartmentTeamResolver(db),
            new ActionScopedContext(clientId, ActionType.Execute))
        { ShadowMode = false };

        var ticket = NewTicket(clientId, deptId, requesterUserId: Guid.NewGuid());
        ticket.AgentId = Guid.NewGuid();   // máquina vinculada, como no caso real

        var decision = await policy.CanExecuteOnMachineAsync(memberId, ticket);

        Assert.That(decision.Allowed, Is.False,
            "Membro do time nao basta: o DEPARTAMENTO precisa estar habilitado a executar.");
        Assert.That(decision.Code, Is.EqualTo(TicketAccessPolicy.CodeDepartmentCannotExecute));
    }

    [Test]
    public async Task Sem_permissao_de_execucao_nao_roda_mesmo_com_departamento_habilitado()
    {
        var (db, clientId, deptId, memberId) = await BuildExecuteScenarioAsync(departmentCanExecute: true);
        await using var _db = db;

        // O contexto concede apenas View — não Execute.
        var policy = new TicketAccessPolicy(
            db, new DepartmentTeamResolver(db),
            new ActionScopedContext(clientId, ActionType.View))
        { ShadowMode = false };

        var ticket = NewTicket(clientId, deptId, requesterUserId: Guid.NewGuid());
        var decision = await policy.CanExecuteOnMachineAsync(memberId, ticket);

        Assert.That(decision.Allowed, Is.False,
            "As duas condicoes sao obrigatorias: permissao E habilitacao do departamento.");
        Assert.That(decision.Code, Is.EqualTo(TicketAccessPolicy.CodeNoExecutePermission));
    }

    // ── Shadow mode vindo da CONFIGURAÇÃO ───────────────────────────────
    //
    // Antes de ligar a regra em produção é preciso poder desligá-la. Estes testes
    // garantem que a chave de configuração realmente controla o modo — se ela
    // fosse ignorada, a regra ficaria permanentemente em observação (nunca
    // bloquearia) ou permanentemente ativa (quebraria a operação).

    private static IConfiguration ConfigWithShadow(bool value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tickets:DepartmentAuth:ShadowMode"] = value.ToString()
            })
            .Build();

    [Test]
    public async Task Configuracao_pode_desligar_o_shadow_para_ativar_a_regra()
    {
        var (db, _, clientId, _, _, _, _) = await BuildScenarioAsync();
        await using var _db = db;

        var policy = new TicketAccessPolicy(
            db, new DepartmentTeamResolver(db),
            new FakeScopeContext(new UserScopeAccess
            {
                HasGlobalAccess = false, AllowedClientIds = [clientId], AllowedSiteIds = []
            }),
            ConfigWithShadow(false));

        Assert.That(policy.ShadowMode, Is.False, "a configuracao deve ATIVAR o bloqueio real");
    }

    [Test]
    public async Task Configuracao_pode_manter_o_shadow_ligado()
    {
        var (db, _, clientId, _, _, _, _) = await BuildScenarioAsync();
        await using var _db = db;

        var policy = new TicketAccessPolicy(
            db, new DepartmentTeamResolver(db),
            new FakeScopeContext(new UserScopeAccess
            {
                HasGlobalAccess = false, AllowedClientIds = [clientId], AllowedSiteIds = []
            }),
            ConfigWithShadow(true));

        Assert.That(policy.ShadowMode, Is.True);
    }

    [Test]
    public async Task Sem_configuracao_o_padrao_e_observar_nao_bloquear()
    {
        var (db, _, clientId, _, _, _, _) = await BuildScenarioAsync();
        await using var _db = db;

        // Padrão seguro: ligar o código não pode quebrar o atendimento existente.
        var policy = new TicketAccessPolicy(
            db, new DepartmentTeamResolver(db),
            new FakeScopeContext(new UserScopeAccess
            {
                HasGlobalAccess = false, AllowedClientIds = [clientId], AllowedSiteIds = []
            }));

        Assert.That(policy.ShadowMode, Is.True,
            "sem configuracao explicita a regra deve apenas OBSERVAR");
    }

    // ── Herança de departamento global (D7) autoriza ────────────────────

    [Test]
    public async Task Membro_herdado_do_departamento_global_atende()
    {
        var db = CreateDb();
        await using var _db = db;
        var clientId = Guid.NewGuid();
        var globalDept = new Department
        {
            Id = Guid.NewGuid(), ClientId = null, Name = "Global", IsActive = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        var clientDept = new Department
        {
            Id = Guid.NewGuid(), ClientId = clientId, Name = "Cliente", IsActive = true,
            InheritFromGlobalId = globalDept.Id,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        var globalMemberId = Guid.NewGuid();
        db.Departments.AddRange(globalDept, clientDept);
        db.Users.Add(new User { Id = globalMemberId, Login = "g", Email = "g@x.com", FullName = "Global", IsActive = true });
        db.DepartmentMembers.Add(new DepartmentMember
        {
            Id = Guid.NewGuid(), DepartmentId = globalDept.Id, UserId = globalMemberId,
            IsActive = true, CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var policy = new TicketAccessPolicy(
            db, new DepartmentTeamResolver(db),
            new FakeScopeContext(new UserScopeAccess
            {
                HasGlobalAccess = false, AllowedClientIds = [clientId], AllowedSiteIds = []
            }))
        {
            ShadowMode = false
        };

        var ticket = NewTicket(clientId, clientDept.Id, requesterUserId: Guid.NewGuid());
        var decision = await policy.CanTreatAsync(globalMemberId, ticket);

        Assert.That(decision.Allowed, Is.True,
            "A heranca de um salto do departamento global deve autorizar o atendimento.");
    }
}
