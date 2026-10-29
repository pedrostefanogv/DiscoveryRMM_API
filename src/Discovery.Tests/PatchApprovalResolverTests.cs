using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Resolução do estado efetivo de aprovação: escopo mais específico vence,
/// Deny vence Approve em empate e política auto-aprova por severidade.
/// </summary>
[TestFixture]
public class PatchApprovalResolverTests
{
    private static DiscoveryDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase("patch-approval-" + Guid.NewGuid().ToString("N"))
            .Options;
        return new PatchTestDbContext(options);
    }

    /// <summary>
    /// Contexto reduzido: o provider InMemory não mapeia a coluna pgvector de
    /// KnowledgeArticleChunk, então só as entidades usadas aqui são mapeadas
    /// (mesmo padrão de AgentTrashTestDbContext).
    /// </summary>
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
                typeof(PatchPolicy),
                typeof(AppCatalogEntry)
            };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null &&
                                        type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowed.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            // Sem base.OnModelCreating: o modelo é reduzido de propósito (o mesmo
            // padrão de AgentTrashTestDbContext) para o InMemory não ver pgvector.
            modelBuilder.Entity<Client>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<Site>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<Agent>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<AgentWindowsUpdate>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<AgentWindowsUpdateSnapshot>(entity => entity.HasKey(item => item.AgentId));
            modelBuilder.Entity<PatchApproval>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<PatchPolicy>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<AppCatalogEntry>(entity => entity.HasKey(item => item.Id));
        }
    }

    private static (DiscoveryDbContext Db, Guid AgentId, Guid ClientId, Guid SiteId) Seed()
    {
        var db = NewContext();
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        db.Clients.Add(new Client { Id = clientId, Name = "Cliente" });
        db.Sites.Add(new Site { Id = siteId, ClientId = clientId, Name = "Site" });
        db.Agents.Add(new Agent { Id = agentId, SiteId = siteId, Hostname = "HOST" });
        db.SaveChanges();

        return (db, agentId, clientId, siteId);
    }

    private static AgentWindowsUpdate Update(
        Guid agentId,
        string groupKey,
        string? severity = null,
        DateTime? publishedAt = null)
        => new()
        {
            PublishedAt = publishedAt,
            Id = Guid.NewGuid(),
            AgentId = agentId,
            UpdateKey = groupKey.StartsWith("kb:") ? "guid:" + Guid.NewGuid() + ":1" : groupKey,
            GroupKey = groupKey,
            KbIds = groupKey.StartsWith("kb:") ? "[\"" + groupKey[3..] + "\"]" : null,
            Title = "Update " + groupKey,
            Severity = severity,
            Classification = "Security Updates",
            IsPending = true,
            IsPresent = true,
            Status = PatchUpdateStatus.Pending,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    [Test]
    public async Task ExplicitDenyWinsOverApproveInSameScope()
    {
        var (db, agentId, clientId, siteId) = Seed();
        db.AgentWindowsUpdates.Add(Update(agentId, "kb:KB5034441"));
        db.PatchApprovals.Add(new PatchApproval
        {
            Id = Guid.NewGuid(),
            ScopeType = PatchApprovalScopeType.Global,
            UpdateKey = "kb:KB5034441",
            Action = PatchApprovalActionType.Deny,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var resolver = new PatchApprovalResolver(db);
        await resolver.RecomputeForAgentAsync(agentId);

        var row = await db.AgentWindowsUpdates.SingleAsync();
        Assert.That(row.ApprovalState, Is.EqualTo(PatchApprovalState.Denied));
        Assert.That(row.Status, Is.EqualTo(PatchUpdateStatus.Denied));
        Assert.That(row.ApprovalSource, Is.EqualTo("rule"));
    }

    [Test]
    public async Task AgentScopeWinsOverGlobal()
    {
        var (db, agentId, clientId, siteId) = Seed();
        db.AgentWindowsUpdates.Add(Update(agentId, "kb:KB5034441"));
        db.PatchApprovals.Add(new PatchApproval
        {
            Id = Guid.NewGuid(),
            ScopeType = PatchApprovalScopeType.Global,
            UpdateKey = "kb:KB5034441",
            Action = PatchApprovalActionType.Deny,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        db.PatchApprovals.Add(new PatchApproval
        {
            Id = Guid.NewGuid(),
            ScopeType = PatchApprovalScopeType.Agent,
            ScopeId = agentId,
            ClientId = clientId,
            SiteId = siteId,
            AgentId = agentId,
            UpdateKey = "kb:KB5034441",
            Action = PatchApprovalActionType.Approve,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var resolver = new PatchApprovalResolver(db);
        await resolver.RecomputeForAgentAsync(agentId);

        var row = await db.AgentWindowsUpdates.SingleAsync();
        Assert.That(row.ApprovalState, Is.EqualTo(PatchApprovalState.Approved));
        Assert.That(row.Status, Is.EqualTo(PatchUpdateStatus.Approved));
    }

    [Test]
    public async Task PolicyAutoApprovesBySeverity()
    {
        var (db, agentId, clientId, siteId) = Seed();
        db.AgentWindowsUpdates.Add(Update(agentId, "kb:KB5034441", "Critical"));
        db.AgentWindowsUpdates.Add(Update(agentId, "kb:KB5034100", "Low"));
        db.PatchPolicies.Add(new PatchPolicy
        {
            Id = Guid.NewGuid(),
            ScopeType = PatchApprovalScopeType.Global,
            AutoApproveSeverities = "[\"Critical\"]",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var resolver = new PatchApprovalResolver(db);
        await resolver.RecomputeForAgentAsync(agentId);

        var critical = await db.AgentWindowsUpdates.SingleAsync(update => update.Severity == "Critical");
        var low = await db.AgentWindowsUpdates.SingleAsync(update => update.Severity == "Low");
        Assert.That(critical.ApprovalState, Is.EqualTo(PatchApprovalState.AutoApproved));
        Assert.That(critical.ApprovalSource, Is.EqualTo("policy"));
        Assert.That(low.ApprovalState, Is.EqualTo(PatchApprovalState.Unknown));
    }

    // ── Deferral por idade de publicação (D09, fail-closed) ──
    //
    // Política auto-aprova Critical, mas com deferral de 7 dias. O update só
    // pode ser auto-aprovado depois de PublishedAt + 7d; sem data é
    // fail-closed (retido). Aprovação manual explícita vence o deferral.

    private static PatchPolicy DeferralPolicy(int deferralDays) => new()
    {
        Id = Guid.NewGuid(),
        ScopeType = PatchApprovalScopeType.Global,
        AutoApproveSeverities = "[\"Critical\"]",
        DeferralDays = deferralDays,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    [Test]
    public async Task Deferral_PublicacaoRecente_NaoAutoAprova()
    {
        var (db, agentId, _, _) = Seed();
        db.AgentWindowsUpdates.Add(Update(agentId, "kb:KB5034441", "Critical", DateTime.UtcNow.AddDays(-2)));
        db.PatchPolicies.Add(DeferralPolicy(7));
        await db.SaveChangesAsync();

        var resolver = new PatchApprovalResolver(db);
        await resolver.RecomputeForAgentAsync(agentId);

        var row = await db.AgentWindowsUpdates.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(row.ApprovalState, Is.EqualTo(PatchApprovalState.Unknown));
            Assert.That(row.ApprovalSource, Is.Null);
            Assert.That(row.Status, Is.EqualTo(PatchUpdateStatus.Pending));
        });
    }

    [Test]
    public async Task Deferral_PublicacaoAntiga_AutoAprova()
    {
        var (db, agentId, _, _) = Seed();
        db.AgentWindowsUpdates.Add(Update(agentId, "kb:KB5034441", "Critical", DateTime.UtcNow.AddDays(-30)));
        db.PatchPolicies.Add(DeferralPolicy(7));
        await db.SaveChangesAsync();

        var resolver = new PatchApprovalResolver(db);
        await resolver.RecomputeForAgentAsync(agentId);

        var row = await db.AgentWindowsUpdates.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(row.ApprovalState, Is.EqualTo(PatchApprovalState.AutoApproved));
            Assert.That(row.ApprovalSource, Is.EqualTo("policy"));
            Assert.That(row.Status, Is.EqualTo(PatchUpdateStatus.Approved));
        });
    }

    [Test]
    public async Task Deferral_SemPublishedAt_EhFailClosed()
    {
        var (db, agentId, _, _) = Seed();
        db.AgentWindowsUpdates.Add(Update(agentId, "kb:KB5034441", "Critical", publishedAt: null));
        db.PatchPolicies.Add(DeferralPolicy(7));
        await db.SaveChangesAsync();

        var resolver = new PatchApprovalResolver(db);
        await resolver.RecomputeForAgentAsync(agentId);

        var row = await db.AgentWindowsUpdates.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(row.ApprovalState, Is.EqualTo(PatchApprovalState.Unknown));
            Assert.That(row.ApprovalSource, Is.Null);
        });
    }

    [Test]
    public async Task Deferral_AprovacaoManualVence()
    {
        var (db, agentId, _, _) = Seed();
        // Publicado ontem: ainda dentro do deferral de 7 dias.
        db.AgentWindowsUpdates.Add(Update(agentId, "kb:KB5034441", "Critical", DateTime.UtcNow.AddDays(-1)));
        db.PatchPolicies.Add(DeferralPolicy(7));
        db.PatchApprovals.Add(new PatchApproval
        {
            Id = Guid.NewGuid(),
            ScopeType = PatchApprovalScopeType.Global,
            UpdateKey = "kb:KB5034441",
            Action = PatchApprovalActionType.Approve,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var resolver = new PatchApprovalResolver(db);
        await resolver.RecomputeForAgentAsync(agentId);

        var row = await db.AgentWindowsUpdates.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(row.ApprovalState, Is.EqualTo(PatchApprovalState.Approved));
            Assert.That(row.ApprovalSource, Is.EqualTo("rule"));
        });
    }

    [Test]
    public async Task Deferral_Zero_MantemComportamentoAnterior()
    {
        var (db, agentId, _, _) = Seed();
        // Sem PublishedAt e sem deferral: política auto-aprova como antes.
        db.AgentWindowsUpdates.Add(Update(agentId, "kb:KB5034441", "Critical"));
        db.PatchPolicies.Add(DeferralPolicy(0));
        await db.SaveChangesAsync();

        var resolver = new PatchApprovalResolver(db);
        await resolver.RecomputeForAgentAsync(agentId);

        var row = await db.AgentWindowsUpdates.SingleAsync();
        Assert.That(row.ApprovalState, Is.EqualTo(PatchApprovalState.AutoApproved));
    }

    [Test]
    public async Task SnapshotCountsReflectApprovals()
    {
        var (db, agentId, clientId, siteId) = Seed();
        db.AgentWindowsUpdates.Add(Update(agentId, "kb:KB5034441"));
        db.AgentWindowsUpdates.Add(Update(agentId, "kb:KB5034100"));
        db.PatchApprovals.Add(new PatchApproval
        {
            Id = Guid.NewGuid(),
            ScopeType = PatchApprovalScopeType.Global,
            UpdateKey = "kb:KB5034441",
            Action = PatchApprovalActionType.Approve,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var resolver = new PatchApprovalResolver(db);
        await resolver.RecomputeForAgentAsync(agentId);

        var snapshot = await db.AgentWindowsUpdateSnapshots.SingleAsync();
        Assert.That(snapshot.PendingCount, Is.EqualTo(2));
        Assert.That(snapshot.DeniedCount, Is.EqualTo(0));
    }

    // ── WU-21 (D24): catálogo curado para apps de terceiros ─────────────────

    private static PatchPolicy ThirdPartyPolicy() => new()
    {
        Id = Guid.NewGuid(),
        ScopeType = PatchApprovalScopeType.Global,
        AutoApproveSeverities = "[\"Important\"]",
        Sources = "[\"os\",\"third_party\"]",
        AllowThirdPartyApps = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static AgentWindowsUpdate ThirdPartyUpdate(Guid agentId, string appId, string source = "choco") => new()
    {
        Id = Guid.NewGuid(),
        AgentId = agentId,
        UpdateKey = source + ":" + appId,
        GroupKey = source + ":" + appId,
        Title = appId,
        Severity = "Important",
        Classification = "Third-party",
        Source = source,
        IsPending = true,
        IsPresent = true,
        Status = PatchUpdateStatus.Pending,
        FirstSeenAt = DateTime.UtcNow,
        LastSeenAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static AppCatalogEntry CatalogEntry(string appId, string status, string source = "choco") => new()
    {
        Id = Guid.NewGuid(),
        Source = source,
        PackageId = appId,
        Name = appId,
        Version = "2.0.0",
        InstallerUrl = "https://example.com/app.msi",
        Sha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        Status = status,
        CuratedAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    [Test]
    public async Task ProviderManagedThirdPartyAppIsAutoApprovedWithConsentOnly()
    {
        var (db, agentId, _, _) = Seed();
        // choco: o gerenciador valida o próprio feed; catálogo não é pré-requisito.
        db.AgentWindowsUpdates.Add(ThirdPartyUpdate(agentId, "googlechrome"));
        db.PatchPolicies.Add(ThirdPartyPolicy());
        await db.SaveChangesAsync();

        var resolver = new PatchApprovalResolver(db);
        await resolver.RecomputeForAgentAsync(agentId);

        var row = await db.AgentWindowsUpdates.SingleAsync();
        Assert.That(row.ApprovalState, Is.EqualTo(PatchApprovalState.AutoApproved));
        Assert.That(row.ApprovalSource, Is.EqualTo("policy"));
    }

    [Test]
    public async Task ThirdPartyWithoutConsentIsNeverAutoApproved()
    {
        var (db, agentId, _, _) = Seed();
        db.AgentWindowsUpdates.Add(ThirdPartyUpdate(agentId, "googlechrome"));
        var policy = ThirdPartyPolicy();
        policy.AllowThirdPartyApps = false;
        db.PatchPolicies.Add(policy);
        await db.SaveChangesAsync();

        var resolver = new PatchApprovalResolver(db);
        await resolver.RecomputeForAgentAsync(agentId);

        var row = await db.AgentWindowsUpdates.SingleAsync();
        Assert.That(row.ApprovalState, Is.EqualTo(PatchApprovalState.Unknown));
    }

    [Test]
    public async Task DirectDownloadMsiWithoutCuratedCatalogStaysUnknown()
    {
        var (db, agentId, _, _) = Seed();
        db.AgentWindowsUpdates.Add(ThirdPartyUpdate(agentId, "vendor-app", source: "msi"));
        db.PatchPolicies.Add(ThirdPartyPolicy());
        await db.SaveChangesAsync();

        var resolver = new PatchApprovalResolver(db);
        await resolver.RecomputeForAgentAsync(agentId);

        var row = await db.AgentWindowsUpdates.SingleAsync();
        Assert.That(row.ApprovalState, Is.EqualTo(PatchApprovalState.Unknown),
            "download direto exige versão curada (hash/assinatura vêm do catálogo)");
    }

    [Test]
    public async Task DirectDownloadMsiWithActiveCatalogEntryIsAutoApproved()
    {
        var (db, agentId, _, _) = Seed();
        db.AgentWindowsUpdates.Add(ThirdPartyUpdate(agentId, "vendor-app", source: "msi"));
        db.PatchPolicies.Add(ThirdPartyPolicy());
        db.AppCatalogEntries.Add(CatalogEntry("vendor-app", "active", source: "msi"));
        await db.SaveChangesAsync();

        var resolver = new PatchApprovalResolver(db);
        await resolver.RecomputeForAgentAsync(agentId);

        var row = await db.AgentWindowsUpdates.SingleAsync();
        Assert.That(row.ApprovalState, Is.EqualTo(PatchApprovalState.AutoApproved));
    }

    [Test]
    public async Task RevokedCatalogEntryBlocksDirectDownloadAutoApproval()
    {
        var (db, agentId, _, _) = Seed();
        db.AgentWindowsUpdates.Add(ThirdPartyUpdate(agentId, "vendor-app", source: "msi"));
        db.PatchPolicies.Add(ThirdPartyPolicy());
        db.AppCatalogEntries.Add(CatalogEntry("vendor-app", "revoked", source: "msi"));
        await db.SaveChangesAsync();

        var resolver = new PatchApprovalResolver(db);
        await resolver.RecomputeForAgentAsync(agentId);

        var row = await db.AgentWindowsUpdates.SingleAsync();
        Assert.That(row.ApprovalState, Is.EqualTo(PatchApprovalState.Unknown),
            "versão revogada (CVE/regressão) não pode ser auto-aprovada");
    }
}
