using Discovery.Core.Cqrs.Patches;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Cqrs.Patches;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Testes do <see cref="GetPatchComplianceQueryHandler"/>: cálculo do score,
/// fail-closed sem scan e agregação client vs site vs global.
/// </summary>
[TestFixture]
public class PatchComplianceHandlerTests
{
    private static (Discovery.Infrastructure.Data.DiscoveryDbContext Db, Guid ClientId, Guid SiteAId, Guid SiteBId, Guid AgentAId, Guid AgentBId) Seed()
    {
        var db = BoardTestDbContext.NewContext();
        var clientId = Guid.NewGuid();
        var siteAId = Guid.NewGuid();
        var siteBId = Guid.NewGuid();
        var agentAId = Guid.NewGuid();
        var agentBId = Guid.NewGuid();

        db.Clients.Add(new Client { Id = clientId, Name = "Cliente Alfa" });
        db.Sites.Add(new Site { Id = siteAId, ClientId = clientId, Name = "Matriz" });
        db.Sites.Add(new Site { Id = siteBId, ClientId = clientId, Name = "Filial" });
        db.Agents.Add(new Agent { Id = agentAId, SiteId = siteAId, Hostname = "HOST-A" });
        db.Agents.Add(new Agent { Id = agentBId, SiteId = siteBId, Hostname = "HOST-B" });
        db.SaveChanges();

        return (db, clientId, siteAId, siteBId, agentAId, agentBId);
    }

