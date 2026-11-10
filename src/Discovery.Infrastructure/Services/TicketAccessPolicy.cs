using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Implementação única de <see cref="ITicketAccessPolicy"/>.
///
/// Regras (nesta ordem):
/// 1. Acesso global a Tickets => permitido em tudo (não regride o comportamento atual).
/// 2. VER: ACL de Cliente/Site (legado, inalterado).
/// 3. TRATAR: membro ativo do time do departamento do chamado.
/// 4. FECHAR: pode tratar OU é o solicitante E o destino é estado final.
///
/// Herança de departamento: quem responde "o usuário é do time?" é o
/// <see cref="IDepartmentTeamResolver"/>, que já implementa a herança de um salto
/// a partir do departamento GLOBAL pai (D7). Reusar o resolvedor é essencial:
/// duplicar a regra aqui faria as duas versões divergirem com o tempo.
///
/// Chamados SEM departamento (<c>DepartmentId</c> nulo) são tratados como
/// "sem time": apenas quem tem acesso global trata — política D1 do plano.
/// </summary>
public sealed class TicketAccessPolicy : ITicketAccessPolicy
{
    // Códigos estáveis para a UI traduzir em mensagem amigável e para auditoria.
    public const string CodeNotInTeam = "TICKET_NOT_IN_YOUR_TEAM";
    public const string CodeCannotCloseOthers = "CANNOT_CLOSE_OTHERS_TICKET";
    public const string CodeNoDepartment = "TICKET_HAS_NO_DEPARTMENT";
    public const string CodeNoExecutePermission = "NO_MACHINE_EXECUTE_PERMISSION";
    public const string CodeDepartmentCannotExecute = "DEPARTMENT_CANNOT_EXECUTE_ON_MACHINES";
    public const string CodeOutOfScope = "TICKET_OUT_OF_SCOPE";

    private readonly DiscoveryDbContext _db;
    private readonly IDepartmentTeamResolver _teamResolver;
    private readonly IScopeContext _scopeContext;
    private readonly ILogger<TicketAccessPolicy>? _logger;

    /// <summary>
    /// Bootstrap do appsettings (<c>Tickets:DepartmentAuth:ShadowMode</c>). É o
    /// FALLBACK: só vale enquanto a configuração do banco não foi lida com
    /// sucesso (ou em testes que constroem a política sem repositório).
    /// </summary>
    private readonly bool _shadowModeBootstrap;

    public TicketAccessPolicy(
        DiscoveryDbContext db,
        IDepartmentTeamResolver teamResolver,
        IScopeContext scopeContext,
        IConfiguration? configuration = null,
        ILogger<TicketAccessPolicy>? logger = null)
    {
        _db = db;
        _teamResolver = teamResolver;
        _scopeContext = scopeContext;
        _logger = logger;

        // Bootstrap pelo appsettings (chave Tickets:DepartmentAuth:ShadowMode).
        // O padrão é TRUE de propósito: papéis existentes (Operator/Support) têm
        // Tickets.Edit amplo e seriam bloqueados de imediato se a regra entrasse
        // valendo. Assim a regra pode ser LIGADA sem quebrar a operação, medindo
        // primeiro quem seria barrado.
        _shadowModeBootstrap = configuration?.GetValue(
            "Tickets:DepartmentAuth:ShadowMode", defaultValue: true) ?? true;

        ShadowMode = _shadowModeBootstrap;
    }

    /// <summary>
    /// Modo de observação. Em shadow as negações de tratar/fechar são REGISTRADAS
    /// mas não aplicadas. Definir como false passa a BLOQUEAR de fato — é o
    /// "liga a regra".
    ///
    /// A propriedade tem setter público para os testes alternarem sem recriar o
    /// serviço.
    ///
    /// FONTE DE VERDADE (CFG-A): a configuração do servidor
    /// (<c>ServerConfiguration.DepartmentAuthShadowMode</c>, editável na UI e
    /// herdável por cliente/site). O valor do appsettings é apenas o BOOTSTRAP:
    /// vale enquanto o banco não foi lido (ver <see cref="RefreshShadowModeAsync"/>).
    /// </summary>
    public bool ShadowMode { get; set; } = true;

