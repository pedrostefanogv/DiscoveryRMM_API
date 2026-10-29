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

    private static AgentWindowsUpdate PendingRow(Guid agentId, string updateKey, PatchUpdateStatus status) => new()
    {
        Id = Guid.NewGuid(),
        AgentId = agentId,
        UpdateKey = updateKey,
        GroupKey = "kb:KB5127000",
        KbIds = "[\"KB5127000\"]",
        Title = "Update " + updateKey,
        IsPending = true,
        IsPresent = true,
        Status = status,
        ReportedAt = DateTime.UtcNow,
        FirstSeenAt = DateTime.UtcNow,
        LastSeenAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static AgentWindowsUpdate InstalledRow(Guid agentId, string updateKey, string groupKey) => new()
    {
        Id = Guid.NewGuid(),
        AgentId = agentId,
        UpdateKey = updateKey,
        GroupKey = groupKey,
        KbIds = "[\"KB5127690\"]",
        Title = "Installed " + updateKey,
        IsInstalled = true,
        IsPresent = true,
        Status = PatchUpdateStatus.Installed,
        InstalledAt = new DateTime(2026, 10, 16, 21, 9, 57, DateTimeKind.Utc),
        ReportedAt = DateTime.UtcNow,
        FirstSeenAt = DateTime.UtcNow,
        LastSeenAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

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

    // ── Diagnóstico do scan + correções de dado (M239) ─────────────────────

    // ── Regressões apontadas na revisão final ──────────────────────────────

    [Test]
    public async Task PendingFilterIncludesApprovedAndDeniedPending()
    {
        var (db, agentId) = Seed();
        var repo = new PatchRepository(db);

        // O resolver grava Status=Approved/Denied para pendentes aprovadas e
        // recusadas: filtrar Status==Pending sumia com elas da aba.
        db.AgentWindowsUpdates.AddRange(
            PendingRow(agentId, "guid:11111111-1111-1111-1111-111111111111:1", PatchUpdateStatus.Pending),
            PendingRow(agentId, "guid:11111111-1111-1111-1111-111111111112:1", PatchUpdateStatus.Approved),
            PendingRow(agentId, "guid:11111111-1111-1111-1111-111111111113:1", PatchUpdateStatus.Denied));
        await db.SaveChangesAsync();

        var page = await repo.GetAgentUpdatesPageAsync(
            agentId, 1, 25, null, PatchUpdateStatus.Pending, null, true);

        Assert.That(page.Items, Has.Count.EqualTo(3), "aba Pendentes precisa listar todas as pendentes");
        Assert.That(page.Items.All(item => item.IsPending), Is.True);

        // O filtro de aprovação continua refinando por status.
        var approved = await repo.GetAgentUpdatesPageAsync(
            agentId, 1, 25, null, PatchUpdateStatus.Approved, null, true);
        Assert.That(approved.Items, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task IncompleteScanDoesNotResurrectDuplicateInstalledRow()
    {
        var (db, agentId) = Seed();
        var repo = new PatchRepository(db);

        // Mesma KB instalada em duas linhas (WUA guid + QFE kb): a aba mostra uma.
        db.AgentWindowsUpdates.AddRange(
            InstalledRow(agentId, "guid:22222222-2222-2222-2222-222222222222:200", "kb:KB5127690"),
            InstalledRow(agentId, "kb:KB5127690", "kb:KB5127690"));
        db.AgentWindowsUpdateSnapshots.Add(new AgentWindowsUpdateSnapshot
        {
            AgentId = agentId,
            ScanStatus = "ok",
            InstalledCount = 1,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var scan = Scan();
        scan.SkippedCount = 1;
        scan.SkippedReasons = new Dictionary<string, int> { ["unreadable"] = 1 };

        await repo.ReplaceInventoryAsync(
            agentId, DateTime.UtcNow, scan, null,
            [Entry("kb:KB5127690", installed: true, kb: "KB5127690")]);

        var guidRow = await db.AgentWindowsUpdates
            .SingleAsync(row => row.UpdateKey.StartsWith("guid:"));
        Assert.That(guidRow.IsPresent, Is.False, "linha antiga do mesmo KB não pode ressuscitar");

        var snapshot = await db.AgentWindowsUpdateSnapshots.SingleAsync(item => item.AgentId == agentId);
        Assert.That(snapshot.InstalledCount, Is.EqualTo(1), "aba Instaladas mostra uma linha");
    }

    [Test]
    public async Task PreservedSupersededPendingGoesBackToPending()
    {
        var (db, agentId) = Seed();
        var repo = new PatchRepository(db);

        db.AgentWindowsUpdates.Add(new AgentWindowsUpdate
        {
            Id = Guid.NewGuid(),
            AgentId = agentId,
            UpdateKey = "guid:33333333-3333-3333-3333-333333333333:200",
            GroupKey = "kb:KB5127000",
            KbIds = "[\"KB5127000\"]",
            Title = "Cumulative Update",
            IsPending = true,
            IsPresent = false,
            Status = PatchUpdateStatus.Superseded,
            ReportedAt = DateTime.UtcNow.AddHours(-2),
            FirstSeenAt = DateTime.UtcNow.AddHours(-2),
            LastSeenAt = DateTime.UtcNow.AddHours(-2),
            CreatedAt = DateTime.UtcNow.AddHours(-2),
            UpdatedAt = DateTime.UtcNow.AddHours(-2)
        });
        await db.SaveChangesAsync();

        var scan = Scan();
        scan.SkippedCount = 1;
        scan.SkippedReasons = new Dictionary<string, int> { ["unreadable"] = 1 };
        await repo.ReplaceInventoryAsync(
            agentId, DateTime.UtcNow, scan, null,
            [Entry("kb:KB5127690", installed: true, kb: "KB5127690")]);

        var row = await db.AgentWindowsUpdates
            .SingleAsync(item => item.UpdateKey.StartsWith("guid:"));
        Assert.That(row.IsPresent, Is.True);
        Assert.That(row.Status, Is.EqualTo(PatchUpdateStatus.Pending), "volta a ser pendente de verdade");

        var snapshot = await db.AgentWindowsUpdateSnapshots.SingleAsync(item => item.AgentId == agentId);
        Assert.That(snapshot.PendingCount, Is.EqualTo(1), "contador bate com a lista");

        var listed = await repo.GetAgentUpdatesPageAsync(
            agentId, 1, 25, null, PatchUpdateStatus.Pending, null, true);
        Assert.That(listed.Items.Select(item => item.UpdateKey), Does.Contain(row.UpdateKey));
    }

    [Test]
    public async Task RecollectionCorrectsInstalledAt()
    {
        var (db, agentId) = Seed();
        await SeedGoodInventoryAsync(db, agentId);

        var repo = new PatchRepository(db);
        // O agente corrigiu a data (o parse de locale do QFE havia trocado
        // dia/mês): a recoleta DEVE sobrescrever, não preservar o valor errado.
        var corrected = new DateTime(2026, 5, 3, 0, 0, 0, DateTimeKind.Utc);
        var entry = Entry("kb:KB5127690", installed: true, kb: "KB5127690");
        entry.InstalledAt = corrected;

        await repo.ReplaceInventoryAsync(agentId, DateTime.UtcNow, Scan(), null, [entry]);

        var row = await db.AgentWindowsUpdates.SingleAsync(item => item.InstalledAt != null);
        Assert.That(row.InstalledAt, Is.EqualTo(corrected), "recoleta corrige a data de instalação");
    }

    [Test]
    public async Task ScanWithUnreadableItemsDoesNotSupersedeMissingPending()
    {
        var (db, agentId) = Seed();
        await SeedGoodInventoryAsync(db, agentId);

        var repo = new PatchRepository(db);
        var scan = Scan();
        scan.SkippedCount = 1;
        scan.SkippedReasons = new Dictionary<string, int> { ["unreadable"] = 1 };

        // Lista veio sem a pendente — mas o scan confessou que não conseguiu ler
        // um item: isso não é prova de remoção.
        await repo.ReplaceInventoryAsync(
            agentId, DateTime.UtcNow, scan, null,
            [Entry("kb:KB5127690", installed: true, kb: "KB5127690")]);

        var (pending, _, anySuperseded) = await StateAsync(db, agentId);
        Assert.That(anySuperseded, Is.False, "leitura incompleta não marca Superseded");
        Assert.That(pending, Is.EqualTo(1), "pendência preservada quando o scan é incompleto");
    }

    [Test]
    public async Task IncompleteScanPreservesCountersAndRowsConsistently()
    {
        var (db, agentId) = Seed();
        await SeedGoodInventoryAsync(db, agentId);
        var repo = new PatchRepository(db);

        var scan = Scan();
        scan.SkippedCount = 1;
        scan.SkippedReasons = new Dictionary<string, int> { ["unreadable"] = 1 };

        await repo.ReplaceInventoryAsync(
            agentId, DateTime.UtcNow, scan, null,
            [Entry("kb:KB5127690", installed: true, kb: "KB5127690")]);

        var (pending, installed, _) = await StateAsync(db, agentId);
        var snapshot = await db.AgentWindowsUpdateSnapshots.SingleAsync(item => item.AgentId == agentId);

        // Contador da aba e tabela precisam contar a MESMA coisa.
        Assert.That(snapshot.PendingCount, Is.EqualTo(pending), "contador preservado acompanha as linhas preservadas");
        Assert.That(snapshot.InstalledCount, Is.EqualTo(installed));
        Assert.That(snapshot.PendingCount, Is.EqualTo(1), "pendência conhecida não é apagada por leitura incompleta");
    }

    [Test]
    public async Task ScanWithOnlyDriverSkipsStillSupersedes()
    {
        var (db, agentId) = Seed();
        await SeedGoodInventoryAsync(db, agentId);

        var repo = new PatchRepository(db);
        var scan = Scan();
        scan.SkippedCount = 2;
        scan.SkippedReasons = new Dictionary<string, int> { ["driver"] = 2 };

        // Driver fora do escopo é descarte normal: a lista de pendentes é completa.
        await repo.ReplaceInventoryAsync(
            agentId, DateTime.UtcNow, scan, null,
            [Entry("kb:KB5127690", installed: true, kb: "KB5127690")]);

        var (pending, _, anySuperseded) = await StateAsync(db, agentId);
        Assert.That(anySuperseded, Is.True, "descarte conhecido e completo mantém a regra de Superseded");
        Assert.That(pending, Is.EqualTo(0));
    }

    [Test]
    public async Task SnapshotPersistsScanDiagnosticsAndExtendedProbe()
    {
        var (db, agentId) = Seed();
        await SeedGoodInventoryAsync(db, agentId);

        var repo = new PatchRepository(db);
        var scan = Scan();
        scan.ErrorCode = null;
        scan.SkippedCount = 3;
        scan.SkippedReasons = new Dictionary<string, int> { ["driver"] = 2, ["no_identity"] = 1 };
        scan.Trigger = "change";
        scan.Source = "wua";

        await repo.ReplaceInventoryAsync(
            agentId,
            DateTime.UtcNow,
            scan,
            null,
            [Entry("kb:KB5127690", installed: true, kb: "KB5127690")],
            new PatchServiceStateMeta
            {
                ServiceState = "running",
                WsusServer = "http://wsus.empresa.local:8530",
                AuOptions = 4,
                NoAutoUpdate = false,
                DeferralDays = 7,
                ActiveHoursStart = 8,
                ActiveHoursEnd = 18,
                QfeInstalledCount = 42,
                DownloadInProgress = true,
                DownloadPercent = 42
            });

        var snapshot = await db.AgentWindowsUpdateSnapshots.SingleAsync(item => item.AgentId == agentId);
        Assert.That(snapshot.SkippedCount, Is.EqualTo(3));
        Assert.That(snapshot.SkippedReasons, Does.Contain("\"driver\":2"));
        Assert.That(snapshot.ScanTrigger, Is.EqualTo("change"));
        Assert.That(snapshot.ScanSource, Is.EqualTo("wua"));
        Assert.That(snapshot.WsusServer, Is.EqualTo("http://wsus.empresa.local:8530"));
        Assert.That(snapshot.AuOptions, Is.EqualTo(4));
        Assert.That(snapshot.DeferralDays, Is.EqualTo(7));
        Assert.That(snapshot.ActiveHoursStart, Is.EqualTo(8));
        Assert.That(snapshot.ActiveHoursEnd, Is.EqualTo(18));
        Assert.That(snapshot.QfeInstalledCount, Is.EqualTo(42));
        Assert.That(snapshot.DownloadPercent, Is.EqualTo(42));
    }

    [Test]
    public async Task ZeroSkippedCountClearsPreviousSkipReasons()
    {
        var (db, agentId) = Seed();
        await SeedGoodInventoryAsync(db, agentId);
        var repo = new PatchRepository(db);
        var entries = new[] { Entry("kb:KB5127690", installed: true, kb: "KB5127690") };

        var withSkips = Scan();
        withSkips.SkippedCount = 3;
        withSkips.SkippedReasons = new Dictionary<string, int> { ["driver"] = 2, ["no_identity"] = 1 };
        await repo.ReplaceInventoryAsync(agentId, DateTime.UtcNow, withSkips, null, entries);

        var snapshot = await db.AgentWindowsUpdateSnapshots.SingleAsync(item => item.AgentId == agentId);
        Assert.That(snapshot.SkippedCount, Is.EqualTo(3));

        // Scan seguinte sem descartes: contador zerado precisa LIMPAR os motivos.
        var clean = Scan();
        clean.SkippedCount = 0;
        clean.SkippedReasons = null;
        await repo.ReplaceInventoryAsync(agentId, DateTime.UtcNow, clean, null, entries);

        snapshot = await db.AgentWindowsUpdateSnapshots.SingleAsync(item => item.AgentId == agentId);
        Assert.That(snapshot.SkippedCount, Is.EqualTo(0));
        Assert.That(snapshot.SkippedReasons, Is.Null, "motivos antigos não podem sobreviver ao contador zerado");
    }

    [Test]
    public async Task PolicyKnownTrueClearsRemovedPolicy()
    {
        var (db, agentId) = Seed();
        await SeedGoodInventoryAsync(db, agentId);
        var repo = new PatchRepository(db);

        await repo.UpdateServiceStateAsync(agentId, new PatchServiceStateMeta
        {
            ServiceState = "running",
            PolicyKnown = true,
            WsusServer = "http://wsus.empresa.local:8530",
            AuOptions = 4,
            DeferralDays = 7,
            ActiveHoursStart = 8,
            ActiveHoursEnd = 18
        });

        // Política removida da máquina (WSUS desconfigurado, deferral zerado).
        await repo.UpdateServiceStateAsync(agentId, new PatchServiceStateMeta
        {
            ServiceState = "running",
            PolicyKnown = true,
            WsusServer = null,
            AuOptions = 0,
            DeferralDays = 0
        });

        var snapshot = await db.AgentWindowsUpdateSnapshots.SingleAsync(item => item.AgentId == agentId);
        Assert.That(snapshot.WsusServer, Is.Null, "WSUS removido precisa sair da UI");
        Assert.That(snapshot.AuOptions, Is.Null);
        Assert.That(snapshot.DeferralDays, Is.Null);
        Assert.That(snapshot.ActiveHoursStart, Is.Null);
        Assert.That(snapshot.ActiveHoursEnd, Is.Null);
    }

    [Test]
    public async Task PolicyKnownFalsePreservesKnownPolicy()
    {
        var (db, agentId) = Seed();
        await SeedGoodInventoryAsync(db, agentId);
        var repo = new PatchRepository(db);

        await repo.UpdateServiceStateAsync(agentId, new PatchServiceStateMeta
        {
            ServiceState = "running",
            PolicyKnown = true,
            WsusServer = "http://wsus.empresa.local:8530",
            AuOptions = 4
        });

        // Agente antigo (sem policyKnown) mandando só o serviço: preserva.
        await repo.UpdateServiceStateAsync(agentId, new PatchServiceStateMeta
        {
            ServiceState = "running"
        });

        var snapshot = await db.AgentWindowsUpdateSnapshots.SingleAsync(item => item.AgentId == agentId);
        Assert.That(snapshot.WsusServer, Is.EqualTo("http://wsus.empresa.local:8530"));
        Assert.That(snapshot.AuOptions, Is.EqualTo(4));
    }

    [Test]
    public async Task DownloadPercentIsClearedWhenDownloadEnds()
    {
        var (db, agentId) = Seed();
        await SeedGoodInventoryAsync(db, agentId);
        var repo = new PatchRepository(db);

        await repo.UpdateServiceStateAsync(agentId, new PatchServiceStateMeta
        {
            ServiceState = "running",
            DownloadInProgress = true,
            DownloadPercent = 42
        });
        var snapshot = await db.AgentWindowsUpdateSnapshots.SingleAsync(item => item.AgentId == agentId);
        Assert.That(snapshot.DownloadPercent, Is.EqualTo(42));

        // Download terminou (instalação pendente de reboot).
        await repo.UpdateServiceStateAsync(agentId, new PatchServiceStateMeta
        {
            ServiceState = "running",
            DownloadInProgress = false
        });
        snapshot = await db.AgentWindowsUpdateSnapshots.SingleAsync(item => item.AgentId == agentId);
        Assert.That(snapshot.DownloadInProgress, Is.False);
        Assert.That(snapshot.DownloadPercent, Is.Null, "percentual de download antigo não pode ficar exposto");
    }

    [Test]
    public async Task SnapshotKeepsKnownWsusWhenAgentOmitsIt()
    {
        var (db, agentId) = Seed();
        await SeedGoodInventoryAsync(db, agentId);
        var repo = new PatchRepository(db);

        await repo.UpdateServiceStateAsync(agentId, new PatchServiceStateMeta
        {
            ServiceState = "running",
            WsusServer = "http://wsus.empresa.local:8530",
            AuOptions = 4
        });

        // Sonda seguinte sem wsusServer (agente antigo/leitura parcial).
        await repo.UpdateServiceStateAsync(agentId, new PatchServiceStateMeta
        {
            ServiceState = "running"
        });

        var snapshot = await db.AgentWindowsUpdateSnapshots.SingleAsync(item => item.AgentId == agentId);
        Assert.That(snapshot.WsusServer, Is.EqualTo("http://wsus.empresa.local:8530"), "campo ausente nunca apaga o conhecido");
        Assert.That(snapshot.AuOptions, Is.EqualTo(4));
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
