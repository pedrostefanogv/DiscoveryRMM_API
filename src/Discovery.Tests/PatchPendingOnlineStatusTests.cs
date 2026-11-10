using Discovery.Core.Cqrs.Patches;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Patches;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Status online do agente na lista de pendências de atualização
/// (<see cref="GetPatchPendingQueryHandler"/>): o handler expõe o status por
/// agente, lido numa única chamada ao repositório, sem quebrar quando o agente
/// não tem status conhecido.
///
/// <para>
/// Estes testes rodam com o cache de heartbeat VAZIO — cenário de Redis
/// indisponível —, onde a decisão cai inteiramente no estado persistido.
/// O cruzamento com o heartbeat ao vivo é coberto por
/// <see cref="PatchPendingFreshnessTests"/>.
/// </para>
/// </summary>
[TestFixture]
public class PatchPendingOnlineStatusTests
{
    /// <summary>
    /// Cache sem nenhum heartbeat vivo: força o caminho de degradação para o estado
    /// persistido. A varredura por prefixo também lança, para nenhum teste daqui
    /// depender do conjunto heartbeat:online:agents (vazio em produção).
    /// </summary>
    private sealed class EmptyHeartbeatCache : IHeartbeatCacheService
    {
        public Task<IReadOnlyList<HeartbeatCacheEntry>> GetActiveHeartbeatsAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("varredura por prefixo não é fonte confiável");

        public Task<bool> SetHeartbeatAsync(AgentHeartbeat heartbeat, AgentStatus status, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<HeartbeatCacheEntry?> GetHeartbeatAsync(Guid agentId, CancellationToken ct = default)
            => Task.FromResult<HeartbeatCacheEntry?>(null);

        public Task RemoveHeartbeatAsync(Guid agentId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> IsOnlineAsync(Guid agentId, CancellationToken ct = default) => Task.FromResult(false);

        public Task<IReadOnlyList<Guid>> DetectExpiredAsync(
            IReadOnlyList<Guid> knownOnlineAgentIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task SetTransportAsync(Guid agentId, string transport, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetTransportAsync(Guid agentId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    private static (DiscoveryDbContext Db, Guid ClientId, Guid AgentAId, Guid AgentBId) Seed()
    {
        var db = BoardTestDbContext.NewContext();
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentAId = Guid.NewGuid();
        var agentBId = Guid.NewGuid();

        db.Clients.Add(new Client { Id = clientId, Name = "Cliente Alfa" });
        db.Sites.Add(new Site { Id = siteId, ClientId = clientId, Name = "Matriz" });
        // A está Online e B Offline: o handler deve devolver exatamente o status
        // persistido, sem inferir nada de LastSeenAt.
        db.Agents.Add(new Agent
        {
            Id = agentAId,
            SiteId = siteId,
            Hostname = "HOST-A",
            Status = AgentStatus.Online,
            LastSeenAt = DateTime.UtcNow
        });
        db.Agents.Add(new Agent
        {
            Id = agentBId,
            SiteId = siteId,
            Hostname = "HOST-B",
            Status = AgentStatus.Offline,
            LastSeenAt = DateTime.UtcNow.AddDays(-2)
        });
        db.SaveChanges();

        return (db, clientId, agentAId, agentBId);
    }

    private static AgentWindowsUpdate Pending(Guid agentId) => new()
    {
        Id = Guid.NewGuid(),
        AgentId = agentId,
        UpdateKey = "kb:KB" + Guid.NewGuid().ToString("N")[..8],
        GroupKey = "kb:KB" + Guid.NewGuid().ToString("N")[..8],
        Title = "Update",
        IsPresent = true,
        IsPending = true,
        Status = PatchUpdateStatus.Pending,
        ReportedAt = DateTime.UtcNow,
        FirstSeenAt = DateTime.UtcNow,
        LastSeenAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static AgentWindowsUpdateSnapshot Snapshot(Guid agentId) => new()
    {
        AgentId = agentId,
        ScanStatus = "ok",
        CollectedAt = DateTime.UtcNow,
        PendingCount = 1
    };

    [Test]
    public async Task ListaDePendencias_ExpoeStatusPersistidoPorAgente()
    {
        var (db, _, agentAId, agentBId) = Seed();
        db.AgentWindowsUpdates.Add(Pending(agentAId));
        db.AgentWindowsUpdates.Add(Pending(agentBId));
        db.AgentWindowsUpdateSnapshots.Add(Snapshot(agentAId));
        db.AgentWindowsUpdateSnapshots.Add(Snapshot(agentBId));
        await db.SaveChangesAsync();

        var repo = new PatchRepository(db);
        var handler = new GetPatchPendingQueryHandler(repo, new EmptyHeartbeatCache());
        var result = await handler.Handle(
            new GetPatchPendingQuery(PatchScope.Global, null, null, 25, null, true, []), default);

        Assert.That(result.IsSuccess, Is.True);
        var items = result.Value!.Items;
        Assert.That(items, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(items.Single(item => item.AgentId == agentAId).Status, Is.EqualTo("Online"));
            Assert.That(items.Single(item => item.AgentId == agentBId).Status, Is.EqualTo("Offline"));
        });
    }

    [Test]
    public async Task AgenteSemLinhaDeUpdate_NaoApareceNaLista()
    {
        var (db, _, agentAId, agentBId) = Seed();
        // Só A tem pendência; B existe como agente mas não entra na visão.
        db.AgentWindowsUpdates.Add(Pending(agentAId));
        db.AgentWindowsUpdateSnapshots.Add(Snapshot(agentAId));
        await db.SaveChangesAsync();

        var repo = new PatchRepository(db);
        var handler = new GetPatchPendingQueryHandler(repo, new EmptyHeartbeatCache());
        var result = await handler.Handle(
            new GetPatchPendingQuery(PatchScope.Global, null, null, 25, null, true, []), default);

        Assert.That(result.IsSuccess, Is.True);
        var items = result.Value!.Items;
        Assert.That(items, Has.Count.EqualTo(1));
        Assert.That(items.Single().AgentId, Is.EqualTo(agentAId));
        Assert.That(items.Any(item => item.AgentId == agentBId), Is.False);
    }

    [Test]
    public async Task EscopoPorCliente_LimitaListaAoClienteInformado()
    {
        var (db, clientId, agentAId, agentBId) = Seed();
        db.AgentWindowsUpdates.Add(Pending(agentAId));
        db.AgentWindowsUpdates.Add(Pending(agentBId));
        db.AgentWindowsUpdateSnapshots.Add(Snapshot(agentAId));
        db.AgentWindowsUpdateSnapshots.Add(Snapshot(agentBId));
        await db.SaveChangesAsync();

        var repo = new PatchRepository(db);
        var handler = new GetPatchPendingQueryHandler(repo, new EmptyHeartbeatCache());

        // Cliente dono dos agentes: os dois aparecem.
        var own = await handler.Handle(
            new GetPatchPendingQuery(PatchScope.Client, clientId, null, 25, null, true, []), default);
        Assert.That(own.IsSuccess, Is.True);
        Assert.That(own.Value!.Items, Has.Count.EqualTo(2));

        // Cliente inexistente: nada vaza entre clientes.
        var other = await handler.Handle(
            new GetPatchPendingQuery(PatchScope.Client, Guid.NewGuid(), null, 25, null, true, []), default);
        Assert.That(other.IsSuccess, Is.True);
        Assert.That(other.Value!.Items, Is.Empty);
    }
}