    /// <summary>
    /// Relê o modo de observação da configuração do SERVIDOR (fonte de verdade).
    ///
    /// O appsettings continua sendo o bootstrap — se a leitura falhar (banco
    /// indisponível, tabela ainda não migrada em ambiente novo) mantém-se o valor
    /// atual em vez de derrubar a autorização de chamados.
    /// </summary>
    public Task RefreshShadowModeAsync(CancellationToken ct = default)
    {
        InvalidateShadowModeCache();
        _ = ResolveShadowModeCached();
        return Task.CompletedTask;
    }

    /// <summary>Valor do bootstrap do appsettings, exposto para diagnóstico/testes.</summary>
    public bool ShadowModeBootstrap => _shadowModeBootstrap;

    private bool? _resolvedShadowMode;

    /// <summary>
    /// Modo efetivo com leitura PREGUIÇOSA e única da configuração do servidor.
    ///
    /// O serviço é registrado como singleton no auto-registro, então não dá para
    /// injetar um escopo de DbContext no construtor sem virar captive dependency.
    /// A leitura é feita sob demanda, na primeira negação, e memorizada por
    /// instância; um banco indisponível cai para o bootstrap do appsettings.
    ///
    /// Leitura SÍNCRONA de propósito: <see cref="ApplyShadow"/> está no contrato
    /// síncrono da interface e é chamado no meio de decisões já assíncronas —
    /// torná-lo async mudaria a assinatura pública e todos os chamadores.
    /// </summary>
    private bool ResolveShadowModeCached()
    {
        if (_resolvedShadowMode.HasValue)
            return _resolvedShadowMode.Value;

        var value = _shadowModeBootstrap;
        try
        {
            // Projeção mínima + AsNoTracking: uma linha singleton, custo desprezível.
            var row = _db.ServerConfigurations
                .AsNoTracking()
                .Select(config => (bool?)config.DepartmentAuthShadowMode)
                .FirstOrDefault();

            if (row.HasValue)
                value = row.Value;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Não foi possível ler DepartmentAuthShadowMode da configuração do servidor; " +
                "mantendo o bootstrap do appsettings (ShadowMode={ShadowMode}).",
                value);
        }

