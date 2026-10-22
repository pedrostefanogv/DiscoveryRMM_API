using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Robustez do <see cref="PatchRepository.ReplaceInventoryAsync"/>: upload
/// vazio ou parcial NUNCA destrói o inventário bom.
///
/// Cenários reais do campo: o agente roda como SYSTEM e a busca WUA volta vazia
/// (wuauserv desabilitado, WSUS, download em andamento); a lista de instaladas
/// (QFE) falha em scan "partial"; o agente antigo não manda o bloco "state".
/// </summary>
[TestFixture]
public class PatchRepositoryInventoryTests
{
    private static DiscoveryDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase("patch-inventory-" + Guid.NewGuid().ToString("N"))
            .Options;
        return new PatchTestDbContext(options);
    }

    /// <summary>Contexto reduzido (mesmo padrão de PatchApprovalResolverTests).</summary>
    private sealed class PatchTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(Client),
                typeof(Site),
                typeof(Agent),
                typeof(AgentWindowsUpdate),
                typeof(AgentWindowsUpdateSnapshot),
                typeof(PatchApproval),
                typeof(PatchPolicy)
            };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null &&
                                        type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowed.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Client>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<Site>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<Agent>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<AgentWindowsUpdate>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<AgentWindowsUpdateSnapshot>(entity => entity.HasKey(item => item.AgentId));
            modelBuilder.Entity<PatchApproval>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<PatchPolicy>(entity => entity.HasKey(item => item.Id));
        }
    }

    private static (DiscoveryDbContext Db, Guid AgentId) Seed()
    {
        var db = NewContext();
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        db.Clients.Add(new Client { Id = clientId, Name = "Cliente" });
        db.Sites.Add(new Site { Id = siteId, ClientId = clientId, Name = "Site" });
        db.Agents.Add(new Agent { Id = agentId, SiteId = siteId, Hostname = "HOST" });
        db.SaveChanges();

        return (db, agentId);
    }

    /// <summary>Estado "bom" anterior: 1 pendente + 1 instalada + snapshot ok.</summary>
    private static async Task SeedGoodInventoryAsync(DiscoveryDbContext db, Guid agentId)
    {
        var installedAt = new DateTime(2026, 10, 16, 21, 9, 57, DateTimeKind.Utc);
        db.AgentWindowsUpdates.AddRange(
            new AgentWindowsUpdate
            {
                Id = Guid.NewGuid(),
                AgentId = agentId,
                UpdateKey = "guid:11111111-1111-1111-1111-111111111111:200",
                GroupKey = "kb:KB5127000",
                KbIds = "[\"KB5127000\"]",
                Title = "2026-10 Cumulative Update",
                IsPending = true,
                Status = PatchUpdateStatus.Approved,
                ApprovalState = PatchApprovalState.Approved,
                IsPresent = true,
                ReportedAt = DateTime.UtcNow,
                FirstSeenAt = DateTime.UtcNow,
                LastSeenAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new AgentWindowsUpdate
            {
                Id = Guid.NewGuid(),
                AgentId = agentId,
                UpdateKey = "kb:KB5127690",
                GroupKey = "kb:KB5127690",
                KbIds = "[\"KB5127690\"]",
                Title = "Windows 11 Insider Preview Quality Update (KB5127690)",
                IsInstalled = true,
                Status = PatchUpdateStatus.Installed,
                InstalledAt = installedAt,
                IsPresent = true,
                ReportedAt = DateTime.UtcNow,
                FirstSeenAt = DateTime.UtcNow,
                LastSeenAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });

        db.AgentWindowsUpdateSnapshots.Add(new AgentWindowsUpdateSnapshot
        {
            AgentId = agentId,
            CollectedAt = DateTime.UtcNow.AddHours(-1),
            ScanStatus = "ok",
            PendingCount = 1,
            InstalledCount = 1,
            PendingReboot = false,
            UpdatedAt = DateTime.UtcNow.AddHours(-1)
        });

        await db.SaveChangesAsync();
    }

    private static AgentWindowsUpdateEntry Entry(string updateKey, bool installed, string? kb = null)
        => new()
        {
            UpdateKey = updateKey,
            KbIds = kb is null ? null : "[\"" + kb + "\"]",
            Title = kb is null ? updateKey : "Update " + kb,
            IsInstalled = installed,
            IsPending = !installed,
            Source = "wua",
            InstalledAt = installed ? new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc) : null
        };

    private static PatchScanMeta Scan(string status = "ok") => new() { Status = status, DurationMs = 100 };

    private static async Task<(int Pending, int Installed, bool AnySuperseded)> StateAsync(
        DiscoveryDbContext db, Guid agentId)
    {
        var rows = await db.AgentWindowsUpdates.Where(row => row.AgentId == agentId).ToListAsync();
        return (
            rows.Count(row => row.IsPresent && row.IsPending),
            rows.Count(row => row.IsPresent && row.IsInstalled),
            rows.Any(row => row.Status == PatchUpdateStatus.Superseded));
    }

    [Test]
    public async Task EmptyUploadKeepsInventoryAndCounters()
    {
        var (db, agentId) = Seed();
        await SeedGoodInventoryAsync(db, agentId);

        var repo = new PatchRepository(db);
        await repo.ReplaceInventoryAsync(agentId, DateTime.UtcNow, Scan(), null, []);

        var (pending, installed, anySuperseded) = await StateAsync(db, agentId);
        Assert.That(pending, Is.EqualTo(1), "pendente preservada");
        Assert.That(installed, Is.EqualTo(1), "instalada preservada");
        Assert.That(anySuperseded, Is.False, "upload vazio não marca Superseded");

        var snapshot = await db.AgentWindowsUpdateSnapshots.SingleAsync(item => item.AgentId == agentId);
        Assert.That(snapshot.PendingCount, Is.EqualTo(1), "contador de pendentes não é zerado");
        Assert.That(snapshot.InstalledCount, Is.EqualTo(1), "contador de instaladas não é zerado");
    }

    [Test]
    public async Task EmptyUploadStillPersistsScanStatusAndProbe()
    {
        var (db, agentId) = Seed();
        await SeedGoodInventoryAsync(db, agentId);

        var repo = new PatchRepository(db);
        var scan = Scan("error");
        scan.ErrorCode = "ErrServiceDisabled";
        await repo.ReplaceInventoryAsync(
            agentId,
            DateTime.UtcNow,
            scan,
            null,
            [],
            new PatchServiceStateMeta
            {
                ServiceState = "disabled",
                WuOnline = false,
                PolicyManaged = true,
                DownloadInProgress = false
            });

        var snapshot = await db.AgentWindowsUpdateSnapshots.SingleAsync(item => item.AgentId == agentId);
        Assert.That(snapshot.ScanStatus, Is.EqualTo("error"));
        Assert.That(snapshot.ScanError, Is.EqualTo("ErrServiceDisabled"));
        Assert.That(snapshot.ServiceState, Is.EqualTo("disabled"));
        Assert.That(snapshot.WuOnline, Is.False);
        Assert.That(snapshot.PolicyManaged, Is.True);
        Assert.That(snapshot.StateChangedAt, Is.Not.Null);
        // Contadores antigos ficam: a falha não prova que não há pendências.
        Assert.That(snapshot.PendingCount, Is.EqualTo(1));
    }

    [Test]
    public async Task PartialScanWithoutPendingKeepsPendingRowsAndCounter()
    {
        var (db, agentId) = Seed();
        await SeedGoodInventoryAsync(db, agentId);

        var repo = new PatchRepository(db);
        // Scan parcial que só trouxe a lista de instaladas (WUA falhou).
        await repo.ReplaceInventoryAsync(
            agentId, DateTime.UtcNow, Scan("partial"), null,
            [Entry("kb:KB5127690", installed: true, kb: "KB5127690")]);

        var (pending, installed, anySuperseded) = await StateAsync(db, agentId);
        Assert.That(pending, Is.EqualTo(1), "pendente preservada em scan parcial");
        Assert.That(installed, Is.EqualTo(1));
        Assert.That(anySuperseded, Is.False, "silêncio do WUA não marca Superseded");

        var snapshot = await db.AgentWindowsUpdateSnapshots.SingleAsync(item => item.AgentId == agentId);
        Assert.That(snapshot.PendingCount, Is.EqualTo(1), "contador preservado em scan parcial");
        Assert.That(snapshot.ScanStatus, Is.EqualTo("partial"));
    }

    [Test]
    public async Task PartialScanWithoutInstalledKeepsInstalledRowsAndInstalledAt()
    {
        var (db, agentId) = Seed();
        await SeedGoodInventoryAsync(db, agentId);
        var before = (await db.AgentWindowsUpdates.SingleAsync(row => row.IsInstalled)).InstalledAt;

        var repo = new PatchRepository(db);
        // QFE falhou: só veio a lista de pendentes.
        await repo.ReplaceInventoryAsync(
            agentId, DateTime.UtcNow, Scan("partial"), null,
            [Entry("guid:11111111-1111-1111-1111-111111111111:200", installed: false, kb: "KB5127000")]);

        var installedRow = await db.AgentWindowsUpdates.SingleAsync(row => row.IsInstalled);
        Assert.That(installedRow.IsPresent, Is.True, "instalada preservada em scan parcial");
        Assert.That(installedRow.InstalledAt, Is.EqualTo(before), "installedAt preservado");

        var snapshot = await db.AgentWindowsUpdateSnapshots.SingleAsync(item => item.AgentId == agentId);
        Assert.That(snapshot.InstalledCount, Is.EqualTo(1), "contador de instaladas preservado");
    }

    [Test]
    public async Task FullOkScanStillReplacesAndSupersedes()
    {
        var (db, agentId) = Seed();
        await SeedGoodInventoryAsync(db, agentId);

        var repo = new PatchRepository(db);
        // Scan ok completo: a pendente sumiu (instalada/retencionada no Windows).
        await repo.ReplaceInventoryAsync(
            agentId, DateTime.UtcNow, Scan(), null,
            [Entry("kb:KB5127690", installed: true, kb: "KB5127690")]);

        var (pending, installed, anySuperseded) = await StateAsync(db, agentId);
        Assert.That(pending, Is.EqualTo(0), "scan ok sem a pendente reflete a realidade");
        Assert.That(installed, Is.EqualTo(1));
        Assert.That(anySuperseded, Is.True, "scan ok marca Superseded normalmente");
    }

    [Test]
    public async Task InstalledAtIsPreservedWhenAgentOmitsIt()
    {
        var (db, agentId) = Seed();
        await SeedGoodInventoryAsync(db, agentId);
        var before = (await db.AgentWindowsUpdates.SingleAsync(row => row.IsInstalled)).InstalledAt;

        var entry = Entry("kb:KB5127690", installed: true, kb: "KB5127690");
        entry.InstalledAt = null; // QFE não informa InstallDate

        var repo = new PatchRepository(db);
        await repo.ReplaceInventoryAsync(agentId, DateTime.UtcNow, Scan(), null, [entry]);

        var row = await db.AgentWindowsUpdates.SingleAsync(item => item.IsInstalled);
        Assert.That(row.InstalledAt, Is.EqualTo(before));
        Assert.That(row.Title, Is.EqualTo("Update KB5127690"));
    }

    [Test]
    public async Task MissingProbeDoesNotEraseKnownState()
    {
        var (db, agentId) = Seed();
        await SeedGoodInventoryAsync(db, agentId);

        var repo = new PatchRepository(db);
        await repo.ReplaceInventoryAsync(
            agentId, DateTime.UtcNow, Scan(), null,
            [Entry("kb:KB5127690", installed: true, kb: "KB5127690")],
            new PatchServiceStateMeta { ServiceState = "running", WuOnline = true, LastInstallSuccessAt = DateTime.UtcNow });

        // Upload de agente antigo (sem bloco "state").
        await repo.ReplaceInventoryAsync(
            agentId, DateTime.UtcNow, Scan(), null,
            [Entry("kb:KB5127690", installed: true, kb: "KB5127690")]);

        var snapshot = await db.AgentWindowsUpdateSnapshots.SingleAsync(item => item.AgentId == agentId);
        Assert.That(snapshot.ServiceState, Is.EqualTo("running"));
        Assert.That(snapshot.WuOnline, Is.True);
        Assert.That(snapshot.LastInstallSuccessAt, Is.Not.Null);
    }

    [Test]
    public async Task UpdateServiceStateOnlyTouchesProbeColumns()
    {
        var (db, agentId) = Seed();
        await SeedGoodInventoryAsync(db, agentId);

        var repo = new PatchRepository(db);
        await repo.UpdateServiceStateAsync(agentId, new PatchServiceStateMeta
        {
            DownloadInProgress = true,
            StateHash = "abc123"
        });

        var snapshot = await db.AgentWindowsUpdateSnapshots.SingleAsync(item => item.AgentId == agentId);
        Assert.That(snapshot.DownloadInProgress, Is.True);
        Assert.That(snapshot.StateHash, Is.EqualTo("abc123"));
        // Sonda não é scan: status/contadores do último scan ficam intactos.
        Assert.That(snapshot.ScanStatus, Is.EqualTo("ok"));
        Assert.That(snapshot.PendingCount, Is.EqualTo(1));
    }

    [Test]
    public async Task PendingRebootBadgeOnlyForPrimaryReasons()
    {
        var (db, agentId) = Seed();
        await SeedGoodInventoryAsync(db, agentId);
        var repo = new PatchRepository(db);

        // Motivo residual (arquivos em uso de qualquer instalador): NÃO acende badge.
        await repo.UpdateAgentPendingRebootAsync(
            agentId, pending: true, reasons: ["file_rename"], checkedAt: DateTime.UtcNow);

        var agent = await db.Agents.SingleAsync(item => item.Id == agentId);
        Assert.That(agent.PendingReboot, Is.False, "file_rename não acende o badge em /agents");
        Assert.That(agent.PendingRebootReasons, Is.Not.Null, "motivo continua gravado para o detalhe");

        var snapshot = await db.AgentWindowsUpdateSnapshots.SingleAsync(item => item.AgentId == agentId);
        Assert.That(snapshot.PendingReboot, Is.False);
        Assert.That(snapshot.PendingRebootReasons, Does.Contain("file_rename"));
    }

    [Test]
    public async Task PendingRebootBadgeOnForWindowsUpdateReason()
    {
        var (db, agentId) = Seed();
        await SeedGoodInventoryAsync(db, agentId);
        var repo = new PatchRepository(db);

        // Mixto: update + residual — o principal acende o badge e os reasons
        // completos ficam gravados para o painel de detalhes.
        await repo.UpdateAgentPendingRebootAsync(
            agentId, pending: true, reasons: ["windows_update", "file_rename"], checkedAt: DateTime.UtcNow);

        var agent = await db.Agents.SingleAsync(item => item.Id == agentId);
        Assert.That(agent.PendingReboot, Is.True, "windows_update acende o badge");
        Assert.That(agent.PendingRebootReasons, Does.Contain("file_rename"));

        var snapshot = await db.AgentWindowsUpdateSnapshots.SingleAsync(item => item.AgentId == agentId);
        Assert.That(snapshot.PendingReboot, Is.True);
    }

    [Test]
    public async Task ScanPathAppliesSameBadgeFilter()
    {
        var (db, agentId) = Seed();
        await SeedGoodInventoryAsync(db, agentId);
        var repo = new PatchRepository(db);

        // Scan com sonda de reinício só por file_rename: badge permanece apagado.
        await repo.UpdateScanStatusAsync(agentId, Scan(), new PatchPendingRebootMeta
        {
            Pending = true,
            Reasons = ["file_rename", "computer_rename"],
            CheckedAt = DateTime.UtcNow
        });

        var agent = await db.Agents.SingleAsync(item => item.Id == agentId);
        Assert.That(agent.PendingReboot, Is.False, "scan path usa o mesmo filtro do badge");
        Assert.That(agent.PendingRebootReasons, Does.Contain("computer_rename"));
    }

    [Test]
    public async Task PendingRebootWithoutReasonsStillLightsBadge()
    {
        var (db, agentId) = Seed();
        await SeedGoodInventoryAsync(db, agentId);
        var repo = new PatchRepository(db);

        // Agente antigo / motivo fora da allowlist: pending=true sem reasons.
        // Não pode apagar o badge — só motivo residual CONHECIDO filtra.
        await repo.UpdateScanStatusAsync(agentId, Scan(), new PatchPendingRebootMeta
        {
            Pending = true,
            Reasons = [],
            CheckedAt = DateTime.UtcNow
        });

        var agent = await db.Agents.SingleAsync(item => item.Id == agentId);
        Assert.That(agent.PendingReboot, Is.True, "pending sem motivo informado preserva o badge");

        var snapshot = await db.AgentWindowsUpdateSnapshots.SingleAsync(item => item.AgentId == agentId);
        Assert.That(snapshot.PendingReboot, Is.True);
    }
}
