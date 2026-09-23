using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Inventory.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;
using Discovery.Infrastructure.Cqrs.Agents.QueryHandlers;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Paginação por cursor (ponteiro) das abas de Portas em Escuta / Conexões
/// Abertas do detalhe do agente. O cursor é um índice dentro da lista filtrada
/// e ordenada do snapshot de componentes — cobre slicing, continuação, filtro,
/// limite, cursor inválido (não pode repetir a 1ª página) e o campo State.
/// </summary>
public class AgentNetworkPageHandlerTests
{
    private static readonly Guid AgentId = Guid.Parse("019faa6f-8eaa-7c38-9878-36cc96f9be3e");

    private sealed class FakeAgentRepository(Agent agent) : IAgentRepository
    {
        public Task<Agent?> GetByIdAsync(Guid id) => Task.FromResult<Agent?>(agent);
        public Task<IEnumerable<Agent>> GetAllAsync() => Task.FromResult<IEnumerable<Agent>>([agent]);
        public Task<IEnumerable<Agent>> GetBySiteIdAsync(Guid siteId) => Task.FromResult<IEnumerable<Agent>>([agent]);
        public Task<IEnumerable<Agent>> GetByClientIdAsync(Guid clientId) => Task.FromResult<IEnumerable<Agent>>([agent]);
        public Task<Agent> CreateAsync(Agent a) => Task.FromResult(agent);
        public Task UpdateAsync(Agent a) => Task.CompletedTask;
        public Task UpdateStatusAsync(Guid id, AgentStatus status, string? ip) => Task.CompletedTask;
        public Task<IReadOnlyList<Agent>> GetOnlineAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Agent>>([agent]);
        public Task ApproveZeroTouchAsync(Guid agentId) => Task.CompletedTask;
        public Task SetMaintenanceAsync(Guid id, bool enabled, string? reason, Guid changedByUserId) => Task.CompletedTask;
        public Task TransferSiteAsync(Guid agentId, Guid newSiteId) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
        public Task<IReadOnlyList<Agent>> FindByFingerprintAsync(string hash, Guid clientId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Agent>>([]);
    }

    private sealed class FakeHardwareRepository(AgentHardwareComponents components) : IAgentHardwareRepository
    {
        public Task<AgentHardwareInfo?> GetByAgentIdAsync(Guid agentId) => Task.FromResult<AgentHardwareInfo?>(null);
        public Task<AgentHardwareComponents> GetComponentsAsync(Guid agentId) => Task.FromResult(components);
        public Task UpsertAsync(AgentHardwareInfo hardware, AgentHardwareComponents? components = null) => Task.CompletedTask;
    }

    private static AgentHardwareComponents ComponentsWithSockets(int count)
    {
        var sockets = Enumerable.Range(1, count)
            .Select(i => new OpenSocketInfo
            {
                Id = Guid.NewGuid(),
                AgentId = AgentId,
                ProcessName = "discovery-service.exe",
                ProcessId = 3292,
                LocalAddress = "192.168.10.72",
                LocalPort = 40000 + i,
                RemoteAddress = "192.168.10." + (i % 250),
                RemotePort = 41080,
                Protocol = "tcp",
                Family = "IPv4",
                State = i % 2 == 0 ? "TIME_WAIT" : "ESTABLISHED",
                CollectedAt = DateTime.UtcNow
            })
            .ToList();
        return new AgentHardwareComponents { OpenSockets = sockets };
    }

    private static GetAgentOpenSocketsPageQueryHandler SocketsHandler(AgentHardwareComponents components)
        => new(new FakeAgentRepository(new Agent { Id = AgentId }), new FakeHardwareRepository(components));

    private static GetAgentListeningPortsPageQueryHandler PortsHandler(AgentHardwareComponents components)
        => new(new FakeAgentRepository(new Agent { Id = AgentId }), new FakeHardwareRepository(components));

