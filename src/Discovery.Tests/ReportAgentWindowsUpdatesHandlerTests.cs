using Discovery.Core.Cqrs.AgentAuth.Patches;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Upload do inventário de Windows Update: o handler decide quando preservar o
/// inventário bom (scan parcial/erro/vazio) e repassa o bloco opcional "state"
/// ao repositório.
///
/// Usa um repositório fake que só registra as chamadas — a lógica de dados em
/// si é coberta por PatchRepositoryInventoryTests.
/// </summary>
[TestFixture]
public class ReportAgentWindowsUpdatesHandlerTests
{
    private static (ReportAgentWindowsUpdatesHandler Handler, RecordingPatchRepository Repo, Guid AgentId)
        Build()
    {
        var agentId = Guid.NewGuid();
        var agent = new Agent { Id = agentId, SiteId = Guid.NewGuid(), Hostname = "HOST" };

        var repo = new RecordingPatchRepository();
        var handler = new ReportAgentWindowsUpdatesHandler(
            repo,
            new NoopApprovalResolver(),
            new SingleAgentRepository(agent),
            new EmptySiteRepository(),
            new NoopEventPublisher());

        return (handler, repo, agentId);
    }

    private static ReportAgentWindowsUpdatesCommand Command(Guid agentId, string scanStatus, string? state)
        => new(
            agentId,
            DateTime.UtcNow,
            new { status = scanStatus, durationMs = 1500, errorCode = scanStatus == "ok" ? null : scanStatus },
            new { pending = false, checkedAt = DateTime.UtcNow },
            null,
            state is null ? null : System.Text.Json.JsonDocument.Parse(state).RootElement.Clone());

