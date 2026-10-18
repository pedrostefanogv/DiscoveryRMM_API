using System.Text.Json;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Datasets de relatório de patches (Windows Update) e do inventário de agentes.
/// Garante colunas/filtros principais para o construtor de templates.
/// </summary>
[TestFixture]
public class ReportPatchDatasetsTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid SiteId = Guid.NewGuid();
    private static readonly Guid AgentA = Guid.NewGuid();
    private static readonly Guid AgentB = Guid.NewGuid();

    private static DiscoveryDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"report-patch-{Guid.NewGuid():N}")
            .Options;
        return new ReportPatchTestDbContext(options);
    }

    private static ReportTemplate Template(ReportDatasetType type) => new()
    {
        Id = Guid.NewGuid(),
        Name = "template",
        DatasetType = type,
        LayoutJson = "{}"
    };

    private static async Task<(DiscoveryDbContext Db, ReportDatasetQueryService Service)> BuildAsync()
    {
        var db = CreateDbContext();
        var now = DateTime.UtcNow;

        db.Clients.Add(new Client { Id = ClientId, Name = "Acme", IsActive = true });
        db.Sites.Add(new Site { Id = SiteId, ClientId = ClientId, Name = "Matriz", IsActive = true });
        db.Agents.Add(new Agent
        {
            Id = AgentA,
            SiteId = SiteId,
            Hostname = "PC-01",
            OperatingSystem = "Windows 11 Pro",
            OsVersion = "10.0 (25H2)",
            AgentVersion = "1.2.1",
            Status = AgentStatus.Online,
            PendingReboot = true,
            PendingRebootReasons = "[\"windows_update\"]",
            LastSeenAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.Agents.Add(new Agent
        {
            Id = AgentB,
            SiteId = SiteId,
            Hostname = "PC-02",
            OperatingSystem = "Windows 10 Pro",
            OsVersion = "10.0 (22H2)",
            AgentVersion = "1.2.1",
            Status = AgentStatus.Offline,
            PendingReboot = false,
            LastSeenAt = now.AddHours(-2),
            CreatedAt = now,
            UpdatedAt = now
        });

        db.AgentWindowsUpdates.AddRange(
            new AgentWindowsUpdate
            {
                Id = Guid.NewGuid(),
                AgentId = AgentA,
                UpdateKey = "guid:3c3f1f1a-1111-2222-3333-444455556666:1",
                GroupKey = "kb:KB5034441",
                KbIds = "[\"KB5034441\"]",
                Title = "Security Update",
                Classification = "Security Updates",
                Severity = "Critical",
                IsPending = true,
                IsInstalled = false,
                RequiresReboot = true,
                ApprovalState = PatchApprovalState.Approved,
                ApprovalSource = "rule",
                Status = PatchUpdateStatus.Approved,
                IsPresent = true,
                FirstSeenAt = now,
                LastSeenAt = now,
                ReportedAt = now,
                CreatedAt = now,
                UpdatedAt = now
            },
            new AgentWindowsUpdate
            {
                Id = Guid.NewGuid(),
                AgentId = AgentA,
                UpdateKey = "kb:KB5034100",
                GroupKey = "kb:KB5034100",
                KbIds = "[\"KB5034100\"]",
                Title = "Definition Update",
                Classification = "Definition Updates",
                Severity = "Important",
                IsPending = false,
                IsInstalled = true,
                ApprovalState = PatchApprovalState.Unknown,
                Status = PatchUpdateStatus.Installed,
                IsPresent = true,
                FirstSeenAt = now,
                LastSeenAt = now,
                ReportedAt = now,
                InstalledAt = now,
                CreatedAt = now,
                UpdatedAt = now
            },
            new AgentWindowsUpdate
            {
                Id = Guid.NewGuid(),
                AgentId = AgentB,
                UpdateKey = "guid:11111111-1111-1111-1111-111111111111:2",
                GroupKey = "kb:KB5034500",
                KbIds = "[\"KB5034500\"]",
                Title = "Update recusada",
                Classification = "Security Updates",
                Severity = "Important",
                IsPending = true,
                IsInstalled = false,
                ApprovalState = PatchApprovalState.Denied,
                ApprovalSource = "rule",
                Status = PatchUpdateStatus.Denied,
                IsPresent = true,
                FirstSeenAt = now,
                LastSeenAt = now,
                ReportedAt = now,
                CreatedAt = now,
                UpdatedAt = now
            });

        db.AgentWindowsUpdateSnapshots.AddRange(
            new AgentWindowsUpdateSnapshot
            {
                AgentId = AgentA,
                CollectedAt = now,
                ScanStatus = "ok",
                PendingCount = 1,
                InstalledCount = 1,
                DeniedCount = 0,
                PendingReboot = true,
                PendingRebootReasons = "[\"windows_update\"]",
                RebootCheckedAt = now,
                UpdatedAt = now
            },
            new AgentWindowsUpdateSnapshot
            {
                AgentId = AgentB,
                CollectedAt = now.AddHours(-2),
                ScanStatus = "partial",
                PendingCount = 1,
                InstalledCount = 0,
                DeniedCount = 1,
                PendingReboot = false,
                UpdatedAt = now
            });

        db.PatchApprovals.Add(new PatchApproval
        {
            Id = Guid.NewGuid(),
            ScopeType = PatchApprovalScopeType.Global,
            UpdateKey = "kb:KB5034441",
            KbId = "KB5034441",
            Action = PatchApprovalActionType.Approve,
            Reason = "aprovada pelo time de TI",
            CreatedBy = "user:1",
            UpdatedBy = "user:1",
            CreatedAt = now,
            UpdatedAt = now
        });
        db.PatchApprovalAudits.Add(new PatchApprovalAudit
        {
            Id = Guid.NewGuid(),
            ChangeType = PatchApprovalAuditChangeType.Created,
            ScopeType = PatchApprovalScopeType.Global,
            UpdateKey = "kb:KB5034441",
            KbId = "KB5034441",
            NewAction = PatchApprovalActionType.Approve,
            Reason = "aprovada pelo time de TI",
            ChangedBy = "user:1",
            IpAddress = "10.0.0.1",
            ChangedAt = now
        });

        await db.SaveChangesAsync();
        return (db, new ReportDatasetQueryService(db, new MemoryCache(new MemoryCacheOptions())));
    }

    [Test]
    public async Task WindowsUpdatesDataset_ReturnsInventoryAndColumns()
    {
        var (db, service) = await BuildAsync();
        using var _ = db;

        var result = await service.QueryAsync(Template(ReportDatasetType.WindowsUpdates), null, CancellationToken.None);

        Assert.That(result.Rows, Has.Count.EqualTo(3));
        Assert.That(result.Columns, Does.Contain("kbIds"));
        Assert.That(result.Columns, Does.Contain("approvalState"));
        var row = result.Rows.Single(item => (string?)item["agentHostname"] == "PC-01" &&
            (string?)item["kbIds"] == "KB5034441");
        Assert.That(row["severity"], Is.EqualTo("Critical"));
        Assert.That(row["approvalState"], Is.EqualTo("Approved"));
        Assert.That(row["isPending"], Is.EqualTo(true));
        Assert.That(row["requiresReboot"], Is.EqualTo(true));
    }

    [Test]
    public async Task WindowsUpdatesDataset_AppliesPendingSeverityAndApprovalFilters()
    {
        var (db, service) = await BuildAsync();
        using var _ = db;

        var pending = await service.QueryAsync(
            Template(ReportDatasetType.WindowsUpdates), """{"onlyPending":true}""", CancellationToken.None);
        Assert.That(pending.Rows, Has.Count.EqualTo(2));

        var critical = await service.QueryAsync(
            Template(ReportDatasetType.WindowsUpdates), """{"onlyPending":true,"severity":"critical"}""", CancellationToken.None);
        Assert.That(critical.Rows, Has.Count.EqualTo(1));
        Assert.That(critical.Rows[0]["kbIds"], Is.EqualTo("KB5034441"));

        var denied = await service.QueryAsync(
            Template(ReportDatasetType.WindowsUpdates), """{"approvalState":"Denied"}""", CancellationToken.None);
        Assert.That(denied.Rows, Has.Count.EqualTo(1));
        Assert.That(denied.Rows[0]["agentHostname"], Is.EqualTo("PC-02"));
    }

    [Test]
    public async Task PatchComplianceDataset_ComputesActionableCounts()
    {
        var (db, service) = await BuildAsync();
        using var _ = db;

        var result = await service.QueryAsync(Template(ReportDatasetType.PatchCompliance), null, CancellationToken.None);

        Assert.That(result.Rows, Has.Count.EqualTo(2));
        var pc1 = result.Rows.Single(row => (string?)row["agentHostname"] == "PC-01");
        Assert.That(pc1["pendingCount"], Is.EqualTo(1));
        Assert.That(pc1["criticalCount"], Is.EqualTo(1));
        Assert.That(pc1["installedCount"], Is.EqualTo(1));
        Assert.That(pc1["pendingReboot"], Is.EqualTo(true));
        Assert.That(pc1["pendingRebootReasons"], Is.EqualTo("Windows Update"));

        var pc2 = result.Rows.Single(row => (string?)row["agentHostname"] == "PC-02");
        Assert.That(pc2["pendingCount"], Is.EqualTo(0), "recusadas não são pendências acionáveis");
        Assert.That(pc2["deniedCount"], Is.EqualTo(1));
        Assert.That(pc2["scanStatus"], Is.EqualTo("partial"));
    }

    [Test]
    public async Task PatchComplianceDataset_FiltersPendingRebootAndWithPending()
    {
        var (db, service) = await BuildAsync();
        using var _ = db;

        var reboot = await service.QueryAsync(
            Template(ReportDatasetType.PatchCompliance), """{"pendingReboot":true}""", CancellationToken.None);
        Assert.That(reboot.Rows, Has.Count.EqualTo(1));
        Assert.That(reboot.Rows[0]["agentHostname"], Is.EqualTo("PC-01"));

        var withPending = await service.QueryAsync(
            Template(ReportDatasetType.PatchCompliance), """{"onlyWithPending":true}""", CancellationToken.None);
        Assert.That(withPending.Rows, Has.Count.EqualTo(1));
        Assert.That(withPending.Rows[0]["agentHostname"], Is.EqualTo("PC-01"));
    }

    [Test]
    public async Task PatchApprovalsAndAuditDatasets_ReturnRules()
    {
        var (db, service) = await BuildAsync();
        using var _ = db;

        var approvals = await service.QueryAsync(Template(ReportDatasetType.PatchApprovals), null, CancellationToken.None);
        Assert.That(approvals.Rows, Has.Count.EqualTo(1));
        Assert.That(approvals.Rows[0]["action"], Is.EqualTo("Approve"));
        Assert.That(approvals.Rows[0]["scopeType"], Is.EqualTo("Global"));

        var audit = await service.QueryAsync(Template(ReportDatasetType.PatchApprovalAudit), null, CancellationToken.None);
        Assert.That(audit.Rows, Has.Count.EqualTo(1));
        Assert.That(audit.Rows[0]["changedBy"], Is.EqualTo("user:1"));
        Assert.That(audit.Rows[0]["ipAddress"], Is.EqualTo("10.0.0.1"));
    }

    [Test]
    public async Task AgentsDataset_ReturnsFleetStateAndFilters()
    {
        var (db, service) = await BuildAsync();
        using var _ = db;

        var all = await service.QueryAsync(Template(ReportDatasetType.Agents), null, CancellationToken.None);
        Assert.That(all.Rows, Has.Count.EqualTo(2));
        var pc1 = all.Rows.Single(row => (string?)row["hostname"] == "PC-01");
        Assert.That(pc1["status"], Is.EqualTo("Online"));
        Assert.That(pc1["pendingReboot"], Is.EqualTo(true));
        Assert.That(pc1["pendingRebootReasons"], Is.EqualTo("Windows Update"));

        var offline = await service.QueryAsync(
            Template(ReportDatasetType.Agents), """{"status":"offline"}""", CancellationToken.None);
        Assert.That(offline.Rows, Has.Count.EqualTo(1));
        Assert.That(offline.Rows[0]["hostname"], Is.EqualTo("PC-02"));

        var reboot = await service.QueryAsync(
            Template(ReportDatasetType.Agents), """{"pendingReboot":true}""", CancellationToken.None);
        Assert.That(reboot.Rows, Has.Count.EqualTo(1));
        Assert.That(reboot.Rows[0]["hostname"], Is.EqualTo("PC-01"));
    }

    [Test]
    public void DatasetCatalog_ExposesPatchAndAgentDatasets()
    {
        var catalog = new ReportDatasetCatalogProvider().GetAll();
        var keys = catalog.Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.That(keys, Does.Contain("windowsUpdates"));
        Assert.That(keys, Does.Contain("patchCompliance"));
        Assert.That(keys, Does.Contain("patchApprovals"));
        Assert.That(keys, Does.Contain("patchApprovalAudit"));
        Assert.That(keys, Does.Contain("agents"));

        var updates = catalog.Single(item => item.Key == "windowsUpdates");
        Assert.That(updates.DatasetType, Is.EqualTo((int)ReportDatasetType.WindowsUpdates));
        Assert.That(updates.Fields, Does.Contain("kbIds"));
        Assert.That(updates.Fields, Does.Contain("approvalState"));
        Assert.That(updates.FieldMetadata.Any(meta => meta.Field == "kbIds"), Is.True);
    }

    private sealed class ReportPatchTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(Client),
                typeof(Site),
                typeof(Agent),
                typeof(AgentLabel),
                typeof(AgentWindowsUpdate),
                typeof(AgentWindowsUpdateSnapshot),
                typeof(PatchApproval),
                typeof(PatchApprovalAudit)
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
            modelBuilder.Entity<AgentLabel>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<AgentWindowsUpdate>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<AgentWindowsUpdateSnapshot>(entity => entity.HasKey(item => item.AgentId));
            modelBuilder.Entity<PatchApproval>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<PatchApprovalAudit>(entity => entity.HasKey(item => item.Id));
        }
    }
}