    [Test]
    public async Task OpenSocketsPage_FirstPage_SlicesSortsAndMapsState()
    {
        var handler = SocketsHandler(ComponentsWithSockets(25));

        var result = await handler.Handle(new GetAgentOpenSocketsPageQuery(AgentId, Cursor: null, Limit: 10), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        var page = result.Value!;
        Assert.That(page.Items.Count, Is.EqualTo(10));
        Assert.That(page.TotalCount, Is.EqualTo(25));
        Assert.That(page.HasMore, Is.True);
        Assert.That(page.NextCursor, Is.Not.Null);
        // Estado TCP flui até o DTO (coluna nova da tabela).
        Assert.That(page.Items.All(i => i.State is "TIME_WAIT" or "ESTABLISHED"), Is.True);
        // CollectedAt também: sem ele a coluna "Última coleta" exibia "—".
        Assert.That(page.Items.All(i => i.CollectedAt != default), Is.True);
        // Ordenação determinística por LocalPort: 40001..40010 na primeira página.
        Assert.That(page.Items.Select(i => i.LocalPort).ToList(),
            Is.EqualTo(Enumerable.Range(40001, 10).ToList()));
    }

    [Test]
    public async Task OpenSocketsPage_CursorContinuesAndEnds()
    {
        var handler = SocketsHandler(ComponentsWithSockets(25));

        var first = await handler.Handle(new GetAgentOpenSocketsPageQuery(AgentId, Cursor: null, Limit: 10), CancellationToken.None);
        var second = await handler.Handle(new GetAgentOpenSocketsPageQuery(AgentId, Cursor: first.Value!.NextCursor, Limit: 10), CancellationToken.None);
        var third = await handler.Handle(new GetAgentOpenSocketsPageQuery(AgentId, Cursor: second.Value!.NextCursor, Limit: 10), CancellationToken.None);

        Assert.That(second.Value.TotalCount, Is.EqualTo(25));
        Assert.That(second.Value.HasMore, Is.True);
        // Sem sobreposição nem buraco entre páginas.
        var all = first.Value!.Items.Select(i => i.LocalPort)
            .Concat(second.Value!.Items.Select(i => i.LocalPort))
            .Concat(third.Value!.Items.Select(i => i.LocalPort))
            .ToList();
        Assert.That(all.Count, Is.EqualTo(25));
        Assert.That(all.Distinct().Count(), Is.EqualTo(25));
        Assert.That(third.Value.HasMore, Is.False);
        Assert.That(third.Value.NextCursor, Is.Null);
    }

    [Test]
    public async Task OpenSocketsPage_InvalidCursor_FailsInsteadOfRepeatingFirstPage()
    {
        var handler = SocketsHandler(ComponentsWithSockets(5));

        var result = await handler.Handle(new GetAgentOpenSocketsPageQuery(AgentId, Cursor: "###nao-base64###", Limit: 10), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
    }

    [Test]
    public async Task OpenSocketsPage_SearchFiltersTotal()
    {
        var handler = SocketsHandler(ComponentsWithSockets(20));

        var result = await handler.Handle(new GetAgentOpenSocketsPageQuery(AgentId, Cursor: null, Limit: 5, Search: "TIME_WAIT"), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.TotalCount, Is.EqualTo(10));
        Assert.That(result.Value!.Items.All(i => i.State == "TIME_WAIT"), Is.True);
    }

    [Test]
    public async Task ListeningPortsPage_LimitIsClampedToBackendMax()
    {
        var ports = Enumerable.Range(1, 300)
            .Select(i => new ListeningPortInfo
            {
                Id = Guid.NewGuid(),
                AgentId = AgentId,
                ProcessName = "svc.exe",
                ProcessId = 1000 + i,
                Protocol = "tcp",
                Address = "0.0.0.0",
                Port = 10000 + i
            })
            .ToList();
        var handler = PortsHandler(new AgentHardwareComponents { ListeningPorts = ports });

        // Limit acima do teto do backend (200) é clampado — uma página "Todos"
        // nunca devolve mais do que o snapshot persistiu.
        var result = await handler.Handle(new GetAgentListeningPortsPageQuery(AgentId, Cursor: null, Limit: 5000), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Items.Count, Is.EqualTo(HardwareInventoryParser.MaxListeningPorts));
        Assert.That(result.Value!.TotalCount, Is.EqualTo(300));
        Assert.That(result.Value!.HasMore, Is.True);
        Assert.That(result.Value!.Limit, Is.EqualTo(5000));
        Assert.That(result.Value!.Items.All(i => i.CollectedAt != default), Is.True);
    }

    [Test]
    public async Task OpenSocketsPage_StaleCursorBeyondSnapshot_ReturnsEmptyPageWithoutError()
    {
        var handler = SocketsHandler(ComponentsWithSockets(5));
        var stale = CursorPaginationHelper.EncodeIndexCursor(100);

        var result = await handler.Handle(new GetAgentOpenSocketsPageQuery(AgentId, Cursor: stale, Limit: 10), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Items, Is.Empty);
        Assert.That(result.Value!.HasMore, Is.False);
        Assert.That(result.Value.NextCursor, Is.Null);
    }
}