    private static AgentWindowsUpdate Pending(Guid agentId, bool mandatory = false, bool failed = false) => new()
    {
        Id = Guid.NewGuid(),
        AgentId = agentId,
        UpdateKey = "kb:KB" + Guid.NewGuid().ToString("N")[..8],
        GroupKey = "kb:KB" + Guid.NewGuid().ToString("N")[..8],
        Title = "Update",
        IsPresent = true,
        IsPending = true,
        IsMandatory = mandatory,
        Status = failed ? PatchUpdateStatus.Failed : PatchUpdateStatus.Pending,
        ReportedAt = DateTime.UtcNow,
        FirstSeenAt = DateTime.UtcNow,
        LastSeenAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static AgentWindowsUpdateSnapshot Snapshot(Guid agentId, DateTime? collectedAt) => new()
    {
        AgentId = agentId,
        CollectedAt = collectedAt,
        ScanStatus = "ok"
    };

    [Test]
    public async Task Score_ContaApenasAgentesSemPendenciaFalha()
    {
        var (db, clientId, _, _, agentAId, agentBId) = Seed();
        // Agente A: scan recente, 1 pendente obrigatório (não falho) → compliant.
        db.AgentWindowsUpdates.Add(Pending(agentAId, mandatory: true));
        db.AgentWindowsUpdateSnapshots.Add(Snapshot(agentAId, DateTime.UtcNow));
        // Agente B: scan recente, 1 pendente FALHO → não-compliant e derruba o score.
        db.AgentWindowsUpdates.Add(Pending(agentBId, failed: true));
        db.AgentWindowsUpdateSnapshots.Add(Snapshot(agentBId, DateTime.UtcNow));
        await db.SaveChangesAsync();

        var handler = new GetPatchComplianceQueryHandler(db);
        var result = await handler.Handle(new GetPatchComplianceQuery(clientId, null), default);

        Assert.That(result.IsSuccess, Is.True);
        var dto = result.Value!;
        Assert.Multiple(() =>
        {
            Assert.That(dto.Totals.Agents, Is.EqualTo(2));
            // Só B é compliant: A tem pendente obrigatório; B (pendente falho,
            // não obrigatório) derruba o score mas não a conformidade.
            Assert.That(dto.Totals.Compliant, Is.EqualTo(1));
            Assert.That(dto.Totals.Score, Is.EqualTo(50.0));
            Assert.That(dto.Totals.PendingPatches, Is.EqualTo(2));
            Assert.That(dto.Totals.FailedPatches, Is.EqualTo(1));
            // Uma row por site.
            Assert.That(dto.Rows, Has.Count.EqualTo(2));
            Assert.That(dto.Scope.ClientName, Is.EqualTo("Cliente Alfa"));
        });
    }

    [Test]
    public async Task ScanAusente_EhFailClosed_UltimoScanNulo()
    {
        var (db, clientId, siteAId, _, agentAId, _) = Seed();
        // Agente A: pendente obrigatório e SEM snapshot → fail-closed, lastScanUtc nulo.
        db.AgentWindowsUpdates.Add(Pending(agentAId, mandatory: true));
        // Agente novo na Matriz com scan velho (fora da janela de 7 dias).
        var agentOld = Guid.NewGuid();
        db.Agents.Add(new Agent { Id = agentOld, SiteId = siteAId, Hostname = "HOST-OLD" });
        db.AgentWindowsUpdateSnapshots.Add(Snapshot(agentOld, DateTime.UtcNow.AddDays(-30)));
        await db.SaveChangesAsync();

        var handler = new GetPatchComplianceQueryHandler(db);
        var result = await handler.Handle(new GetPatchComplianceQuery(clientId, null), default);

        Assert.That(result.IsSuccess, Is.True);
        var dto = result.Value!;
        Assert.That(dto.Totals.Compliant, Is.EqualTo(0));
        var row = dto.Rows.Single(r => r.SiteId == siteAId);
        Assert.Multiple(() =>
        {
            Assert.That(row.Agents, Is.EqualTo(2));
            Assert.That(row.Compliant, Is.EqualTo(0));
            Assert.That(row.LastScanUtc, Is.Not.Null);
            Assert.That(row.LastScanUtc!.Value, Is.LessThan(DateTime.UtcNow.AddDays(-7)));
        });
        // Janela maior (60 dias) torna o scan velho recente → agente fica compliant.
        var widened = await handler.Handle(new GetPatchComplianceQuery(clientId, null, ScanWindowDays: 60), default);
        Assert.That(widened.Value!.Rows.Single(r => r.SiteId == siteAId).Compliant, Is.EqualTo(1));
    }

    [Test]
    public async Task Agregacao_Global_RowsPorSite_Site_RowsPorAgente()
    {
        var (db, clientId, siteAId, siteBId, agentAId, agentBId) = Seed();
        db.AgentWindowsUpdateSnapshots.Add(Snapshot(agentAId, DateTime.UtcNow));
        db.AgentWindowsUpdateSnapshots.Add(Snapshot(agentBId, DateTime.UtcNow));
        await db.SaveChangesAsync();

        var handler = new GetPatchComplianceQueryHandler(db);

        // Escopo site → uma row por agente.
        var siteScope = await handler.Handle(new GetPatchComplianceQuery(null, siteAId), default);
        Assert.Multiple(() =>
        {
            Assert.That(siteScope.Value!.Rows, Has.Count.EqualTo(1));
            Assert.That(siteScope.Value!.Scope.SiteId, Is.EqualTo(siteAId));
            Assert.That(siteScope.Value!.Scope.ClientName, Is.EqualTo("Cliente Alfa"));
            Assert.That(siteScope.Value!.Rows[0].Agents, Is.EqualTo(1));
            Assert.That(siteScope.Value!.Rows[0].Score, Is.EqualTo(100.0));
        });

        // Escopo client → uma row por site do cliente.
        var clientScope = await handler.Handle(new GetPatchComplianceQuery(clientId, null), default);
        Assert.Multiple(() =>
        {
            Assert.That(clientScope.Value!.Rows.Select(r => r.SiteId), Is.EquivalentTo(new[] { siteAId, siteBId }));
            Assert.That(clientScope.Value!.Totals.Agents, Is.EqualTo(2));
        });

        // Global (sem client/site) → rows por site de todos os clients + totals globais.
        var global = await handler.Handle(new GetPatchComplianceQuery(null, null), default);
        Assert.Multiple(() =>
        {
            Assert.That(global.Value!.Rows, Has.Count.EqualTo(2));
            Assert.That(global.Value!.Totals.Agents, Is.EqualTo(2));
            Assert.That(global.Value!.Scope.ClientId, Is.Null);
        });
    }
}