        _resolvedShadowMode = value;
        ShadowMode = value;
        return value;
    }

    /// <summary>
    /// Descarta o valor memorizado — usado por <see cref="RefreshShadowModeAsync"/>
    /// e pelos testes que alternam a configuração sem recriar o serviço.
    /// </summary>
    public void InvalidateShadowModeCache() => _resolvedShadowMode = null;

    public async Task<bool> HasGlobalTicketAccessAsync(Guid? userId, CancellationToken ct = default)
    {
        if (userId is null) return false;
        var access = await _scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.View);
        return access.HasGlobalAccess;
    }

    public async Task<TicketAccessDecision> CanViewAsync(
        Guid? userId, Ticket ticket, CancellationToken ct = default)
    {
        if (userId is null)
            return TicketAccessDecision.Deny("Sem usuário autenticado.", CodeOutOfScope);

        var access = await _scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.View);
        if (access.HasGlobalAccess)
            return TicketAccessDecision.Permit("Acesso global a chamados.");

        // Comportamento legado preservado: a leitura continua por Cliente/Site.
        // O eixo de departamento NÃO restringe a visão nesta fase — restringir
        // aqui esconderia do solicitante o chamado que ele mesmo abriu.
        if (access.AllowedClientIds.Contains(ticket.ClientId))
            return TicketAccessDecision.Permit("Cliente no escopo do usuário.");

        if (ticket.SiteId.HasValue && access.AllowedSiteIds.Contains(ticket.SiteId.Value))
            return TicketAccessDecision.Permit("Site no escopo do usuário.");

        return TicketAccessDecision.Deny("Chamado fora do escopo de cliente/site.", CodeOutOfScope);
    }

    public async Task<TicketAccessDecision> CanTreatAsync(
        Guid? userId, Ticket ticket, CancellationToken ct = default)
    {
        if (userId is null)
            return TicketAccessDecision.Deny("Sem usuário autenticado.", CodeOutOfScope);

        if (await HasGlobalTicketAccessAsync(userId, ct))
            return TicketAccessDecision.Permit("Acesso global a chamados.");

        // Escopo de cliente/site continua sendo pré-requisito: ninguém trata um
        // chamado de um cliente que nem enxerga.
        var view = await CanViewAsync(userId, ticket, ct);
        if (!view.Allowed)
            return view;

        if (ticket.DepartmentId is null)
            return TicketAccessDecision.Deny(
                "Chamado sem departamento definido; apenas acesso global pode tratar.",
                CodeNoDepartment);

        if (await IsMemberOfDepartmentAsync(userId.Value, ticket.DepartmentId.Value, ct))
            return TicketAccessDecision.Permit("Membro do time do departamento.");

        return TicketAccessDecision.Deny(
            "Usuário não pertence ao time de atendimento deste departamento.",
            CodeNotInTeam);
    }

    public async Task<TicketAccessDecision> CanCloseAsync(
        Guid? userId, Ticket ticket, Guid targetStateId, CancellationToken ct = default)
    {
        if (userId is null)
            return TicketAccessDecision.Deny("Sem usuário autenticado.", CodeOutOfScope);

        // Quem trata, fecha — inclusive chamados de colegas do mesmo time (R6).
        var treat = await CanTreatAsync(userId, ticket, ct);
        if (treat.Allowed)
            return treat;

        // Exceção explícita do requisito: quem ABRIU pode FECHAR o próprio chamado,
        // e somente isso — não pode tratar, atribuir nem mover para outro estado.
        var isRequester = ticket.RequesterUserId.HasValue && ticket.RequesterUserId.Value == userId.Value;
        if (!isRequester)
            return TicketAccessDecision.Deny(
                "Somente o time do departamento ou quem abriu o chamado pode encerrá-lo.",
                CodeCannotCloseOthers);

        var view = await CanViewAsync(userId, ticket, ct);
        if (!view.Allowed)
            return view;

        if (!await IsFinalStateAsync(ticket.ClientId, targetStateId, ct))
            return TicketAccessDecision.Deny(
                "Quem abriu o chamado só pode encerrá-lo (não pode alterá-lo para outros estados).",
                CodeCannotCloseOthers);

        return TicketAccessDecision.Permit("Solicitante encerrando o próprio chamado.");
    }

    public async Task<TicketAccessDecision> CanExecuteOnMachineAsync(
        Guid? userId, Ticket ticket, CancellationToken ct = default)
    {
        if (userId is null)
            return TicketAccessDecision.Deny("Sem usuário autenticado.", CodeOutOfScope);

        // 1) Permissão explícita de execução (Tickets:Execute). Sem ela, nada roda —
        //    inclusive para quem tem acesso global, porque a permissão é do PAPEL.
        if (!await HasExecutePermissionAsync(userId.Value, ct))
            return TicketAccessDecision.Deny(
                "Sem permissão para executar automação em máquinas (Tickets:Execute).",
                CodeNoExecutePermission);

        // 2) Tratar o chamado continua sendo pré-requisito.
        var treat = await CanTreatAsync(userId, ticket, ct);
        if (!treat.Allowed)
            return treat;

        // 3) O DEPARTAMENTO precisa estar habilitado a executar na máquina.
        //    Esta é a trava que separa "compras" de "infra".
        if (ticket.DepartmentId is null)
            return TicketAccessDecision.Deny(
                "Chamado sem departamento: execução em máquina exige um departamento habilitado.",
                CodeDepartmentCannotExecute);

        if (!await CanDepartmentExecuteAsync(ticket.DepartmentId.Value, ct))
            return TicketAccessDecision.Deny(
                "O departamento deste chamado não está habilitado a executar automação em máquinas.",
                CodeDepartmentCannotExecute);

        return TicketAccessDecision.Permit("Departamento habilitado e usuário com permissão de execução.");
    }

    /// <summary>
    /// O departamento do chamado está habilitado a executar na máquina? Considera
    /// também o departamento GLOBAL pai, para que um cliente que herda o time
    /// herde a habilitação junto (é a mesma relação de confiança).
    /// </summary>
    private async Task<bool> CanDepartmentExecuteAsync(
        Guid departmentId, CancellationToken ct)
    {
        return await _db.Departments.AsNoTracking()
            .AnyAsync(d => d.Id == departmentId && d.CanExecuteOnMachines, ct);
    }

    /// <summary>
    /// O usuário tem <c>Tickets:Execute</c> em algum escopo? Usa o acesso já
    /// cacheado no request (o mesmo caminho do restante da política).
    /// </summary>
    private async Task<bool> HasExecutePermissionAsync(Guid userId, CancellationToken ct)
    {
        var access = await _scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.Execute);
        return access.HasGlobalAccess
            || access.AllowedClientIds.Count > 0
            || access.AllowedSiteIds.Count > 0;
    }

    public async Task<IReadOnlyList<Guid>> GetMemberDepartmentIdsAsync(
        Guid? userId, CancellationToken ct = default)
    {
        if (userId is null) return [];

        return await _db.DepartmentMembers.AsNoTracking()
            .Where(m => m.UserId == userId.Value && m.IsActive)
            .Select(m => m.DepartmentId)
            .Distinct()
            .ToListAsync(ct);
    }

    /// <summary>
    /// Cache intra-request do vínculo (userId, departmentId) -> é do time?
    ///
    /// O serviço é Scoped (uma instância por request), então este dicionário vive
    /// exatamente o tempo do request. Isso importa porque uma única decisão de
    /// "fechar" chama CanTreat internamente, e o endpoint de capacidades avalia
    /// TODOS os estados finais — sem o cache, um workflow com 3 estados finais
    /// faria 3+ consultas idênticas de equipe.
    /// </summary>
    private readonly Dictionary<(Guid UserId, Guid DepartmentId), bool> _membershipCache = new();

    /// <summary>
    /// O usuário é membro ATIVO do time? Delega a herança ao resolvedor único
    /// (próprio departamento + global pai), evitando uma segunda implementação.
    /// </summary>
    private async Task<bool> IsMemberOfDepartmentAsync(Guid userId, Guid departmentId, CancellationToken ct)
    {
        var key = (userId, departmentId);
        if (_membershipCache.TryGetValue(key, out var cached))
            return cached;

        var members = await _teamResolver.ResolveMembersAsync(departmentId, ct);
        var isMember = members.Any(m => m.UserId == userId);
        _membershipCache[key] = isMember;
        return isMember;
    }

    private async Task<bool> IsFinalStateAsync(Guid clientId, Guid targetStateId, CancellationToken ct)
    {
        // Um estado final é o que caracteriza "fechar". Restringir a transição a
        // estados finais impede que o solicitante use a exceção para mover o
        // chamado para qualquer outro estado (ex.: reabrir ou pular etapas).
        return await _db.WorkflowStates.AsNoTracking()
            .AnyAsync(s => s.Id == targetStateId && s.ClientId == clientId && s.IsFinal, ct);
    }

    /// <summary>
    /// Registra a negação quando em shadow mode e devolve se o acesso deve ser
    /// CONCEDIDO de todo modo. Fora do shadow, a negação vale.
    /// </summary>
    public TicketAccessDecision ApplyShadow(TicketAccessDecision decision, Guid? userId, Guid ticketId, string action)
    {
        // Uma decisão PERMITIDA não passa pelo shadow: não precisa nem resolver a
        // configuração. Só quando há negação é que o modo importa — e é aí que
        // lemos o valor efetivo do banco (cacheado por escopo, ver abaixo).
        if (decision.Allowed)
            return decision;

        if (!ResolveShadowModeCached())
            return decision;

        _logger?.LogWarning(
            "SHADOW departamento: usuario {UserId} seria BLOQUEADO em {Action} do chamado {TicketId} ({Code}).",
            userId, action, ticketId, decision.Code);

        return TicketAccessDecision.Permit($"shadow:{decision.Code}");
    }
}