    [Test]
    public async Task PartialScanWithoutUpdatesPreservesInventory()
    {
        var (handler, repo, agentId) = Build();

        var result = await handler.Handle(
            Command(agentId, "partial", """{ "serviceState": "running", "wuOnline": true }"""),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(repo.ReplaceCallCount, Is.EqualTo(0), "upload parcial vazio não substitui inventário");
        Assert.That(repo.UpdateScanStatusCallCount, Is.EqualTo(1));
        Assert.That(repo.LastServiceState, Is.Not.Null, "sonda chega ao repositório mesmo sem lista");
        Assert.That(repo.LastServiceState!.ServiceState, Is.EqualTo("running"));
        Assert.That(repo.LastScanStatus, Is.EqualTo("partial"));
    }

    [Test]
    public async Task ErrorScanWithoutUpdatesPreservesInventory()
    {
        var (handler, repo, agentId) = Build();

        var result = await handler.Handle(
            Command(agentId, "error", """{ "serviceState": "disabled", "wuOnline": false }"""),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(repo.ReplaceCallCount, Is.EqualTo(0));
        Assert.That(repo.LastServiceState!.ServiceState, Is.EqualTo("disabled"));
        Assert.That(repo.LastServiceState.WuOnline, Is.False);
    }

    [Test]
    public async Task OkScanWithoutUpdatesDoesNotWipeInventory()
    {
        var (handler, repo, agentId) = Build();

        // WUA devolveu lista vazia com status ok (serviço desabilitado/WSUS):
        // o repositório recebe a chamada, mas com lista vazia ele preserva tudo.
        var result = await handler.Handle(Command(agentId, "ok", null), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(repo.ReplaceCallCount, Is.EqualTo(1));
        Assert.That(repo.LastEntries, Is.Not.Null);
        Assert.That(repo.LastEntries!, Is.Empty);
    }

    [Test]
    public async Task LegacyEnvelopeWithoutStateStillWorks()
    {
        var (handler, repo, agentId) = Build();

        var result = await handler.Handle(
            Command(agentId, "partial", null), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(repo.LastServiceState, Is.Null, "agente antigo: sem sonda, sem erro");
    }

    // ── Fakes ─────────────────────────────────────────────────────────────

    private sealed class RecordingPatchRepository : IPatchRepository
    {
        public int ReplaceCallCount { get; private set; }
        public int UpdateScanStatusCallCount { get; private set; }
        public PatchServiceStateMeta? LastServiceState { get; private set; }
        public string? LastScanStatus { get; private set; }
        public IReadOnlyList<AgentWindowsUpdateEntry>? LastEntries { get; private set; }

        public Task ReplaceInventoryAsync(
            Guid agentId,
            DateTime collectedAt,
            PatchScanMeta scan,
            PatchPendingRebootMeta? pendingReboot,
            IReadOnlyList<AgentWindowsUpdateEntry> updates,
            PatchServiceStateMeta? serviceState = null,
            CancellationToken ct = default)
        {
            ReplaceCallCount++;
            LastServiceState = serviceState;
            LastScanStatus = scan.Status;
            LastEntries = updates;
            return Task.CompletedTask;
        }

        public Task UpdateScanStatusAsync(
            Guid agentId,
            PatchScanMeta scan,
            PatchPendingRebootMeta? pendingReboot,
            CancellationToken ct = default)
            => UpdateScanStatusAsync(agentId, scan, pendingReboot, null, ct);

        public Task UpdateScanStatusAsync(
            Guid agentId,
            PatchScanMeta scan,
            PatchPendingRebootMeta? pendingReboot,
            PatchServiceStateMeta? serviceState,
            CancellationToken ct = default)
        {
            UpdateScanStatusCallCount++;
            LastServiceState = serviceState;
            LastScanStatus = scan.Status;
            return Task.CompletedTask;
        }

        public Task UpdateServiceStateAsync(Guid agentId, PatchServiceStateMeta state, CancellationToken ct = default)
        {
            LastServiceState = state;
            return Task.CompletedTask;
        }

        public Task<AgentWindowsUpdatePageResult> GetAgentUpdatesPageAsync(
            Guid agentId, int page, int pageSize, string? search,
            Discovery.Core.Enums.PatchUpdateStatus? status, string? severity, bool descending,
            CancellationToken ct = default)
            => Task.FromResult(new AgentWindowsUpdatePageResult());

        public Task<AgentWindowsUpdateSnapshot?> GetSnapshotAsync(Guid agentId, CancellationToken ct = default)
            => Task.FromResult<AgentWindowsUpdateSnapshot?>(null);

        public Task<AgentWindowsUpdate?> GetAgentUpdateAsync(Guid agentId, string updateKey, CancellationToken ct = default)
            => Task.FromResult<AgentWindowsUpdate?>(null);

        public Task<IReadOnlyList<AgentWindowsUpdate>> GetPendingForAgentAsync(Guid agentId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentWindowsUpdate>>([]);

        public Task<IReadOnlyList<AgentWindowsUpdate>> GetPendingForAgentsAsync(
            IReadOnlyCollection<Guid> agentIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentWindowsUpdate>>([]);

        public Task UpdateAgentPendingRebootAsync(
            Guid agentId, bool pending, IReadOnlyList<string> reasons, DateTime checkedAt,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<int> ExpireStaleInstallingAsync(TimeSpan maxAge, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<int> PurgeStaleInventoryAsync(TimeSpan maxAge, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task UpdateInstallStateAsync(
            Guid agentId, IReadOnlyCollection<string> updateKeys,
            Discovery.Core.Enums.PatchUpdateStatus status, int? resultCode, string? error,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<PatchInventoryGroup>> GetInventoryGroupsAsync(
            Guid? clientId, Guid? siteId, string? cursor, int limit, string? search,
            string? severity, string? status, bool descending, bool hasGlobalAccess,
            IReadOnlyCollection<Guid> allowedClientIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PatchInventoryGroup>>([]);

        public Task<IReadOnlyList<PatchInventoryAgentRow>> GetInventoryAgentsAsync(
            string groupKey, Guid? clientId, Guid? siteId, string? cursor, int limit,
            Discovery.Core.Enums.PatchUpdateStatus? status, bool hasGlobalAccess,
            IReadOnlyCollection<Guid> allowedClientIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PatchInventoryAgentRow>>([]);

        public Task<IReadOnlyList<PatchPendingAgentRow>> GetPendingAgentsAsync(
            Guid? clientId, Guid? siteId, string? cursor, int limit, string? search,
            bool hasGlobalAccess, IReadOnlyCollection<Guid> allowedClientIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PatchPendingAgentRow>>([]);

        public Task<PatchScopeSnapshot> GetScopeSnapshotAsync(
            Guid? clientId, Guid? siteId, bool hasGlobalAccess,
            IReadOnlyCollection<Guid> allowedClientIds, CancellationToken ct = default)
            => Task.FromResult(new PatchScopeSnapshot());

        public Task<IReadOnlyList<PatchSeverityCount>> GetPendingBySeverityAsync(
            Guid? clientId, Guid? siteId, bool hasGlobalAccess,
            IReadOnlyCollection<Guid> allowedClientIds, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PatchSeverityCount>>([]);

        public Task<IReadOnlyList<PatchSeverityCount>> GetPendingByClassificationAsync(
            Guid? clientId, Guid? siteId, bool hasGlobalAccess,
            IReadOnlyCollection<Guid> allowedClientIds, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PatchSeverityCount>>([]);

        public Task<IReadOnlyList<Guid>> GetAgentIdsInScopeAsync(
            Guid? clientId, Guid? siteId, bool hasGlobalAccess,
            IReadOnlyCollection<Guid> allowedClientIds, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task<IReadOnlyList<PatchApproval>> GetApprovalsAsync(
            Discovery.Core.Enums.PatchApprovalScopeType? scopeType, Guid? scopeId, string? search,
            int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PatchApproval>>([]);

        public Task<PatchApproval?> GetApprovalAsync(
            Discovery.Core.Enums.PatchApprovalScopeType scopeType, Guid? scopeId, string updateKey,
            CancellationToken ct = default)
            => Task.FromResult<PatchApproval?>(null);

        public Task<PatchApproval?> GetApprovalByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<PatchApproval?>(null);

        public Task AddApprovalAuditAsync(PatchApprovalAudit audit, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<PatchApproval> UpsertApprovalAsync(
            PatchApproval approval, PatchApprovalAudit audit, CancellationToken ct = default)
            => Task.FromResult(approval);

        public Task<bool> DeleteApprovalAsync(
            Guid id, string? changedBy, string? ipAddress, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<IReadOnlyList<PatchApprovalAudit>> GetApprovalAuditAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PatchApprovalAudit>>([]);

        public Task<IReadOnlyList<PatchApproval>> GetApplicableApprovalsForAgentAsync(
            Guid agentId, Guid clientId, Guid siteId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PatchApproval>>([]);

        public Task<PatchPolicy?> GetPolicyAsync(
            Discovery.Core.Enums.PatchApprovalScopeType scopeType, Guid? scopeId,
            CancellationToken ct = default)
            => Task.FromResult<PatchPolicy?>(null);

        public Task<IReadOnlyList<PatchPolicy>> GetApplicablePoliciesForAgentAsync(
            Guid clientId, Guid siteId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PatchPolicy>>([]);

        public Task<PatchPolicy> UpsertPolicyAsync(PatchPolicy policy, CancellationToken ct = default)
            => Task.FromResult(policy);

        public Task<IReadOnlyList<PatchPolicy>> GetPoliciesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PatchPolicy>>([]);

        public Task<PatchOsState> SaveOsStateAsync(PatchOsState state, CancellationToken ct = default)
            => Task.FromResult(state);

        public Task<PatchOsState?> GetLatestOsStateAsync(Guid agentId, CancellationToken ct = default)
            => Task.FromResult<PatchOsState?>(null);

        public Task MarkOsStateRevertedAsync(Guid stateId, string? revertedBy, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task UpdateOsStateBackupAsync(Guid agentId, string previousJson, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class NoopApprovalResolver : IPatchApprovalResolver
    {
        public Task<int> RecomputeForAgentAsync(Guid agentId, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<int> RecomputeForGroupKeyAsync(string groupKey, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<int> RecomputeForScopeAsync(
            Discovery.Core.Enums.PatchApprovalScopeType scopeType, Guid? scopeId,
            CancellationToken ct = default)
            => Task.FromResult(0);

        public Task RefreshSnapshotCountsAsync(Guid agentId, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class SingleAgentRepository(Agent agent) : IAgentRepository
    {
        public Task<Agent?> GetByIdAsync(Guid id) => Task.FromResult<Agent?>(id == agent.Id ? agent : null);

        // Membros não usados pelo handler: mantidos por contrato.
        public Task<IReadOnlyList<Agent>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Agent>>([]);
        public Task<IEnumerable<Agent>> GetAllAsync() => throw new NotSupportedException();
        public Task<IEnumerable<Agent>> GetBySiteIdAsync(Guid siteId) => throw new NotSupportedException();
        public Task<IEnumerable<Agent>> GetByClientIdAsync(Guid clientId) => throw new NotSupportedException();
        public Task<Agent> CreateAsync(Agent item) => throw new NotSupportedException();
        public Task UpdateAsync(Agent item) => throw new NotSupportedException();
        public Task UpdateStatusAsync(Guid id, Discovery.Core.Enums.AgentStatus status, string? ipAddress)
            => throw new NotSupportedException();
        public Task SetPolicySyncAsync(Guid id, string fingerprint, DateTime syncedAt, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<Agent>> GetOnlineAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task ApproveZeroTouchAsync(Guid agentId) => throw new NotSupportedException();
        public Task SetMaintenanceAsync(Guid id, bool enabled, string? reason, Guid changedByUserId)
            => throw new NotSupportedException();
        public Task TransferSiteAsync(Guid agentId, Guid newSiteId) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id) => throw new NotSupportedException();
        public Task<IReadOnlyList<Agent>> FindByFingerprintAsync(
            string fingerprintHash, Guid clientId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class EmptySiteRepository : ISiteRepository
    {
        public Task<Site?> GetByIdAsync(Guid id) => Task.FromResult<Site?>(null);
        public Task<IEnumerable<Site>> GetByClientIdAsync(Guid clientId, bool includeInactive = false) => throw new NotSupportedException();
        public Task<IEnumerable<Site>> GetByClientIdsAsync(IEnumerable<Guid> clientIds, bool includeInactive = false) => throw new NotSupportedException();
        public Task<IEnumerable<Site>> GetAllAsync(bool includeInactive = false) => throw new NotSupportedException();
        public Task<IEnumerable<Site>> GetByIdsAsync(IEnumerable<Guid> siteIds, bool includeInactive = false) => throw new NotSupportedException();
        public Task<Site> CreateAsync(Site site) => throw new NotSupportedException();
        public Task UpdateAsync(Site site) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id) => throw new NotSupportedException();
    }

    private sealed class NoopEventPublisher : IPatchEventPublisher
    {
        public Task PublishScanCompletedAsync(
            Guid agentId, string hostname, Guid? clientId, Guid? siteId, int pendingCount,
            int installedCount, bool pendingReboot, string scanStatus, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task PublishInstallCompletedAsync(
            Guid agentId, string hostname, Guid? clientId, Guid? siteId, int installedCount,
            int failedCount, bool rebootRequired, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task PublishApprovalChangedAsync(
            Guid agentId, string hostname, Guid? clientId, Guid? siteId, string updateKey, string action,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
